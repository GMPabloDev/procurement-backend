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
        var evidence = await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ContentDigest == order.OrderingEvidenceRef!.ContentDigest,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The ordering evidence of the order is not available.");
        var targets = PurchaseOrderSerialization.ReadTargets(evidence.CoveredTargetsJson);
        var reservations = await ResolveReservationsAsync(organizationId, targets, cancellationToken);
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

        var attempt = await dbContext.PurchaseOrderBudgetAttempts
            .SingleOrDefaultAsync(
                record => record.PoId == poId &&
                          record.PoVersion == poVersion &&
                          record.Operation == (int)PurchaseOrderBudgetAttemptOperation.Commit,
                cancellationToken);
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
                Guid.TryParse(attempt.OperationId, out var recorded) ? recorded : Guid.Empty,
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
}
