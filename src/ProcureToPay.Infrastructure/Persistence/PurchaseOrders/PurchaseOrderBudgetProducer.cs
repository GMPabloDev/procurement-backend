using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>One resolved reservation of an ordered target: the open RESERVED movement and its remainder.</summary>
public sealed record PurchaseOrderReservation(
    Guid TargetId,
    int TargetVersion,
    Guid MovementId,
    decimal Amount,
    decimal Remaining,
    string PositionKeyDigest,
    string ParentSourceType,
    Guid ParentSourceId,
    int ParentSourceVersion,
    string ParentSourceDigest);

/// <summary>Outcome of one committed budget operation of a Purchase Order.</summary>
public sealed record PurchaseOrderBudgetOutcome(
    Guid OperationId,
    IReadOnlyList<PurchaseOrderBudgetOperationRef> OperationRefs,
    bool Replayed);

/// <summary>
/// First productive producer of <c>COMMIT + contract_version=v1 + source_type=PURCHASE_ORDER</c>
/// (SPEC 11 REQ-04, DEC-08). It resolves the exact reservations of the ordered targets, commits the
/// ordered amount all-or-nothing, releases the open remainder of those reservations by
/// <c>REVERSE</c>, and keeps one durable attempt per operation so a crash or a retry recovers the
/// same keys instead of posting a second effect.
/// </summary>
public sealed class PurchaseOrderBudgetProducer(
    ProcureToPayDbContext dbContext,
    BudgetTransitionService transitions,
    ApprovalWorkloadIdentity? workload = null)
{
    private readonly ApprovalWorkloadIdentity workload = workload ?? new ApprovalWorkloadIdentity(
        PurchaseOrderCodes.DomainWorkloadIssuer, PurchaseOrderCodes.DomainWorkloadClientId);

    /// <summary>
    /// Resolves the exactly-one open reservation of every ordered target. Zero, two or a foreign chain
    /// fails closed instead of committing against an arbitrary movement (REQ-04).
    /// </summary>
    public async Task<IReadOnlyList<PurchaseOrderReservation>> ResolveReservationsAsync(
        Guid organizationId,
        IReadOnlyList<OrderingEvidenceTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var reservations = new List<PurchaseOrderReservation>(targets.Count);
        foreach (var target in targets)
        {
            var movements = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.OrganizationId == organizationId &&
                                   movement.Type == (int)BudgetMovementType.Reserved &&
                                   movement.TargetId == target.TargetId &&
                                   movement.TargetVersion == target.TargetVersion &&
                                   movement.TargetMaterialSnapshotDigest == target.MaterialSnapshotDigest)
                .OrderBy(movement => movement.OccurredAt)
                .ToArrayAsync(cancellationToken);
            if (movements.Length == 0)
            {
                throw new PurchaseOrderDependencyUnavailableException(
                    "An ordered target has no reservation to commit.");
            }

            var open = new List<(BudgetMovementRecord Movement, decimal Remaining)>();
            foreach (var movement in movements)
            {
                var children = await dbContext.BudgetMovements
                    .AsNoTracking()
                    .Where(child => child.ParentMovementId == movement.Id)
                    .SumAsync(child => (decimal?)child.Amount, cancellationToken) ?? 0m;
                var remaining = movement.Amount - children;
                if (remaining > 0m)
                {
                    open.Add((movement, remaining));
                }
            }

            if (open.Count == 0)
            {
                throw new DomainConflictException("The reservation of an ordered target is already consumed.");
            }

            if (open.Count > 1)
            {
                throw new PurchaseOrderDependencyUnavailableException(
                    "An ordered target has more than one open reservation chain.");
            }

            var (record, remainder) = open[0];
            var position = await dbContext.BudgetPositions
                .AsNoTracking()
                .Where(candidate => candidate.Id == record.PositionId)
                .Select(candidate => new { candidate.CostCenterId, candidate.FiscalYear, candidate.SpendCategoryCode })
                .SingleAsync(cancellationToken);
            // SPEC 08: a REVERSE reuses the identity and source of its parent operation, so the
            // producer records the source of the reservation it will release.
            var parentOperation = await dbContext.BudgetOperations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == record.OperationId, cancellationToken);
            reservations.Add(new PurchaseOrderReservation(
                target.TargetId,
                target.TargetVersion,
                record.Id,
                record.Amount,
                remainder,
                $"{position.CostCenterId:D}:{position.FiscalYear}:{position.SpendCategoryCode}",
                parentOperation.SourceType,
                parentOperation.SourceId,
                parentOperation.SourceVersion,
                parentOperation.SourceDigest));
        }

        var duplicate = reservations
            .GroupBy(reservation => reservation.MovementId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "Two ordered targets resolve to the same reservation chain.");
        }

        return reservations;
    }

    /// <summary>
    /// Commits the ordered amount of every target and releases the open remainder by <c>REVERSE</c>,
    /// keeping one durable attempt per operation (REQ-04, NFR-05).
    /// </summary>
    public async Task<PurchaseOrderBudgetOutcome> CommitAsync(
        Guid organizationId,
        Guid poId,
        int poVersion,
        string poDigest,
        PurchaseOrderVersion order,
        string issueKey,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        // REQ-04/REQ-12: a confirmed effect is recognized from its durable attempt *before* the
        // ledger is resolved again, so a crash between the COMMIT and its checkpoint recovers by the
        // same keys instead of failing against the already consumed reservation.
        var existingAttempt = await LoadAttemptAsync(
            poId, poVersion, PurchaseOrderBudgetAttemptOperation.Commit, null, cancellationToken);
        if (existingAttempt is not null && existingAttempt.MovementRefsJson is not null)
        {
            return new PurchaseOrderBudgetOutcome(
                Guid.TryParse(existingAttempt.OperationId, out var recorded) ? recorded : Guid.Empty,
                PurchaseOrderSerialization.ReadBudgetOperationRefs(existingAttempt.MovementRefsJson),
                Replayed: true);
        }

        var evidence = await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ContentDigest == order.OrderingEvidenceRef!.ContentDigest,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The ordering evidence of the order is not available.");
        var targets = PurchaseOrderSerialization.ReadTargets(evidence.CoveredTargetsJson);
        var reservations = existingAttempt is not null
            ? PurchaseOrderSerialization.ReadReservations(existingAttempt.ParentsJson)
            : await ResolveReservationsAsync(organizationId, targets, cancellationToken);
        if (existingAttempt is null)
        {
            var ordered = order.Lines.ToDictionary(
                line => $"{line.RequestLineRef.Id:D}:{line.RequestLineRef.Version}", line => line);
            foreach (var reservation in reservations)
            {
                var line = ordered[$"{reservation.TargetId:D}:{reservation.TargetVersion}"];
                if (line.BaseGrossTotal > reservation.Remaining)
                {
                    throw new DomainConflictException(
                        "The reservation of an ordered target does not cover the ordered amount.");
                }
            }
        }

        var attempt = existingAttempt;
        if (attempt is null)
        {
            attempt = new PurchaseOrderBudgetAttemptRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                PoId = poId,
                PoVersion = poVersion,
                Operation = (int)PurchaseOrderBudgetAttemptOperation.Commit,
                State = (int)PurchaseOrderBudgetAttemptState.Pending,
                OperationKey = $"po-commit:{poId:D}:{poVersion}",
                ParentsJson = PurchaseOrderSerialization.Reservations(reservations),
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt
            };
            dbContext.PurchaseOrderBudgetAttempts.Add(attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (attempt.State == (int)PurchaseOrderBudgetAttemptState.Completed &&
            attempt.MovementRefsJson is not null)
        {
            return new PurchaseOrderBudgetOutcome(
                Guid.TryParse(attempt.OperationId, out var completedId) ? completedId : Guid.Empty,
                PurchaseOrderSerialization.ReadBudgetOperationRefs(attempt.MovementRefsJson),
                Replayed: true);
        }

        attempt.State = (int)PurchaseOrderBudgetAttemptState.Committing;
        attempt.Attempts += 1;
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        var source = new BudgetTransitionSource(
            BudgetCodes.PurchaseOrderSourceType, poId, poVersion, poDigest);
        var commitment = await transitions.ApplyAsync(
            organizationId,
            workload,
            "COMMIT",
            attempt.OperationKey,
            "PURCHASE_ORDER_ISSUED",
            source,
            reservations
                .Select(reservation => new BudgetTransitionCommandMovement(
                    order.Lines.Single(line =>
                        line.RequestLineRef.Id == reservation.TargetId &&
                        line.RequestLineRef.Version == reservation.TargetVersion).BaseGrossTotal,
                    reservation.MovementId,
                    BudgetCodes.ParentMovementVersion,
                    new BudgetTarget(
                        reservation.TargetId,
                        reservation.TargetVersion,
                        targets.Single(target => target.TargetId == reservation.TargetId &&
                                                 target.TargetVersion == reservation.TargetVersion)
                            .MaterialSnapshotDigest,
                        PurchaseOrderCodes.ApprovalTargetType)))
                .ToArray(),
            correlationReference,
            cancellationToken);
        attempt.OperationId = commitment.OperationId.ToString("D");
        attempt.State = (int)PurchaseOrderBudgetAttemptState.Releasing;
        attempt.UpdatedAt = occurredAt;
        var refs = new List<PurchaseOrderBudgetOperationRef>
        {
            new(
                "COMMIT",
                commitment.OperationId,
                attempt.OperationKey,
                poId,
                poVersion,
                poDigest)
        };

        var remainders = reservations
            .Select(reservation =>
            {
                var line = order.Lines.Single(candidate =>
                    candidate.RequestLineRef.Id == reservation.TargetId &&
                    candidate.RequestLineRef.Version == reservation.TargetVersion);
                return (reservation, Remainder: reservation.Remaining - line.BaseGrossTotal);
            })
            .Where(entry => entry.Remainder > 0m)
            .ToArray();
        if (remainders.Length != 0)
        {
            // Every released reservation must come from the same parent operation, otherwise the
            // REVERSE would mix sources and the ledger would refuse it.
            var parentSources = remainders
                .Select(entry => entry.reservation.ParentSourceId)
                .Distinct()
                .ToArray();
            if (parentSources.Length != 1)
            {
                throw new PurchaseOrderDependencyUnavailableException(
                    "The released reservations come from different parent operations.");
            }

            var parentSource = remainders[0].reservation;
            var releaseSource = new BudgetTransitionSource(
                parentSource.ParentSourceType,
                parentSource.ParentSourceId,
                parentSource.ParentSourceVersion,
                parentSource.ParentSourceDigest);
            var releaseKey = $"po-release:{poId:D}:{poVersion}";
            attempt.ReleaseKey = releaseKey;
            await dbContext.SaveChangesAsync(cancellationToken);
            var release = await transitions.ApplyAsync(
                organizationId,
                workload,
                "REVERSE",
                releaseKey,
                "PURCHASE_ORDER_REMAINDER_RELEASED",
                releaseSource,
                remainders
                    .Select(entry => new BudgetTransitionCommandMovement(
                        entry.Remainder,
                        entry.reservation.MovementId,
                        BudgetCodes.ParentMovementVersion,
                        new BudgetTarget(
                            entry.reservation.TargetId,
                            entry.reservation.TargetVersion,
                            targets.Single(target => target.TargetId == entry.reservation.TargetId &&
                                                     target.TargetVersion == entry.reservation.TargetVersion)
                                .MaterialSnapshotDigest,
                            PurchaseOrderCodes.ApprovalTargetType)))
                    .ToArray(),
                correlationReference,
                cancellationToken);
            attempt.ReleaseOperationId = release.OperationId.ToString("D");
            refs.Add(new PurchaseOrderBudgetOperationRef(
                "REVERSE",
                release.OperationId,
                releaseKey,
                poId,
                poVersion,
                poDigest));
        }

        attempt.State = (int)PurchaseOrderBudgetAttemptState.Completed;
        attempt.MovementRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(refs);
        attempt.CompletedAt = occurredAt;
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PurchaseOrderBudgetOutcome(commitment.OperationId, refs, commitment.Replayed);
    }

    /// <summary>
    /// Commits only the positive amount delta of one amendment increase (REQ-05): the additional
    /// reservations of the successor award are consumed per target and their open remainder is
    /// released, so a successor award that reserves more than the delta never leaves funds held.
    /// </summary>
    public async Task<PurchaseOrderBudgetOutcome> CommitAmendmentIncreaseAsync(
        Guid organizationId,
        Guid poId,
        int poVersion,
        string poDigest,
        Guid amendmentId,
        IReadOnlyDictionary<string, decimal> baseDeltasByLine,
        PurchaseOrderVersion order,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(baseDeltasByLine);
        var evidence = await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ContentDigest == order.OrderingEvidenceRef!.ContentDigest,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The ordering evidence of the order is not available.");
        var targets = PurchaseOrderSerialization.ReadTargets(evidence.CoveredTargetsJson)
            .Where(target => baseDeltasByLine.ContainsKey($"{target.TargetId:D}:{target.TargetVersion}"))
            .ToArray();
        if (targets.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "An increase amendment carries no ordered target to commit.");
        }

        var reservations = await ResolveReservationsAsync(organizationId, targets, cancellationToken);
        foreach (var reservation in reservations)
        {
            var delta = baseDeltasByLine[$"{reservation.TargetId:D}:{reservation.TargetVersion}"];
            if (delta <= 0m)
            {
                continue;
            }

            if (delta > reservation.Remaining)
            {
                throw new DomainConflictException(
                    "The successor award does not reserve the amount the amendment increases.");
            }
        }

        var attempt = await FindOrCreateAttemptAsync(
            organizationId, poId, poVersion, amendmentId, PurchaseOrderBudgetAttemptOperation.Commit,
            $"po-amendment-commit:{amendmentId:D}:{poVersion}", reservations, occurredAt, cancellationToken);
        if (attempt.State == (int)PurchaseOrderBudgetAttemptState.Completed &&
            attempt.MovementRefsJson is not null)
        {
            return new PurchaseOrderBudgetOutcome(
                Guid.TryParse(attempt.OperationId, out var recorded) ? recorded : Guid.Empty,
                PurchaseOrderSerialization.ReadBudgetOperationRefs(attempt.MovementRefsJson),
                Replayed: true);
        }

        attempt.State = (int)PurchaseOrderBudgetAttemptState.Committing;
        attempt.Attempts += 1;
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        var source = new BudgetTransitionSource(BudgetCodes.PurchaseOrderSourceType, poId, poVersion, poDigest);
        var committed = reservations
            .Where(reservation => baseDeltasByLine[$"{reservation.TargetId:D}:{reservation.TargetVersion}"] > 0m)
            .Select(reservation => new BudgetTransitionCommandMovement(
                baseDeltasByLine[$"{reservation.TargetId:D}:{reservation.TargetVersion}"],
                reservation.MovementId,
                BudgetCodes.ParentMovementVersion,
                TargetOf(targets, reservation)))
            .ToArray();
        var commitment = await transitions.ApplyAsync(
            organizationId,
            workload,
            "COMMIT",
            attempt.OperationKey,
            "PURCHASE_ORDER_AMENDMENT_INCREASED",
            source,
            committed,
            correlationReference,
            cancellationToken);
        attempt.OperationId = commitment.OperationId.ToString("D");
        attempt.State = (int)PurchaseOrderBudgetAttemptState.Releasing;
        attempt.UpdatedAt = occurredAt;
        var refs = new List<PurchaseOrderBudgetOperationRef>
        {
            new("COMMIT", commitment.OperationId, attempt.OperationKey, poId, poVersion, poDigest)
        };
        var remainders = reservations
            .Where(reservation =>
                reservation.Remaining - baseDeltasByLine[$"{reservation.TargetId:D}:{reservation.TargetVersion}"] > 0m)
            .ToArray();
        if (remainders.Length != 0)
        {
            var releaseKey = $"po-amendment-release:{amendmentId:D}:{poVersion}";
            attempt.ReleaseKey = releaseKey;
            await dbContext.SaveChangesAsync(cancellationToken);
            var release = await transitions.ApplyAsync(
                organizationId,
                workload,
                "REVERSE",
                releaseKey,
                "PURCHASE_ORDER_AMENDMENT_REMAINDER_RELEASED",
                new BudgetTransitionSource(
                    remainders[0].ParentSourceType,
                    remainders[0].ParentSourceId,
                    remainders[0].ParentSourceVersion,
                    remainders[0].ParentSourceDigest),
                remainders
                    .Select(reservation => new BudgetTransitionCommandMovement(
                        reservation.Remaining -
                        baseDeltasByLine[$"{reservation.TargetId:D}:{reservation.TargetVersion}"],
                        reservation.MovementId,
                        BudgetCodes.ParentMovementVersion,
                        TargetOf(targets, reservation)))
                    .ToArray(),
                correlationReference,
                cancellationToken);
            attempt.ReleaseOperationId = release.OperationId.ToString("D");
            refs.Add(new PurchaseOrderBudgetOperationRef("REVERSE", release.OperationId, releaseKey, poId, poVersion, poDigest));
        }

        attempt.State = (int)PurchaseOrderBudgetAttemptState.Completed;
        attempt.MovementRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(refs);
        attempt.CompletedAt = occurredAt;
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PurchaseOrderBudgetOutcome(commitment.OperationId, refs, commitment.Replayed);
    }

    /// <summary>
    /// Releases the reduction of one amendment (REQ-05): the still-open committed remainder of the
    /// whole Purchase Order lineage — the issue plus every increase amendment — returns first to
    /// <c>RESERVED</c> and then to <c>AVAILABLE</c>. Allocations are consumed oldest-first, so a
    /// reduction larger than the original commitment is still backed by real open commitments, and a
    /// commitment already advanced by a later transition fails closed.
    /// </summary>
    public async Task<PurchaseOrderBudgetOutcome> ReleaseAmendmentReductionAsync(
        Guid organizationId,
        Guid poId,
        int poVersion,
        string poDigest,
        Guid amendmentId,
        IReadOnlyList<(Guid TargetId, int TargetVersion, decimal Amount)> reductions,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reductions);
        var reductionEntries = reductions.Where(entry => entry.Amount > 0m).ToArray();
        if (reductionEntries.Length == 0)
        {
            throw new DomainConflictException("The amendment does not reduce a committed amount.");
        }

        var attempt = await FindOrCreateAttemptAsync(
            organizationId,
            poId,
            poVersion,
            amendmentId,
            PurchaseOrderBudgetAttemptOperation.Reverse,
            $"po-amendment-reverse:{amendmentId:D}:{poVersion}",
            [],
            occurredAt,
            cancellationToken);
        if (attempt.State == (int)PurchaseOrderBudgetAttemptState.Completed &&
            attempt.MovementRefsJson is not null)
        {
            return new PurchaseOrderBudgetOutcome(
                Guid.TryParse(attempt.OperationId, out var recorded) ? recorded : Guid.Empty,
                PurchaseOrderSerialization.ReadBudgetOperationRefs(attempt.MovementRefsJson),
                Replayed: true);
        }

        // REQ-05/REQ-12: the plan is durable and immutable. A retry re-executes exactly the recorded
        // reversals instead of re-allocating remainders that a crashed run already reduced.
        var recordedPlan = PurchaseOrderSerialization.ReadReductionPlan(attempt.ParentsJson);
        if (recordedPlan.Count > 0)
        {
            return await ExecuteReductionPlanAsync(
                organizationId, poId, poVersion, poDigest, amendmentId, attempt, recordedPlan,
                correlationReference, occurredAt, cancellationToken);
        }

        // Every COMMIT of the lineage, oldest first, with the open remainder of each commitment and
        // the exact source of its operation and of the reservation that backed it.
        var commits = await dbContext.PurchaseOrderBudgetAttempts
            .AsNoTracking()
            .Where(record => record.PoId == poId &&
                             record.OrganizationId == organizationId &&
                             record.Operation == (int)PurchaseOrderBudgetAttemptOperation.Commit &&
                             record.State == (int)PurchaseOrderBudgetAttemptState.Completed &&
                             record.OperationId != null)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        if (commits.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The order has no confirmed commitment to reduce.");
        }

        var reverses = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.Type == (int)BudgetMovementType.Reverse &&
                               movement.ParentMovementId != null)
            .Select(movement => new { Parent = movement.ParentMovementId!.Value, movement.Amount })
            .ToArrayAsync(cancellationToken);
        var commitments = new List<OpenCommitment>();
        foreach (var commit in commits)
        {
            var operationId = Guid.Parse(commit.OperationId!);
            var operation = await dbContext.BudgetOperations
                .AsNoTracking()
                .SingleAsync(record => record.Id == operationId, cancellationToken);
            var source = new BudgetTransitionSource(
                operation.SourceType, operation.SourceId, operation.SourceVersion, operation.SourceDigest);
            var movements = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.OperationId == operationId &&
                                   movement.Type == (int)BudgetMovementType.Committed)
                .OrderBy(movement => movement.OccurredAt)
                .ThenBy(movement => movement.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var movement in movements)
            {
                var remaining = movement.Amount - reverses
                    .Where(reverse => reverse.Parent == movement.Id)
                    .Sum(reverse => reverse.Amount);
                if (remaining <= 0m)
                {
                    continue;
                }

                if (movement.ParentMovementId is not Guid reservedId)
                {
                    throw new PurchaseOrderDependencyUnavailableException(
                        "A commitment of the order has no reservation to release.");
                }

                var reservedOperationId = await dbContext.BudgetMovements
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == reservedId)
                    .Select(candidate => candidate.OperationId)
                    .SingleAsync(cancellationToken);
                var reservedOperation = await dbContext.BudgetOperations
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == reservedOperationId, cancellationToken);
                commitments.Add(new OpenCommitment(
                    movement.TargetId ?? Guid.Empty,
                    movement.TargetVersion ?? 0,
                    remaining,
                    movement.Id,
                    reservedId,
                    movement.TargetMaterialSnapshotDigest ?? string.Empty,
                    source,
                    new BudgetTransitionSource(
                        reservedOperation.SourceType,
                        reservedOperation.SourceId,
                        reservedOperation.SourceVersion,
                        reservedOperation.SourceDigest)));
            }
        }

        if (commitments.Count == 0)
        {
            throw new DomainConflictException(
                "The committed amount of the order was already advanced by a later transition.");
        }

        // Allocate every reduction across the open commitments, oldest-first and deterministically.
        var allocations = new List<(OpenCommitment Commitment, decimal Amount)>();
        foreach (var entry in reductionEntries)
        {
            var remaining = entry.Amount;
            foreach (var commitment in commitments.Where(candidate =>
                         candidate.TargetId == entry.TargetId &&
                         candidate.TargetVersion == entry.TargetVersion))
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var taken = Math.Min(remaining, commitment.Remaining);
                if (taken <= 0m)
                {
                    continue;
                }

                allocations.Add((commitment, taken));
                remaining -= taken;
            }

            if (remaining > 0m)
            {
                throw new DomainConflictException(
                    "The committed amount of the order was already advanced by a later transition.");
            }
        }

        if (allocations.Count == 0)
        {
            throw new DomainConflictException(
                "The committed amount of the order was already advanced by a later transition.");
        }

        var plan = allocations
            .Select(allocation => new PurchaseOrderSerialization.ReductionAllocation(
                allocation.Amount,
                allocation.Commitment.CommittedId,
                allocation.Commitment.ReservedId,
                allocation.Commitment.TargetId,
                allocation.Commitment.TargetVersion,
                allocation.Commitment.MaterialSnapshotDigest,
                allocation.Commitment.CommittedSource.Type,
                allocation.Commitment.CommittedSource.Id,
                allocation.Commitment.CommittedSource.Version,
                allocation.Commitment.CommittedSource.Digest,
                allocation.Commitment.ReservedSource.Type,
                allocation.Commitment.ReservedSource.Id,
                allocation.Commitment.ReservedSource.Version,
                allocation.Commitment.ReservedSource.Digest))
            .ToArray();
        attempt.State = (int)PurchaseOrderBudgetAttemptState.Releasing;
        attempt.Attempts += 1;
        attempt.ParentsJson = PurchaseOrderSerialization.ReductionPlan(plan);
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await ExecuteReductionPlanAsync(
            organizationId, poId, poVersion, poDigest, amendmentId, attempt, plan,
            correlationReference, occurredAt, cancellationToken);
    }

    /// <summary>
    /// Executes the recorded reduction plan exactly once per group (REQ-05, REQ-12). Every group is a
    /// deterministic operation key, and the executed references are checkpointed after each group, so
    /// a crash between groups replays the same operations instead of posting a second reversal.
    /// </summary>
    private async Task<PurchaseOrderBudgetOutcome> ExecuteReductionPlanAsync(
        Guid organizationId,
        Guid poId,
        int poVersion,
        string poDigest,
        Guid amendmentId,
        PurchaseOrderBudgetAttemptRecord attempt,
        IReadOnlyList<PurchaseOrderSerialization.ReductionAllocation> plan,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var refs = attempt.MovementRefsJson is null
            ? new List<PurchaseOrderBudgetOperationRef>()
            : PurchaseOrderSerialization.ReadBudgetOperationRefs(attempt.MovementRefsJson).ToList();
        var committedGroups = plan
            .GroupBy(allocation => (
                allocation.CommittedSourceType,
                allocation.CommittedSourceId,
                allocation.CommittedSourceVersion,
                allocation.CommittedSourceDigest))
            .Select(group => new
            {
                Source = new BudgetTransitionSource(
                    group.Key.CommittedSourceType,
                    group.Key.CommittedSourceId,
                    group.Key.CommittedSourceVersion,
                    group.Key.CommittedSourceDigest),
                Allocations = group.ToArray()
            })
            .OrderBy(group => group.Source.Type, StringComparer.Ordinal)
            .ThenBy(group => group.Source.Id)
            .ThenBy(group => group.Source.Version)
            .ToArray();
        for (var index = 0; index < committedGroups.Length; index++)
        {
            var group = committedGroups[index];
            var key = committedGroups.Length == 1
                ? attempt.OperationKey
                : $"{attempt.OperationKey}:{index}";
            var release = await transitions.ApplyAsync(
                organizationId,
                workload,
                "REVERSE",
                key,
                "PURCHASE_ORDER_AMENDMENT_REDUCED",
                group.Source,
                group.Allocations
                    .Select(allocation => new BudgetTransitionCommandMovement(
                        allocation.Amount,
                        allocation.CommittedId,
                        BudgetCodes.ParentMovementVersion,
                        new BudgetTarget(
                            allocation.TargetId,
                            allocation.TargetVersion,
                            allocation.MaterialDigest,
                            PurchaseOrderCodes.ApprovalTargetType)))
                    .ToArray(),
                correlationReference,
                cancellationToken);
            attempt.OperationId ??= release.OperationId.ToString("D");
            if (refs.All(reference => !string.Equals(reference.OperationKey, key, StringComparison.Ordinal)))
            {
                refs.Add(new PurchaseOrderBudgetOperationRef(
                    "REVERSE", release.OperationId, key, poId, poVersion, poDigest));
            }

            attempt.MovementRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(refs);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var reservedGroups = plan
            .GroupBy(allocation => (
                allocation.ReservedSourceType,
                allocation.ReservedSourceId,
                allocation.ReservedSourceVersion,
                allocation.ReservedSourceDigest))
            .Select(group => new
            {
                Source = new BudgetTransitionSource(
                    group.Key.ReservedSourceType,
                    group.Key.ReservedSourceId,
                    group.Key.ReservedSourceVersion,
                    group.Key.ReservedSourceDigest),
                Allocations = group.ToArray()
            })
            .OrderBy(group => group.Source.Type, StringComparer.Ordinal)
            .ThenBy(group => group.Source.Id)
            .ThenBy(group => group.Source.Version)
            .ToArray();
        for (var index = 0; index < reservedGroups.Length; index++)
        {
            var group = reservedGroups[index];
            var key = reservedGroups.Length == 1
                ? $"po-amendment-reserved-release:{amendmentId:D}:{poVersion}"
                : $"po-amendment-reserved-release:{amendmentId:D}:{poVersion}:{index}";
            var release = await transitions.ApplyAsync(
                organizationId,
                workload,
                "REVERSE",
                key,
                "PURCHASE_ORDER_AMENDMENT_RESERVATION_RELEASED",
                group.Source,
                group.Allocations
                    .Select(allocation => new BudgetTransitionCommandMovement(
                        allocation.Amount,
                        allocation.ReservedId,
                        BudgetCodes.ParentMovementVersion,
                        new BudgetTarget(
                            allocation.TargetId,
                            allocation.TargetVersion,
                            allocation.MaterialDigest,
                            PurchaseOrderCodes.ApprovalTargetType)))
                    .ToArray(),
                correlationReference,
                cancellationToken);
            attempt.ReleaseKey ??= key;
            attempt.ReleaseOperationId ??= release.OperationId.ToString("D");
            if (refs.All(reference => !string.Equals(reference.OperationKey, key, StringComparison.Ordinal)))
            {
                refs.Add(new PurchaseOrderBudgetOperationRef(
                    "REVERSE", release.OperationId, key, poId, poVersion, poDigest));
            }

            attempt.MovementRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(refs);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        attempt.State = (int)PurchaseOrderBudgetAttemptState.Completed;
        attempt.MovementRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(refs);
        attempt.CompletedAt = occurredAt;
        attempt.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PurchaseOrderBudgetOutcome(
            Guid.TryParse(attempt.OperationId, out var reducedOperation) ? reducedOperation : Guid.Empty,
            refs,
            Replayed: false);
    }

    /// <summary>One open commitment of the lineage with the reservation that backed it (REQ-05).</summary>
    private sealed record OpenCommitment(
        Guid TargetId,
        int TargetVersion,
        decimal Remaining,
        Guid CommittedId,
        Guid ReservedId,
        string MaterialSnapshotDigest,
        BudgetTransitionSource CommittedSource,
        BudgetTransitionSource ReservedSource);

    private static BudgetTarget TargetOf(
        IReadOnlyList<OrderingEvidenceTarget> targets,
        PurchaseOrderReservation reservation) =>
        new(
            reservation.TargetId,
            reservation.TargetVersion,
            targets.Single(target => target.TargetId == reservation.TargetId &&
                                     target.TargetVersion == reservation.TargetVersion).MaterialSnapshotDigest,
            PurchaseOrderCodes.ApprovalTargetType);

    private static BudgetTarget TargetOf(OpenCommitment commitment) =>
        new(
            commitment.TargetId,
            commitment.TargetVersion,
            commitment.MaterialSnapshotDigest,
            PurchaseOrderCodes.ApprovalTargetType);

    private static string MaterialDigestOf(
        IReadOnlyList<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetMovementRecord> movements,
        Guid movementId) =>
        movements.Single(movement => movement.Id == movementId).TargetMaterialSnapshotDigest ??
        throw new PurchaseOrderDependencyUnavailableException(
            "The commitment of a reduced target carries no material snapshot.");

    /// <summary>
    /// Loads the durable attempt of one operation with the <em>database</em> state, detaching any
    /// stale tracked copy of the same row. A retry in the same context must never decide on a cached
    /// attempt: the confirmed keys and refs live in the table, not in the change tracker (REQ-04).
    /// </summary>
    private async Task<PurchaseOrderBudgetAttemptRecord?> LoadAttemptAsync(
        Guid poId,
        int poVersion,
        PurchaseOrderBudgetAttemptOperation operation,
        Guid? amendmentId,
        CancellationToken cancellationToken)
    {
        var fresh = await dbContext.PurchaseOrderBudgetAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.PoId == poId &&
                          record.PoVersion == poVersion &&
                          record.Operation == (int)operation &&
                          record.AmendmentId == amendmentId,
                cancellationToken);
        if (fresh is null)
        {
            return null;
        }

        var tracked = dbContext.ChangeTracker
            .Entries<PurchaseOrderBudgetAttemptRecord>()
            .FirstOrDefault(entry => entry.Entity.Id == fresh.Id);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        dbContext.Attach(fresh);
        return fresh;
    }

    private async Task<PurchaseOrderBudgetAttemptRecord> FindOrCreateAttemptAsync(
        Guid organizationId,
        Guid poId,
        int poVersion,
        Guid amendmentId,
        PurchaseOrderBudgetAttemptOperation operation,
        string operationKey,
        IReadOnlyList<PurchaseOrderReservation> reservations,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var existing = await LoadAttemptAsync(poId, poVersion, operation, amendmentId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var attempt = new PurchaseOrderBudgetAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PoId = poId,
            PoVersion = poVersion,
            AmendmentId = amendmentId,
            Operation = (int)operation,
            State = (int)PurchaseOrderBudgetAttemptState.Pending,
            OperationKey = PurchaseOrderCodes.Key(operationKey, "operation_key"),
            ParentsJson = reservations.Count == 0
                ? "[]"
                : PurchaseOrderSerialization.Reservations(reservations),
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt
        };
        dbContext.PurchaseOrderBudgetAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return attempt;
    }
}
