using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>Result of one precheck as the requester receives it, already minimized (REQ-04).</summary>
public sealed record BudgetPrecheckTargetView(Guid Id, int Version, string Result);

/// <summary>Outcome of the precheck: one state per covered line plus the aggregate (REQ-04).</summary>
public sealed record BudgetPrecheckOutcome(
    Guid OperationId,
    string Result,
    IReadOnlyList<BudgetPrecheckTargetView> Targets,
    bool Replayed);

/// <summary>Outcome of one applied batch with the evidence rows needed by its proof (REQ-10).</summary>
public sealed record BudgetBatchOutcome(
    Guid OperationId,
    bool Replayed,
    IReadOnlyList<Guid> MovementIds,
    IReadOnlyList<BudgetEvidenceMovement> EvidenceRows,
    IReadOnlyDictionary<BudgetPositionKey, string> PositionKeys,
    IReadOnlyList<BudgetLedgerPostedMovement> Posted);

/// <summary>
/// One posted movement with the position it touched, as the caller's response sees it. It is named
/// away from the domain's <c>BudgetPostedMovement</c> (the calculator's view) because the two live
/// in scopes that the ledger resolves together.
/// </summary>
public sealed record BudgetLedgerPostedMovement(BudgetPositionKey Position, BudgetMovementRecord Movement);

/// <summary>
/// One open reservation of a superseded case that the replacement may count as available (REQ-08).
/// </summary>
public sealed record BudgetPredecessorReservation(
    Guid CaseId,
    IReadOnlyList<Guid> ReservedMovementIds);

/// <summary>One resolved predecessor reservation ready to be reversed by a transfer.</summary>
public sealed record BudgetPredecessorParent(
    Guid Id,
    Guid PositionId,
    Guid CaseId,
    decimal Amount,
    BudgetPositionPayload Payload,
    BudgetPostedMovement Posted);

/// <summary>
/// Outcome of the serialized transfer of REQ-08. <see cref="ReverseOperationId"/> always exists;
/// <see cref="ReserveOperationId"/> exists only when the replacement set fitted entirely, so a
/// failed transfer reverses nothing.
/// </summary>
public sealed record BudgetTransferReserveOutcome(
    BudgetCheckResult Result,
    Guid ReverseOperationId,
    Guid? RequestOperationId,
    Guid? ReserveOperationId,
    IReadOnlyList<BudgetEvidenceMovement> ReversedMovements,
    IReadOnlyList<BudgetEvidenceMovement> RequestedMovements,
    IReadOnlyList<BudgetEvidenceMovement> ReservedMovements);

/// <summary>Outcome of the owner's decision: reserved, or a business insufficiency (REQ-06).</summary>
public sealed record BudgetReserveOutcome(
    BudgetCheckResult Result,
    Guid? RequestOperationId,
    Guid? ReserveOperationId,
    IReadOnlyList<BudgetEvidenceMovement> RequestedMovements,
    IReadOnlyList<BudgetEvidenceMovement> ReservedMovements);

/// <summary>One movement of a batch together with the position it belongs to (REQ-09).</summary>
public sealed record BudgetOperationMovement(BudgetPositionKey Position, BudgetMovementRequest Movement);

/// <summary>Immutable description of one operation batch before it is posted.</summary>
public sealed record BudgetOperationSpec(
    BudgetOperationKind Kind,
    Guid OrganizationId,
    string OperationKey,
    string Fingerprint,
    BudgetSource Source,
    BudgetActor Actor,
    string ReasonCode,
    string Result,
    IReadOnlyList<BudgetOperationMovement> Movements,
    Guid? CauseAuditId = null,
    string? CauseStream = null);

/// <summary>
/// Ledger operations of the Budget module (SPEC 08 REQ-02, REQ-03, REQ-04, REQ-06, REQ-09). Every
/// batch locks its positions by <c>position_key_digest</c> ascending, applies all its movements or
/// none, and writes the immutable operation, its movements, the projection and the audit atomically.
/// </summary>
public sealed class BudgetLedgerService(ProcureToPayDbContext dbContext, BudgetPersistenceService positions)
{
    /// <summary>
    /// Auditable, non-binding availability check (REQ-04). It persists one REQUESTED movement per
    /// covered line and never touches a bucket, so two concurrent prechecks can both report
    /// AVAILABLE without holding anything.
    /// </summary>
    public async Task<BudgetPrecheckOutcome> PrecheckAsync(
        Guid organizationId,
        string precheckKey,
        string manifestDigest,
        BudgetSource source,
        IReadOnlyList<BudgetDemand> demands,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demands);
        if (demands.Count is < 1 or > BudgetCodes.MaxDemandCount)
        {
            throw new DomainValidationException(
                $"A precheck accepts between 1 and {BudgetCodes.MaxDemandCount} demands.");
        }

