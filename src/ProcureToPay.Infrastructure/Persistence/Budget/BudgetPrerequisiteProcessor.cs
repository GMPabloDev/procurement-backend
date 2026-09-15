using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>One predecessor whose open reservations must count as available for its replacement (REQ-08).</summary>
public sealed record BudgetPredecessorHold(
    Guid AttemptId,
    Guid CaseId,
    IReadOnlyList<Guid> ReservedMovementIds);

/// <summary>Outcome of one processed budget prerequisite.</summary>
public sealed record BudgetProcessorOutcome(
    Guid AttemptId,
    string State,
    string? SignalResult,
    BudgetCheckResult CheckResult);

/// <summary>
/// Real processor of the <c>budget-check-owner/v1</c> prerequisite (SPEC 08 REQ-06, REQ-07, DEC-03).
/// It reserves every demand of the prerequisite all-or-nothing, then signals Approval with the
/// reproducible evidence; a technical failure leaves the prerequisite <c>WAITING</c> for a retry and
/// a terminal case is compensated instead of signalled. The attempt is durable, leased and fenced,
/// so a restart never posts a second reservation.
/// </summary>
public sealed class BudgetPrerequisiteProcessor(
    ProcureToPayDbContext dbContext,
    BudgetPersistenceService positions,
    BudgetLedgerService ledger,
    PurchaseRequests.PurchaseRequestBudgetDemandBuilder builder,
    ApprovalWorkflowService workflow,
    ApprovalInstanceIdentity instanceIdentity)
{
    /// <summary>Lease of one attempt before another instance may reclaim it (REQ-07).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>Readiness budget of one due attempt (REQ-07, NFR-05).</summary>
    public static readonly TimeSpan DueBudget = TimeSpan.FromSeconds(60);

    /// <summary>Claims every processable budget attempt and advances it once (REQ-07).</summary>
    public async Task<IReadOnlyList<BudgetProcessorOutcome>> ProcessDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Status == (int)PrerequisiteStatus.Waiting &&
                             record.OwnerAdapterId == BudgetCodes.BudgetOwnerAdapterId &&
                             record.OwnerAdapterVersion == BudgetCodes.BudgetOwnerAdapterVersion)
            .OrderBy(record => record.Id)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var outcomes = new List<BudgetProcessorOutcome>();
        foreach (var prerequisiteId in candidates)
        {
            var outcome = await ProcessOneAsync(prerequisiteId, now, cancellationToken);
            if (outcome is not null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Processes one prerequisite end to end. Returns null when the attempt is owned by another
    /// live lease, so two workers never post the same reservation twice (REQ-07).
    /// </summary>
    public async Task<BudgetProcessorOutcome?> ProcessOneAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var attempt = await FindOrCreateAttemptAsync(prerequisiteId, now, cancellationToken);
        if (!await ClaimAsync(attempt, now, cancellationToken))
        {
            return null;
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == attempt.CaseId, cancellationToken)
            ?? throw new BudgetDependencyUnavailableException("The approval case is not visible.");
        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == attempt.PrerequisiteId, cancellationToken);
        var ownerWorkload = new ApprovalWorkloadIdentity(
            prerequisite.OwnerWorkloadIssuer, prerequisite.OwnerWorkloadClientId);
        var demands = await BuildDemandsAsync(attempt, prerequisite.TargetsJson, cancellationToken);
        var parametersDigest = Sha256(prerequisite.ParametersJson);
        var source = new BudgetSource(
            BudgetCodes.PurchaseRequestSourceType,
            attempt.RequestId,
            attempt.RequestVersion,
            attempt.ParametersDigest);
        var actor = BudgetActor.ForWorkload(ownerWorkload.Issuer, ownerWorkload.ClientId);

        // A terminal case cannot be satisfied; any reservation already posted is compensated and the
        // prerequisite is never signalled as available (REQ-07, REQ-08).
        var caseStatus = (ApprovalCaseStatus)caseRecord.Status;
        if (caseStatus is ApprovalCaseStatus.Cancelled or ApprovalCaseStatus.Superseded)
        {
            if (attempt.ReserveOperationId is not null)
            {
                var reversed = await ReleaseReservationsAsync(
                    attempt, source, actor, correlation: attempt.SignalKey, now, cancellationToken);
                if (reversed)
                {
                    await CompleteCompensatedAsync(attempt, now, cancellationToken);
                    return new BudgetProcessorOutcome(
                        attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated), null, BudgetCheckResult.Available);
                }
            }

            await MarkPendingAsync(attempt, "CASE_TERMINAL", now, cancellationToken);
            return new BudgetProcessorOutcome(
                attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending), null, BudgetCheckResult.Available);
        }

        var predecessors = await FindPredecessorHoldsAsync(attempt, cancellationToken);
        BudgetReserveOutcome reserve;
        try
        {
            if (predecessors.Count > 0)
            {
                // REQ-08: with a superseded predecessor still holding funds, the replacement is
                // reserved through the serialized transfer, which counts that hold as available and
                // reverses it in the same transaction only when the whole new set fits.
                var transfer = await ledger.TransferReserveAsync(
                    attempt.OrganizationId,
                    attempt.RequestKey,
                    attempt.ReserveKey,
                    source,
                    actor,
                    "BUDGET_CHECK",
                    attempt.CaseId,
                    demands,
                    predecessors
                        .Select(hold => new BudgetPredecessorReservation(hold.CaseId, hold.ReservedMovementIds))
                        .ToArray(),
                    attempt.SignalKey,
                    cancellationToken);
                reserve = new BudgetReserveOutcome(
                    transfer.Result,
                    transfer.RequestOperationId,
                    transfer.ReserveOperationId,
                    transfer.RequestedMovements,
                    transfer.ReservedMovements);
            }
            else
            {
                reserve = await ledger.ReserveAsync(
                    attempt.OrganizationId,
                    attempt.RequestKey,
                    attempt.ReserveKey,
                    source,
                    actor,
                    "BUDGET_CHECK",
                    attempt.CaseId,
                    demands,
                    attempt.SignalKey,
                    causeAuditId: null,
                    causeStream: null,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is BudgetDependencyUnavailableException or
                                              BudgetReferenceInvalidException or DomainException)
        {
            await MarkPendingAsync(attempt, ErrorCode(exception), now, cancellationToken);
            throw new BudgetDependencyUnavailableException(
                $"{ErrorCode(exception)}: {exception.Message}", exception);
        }

        await RecordReserveAsync(attempt, reserve, now, cancellationToken);
        if (reserve.Result != BudgetCheckResult.Available)
        {
            await SignalAsync(
                attempt,
                prerequisite,
                ownerWorkload,
                satisfied: false,
                evidenceDigest: null,
                evidenceReference: null,
                now,
                cancellationToken);
            await CompleteAsync(attempt, "FAILED", null, now, cancellationToken);
            return new BudgetProcessorOutcome(
                attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed), "FAILED", reserve.Result);
        }

        var evidenceMovements = reserve.RequestedMovements.Concat(reserve.ReservedMovements).ToArray();
        var evidence = BudgetCanonicalJson.Digest(BudgetFingerprints.EvidencePreimage(
            attempt.OrganizationId,
            attempt.Id,
            attempt.CaseId,
            attempt.PrerequisiteId,
            now,
            BudgetCheckResult.Available,
            attempt.SignalKey,
            parametersDigest,
            attempt.SourceControlDigest,
            evidenceMovements));
        _ = predecessors;
        await SignalAsync(
            attempt,
            prerequisite,
            ownerWorkload,
            satisfied: true,
            evidenceDigest: evidence,
            evidenceReference: $"budget://{attempt.Id:D}",
            now,
            cancellationToken);
        await CompleteAsync(attempt, "SATISFIED", evidence, now, cancellationToken);
        return new BudgetProcessorOutcome(
            attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed), "SATISFIED", BudgetCheckResult.Available);
    }

    /// <summary>
    /// Builds the demands from the persisted Purchase Request version and the material projection of
    /// the evaluation that created the case (REQ-05). The caller never supplies amounts or positions.
    /// </summary>
    private async Task<IReadOnlyList<BudgetDemand>> BuildDemandsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string targetsJson,
        CancellationToken cancellationToken)
    {
        var targets = ApprovalJsonPersistence.DeserializeTargets(targetsJson);
        var parameters = BudgetPrerequisiteParameters.Parse(attempt.ParametersJson);
        var targetSet = parameters.Demands
            .Select(demand => demand.Target)
            .Where(target => target is not null)
            .Select(target => target!)
            .ToArray();
        if (targetSet.Length == 0)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget prerequisite has no confirmed target to check.");
        }

        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == attempt.OrganizationId &&
                          record.RequestId == attempt.RequestId &&
                          record.RequestVersion == attempt.RequestVersion,
                cancellationToken)
            ?? throw new BudgetDependencyUnavailableException(
                "The budget prerequisite has no confirmed completeness manifest.");
        var build = await builder.BuildAsync(
            new BudgetDemandBuildRequest(
                BudgetCodes.DemandBuildVersion,
                attempt.OrganizationId,
                attempt.RequestId,
                attempt.RequestVersion,
                manifest.RequestContentDigest,
                manifest.PolicyManifestDigest,
                targetSet),
            cancellationToken);
        if (build.Demands.Count != targetSet.Length)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget prerequisite targets do not belong to the confirmed request version.");
        }

        _ = targets;
        return build.Demands;
    }

    private async Task<BudgetPrerequisiteAttemptRecord> FindOrCreateAttemptAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.BudgetPrerequisiteAttempts
            .SingleOrDefaultAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken)
            ?? throw new DomainNotFoundException("The budget prerequisite is not visible.");
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisite.CaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var attempt = new BudgetPrerequisiteAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = caseRecord.OrganizationId,
            CaseId = prerequisite.CaseId,
            PrerequisiteId = prerequisiteId,
            PrerequisiteKey = prerequisite.Key,
            RequestId = caseRecord.SubjectId,
            RequestVersion = caseRecord.SubjectVersion,
            ParametersJson = prerequisite.ParametersJson,
            ParametersDigest = Sha256(prerequisite.ParametersJson),
            SourceControlDigest = prerequisite.SourceControlDigest,
            RequestKey = $"budget:{prerequisiteId:D}:request",
            ReserveKey = $"budget:{prerequisiteId:D}:reserve",
            SignalKey = $"budget:{prerequisiteId:D}:signal",
            CompensateKey = $"budget:{prerequisiteId:D}:compensate",
            State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending),
            DueAt = caseRecord.CreatedAt,
            NextAttemptAt = caseRecord.CreatedAt,
            Attempts = 0
        };
        dbContext.BudgetPrerequisiteAttempts.Add(attempt);
        try
        {
            await positions.SaveAsync(cancellationToken);
        }
        catch (DomainConflictException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.BudgetPrerequisiteAttempts
                .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        }

        return attempt;
    }

    /// <summary>
    /// Takes the lease of one attempt. A live lease held by another instance returns false, so the
    /// caller skips it; an expired lease is reclaimed by incrementing the fencing token (REQ-07).
    /// </summary>
    private async Task<bool> ClaimAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.State is "COMPLETED" or "COMPENSATED")
        {
            return false;
        }

        if (attempt.NextAttemptAt is DateTimeOffset due && due > now)
        {
            return false;
        }

        if (attempt.LeaseUntil is DateTimeOffset until && until > now)
        {
            return false;
        }

        attempt.LeaseOwner = instanceIdentity.Owner;
        attempt.LeaseUntil = now + LeaseDuration;
        attempt.FencingToken += 1;
        attempt.Attempts += 1;
        try
        {
            await positions.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DomainConflictException or
                                              DbUpdateConcurrencyException)
        {
            // Another instance claimed the same attempt first: this one skips it instead of
            // posting its own reservation (REQ-07).
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Marks the attempt as reserved, or keeps the post-decision state of an insufficiency. The
    /// lease stays held until the attempt reaches a terminal state, so no other instance reclaims
    /// an in-flight attempt; only an expired lease (a dead worker) is reclaimable (REQ-07).
    /// </summary>
    private async Task RecordReserveAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        BudgetReserveOutcome reserve,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        attempt.RequestOperationId ??= reserve.RequestOperationId;
        attempt.ReserveOperationId ??= reserve.ReserveOperationId;
        attempt.State = reserve.Result == BudgetCheckResult.Available
            ? BudgetAttemptStateCodes.Of(BudgetAttemptState.Reserved)
            : BudgetAttemptStateCodes.Of(BudgetAttemptState.Insufficient);
        attempt.NextAttemptAt = now;
        await positions.SaveAsync(cancellationToken);
    }

    private async Task SignalAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        ApprovalWorkloadIdentity ownerWorkload,
        bool satisfied,
        string? evidenceDigest,
        string? evidenceReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        attempt.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Signalling);
        await positions.SaveAsync(cancellationToken);
        // SPEC 03 owns the signal contract; Budget only supplies its owner identity, key and proof.
        _ = await workflow.SignalAsync(
            attempt.PrerequisiteId,
            new ApprovalSignalCommand(
                ownerWorkload,
                satisfied,
                attempt.SignalKey,
                prerequisite.Version,
                evidenceReference,
                evidenceDigest,
                attempt.SignalKey),
            now,
            cancellationToken);
    }

    private async Task CompleteAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string signalResult,
        string? evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        attempt.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed);
        attempt.SignalResult = signalResult;
        attempt.EvidenceDigest ??= evidenceDigest;
        attempt.LastErrorCode = null;
        attempt.CompletedAt = now;
        attempt.LeaseOwner = null;
        attempt.LeaseUntil = null;
        await positions.SaveAsync(cancellationToken);
    }

    private async Task CompleteCompensatedAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        attempt.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated);
        attempt.CompletedAt = now;
        attempt.LeaseOwner = null;
        attempt.LeaseUntil = null;
        await positions.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Reverses every open reservation of the attempt or none. Returns false when nothing was left
    /// to reverse, so a retry can distinguish "already compensated" from "compensation needed".
    /// </summary>
    private async Task<bool> ReleaseReservationsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        BudgetSource source,
        BudgetActor actor,
        string correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.ReserveOperationId is not Guid reserveOperationId)
        {
            return false;
        }

        var reserved = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.OperationId == reserveOperationId &&
                               movement.Type == (int)BudgetMovementType.Reserved)
            .ToArrayAsync(cancellationToken);
        if (reserved.Length == 0)
        {
            return false;
        }

        var reversedChildren = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.ParentMovementId != null &&
                               movement.Type == (int)BudgetMovementType.Reverse)
            .Select(movement => new { Parent = movement.ParentMovementId!.Value, movement.Amount })
            .ToArrayAsync(cancellationToken);
        var allocations = await dbContext.BudgetAllocationVersions
            .AsNoTracking()
            .Where(version => dbContext.BudgetPositions
                .Where(position => reserved.Select(movement => movement.PositionId).Contains(position.Id))
                .Select(position => position.Id)
                .Contains(version.PositionId) &&
                dbContext.BudgetPositions
                    .Where(position => position.CurrentAllocationVersion == version.Version)
                    .Select(position => position.Id)
                    .Contains(version.PositionId))
            .ToDictionaryAsync(version => version.PositionId, version => version.Currency, cancellationToken);
        var payloads = new Dictionary<BudgetPositionKey, BudgetPositionPayload>();
        var movements = new List<BudgetOperationMovement>();
        foreach (var movement in reserved.OrderBy(movement => movement.Id))
        {
            var reversed = reversedChildren
                .Where(child => child.Parent == movement.Id)
                .Sum(child => child.Amount);
            var remaining = movement.Amount - reversed;
            if (remaining <= 0m)
            {
                continue;
            }

            var position = await dbContext.BudgetPositions
                .AsNoTracking()
                .SingleAsync(record => record.Id == movement.PositionId, cancellationToken);
            var currency = allocations[movement.PositionId];
            _ = currency;
            var payload = await positions.ResolvePositionAsync(
                attempt.OrganizationId,
                position.CostCenterId,
                position.FiscalYear,
                position.SpendCategoryCode,
                await BaseCurrencyAsync(attempt.OrganizationId, cancellationToken),
                cancellationToken);
            payloads[payload.Position] = payload;
            movements.Add(new BudgetOperationMovement(
                payload.Position,
                new BudgetMovementRequest(
                    BudgetMovementType.Reverse,
                    remaining,
                    movement.Id,
                    movement.TargetId is Guid targetId && movement.TargetVersion is int targetVersion
                        ? new BudgetTarget(targetId, targetVersion, movement.TargetMaterialSnapshotDigest!, movement.TargetType!)
                        : null)));
        }

        if (movements.Count == 0)
        {
            return false;
        }

        var fingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            attempt.OrganizationId,
            actor,
            BudgetOperationKind.Release,
            attempt.CompensateKey,
            "BUDGET_RELEASE",
            source,
            movements.Select(entry => new BudgetMovementCommandItem(
                entry.Movement.Amount, entry.Movement.ParentMovementId, entry.Movement.Target))));
        var outcome = await ledger.ApplyOperationAsync(
            new BudgetOperationSpec(
                BudgetOperationKind.Release,
                attempt.OrganizationId,
                attempt.CompensateKey,
                fingerprint,
                source,
                actor,
                "BUDGET_RELEASE",
                "RELEASED",
                movements),
            payloads,
            correlation,
            cancellationToken);
        attempt.ReverseOperationId ??= outcome.OperationId;
        _ = now;
        return true;
    }

    /// <summary>
    /// Open reservations of the case that this one replaced, so the replacement can reuse the held
    /// funds instead of failing against its own predecessor (REQ-08, DEC-07).
    /// </summary>
    private async Task<IReadOnlyList<BudgetPredecessorHold>> FindPredecessorHoldsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        var supersession = await dbContext.CaseSupersessions
            .AsNoTracking()
            .Where(record => record.NewCaseId == attempt.CaseId)
            .Select(record => record.PreviousCaseId)
            .ToArrayAsync(cancellationToken);
        if (supersession.Length == 0)
        {
            return [];
        }

        var holds = await dbContext.BudgetPrerequisiteAttempts
            .AsNoTracking()
            .Where(record => supersession.Contains(record.CaseId) &&
                             record.ReserveOperationId != null &&
                             record.State != BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated) &&
                             record.State != BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed))
            .Select(record => new { record.Id, record.CaseId, ReserveOperationId = record.ReserveOperationId!.Value })
            .ToArrayAsync(cancellationToken);
        var result = new List<BudgetPredecessorHold>();
        foreach (var hold in holds)
        {
            // Only the RESERVED movements of the predecessor that no later transition advanced are
            // transferable; a hold already committed or reversed is not counted as available.
            var reserved = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.OperationId == hold.ReserveOperationId &&
                                   movement.Type == (int)BudgetMovementType.Reserved)
                .Select(movement => movement.Id)
                .ToArrayAsync(cancellationToken);
            if (reserved.Length == 0)
            {
                continue;
            }

            var advanced = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.ParentMovementId != null &&
                                   reserved.Contains(movement.ParentMovementId!.Value))
                .Select(movement => movement.ParentMovementId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var open = reserved.Except(advanced).ToArray();
            if (open.Length > 0)
            {
                result.Add(new BudgetPredecessorHold(hold.Id, hold.CaseId, open));
            }
        }

        return result;
    }

    private async Task MarkPendingAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        attempt.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending);
        attempt.LastErrorCode = errorCode;
        attempt.NextAttemptAt = now.AddSeconds(1);
        attempt.LeaseOwner = null;
        attempt.LeaseUntil = null;
        await positions.SaveAsync(cancellationToken);
    }

    private Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);

    private static string ErrorCode(Exception exception) => exception switch
    {
        BudgetReferenceInvalidException => "BUDGET_REFERENCE_INVALID",
        BudgetDependencyUnavailableException => "BUDGET_DEPENDENCY_UNAVAILABLE",
        DomainNotFoundException => "BUDGET_POSITION_MISSING",
        DomainConflictException => "BUDGET_CONFLICT",
        _ => "BUDGET_TECHNICAL_FAILURE"
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
