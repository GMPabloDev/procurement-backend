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
    IReadOnlyDictionary<BudgetPositionKey, string> PositionKeys);

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
                replayedKeys);
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
            keyDigests);
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

    private async Task<List<BudgetMovementRecord>> ReadOperationMovementsAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await dbContext.BudgetMovements
            .AsNoTracking()
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