        var key = BudgetCodes.RequireKey(precheckKey, "precheck_key");
        var correlation = BudgetCodes.RequireCorrelation(correlationReference);
        var preimage = BudgetFingerprints.PrecheckPreimage(
            organizationId, key, manifestDigest, source, demands);
        var fingerprint = BudgetCanonicalJson.Digest(preimage);
        var actor = BudgetActor.ForSystem(BudgetCodes.BudgetOwnerSystemId);
        var existing = await dbContext.BudgetOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.OrganizationId == organizationId &&
                             operation.OperationKey == key &&
                             operation.Kind == (int)BudgetOperationKind.Precheck,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The precheck key was already used with different content.");
            }

            var replayed = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.OperationId == existing.Id)
                .OrderBy(movement => movement.Id)
                .ToArrayAsync(cancellationToken);
            return new BudgetPrecheckOutcome(
                existing.Id,
                existing.Result,
                replayed.Select(movement => new BudgetPrecheckTargetView(
                    movement.TargetId ?? movement.PositionId,
                    movement.TargetVersion ?? 0,
                    "RECORDED")).ToArray(),
                Replayed: true);
        }

        var states = await LoadStatesAsync(
            organizationId,
            demands.Select(demand => demand.Payload),
            cancellationToken,
            requireAllocated: false);
        var evaluated = Evaluate(demands, states);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        foreach (var state in states)
        {
            await BudgetPersistenceService.AcquirePositionLockAsync(
                dbContext, organizationId, state.PositionKeyDigest, cancellationToken);
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var operation = NewOperation(
            organizationId,
            BudgetOperationKind.Precheck,
            key,
            fingerprint,
            source,
            actor,
            "PRECHECK",
            BudgetFingerprints.ResultCode(evaluated.Result),
            correlation,
            occurredAt,
            null,
            null);
        dbContext.BudgetOperations.Add(operation);
        var stateByPosition = states.ToDictionary(state => state.Payload.Position, state => state);
        var targets = new List<BudgetPrecheckTargetView>();
        foreach (var demand in demands.OrderBy(demand => demand.SourceLineId))
        {
            var state = stateByPosition[demand.Payload.Position];
            var movement = NewMovement(
                organizationId,
                operation.Id,
                state,
                BudgetMovementRequestFor(BudgetMovementType.Requested, demand),
                before: state.Buckets,
                after: state.Buckets,
                target: demand.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            targets.Add(new BudgetPrecheckTargetView(
                demand.SourceLineId,
                demand.SourceLineVersion,
                DemandResult(evaluated.Result, demand, state)));
        }

        positions.AddAudit(
            organizationId,
            actor,
            BudgetCodes.ActionPrechecked,
            "BudgetPrecheck",
            operation.Id,
            null,
            1,
            new { result = operation.Result, demand_count = demands.Count },
            correlation,
            occurredAt);
        await positions.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BudgetPrecheckOutcome(operation.Id, operation.Result, targets, Replayed: false);
    }

    /// <summary>
    /// Authoritative reservation of every demand or none (REQ-06, DEC-05). The batch posts one
    /// REQUESTED plus one RESERVED movement per demand when the whole set fits; on insufficiency it
    /// reports the business outcome without posting anything.
    /// </summary>
    public async Task<BudgetReserveOutcome> ReserveAsync(
        Guid organizationId,
        string requestKey,
        string reserveKey,
        BudgetSource source,
        BudgetActor actor,
        string reasonCode,
        Guid caseId,
        IReadOnlyList<BudgetDemand> demands,
        string correlationReference,
        Guid? causeAuditId = null,
        string? causeStream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demands);
        if (demands.Count is < 1 or > BudgetCodes.MaxDemandCount)
        {
            throw new DomainValidationException(
                $"A reservation accepts between 1 and {BudgetCodes.MaxDemandCount} demands.");
        }

        var correlation = BudgetCodes.RequireCorrelation(correlationReference);
        var normalizedReason = BudgetCodes.RequireReasonCode(reasonCode);
        var normalizedRequestKey = BudgetCodes.RequireKey(requestKey, "request_key");
        var normalizedReserveKey = BudgetCodes.RequireKey(reserveKey, "reserve_key");
        var requestedFingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            BudgetOperationKind.ApprovalReserve,
            normalizedRequestKey,
            normalizedReason,
            source,
            demands.Select(demand => new BudgetMovementCommandItem(demand.AmountBase, null, demand.Target))));
        var reserveFingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            BudgetOperationKind.ApprovalReserve,
            normalizedReserveKey,
            normalizedReason,
            source,
            demands.Select(demand => new BudgetMovementCommandItem(
                demand.AmountBase, demand.SourceLineId, demand.Target))));
        var payloads = demands.Select(demand => demand.Payload).ToArray();

        var existingReserve = await dbContext.BudgetOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.OrganizationId == organizationId &&
                             operation.OperationKey == normalizedReserveKey,
                cancellationToken);
        if (existingReserve is not null)
        {
            if (!string.Equals(existingReserve.Fingerprint, reserveFingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The reserve key was already used with different content.");
            }

            var posted = await dbContext.BudgetMovements
                .AsNoTracking()
                .Include(movement => movement.Position)
                .Where(movement => movement.OperationId == existingReserve.Id)
                .OrderBy(movement => movement.Id)
                .ToArrayAsync(cancellationToken);
            var replayKeys = await PositionKeysAsync(organizationId, payloads, cancellationToken);
            return new BudgetReserveOutcome(
                existingReserve.Result == "AVAILABLE" ? BudgetCheckResult.Available : BudgetCheckResult.Insufficient,
                null,
                existingReserve.Id,
                Evidence(posted, replayKeys, BudgetMovementType.Requested),
                Evidence(posted, replayKeys, BudgetMovementType.Reserved));
        }

        var states = await LoadStatesAsync(organizationId, payloads, cancellationToken);
        var evaluated = Evaluate(demands, states);
        if (evaluated.Result != BudgetCheckResult.Available)
        {
            // A precondition failure posts nothing at all: never a partial reservation (DEC-05).
            return new BudgetReserveOutcome(evaluated.Result, null, null, [], []);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        foreach (var state in states)
        {
            await BudgetPersistenceService.AcquirePositionLockAsync(
                dbContext, organizationId, state.PositionKeyDigest, cancellationToken);
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var requestOperation = NewOperation(
            organizationId,
            BudgetOperationKind.ApprovalReserve,
            normalizedRequestKey,
            requestedFingerprint,
            source,
            actor,
            normalizedReason,
            "RECORDED",
            correlation,
            occurredAt,
            causeAuditId,
            causeStream);
        dbContext.BudgetOperations.Add(requestOperation);
        var stateByKey = states.ToDictionary(state => state.Payload.Position, state => state);
        var keyDigests = states.ToDictionary(state => state.Payload.Position, state => state.PositionKeyDigest);
        var requestedMovements = new List<BudgetMovementRecord>();
        var requestedByDemand = new List<(BudgetDemand Demand, BudgetMovementRecord Movement)>();
        foreach (var demand in demands.OrderBy(demand => demand.SourceLineId))
        {
            var state = stateByKey[demand.Payload.Position];
            var movement = NewMovement(
                organizationId,
                requestOperation.Id,
                state,
                BudgetMovementRequestFor(BudgetMovementType.Requested, demand),
                before: state.Buckets,
                after: state.Buckets,
                target: demand.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            requestedMovements.Add(movement);
            requestedByDemand.Add((demand, movement));
        }

        var reserveOperation = NewOperation(
            organizationId,
            BudgetOperationKind.ApprovalReserve,
            normalizedReserveKey,
            reserveFingerprint,
            source,
            actor,
            normalizedReason,
            "AVAILABLE",
            correlation,
            occurredAt,
            causeAuditId,
            causeStream);
        dbContext.BudgetOperations.Add(reserveOperation);
        var reservedMovements = new List<BudgetMovementRecord>();
        foreach (var (demand, requestedMovement) in requestedByDemand)
        {
            var state = stateByKey[demand.Payload.Position];
            var before = state.Buckets;
            var request = BudgetMovementRequestFor(BudgetMovementType.Reserved, demand, requestedMovement.Id);
            var application = BudgetMovementCalculator.Apply(before, request, Posted(requestedMovement));
            state.Buckets = application.After;
            var movement = NewMovement(
                organizationId,
                reserveOperation.Id,
                state,
                request,
                before,
                application.After,
                demand.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            reservedMovements.Add(movement);
        }

        foreach (var state in states.Where(state => state.BalancesTouched))
        {
            UpdateBalance(state);
        }

        positions.AddAudit(
            organizationId,
            actor,
            BudgetCodes.ActionReserved,
            "BudgetOperation",
            reserveOperation.Id,
            null,
            1,
            new { case_id = caseId, demand_count = demands.Count, total = demands.Sum(demand => demand.AmountBase) },
            correlation,
            occurredAt,
            causeAuditId,
            causeStream);
        await positions.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BudgetReserveOutcome(
            BudgetCheckResult.Available,
            requestOperation.Id,
            reserveOperation.Id,
            Evidence(requestedMovements, keyDigests),
            Evidence(reservedMovements, keyDigests));
    }

    /// <summary>
    /// Posts one transition batch (COMMIT, CONSUME or REVERSE) or a release of open reservations,
    /// reusing the operation id on retry (REQ-03, REQ-08, REQ-09).
    /// </summary>
    public async Task<BudgetBatchOutcome> ApplyOperationAsync(
        BudgetOperationSpec spec,
        IReadOnlyDictionary<BudgetPositionKey, BudgetPositionPayload> payloads,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(payloads);
        var correlation = BudgetCodes.RequireCorrelation(correlationReference);
        foreach (var entry in spec.Movements)
        {
            if (!payloads.ContainsKey(entry.Position))
            {
                throw new DomainValidationException(
                    "The operation movement references a position without its attested payload.");
            }
        }

        var existing = await dbContext.BudgetOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.OrganizationId == spec.OrganizationId &&
                             operation.OperationKey == spec.OperationKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, spec.Fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The operation key was already used with different content.");
            }

            var posted = await ReadOperationMovementsAsync(existing.Id, cancellationToken);
            var replayedKeys = await PositionKeysAsync(spec.OrganizationId, payloads.Values, cancellationToken);
            return new BudgetBatchOutcome(
                existing.Id,
                true,
                posted.Select(movement => movement.Id).ToArray(),
                Evidence(posted, replayedKeys),
                replayedKeys,
                posted.Select(movement => new BudgetLedgerPostedMovement(
                    spec.Movements.Single(entry => entry.Movement.ParentMovementId == movement.ParentMovementId).Position,
                    movement)).ToArray());
        }

        var states = await LoadStatesAsync(spec.OrganizationId, payloads.Values, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        foreach (var state in states)
        {
            await BudgetPersistenceService.AcquirePositionLockAsync(
                dbContext, spec.OrganizationId, state.PositionKeyDigest, cancellationToken);
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var operation = NewOperation(
            spec.OrganizationId,
            spec.Kind,
            spec.OperationKey,
            spec.Fingerprint,
            spec.Source,
            spec.Actor,
            spec.ReasonCode,
            spec.Result,
            correlation,
            occurredAt,
            spec.CauseAuditId,
            spec.CauseStream);
        dbContext.BudgetOperations.Add(operation);
        var stateByKey = states.ToDictionary(state => state.Payload.Position, state => state);
        var keyDigests = states.ToDictionary(state => state.Payload.Position, state => state.PositionKeyDigest);
        var postedMovements = new List<BudgetMovementRecord>();
        foreach (var entry in spec.Movements)
        {
            if (!stateByKey.TryGetValue(entry.Position, out var state))
            {
                throw new DomainValidationException("The movement references a position outside the operation.");
            }

            var parent = await LoadParentAsync(spec.OrganizationId, state.PositionId, entry.Movement, cancellationToken);
            var before = state.Buckets;
            var application = BudgetMovementCalculator.Apply(before, entry.Movement, parent);
            state.Buckets = application.After;
            var movement = NewMovement(
                spec.OrganizationId,
                operation.Id,
                state,
                entry.Movement,
                before,
                application.After,
                entry.Movement.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            postedMovements.Add(movement);
        }

        foreach (var state in states.Where(state => state.BalancesTouched))
        {
            UpdateBalance(state);
        }

        positions.AddAudit(
            spec.OrganizationId,
            spec.Actor,
            AuditAction(spec.Kind),
            "BudgetOperation",
            operation.Id,
            null,
            1,
            new { kind = BudgetFingerprints.OperationCode(spec.Kind), movement_count = spec.Movements.Count },
            correlation,
            occurredAt,
            spec.CauseAuditId,
            spec.CauseStream);
        await positions.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BudgetBatchOutcome(
            operation.Id,
            false,
            postedMovements.Select(movement => movement.Id).ToArray(),
            Evidence(postedMovements, keyDigests),
            keyDigests,
            spec.Movements
                .Select((entry, index) => new BudgetLedgerPostedMovement(entry.Position, postedMovements[index]))
                .ToArray());
    }

    /// <summary>Evidence rows of one movement set, keyed by the digest of their position (REQ-10).</summary>
    public static IReadOnlyList<BudgetEvidenceMovement> Evidence(
        IEnumerable<BudgetMovementRecord> movements,
        IReadOnlyDictionary<BudgetPositionKey, string> positionKeys)
    {
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(positionKeys);
        return movements
            .OrderBy(movement => movement.Id)
            .Select(movement => EvidenceRow(movement, positionKeys))
            .ToArray();
    }

    /// <summary>Evidence rows of one movement type, published by the owner as its proof (REQ-06).</summary>
    public static IReadOnlyList<BudgetEvidenceMovement> Evidence(
        IEnumerable<BudgetMovementRecord> movements,
        IReadOnlyDictionary<BudgetPositionKey, string> positionKeys,
        BudgetMovementType type) =>
        Evidence(movements.Where(movement => movement.Type == (int)type), positionKeys);

    /// <summary>Evidence row of one movement, used by the reproducible proof of one attempt.</summary>
    public static BudgetEvidenceMovement EvidenceRow(
        BudgetMovementRecord movement,
        IReadOnlyDictionary<BudgetPositionKey, string> positionKeys)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(positionKeys);
        var position = movement.Position
            ?? throw new BudgetDependencyUnavailableException("The movement position is missing.");
        var key = new BudgetPositionKey(position.CostCenterId, position.FiscalYear, position.SpendCategoryCode);
        if (!positionKeys.TryGetValue(key, out var digest))
        {
            throw new BudgetDependencyUnavailableException("The movement position has no budget position digest.");
        }

        return new BudgetEvidenceMovement(
            movement.Id,
            (BudgetMovementType)movement.Type,
            movement.Amount,
            digest,
            movement.TargetId is Guid targetId && movement.TargetVersion is int targetVersion
                ? new BudgetTarget(
                    targetId,
                    targetVersion,
                    movement.TargetMaterialSnapshotDigest!,
                    movement.TargetType!)
                : null);
    }

    private async Task<BudgetPostedMovement?> LoadParentAsync(
        Guid organizationId,
        Guid positionId,
        BudgetMovementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ParentMovementId is not Guid parentId)
        {
            return null;
        }

        var parentRecord = await dbContext.BudgetMovements
            .AsNoTracking()
            .SingleOrDefaultAsync(movement => movement.Id == parentId, cancellationToken)
            ?? throw new DomainValidationException("The parent movement does not exist.");
        if (parentRecord.OrganizationId != organizationId || parentRecord.PositionId != positionId)
        {
            throw new DomainValidationException("The parent movement belongs to another organization or position.");
        }

        var children = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.ParentMovementId == parentId)
            .Select(movement => movement.Amount)
            .ToArrayAsync(cancellationToken);
        return new BudgetPostedMovement(
            parentRecord.Id,
            (BudgetMovementType)parentRecord.Type,
            parentRecord.Amount,
            parentRecord.ParentMovementId,
            children.Sum(),
            children.Length > 0);
    }

    private static string AuditAction(BudgetOperationKind kind) => kind switch
    {
        BudgetOperationKind.Commit => BudgetCodes.ActionCommitted,
        BudgetOperationKind.Consume => BudgetCodes.ActionConsumed,
        BudgetOperationKind.Release => BudgetCodes.ActionReleased,
        BudgetOperationKind.TransferReserve => BudgetCodes.ActionReserved,
        BudgetOperationKind.Reverse => BudgetCodes.ActionCompensated,
        _ => BudgetCodes.ActionPrechecked
    };

    private static BudgetOperationRecord NewOperation(
        Guid organizationId,
        BudgetOperationKind kind,
        string operationKey,
        string fingerprint,
        BudgetSource source,
        BudgetActor actor,
        string reasonCode,
        string result,
        string correlation,
        DateTimeOffset occurredAt,
        Guid? causeAuditId,
        string? causeStream) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Kind = (int)kind,
            OperationKey = operationKey,
            Fingerprint = fingerprint,
            SourceType = source.Type,
            SourceId = source.Id,
            SourceVersion = source.Version,
            SourceDigest = source.Digest,
            ActorJson = JsonSerializer.Serialize(actor),
            CauseStream = causeStream,
            CauseAuditId = causeAuditId,
            ReasonCode = reasonCode,
            Result = result,
            OccurredAt = occurredAt,
            CorrelationReference = correlation
        };

    private static BudgetMovementRecord NewMovement(
        Guid organizationId,
        Guid operationId,
        BudgetPositionState state,
        BudgetMovementRequest request,
        BudgetBuckets before,
        BudgetBuckets after,
        BudgetTarget? target,
        DateTimeOffset occurredAt)
    {
        // Demand never advances the projection, so it also never moves the balance version; the
        // balance version counts exactly the transactions that touch a bucket (REQ-02).
        var currentBalanceVersion = state.Balance?.BalanceVersion ?? 0;
        var nextBalanceVersion = BudgetPersistenceService.NextBalanceVersion(
            request.Type, currentBalanceVersion);
        if (nextBalanceVersion is not null)
        {
            // Only the movements that actually change the projection advance the balance version
            // and therefore the position pointer (REQ-02).
            state.BalancesTouched = true;
        }
        return new BudgetMovementRecord
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            OrganizationId = organizationId,
            PositionId = state.PositionId,
            AllocationVersionAtPosting = state.Balance?.AllocationVersion ?? 0,
            Type = (int)request.Type,
            Amount = request.Amount,
            Currency = state.Currency,
            ParentMovementId = request.ParentMovementId,
            TargetId = target?.Id,
            TargetVersion = target?.Version,
            TargetMaterialSnapshotDigest = target?.MaterialSnapshotDigest,
            TargetType = target?.Type,
            BalanceVersionAfter = nextBalanceVersion ?? currentBalanceVersion,
            AllocatedBefore = before.Allocated,
            ReservedBefore = before.Reserved,
            CommittedBefore = before.Committed,
            ConsumedBefore = before.Consumed,
            AllocatedAfter = after.Allocated,
            ReservedAfter = after.Reserved,
            CommittedAfter = after.Committed,
            ConsumedAfter = after.Consumed,
            OccurredAt = occurredAt
        };
    }

    private static void UpdateBalance(BudgetPositionState state)
    {
        var balance = state.Balance
            ?? throw new BudgetDependencyUnavailableException(
                "A movement cannot be posted on a position without an allocation.");
        balance.Allocated = state.Buckets.Allocated;
        balance.Reserved = state.Buckets.Reserved;
        balance.Committed = state.Buckets.Committed;
        balance.Consumed = state.Buckets.Consumed;
        balance.BalanceVersion += 1;
        if (state.Position is not null)
        {
            state.Position.CurrentBalanceVersion = balance.BalanceVersion;
        }
    }

    private static BudgetMovementRequest BudgetMovementRequestFor(
        BudgetMovementType type,
        BudgetDemand demand,
        Guid? parentMovementId = null) =>
        new(type, demand.AmountBase, parentMovementId, demand.Target);

    private static BudgetPostedMovement Posted(BudgetMovementRecord movement) =>
        new(
            movement.Id,
            (BudgetMovementType)movement.Type,
            movement.Amount,
            movement.ParentMovementId,
            0m,
            false);

    /// <summary>
    /// Loads the state of every referenced position. <paramref name="requireAllocated"/> is true for
    /// any financial transition, where a missing allocation is a contract violation instead of the
    /// UNFUNDED answer of a precheck (REQ-03, REQ-04).
    /// </summary>
    private async Task<List<BudgetPositionState>> LoadStatesAsync(
        Guid organizationId,
        IEnumerable<BudgetPositionPayload> payloads,
        CancellationToken cancellationToken,
        bool requireAllocated = true)
    {
        var states = new List<BudgetPositionState>();
        foreach (var payload in payloads.DistinctBy(candidate => candidate.Position))
        {
            var digest = BudgetFingerprints.PositionKeyDigest(
                organizationId,
                payload.Position.CostCenterId,
                payload.Position.FiscalYear,
                payload.Position.SpendCategoryCode);
            var position = await dbContext.BudgetPositions.SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId && record.PositionKeyDigest == digest,
                cancellationToken);
            if (position is null && requireAllocated)
            {
                throw new DomainNotFoundException("The budget position was not found.");
            }

            states.Add(await BuildStateAsync(digest, payload, position, cancellationToken));
        }

        return states;
    }

    private async Task<BudgetPositionState> BuildStateAsync(
        string digest,
        BudgetPositionPayload payload,
        BudgetPositionRecord? position,
        CancellationToken cancellationToken)
    {
        if (position is null)
        {
            return new BudgetPositionState(
                Guid.Empty,
                digest,
                payload,
                null,
                null,
                BudgetBuckets.Create(0m, 0m, 0m, 0m),
                string.Empty);
        }

        var balance = await dbContext.BudgetBalances
            .SingleAsync(record => record.PositionId == position.Id, cancellationToken);
        var currency = await dbContext.BudgetAllocationVersions
            .AsNoTracking()
            .Where(version => version.PositionId == position.Id &&
                              version.Version == position.CurrentAllocationVersion)
            .Select(version => version.Currency)
            .SingleAsync(cancellationToken);
        return new BudgetPositionState(
            position.Id,
            position.PositionKeyDigest,
            payload,
            position,
            balance,
            BudgetBuckets.Create(balance.Allocated, balance.Reserved, balance.Committed, balance.Consumed),
            currency);
    }

    private async Task<IReadOnlyDictionary<BudgetPositionKey, string>> PositionKeysAsync(
        Guid organizationId,
        IEnumerable<BudgetPositionPayload> payloads,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var digests = new Dictionary<BudgetPositionKey, string>();
        foreach (var payload in payloads.DistinctBy(candidate => candidate.Position))
        {
            digests[payload.Position] = BudgetFingerprints.PositionKeyDigest(
                organizationId,
                payload.Position.CostCenterId,
                payload.Position.FiscalYear,
                payload.Position.SpendCategoryCode);
        }

        return await Task.FromResult<IReadOnlyDictionary<BudgetPositionKey, string>>(digests);
    }

    /// <summary>
    /// Serialized transfer between a superseded case and its replacement (REQ-08, DEC-07). Under the
    /// locks of every involved position it evaluates the replacement set counting the open
    /// reservations of its predecessors as available; when the whole set fits, it reverses those
    /// reservations and posts the new REQUESTED+RESERVED pair in the same transaction. When it does
    /// not fit, it posts nothing at all: the predecessors keep their hold and the caller signals the
    /// business insufficiency. Replay resolves each recorded operation by its own key.
    /// </summary>
    public async Task<BudgetTransferReserveOutcome> TransferReserveAsync(
        Guid organizationId,
        string requestKey,
        string reserveKey,
        BudgetSource source,
        BudgetActor actor,
        string reasonCode,
        Guid caseId,
        IReadOnlyList<BudgetDemand> demands,
        IReadOnlyList<BudgetPredecessorReservation> predecessors,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demands);
        ArgumentNullException.ThrowIfNull(predecessors);
        if (demands.Count is < 1 or > BudgetCodes.MaxDemandCount)
        {
            throw new DomainValidationException(
                $"A reservation accepts between 1 and {BudgetCodes.MaxDemandCount} demands.");
        }

        var correlation = BudgetCodes.RequireCorrelation(correlationReference);
        var normalizedReason = BudgetCodes.RequireReasonCode(reasonCode);
        var normalizedRequestKey = BudgetCodes.RequireKey(requestKey, "request_key");
        var normalizedReserveKey = BudgetCodes.RequireKey(reserveKey, "reserve_key");
        var predecessorParents = await LoadPredecessorParentsAsync(
            organizationId, predecessors, cancellationToken);
        var reverseKey = $"budget:{caseId:D}:transfer-reverse";
        var requestedFingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            BudgetOperationKind.TransferReserve,
            normalizedRequestKey,
            normalizedReason,
            source,
            demands.Select(demand => new BudgetMovementCommandItem(demand.AmountBase, null, demand.Target))));
        var reserveFingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            BudgetOperationKind.TransferReserve,
            normalizedReserveKey,
            normalizedReason,
            source,
            demands.Select(demand => new BudgetMovementCommandItem(demand.AmountBase, null, demand.Target))));
        var reverseFingerprint = await ReverseFingerprintAsync(
            organizationId,
            actor,
            reverseKey,
            normalizedReason,
            source,
            predecessorParents,
            cancellationToken);

        var replayed = await ReplayTransferAsync(
            organizationId,
            normalizedRequestKey,
            normalizedReserveKey,
            reverseKey,
            requestedFingerprint,
            reserveFingerprint,
            reverseFingerprint,
            demands,
            cancellationToken);
        if (replayed is not null)
        {
            return replayed;
        }

        var payloads = demands.Select(demand => demand.Payload).ToArray();
        var states = await LoadStatesAsync(organizationId, payloads, cancellationToken);
        var reversalStates = new List<BudgetPositionState>();
        foreach (var positionId in predecessorParents
                     .Select(parent => parent.PositionId)
                     .Distinct()
                     .ToArray())
        {
            reversalStates.Add(await positions.LoadStateAsync(organizationId, positionId, cancellationToken));
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        // Every involved position is locked in the same order as any other writer, so the reversal and
        // the replacement cannot interleave with a third transaction (REQ-02, REQ-08).
        var locked = states
            .Concat(reversalStates)
            .Where(state => state.Position is not null)
            .DistinctBy(state => state.PositionKeyDigest)
            .OrderBy(state => state.PositionKeyDigest, StringComparer.Ordinal)
            .ToArray();
        foreach (var state in locked)
        {
            await BudgetPersistenceService.AcquirePositionLockAsync(
                dbContext, organizationId, state.PositionKeyDigest, cancellationToken);
        }

        var stateByKey = locked.ToDictionary(state => state.Payload.Position, state => state);
        var keyDigests = locked.ToDictionary(state => state.Payload.Position, state => state.PositionKeyDigest);
        var occurredAt = DateTimeOffset.UtcNow;

        // Evaluate the replacement against the buckets the predecessors still hold: this is the whole
        // point of counting the previous hold as available before deciding.
        var availableWithCredit = locked.ToDictionary(
            state => state.Payload.Position,
            state => state.Buckets.Available + CreditFor(predecessorParents, state.Payload.Position));
        var evaluated = EvaluateWithCredit(demands, locked, predecessorParents);
        if (evaluated != BudgetCheckResult.Available)
        {
            // Nothing is reversed and nothing is reserved: the failure is a business outcome.
            await transaction.CommitAsync(cancellationToken);
            return new BudgetTransferReserveOutcome(evaluated, Guid.Empty, null, null, [], [], []);
        }

        var reverseOperation = NewOperation(
            organizationId,
            BudgetOperationKind.TransferReserve,
            reverseKey,
            reverseFingerprint,
            source,
            actor,
            normalizedReason,
            "TRANSFERRED",
            correlation,
            occurredAt,
            null,
            null);
        dbContext.BudgetOperations.Add(reverseOperation);
        var reversedMovements = new List<BudgetMovementRecord>();
        foreach (var parent in predecessorParents)
        {
            var state = stateByKey[parent.Payload.Position];
            var before = state.Buckets;
            var request = new BudgetMovementRequest(
                BudgetMovementType.Reverse, parent.Amount, parent.Id, null);
            var application = BudgetMovementCalculator.Apply(before, request, parent.Posted);
            state.Buckets = application.After;
            var movement = NewMovement(
                organizationId,
                reverseOperation.Id,
                state,
                request,
                before,
                application.After,
                null,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            reversedMovements.Add(movement);
        }

        positions.AddAudit(
            organizationId,
            actor,
            BudgetCodes.ActionReleased,
            "BudgetOperation",
            reverseOperation.Id,
            null,
            1,
            new { case_id = caseId, predecessor_count = predecessorParents.Count },
            correlation,
            occurredAt,
            null,
            null);

        var requestOperation = NewOperation(
            organizationId,
            BudgetOperationKind.TransferReserve,
            normalizedRequestKey,
            requestedFingerprint,
            source,
            actor,
            normalizedReason,
            "RECORDED",
            correlation,
            occurredAt,
            null,
            null);
        dbContext.BudgetOperations.Add(requestOperation);
        var requestedMovements = new List<BudgetMovementRecord>();
        var requestedByDemand = new List<(BudgetDemand Demand, BudgetMovementRecord Movement)>();
        foreach (var demand in demands)
        {
            var state = stateByKey[demand.Payload.Position];
            var before = state.Buckets;
            var request = BudgetMovementRequestFor(BudgetMovementType.Requested, demand);
            var application = BudgetMovementCalculator.Apply(before, request, null);
            state.Buckets = application.After;
            var movement = NewMovement(
                organizationId,
                requestOperation.Id,
                state,
                request,
                before,
                application.After,
                demand.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            requestedMovements.Add(movement);
            requestedByDemand.Add((demand, movement));
        }

        var reserveOperation = NewOperation(
            organizationId,
            BudgetOperationKind.TransferReserve,
            normalizedReserveKey,
            reserveFingerprint,
            source,
            actor,
            normalizedReason,
            "AVAILABLE",
            correlation,
            occurredAt,
            null,
            null);
        dbContext.BudgetOperations.Add(reserveOperation);
        var reservedMovements = new List<BudgetMovementRecord>();
        foreach (var (demand, requestedMovement) in requestedByDemand)
        {
            var state = stateByKey[demand.Payload.Position];
            var before = state.Buckets;
            var request = BudgetMovementRequestFor(BudgetMovementType.Reserved, demand, requestedMovement.Id);
            // The replacement reservation is validated against the availability the transfer just
            // freed, which includes the predecessor hold reversed above (REQ-08).
            var availableNow = availableWithCredit[state.Payload.Position];
            var application = BudgetMovementCalculator.Apply(
                before,
                request,
                Posted(requestedMovement),
                availableOverride: availableNow);
            state.Buckets = application.After;
            var movement = NewMovement(
                organizationId,
                reserveOperation.Id,
                state,
                request,
                before,
                application.After,
                demand.Target,
                occurredAt);
            dbContext.BudgetMovements.Add(movement);
            reservedMovements.Add(movement);
        }

        foreach (var state in locked.Where(state => state.BalancesTouched))
        {
            UpdateBalance(state);
        }

        positions.AddAudit(
            organizationId,
            actor,
            BudgetCodes.ActionReserved,
            "BudgetOperation",
            reserveOperation.Id,
            null,
            1,
            new { case_id = caseId, demand_count = demands.Count, total = demands.Sum(demand => demand.AmountBase) },
            correlation,
            occurredAt,
            null,
            null);
        await positions.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BudgetTransferReserveOutcome(
            BudgetCheckResult.Available,
            reverseOperation.Id,
            requestOperation.Id,
            reserveOperation.Id,
            Evidence(reversedMovements, keyDigests),
            Evidence(requestedMovements, keyDigests),
            Evidence(reservedMovements, keyDigests));
    }

    /// <summary>
    /// Fingerprint of the reversal command. A redelivery of an already applied transfer finds its
    /// predecessor holds already reversed, so the item set is rebuilt from the recorded operation;
    /// that keeps the replay comparison about the command the caller sent, not about the state.
    /// </summary>
    private async Task<string> ReverseFingerprintAsync(
        Guid organizationId,
        BudgetActor actor,
        string reverseKey,
        string reasonCode,
        BudgetSource source,
        IReadOnlyList<BudgetPredecessorParent> predecessorParents,
        CancellationToken cancellationToken)
    {
        var recorded = await dbContext.BudgetOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.OrganizationId == organizationId &&
                             operation.OperationKey == reverseKey,
                cancellationToken);
        if (recorded is null)
        {
            return BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
                organizationId,
                actor,
                BudgetOperationKind.TransferReserve,
                reverseKey,
                reasonCode,
                source,
                predecessorParents.Select(parent => new BudgetMovementCommandItem(
                    parent.Amount, parent.Id, null))));
        }

        var movements = await ReadOperationMovementsAsync(recorded.Id, cancellationToken);
        return BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            BudgetOperationKind.TransferReserve,
            reverseKey,
            reasonCode,
            source,
            movements
                .OrderBy(movement => movement.Id)
                .Select(movement => new BudgetMovementCommandItem(
                    movement.Amount, movement.ParentMovementId, null))));
    }

    /// <summary>Availability the transfer credits to one position from its predecessors.</summary>
    private static decimal CreditFor(IReadOnlyList<BudgetPredecessorParent> predecessors, BudgetPositionKey position) =>
        predecessors
            .Where(parent => parent.Payload.Position == position)
            .Sum(parent => parent.Amount);

    /// <summary>
    /// Evaluates the replacement set with the open reservations of its predecessors counted as
    /// available, per position and all-or-nothing (REQ-08). It mirrors the demand-only evaluation of
    /// a plain reservation without posting anything.
    /// </summary>
    private static BudgetCheckResult EvaluateWithCredit(
        IReadOnlyList<BudgetDemand> demands,
        IReadOnlyList<BudgetPositionState> states,
        IReadOnlyList<BudgetPredecessorParent> predecessors)
    {
        var stateByPosition = states.ToDictionary(state => state.Payload.Position, state => state);
        var creditByPosition = predecessors
            .GroupBy(parent => parent.Payload.Position)
            .ToDictionary(group => group.Key, group => group.Sum(parent => parent.Amount));
        var result = BudgetCheckResult.Available;
        foreach (var total in demands
                     .GroupBy(demand => demand.Payload.Position)
                     .Select(group => (Position: group.Key, Total: group.Sum(demand => demand.AmountBase))))
        {
            var state = stateByPosition[total.Position];
            if (state.Position is null)
            {
                result = BudgetCheckResult.Unfunded;
                continue;
            }

            var credit = creditByPosition.TryGetValue(total.Position, out var value) ? value : 0m;
            if (state.Buckets.Available + credit < total.Total)
            {
                result = BudgetCheckResult.Insufficient;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the predecessor reservations that the transfer reverts: each id must be an open
    /// RESERVED movement of this organization with no posted reversal yet (REQ-08).
    /// </summary>
    private async Task<IReadOnlyList<BudgetPredecessorParent>> LoadPredecessorParentsAsync(
        Guid organizationId,
        IReadOnlyList<BudgetPredecessorReservation> predecessors,
        CancellationToken cancellationToken)
    {
        var parents = new List<BudgetPredecessorParent>();
        foreach (var predecessor in predecessors)
        {
            foreach (var movementId in predecessor.ReservedMovementIds)
            {
                var movement = await dbContext.BudgetMovements
                    .AsNoTracking()
                    .SingleOrDefaultAsync(record => record.Id == movementId, cancellationToken)
                    ?? throw new DomainNotFoundException("The predecessor movement is not visible.");
                if (movement.OrganizationId != organizationId ||
                    movement.Type != (int)BudgetMovementType.Reserved)
                {
                    throw new DomainNotFoundException("The predecessor movement is not visible.");
                }

                var state = await positions.LoadStateAsync(
                    organizationId, movement.PositionId, cancellationToken);
                var reversed = await dbContext.BudgetMovements
                    .AsNoTracking()
                    .Where(record => record.ParentMovementId == movementId)
                    .Select(record => record.Amount)
                    .ToArrayAsync(cancellationToken);
                var remaining = movement.Amount - reversed.Sum();
                if (remaining <= 0m)
                {
                    // The hold was already released or committed: nothing to transfer.
                    continue;
                }

                parents.Add(new BudgetPredecessorParent(
                    movement.Id,
                    movement.PositionId,
                    predecessor.CaseId,
                    remaining,
                    state.Payload,
                    Posted(movement)));
            }
        }

        return parents;
    }

    /// <summary>Replay of a transfer, resolving whichever operations were already recorded.</summary>
    private async Task<BudgetTransferReserveOutcome?> ReplayTransferAsync(
        Guid organizationId,
        string requestKey,
        string reserveKey,
        string reverseKey,
        string requestedFingerprint,
        string reserveFingerprint,
        string reverseFingerprint,
        IReadOnlyList<BudgetDemand> demands,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.BudgetOperations
            .AsNoTracking()
            .Where(operation => operation.OrganizationId == organizationId &&
                                (operation.OperationKey == requestKey ||
                                 operation.OperationKey == reserveKey ||
                                 operation.OperationKey == reverseKey))
            .ToArrayAsync(cancellationToken);
        if (existing.Length == 0)
        {
            return null;
        }

        var recorded = existing.ToDictionary(operation => operation.OperationKey, StringComparer.Ordinal);
        foreach (var (key, fingerprint) in new[]
                 {
                     (requestKey, requestedFingerprint),
                     (reserveKey, reserveFingerprint),
                     (reverseKey, reverseFingerprint)
                 })
        {
            if (recorded.TryGetValue(key, out var operation) &&
                !string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The transfer key was already used with different content.");
            }
        }

        var replayKeys = await PositionKeysAsync(
            organizationId, demands.Select(demand => demand.Payload).ToArray(), cancellationToken);
        var reversed = recorded.TryGetValue(reverseKey, out var reverse)
            ? Evidence(await ReadOperationMovementsAsync(reverse.Id, cancellationToken), replayKeys)
            : [];
        var requested = recorded.TryGetValue(requestKey, out var request)
            ? Evidence(await ReadOperationMovementsAsync(request.Id, cancellationToken), replayKeys)
            : [];
        var reserved = recorded.TryGetValue(reserveKey, out var reserveOperation)
            ? Evidence(await ReadOperationMovementsAsync(reserveOperation.Id, cancellationToken), replayKeys)
            : [];
        if (reserved.Count == 0 && reversed.Count != 0)
        {
            // The first transfer reversed nothing (insufficiency); the replay reports the same.
            return new BudgetTransferReserveOutcome(
                BudgetCheckResult.Insufficient, reverse.Id, null, null, [], [], []);
        }

        return new BudgetTransferReserveOutcome(
            BudgetCheckResult.Available,
            reverse?.Id ?? Guid.Empty,
            request?.Id,
            reserveOperation?.Id,
            reversed,
            requested,
            reserved);
    }

    /// <summary>
    /// Movements of one already recorded operation, read for replay. The position is materialized
    /// explicitly: a replay may run in a context that never tracked the position row, and the
    /// evidence rows of REQ-10 need the position to rebuild the position key digest.
    /// </summary>
    private async Task<List<BudgetMovementRecord>> ReadOperationMovementsAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await dbContext.BudgetMovements
            .AsNoTracking()
            .Include(movement => movement.Position)
            .Where(movement => movement.OperationId == operationId)
            .OrderBy(movement => movement.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Aggregates the demands per position and decides whether the whole set fits at once. The
    /// evaluation is deliberately bucket-only and never reserves partially (REQ-04, DEC-05).
    /// </summary>
    public static BudgetEvaluation Evaluate(
        IReadOnlyList<BudgetDemand> demands,
        IReadOnlyList<BudgetPositionState> states)
    {
        var stateByPosition = states.ToDictionary(state => state.Payload.Position, state => state);
        var totals = demands
            .GroupBy(demand => demand.Payload.Position)
            .Select(group => (Position: group.Key, Total: group.Sum(demand => demand.AmountBase)))
            .ToArray();
        var result = BudgetCheckResult.Available;
        foreach (var total in totals)
        {
            var state = stateByPosition[total.Position];
            if (state.Position is null)
            {
                result = BudgetCheckResult.Unfunded;
                continue;
            }

            if (state.Buckets.Available < total.Total && result != BudgetCheckResult.Unfunded)
            {
                result = BudgetCheckResult.Insufficient;
            }
        }

        var lineResults = demands
            .Select(demand => new BudgetPrecheckTargetView(
                demand.SourceLineId,
                demand.SourceLineVersion,
                BudgetFingerprints.ResultCode(result)))
            .ToArray();
        return new BudgetEvaluation(result, lineResults);
    }

    /// <summary>Per-line answer of one evaluation: a line of an unfunded position is UNFUNDED.</summary>
    private static string DemandResult(
        BudgetCheckResult aggregate,
        BudgetDemand demand,
        BudgetPositionState state) =>
        state.Position is null ? "UNFUNDED" : BudgetFingerprints.ResultCode(aggregate);

    /// <summary>Aggregate decision of one evaluation with its per-line projection (REQ-04).</summary>
    public sealed record BudgetEvaluation(
        BudgetCheckResult Result,
        IReadOnlyList<BudgetPrecheckTargetView> LineResults);

    /// <summary>Mutable view of one position while a batch is being applied.</summary>
    public sealed class BudgetPositionState(
        Guid positionId,
        string positionKeyDigest,
        BudgetPositionPayload payload,
        BudgetPositionRecord? position,
        BudgetBalanceRecord? balance,
        BudgetBuckets buckets,
        string currency)
    {
        public Guid PositionId { get; } = positionId;

        public string PositionKeyDigest { get; } = positionKeyDigest;

        public BudgetPositionPayload Payload { get; } = payload;

        public BudgetPositionRecord? Position { get; } = position;

        public BudgetBalanceRecord? Balance { get; } = balance;

        public BudgetBuckets Buckets { get; set; } = buckets;

        public string Currency { get; } = currency;

        public bool BalancesTouched { get; set; }
    }
}
