using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Budget;
using BudgetDomain = ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>One position with its current projection, as the authorized reads publish it.</summary>
public sealed record BudgetPositionView(
    Guid PositionId,
    Guid CostCenterId,
    int FiscalYear,
    string SpendCategoryCode,
    int AllocationVersion,
    int BalanceVersion,
    string Currency,
    BudgetBuckets Buckets);

/// <summary>One append-only movement of a position, as the authorized history publishes it.</summary>
public sealed record BudgetMovementView(
    Guid MovementId,
    Guid OperationId,
    int Type,
    decimal Amount,
    Guid? ParentMovementId,
    Guid? TargetId,
    int? TargetVersion,
    DateTimeOffset OccurredAt);

/// <summary>
/// Persistence of the Budget ledger (SPEC 08 REQ-01..REQ-03, REQ-10). Every mutation locks the
/// affected positions by <c>position_key_digest</c> ascending inside one serializable transaction,
/// posts all movements or none, and writes its audit atomically.
/// </summary>
public sealed class BudgetPersistenceService(ProcureToPayDbContext dbContext)
{
    /// <summary>Current projection of one position, or null when the position does not exist yet.</summary>
    public async Task<BudgetPositionView?> FindPositionAsync(
        Guid organizationId,
        BudgetPositionKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var digest = BudgetFingerprints.PositionKeyDigest(
            organizationId, key.CostCenterId, key.FiscalYear, key.SpendCategoryCode);
        var row = await (
                from position in dbContext.BudgetPositions.AsNoTracking()
                join balance in dbContext.BudgetBalances.AsNoTracking()
                    on position.Id equals balance.PositionId
                where position.OrganizationId == organizationId && position.PositionKeyDigest == digest
                select new
                {
                    position.Id,
                    position.CostCenterId,
                    position.FiscalYear,
                    position.SpendCategoryCode,
                    position.CurrentAllocationVersion,
                    balance.BalanceVersion,
                    balance.Allocated,
                    balance.Reserved,
                    balance.Committed,
                    balance.Consumed
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var currency = await dbContext.BudgetAllocationVersions
            .AsNoTracking()
            .Where(version => version.PositionId == row.Id && version.Version == row.CurrentAllocationVersion)
            .Select(version => version.Currency)
            .SingleAsync(cancellationToken);
        return new BudgetPositionView(
            row.Id,
            row.CostCenterId,
            row.FiscalYear,
            row.SpendCategoryCode,
            row.CurrentAllocationVersion,
            row.BalanceVersion,
            currency,
            BudgetBuckets.Create(row.Allocated, row.Reserved, row.Committed, row.Consumed));
    }

    /// <summary>Every position of the organization with its current projection, ordered by identity.</summary>
    public async Task<IReadOnlyList<BudgetPositionView>> ListPositionsAsync(
        Guid organizationId,
        Guid? costCenterId,
        int? fiscalYear,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from position in dbContext.BudgetPositions.AsNoTracking()
                join balance in dbContext.BudgetBalances.AsNoTracking()
                    on position.Id equals balance.PositionId
                join version in dbContext.BudgetAllocationVersions.AsNoTracking()
                    on new { position.Id, Version = position.CurrentAllocationVersion }
                    equals new { Id = version.PositionId, version.Version }
                where position.OrganizationId == organizationId &&
                      (costCenterId == null || position.CostCenterId == costCenterId) &&
                      (fiscalYear == null || position.FiscalYear == fiscalYear)
                select new
                {
                    position.Id,
                    position.CostCenterId,
                    position.FiscalYear,
                    position.SpendCategoryCode,
                    position.CurrentAllocationVersion,
                    balance.BalanceVersion,
                    balance.Allocated,
                    balance.Reserved,
                    balance.Committed,
                    balance.Consumed,
                    version.Currency
                })
            .ToArrayAsync(cancellationToken);
        return rows
            .OrderBy(row => row.CostCenterId)
            .ThenBy(row => row.FiscalYear)
            .ThenBy(row => row.SpendCategoryCode, StringComparer.Ordinal)
            .Select(row => new BudgetPositionView(
                row.Id,
                row.CostCenterId,
                row.FiscalYear,
                row.SpendCategoryCode,
                row.CurrentAllocationVersion,
                row.BalanceVersion,
                row.Currency,
                BudgetBuckets.Create(row.Allocated, row.Reserved, row.Committed, row.Consumed)))
            .ToArray();
    }

    /// <summary>Append-only movement history of one position.</summary>
    public async Task<IReadOnlyList<BudgetMovementView>> ReadMovementsAsync(
        Guid organizationId,
        Guid positionId,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.BudgetPositions.AnyAsync(
            position => position.Id == positionId && position.OrganizationId == organizationId,
            cancellationToken);
        if (!exists)
        {
            throw new DomainNotFoundException("The budget position was not found.");
        }

        var rows = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.PositionId == positionId)
            .OrderBy(movement => movement.OccurredAt)
            .ThenBy(movement => movement.Id)
            .ToArrayAsync(cancellationToken);
        return rows.Select(movement => new BudgetMovementView(
            movement.Id,
            movement.OperationId,
            movement.Type,
            movement.Amount,
            movement.ParentMovementId,
            movement.TargetId,
            movement.TargetVersion,
            movement.OccurredAt)).ToArray();
    }

    /// <summary>Rebuilds the projection from the current allocation plus every movement of the position.</summary>
    public async Task<BudgetBuckets?> RebuildAsync(
        Guid organizationId,
        Guid positionId,
        CancellationToken cancellationToken = default)
    {
        var position = await dbContext.BudgetPositions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == positionId && record.OrganizationId == organizationId,
                cancellationToken);
        if (position is null)
        {
            return null;
        }

        var allocated = await dbContext.BudgetAllocationVersions
            .AsNoTracking()
            .Where(version => version.PositionId == positionId &&
                              version.Version == position.CurrentAllocationVersion)
            .Select(version => version.AllocatedAmount)
            .SingleAsync(cancellationToken);
        var movements = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.PositionId == positionId)
            .OrderBy(movement => movement.OccurredAt)
            .ThenBy(movement => movement.Id)
            .ToArrayAsync(cancellationToken);
        // The ledger is self-describing: the first row carries the projection before its delta and
        // the last one carries the projection after; with no movement the position is fully
        // available. This rebuild is independent from the movement-type arithmetic, so a corrupted
        // type or amount cannot hide a divergence (NFR-01).
        return movements.Length == 0
            ? BudgetBuckets.Create(allocated, 0m, 0m, 0m)
            : BudgetBuckets.Create(
                movements[^1].AllocatedAfter,
                movements[^1].ReservedAfter,
                movements[^1].CommittedAfter,
                movements[^1].ConsumedAfter);
    }

    /// <summary>
    /// Administrative allocation revision (REQ-01). The first accepted allocation creates the
    /// position; later revisions append a successor version and update the projection in the same
    /// transaction, refusing to reduce ALLOCATED below the funds already held.
    /// </summary>
    public async Task<BudgetPositionView> SetAllocationAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid costCenterId,
        int fiscalYear,
        string? spendCategoryCode,
        decimal allocatedAmount,
        string currency,
        int? expectedVersion,
        string? allocationKey,
        string? reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var key = BudgetCodes.RequireKey(allocationKey, "allocation_key");
        var cleanReason = BudgetCodes.RequireReason(reason);
        var cleanCurrency = BudgetCodes.RequireBaseCurrency(currency);
        var cleanAmount = BudgetCodes.RequireAmount(allocatedAmount, "Allocated amount");
        var correlation = BudgetCodes.RequireCorrelation(correlationReference);
        var normalizedCode = ReferenceCatalogCodes.RequireCode(spendCategoryCode, "Spend Category code");
        var payload = await ResolvePositionPayloadAsync(
            organizationId, costCenterId, fiscalYear, normalizedCode, cleanCurrency, cancellationToken);
        var actor = BudgetActor.ForUser(actorUserId);
        var preimage = BudgetFingerprints.AllocationPreimage(
            organizationId, actor, payload, cleanAmount, cleanCurrency, expectedVersion, key, cleanReason);
        var fingerprint = BudgetCanonicalJson.Digest(preimage);
        var positionKeyDigest = BudgetFingerprints.PositionKeyDigest(
            organizationId, costCenterId, fiscalYear, normalizedCode);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        // The critical section is ordered by position key digest so two batches touching the same
        // set of positions never deadlock (REQ-02).
        await AcquirePositionLockAsync(organizationId, positionKeyDigest, cancellationToken);
        var position = await dbContext.BudgetPositions.SingleOrDefaultAsync(
            record => record.OrganizationId == organizationId &&
                      record.PositionKeyDigest == positionKeyDigest,
            cancellationToken);
        var occurredAt = DateTimeOffset.UtcNow;
        if (position is null)
        {
            if (expectedVersion is not null)
            {
                throw new DomainConflictException("The budget position does not exist yet.");
            }

            position = new BudgetPositionRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                CostCenterId = costCenterId,
                FiscalYear = fiscalYear,
                SpendCategoryCode = normalizedCode,
                PositionKeyDigest = positionKeyDigest,
                CurrentAllocationVersion = 1,
                CurrentBalanceVersion = 1
            };
            dbContext.BudgetPositions.Add(position);
            dbContext.BudgetAllocationVersions.Add(new BudgetAllocationVersionRecord
            {
                PositionId = position.Id,
                Version = 1,
                OrganizationId = organizationId,
                CostCenterId = payload.CostCenter.Id,
                CostCenterVersion = payload.CostCenter.Version,
                SpendCategoryCode = payload.SpendCategory.Code,
                SpendCategoryVersion = payload.SpendCategory.Version,
                SpendCategoryDigest = payload.SpendCategory.Digest,
                AllocatedAmount = cleanAmount,
                Currency = cleanCurrency,
                PredecessorVersion = null,
                ActorUserId = actorUserId,
                OccurredAt = occurredAt,
                AllocationKey = key,
                Fingerprint = fingerprint,
                Reason = cleanReason
            });
            dbContext.BudgetBalances.Add(new BudgetBalanceRecord
            {
                PositionId = position.Id,
                BalanceVersion = 1,
                AllocationVersion = 1,
                Allocated = cleanAmount,
                Reserved = 0m,
                Committed = 0m,
                Consumed = 0m
            });
            AddAudit(
                organizationId,
                actor,
                BudgetCodes.ActionAllocated,
                BudgetCodes.TargetBudgetPosition,
                position.Id,
                null,
                1,
                new { allocated = cleanAmount, currency = cleanCurrency, position_key_digest = positionKeyDigest },
                correlation,
                occurredAt);
            await SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BudgetPositionView(
                position.Id,
                position.CostCenterId,
                position.FiscalYear,
                position.SpendCategoryCode,
                1,
                1,
                cleanCurrency,
                BudgetBuckets.Create(cleanAmount, 0m, 0m, 0m));
        }

        var balance = await dbContext.BudgetBalances
            .SingleAsync(record => record.PositionId == position.Id, cancellationToken);
        var held = balance.Reserved + balance.Committed + balance.Consumed;
        if (cleanAmount < held)
        {
            throw new DomainConflictException(
                "The allocated amount cannot be reduced below the funds already reserved, committed or consumed.");
        }

        if (expectedVersion is int version)
        {
            if (version < 1)
            {
                throw new DomainValidationException("ExpectedVersion must be positive.");
            }

            var replay = await dbContext.BudgetAllocationVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.PositionId == position.Id && record.AllocationKey == key,
                    cancellationToken);
            if (replay is not null)
            {
                // Idempotent replay: the same key with the same fingerprint returns the accepted
                // revision, while a different preimage is a conflict.
                if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new DomainConflictException(
                        "The allocation key was already used with different content.");
                }

                await transaction.CommitAsync(cancellationToken);
                return new BudgetPositionView(
                    position.Id,
                    position.CostCenterId,
                    position.FiscalYear,
                    position.SpendCategoryCode,
                    replay.Version,
                    balance.BalanceVersion,
                    replay.Currency,
                    BudgetBuckets.Create(
                        replay.AllocatedAmount, balance.Reserved, balance.Committed, balance.Consumed));
            }

            if (position.CurrentAllocationVersion != version)
            {
                throw new DomainConflictException("The budget position version is stale.");
            }
        }
        else
        {
            throw new DomainValidationException("ExpectedVersion is required for an existing position.");
        }

        var nextVersion = position.CurrentAllocationVersion + 1;
        dbContext.BudgetAllocationVersions.Add(new BudgetAllocationVersionRecord
        {
            PositionId = position.Id,
            Version = nextVersion,
            OrganizationId = organizationId,
            CostCenterId = payload.CostCenter.Id,
            CostCenterVersion = payload.CostCenter.Version,
            SpendCategoryCode = payload.SpendCategory.Code,
            SpendCategoryVersion = payload.SpendCategory.Version,
            SpendCategoryDigest = payload.SpendCategory.Digest,
            AllocatedAmount = cleanAmount,
            Currency = cleanCurrency,
            PredecessorVersion = position.CurrentAllocationVersion,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            AllocationKey = key,
            Fingerprint = fingerprint,
            Reason = cleanReason
        });
        await SaveAsync(cancellationToken);
        // The append-only trigger requires the successor row before the pointer advances.
        var previousVersion = position.CurrentAllocationVersion;
        position.CurrentAllocationVersion = nextVersion;
        var allocatedDelta = cleanAmount - balance.Allocated;
        balance.Allocated = cleanAmount;
        balance.AllocationVersion = nextVersion;
        balance.BalanceVersion += 1;
        position.CurrentBalanceVersion = balance.BalanceVersion;
        AddAudit(
            organizationId,
            actor,
            BudgetCodes.ActionAllocated,
            BudgetCodes.TargetBudgetPosition,
            position.Id,
            previousVersion,
            nextVersion,
            new { allocated_delta = allocatedDelta, currency = cleanCurrency },
            correlation,
            occurredAt);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BudgetPositionView(
            position.Id,
            position.CostCenterId,
            position.FiscalYear,
            position.SpendCategoryCode,
            nextVersion,
            balance.BalanceVersion,
            cleanCurrency,
            BudgetBuckets.Create(balance.Allocated, balance.Reserved, balance.Committed, balance.Consumed));
    }

    /// <summary>
    /// Next balance version of a position after applying one movement, or null when the movement
    /// does not touch a bucket (a precheck demand never advances the projection).
    /// </summary>
    public static int? NextBalanceVersion(BudgetMovementType type, int currentBalanceVersion) =>
        type == BudgetMovementType.Requested ? null : currentBalanceVersion + 1;

    /// <summary>Total held by a projection, used to validate an ALLOCATED reduction.</summary>
    public static decimal Held(BudgetBuckets buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        return buckets.Reserved + buckets.Committed + buckets.Consumed;
    }

    private async Task<BudgetPositionPayload> ResolvePositionPayloadAsync(
        Guid organizationId,
        Guid costCenterId,
        int fiscalYear,
        string spendCategoryCode,
        string currency,
        CancellationToken cancellationToken)
    {
        _ = BudgetCodes.RequireFiscalYear(fiscalYear);
        var costCenter = await (
                from root in dbContext.CostCenters.AsNoTracking()
                join version in dbContext.CostCenterVersions.AsNoTracking()
                    on new { root.Id, Version = root.CurrentVersion } equals new
                    {
                        Id = version.CostCenterId,
                        version.Version
                    }
                where root.OrganizationId == organizationId &&
                      root.Id == costCenterId &&
                      version.Status == (int)EntityStatus.Active
                select new { version.Version })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainValidationException("The cost center is not an active position of this organization.");
        var spendCategory = await ResolveSpendCategoryAsync(organizationId, spendCategoryCode, cancellationToken);
        if (!string.Equals(currency, await BaseCurrencyAsync(organizationId, cancellationToken), StringComparison.Ordinal))
        {
            throw new DomainValidationException("Budget positions are expressed in the organization base currency.");
        }

        return BudgetPositionPayload.For(
            costCenterId,
            fiscalYear,
            new BudgetCostCenterRef(costCenterId, costCenter.Version),
            spendCategory);
    }

    private async Task<BudgetSpendCategoryRef> ResolveSpendCategoryAsync(
        Guid organizationId,
        string spendCategoryCode,
        CancellationToken cancellationToken)
    {
        var normalized = ReferenceCatalogCodes.RequireCode(spendCategoryCode, "Spend Category code");
        var row = await (
                from root in dbContext.SpendCategories.AsNoTracking()
                join version in dbContext.SpendCategoryVersions.AsNoTracking()
                    on new { root.Id, Version = root.CurrentVersion } equals new
                    {
                        Id = version.SpendCategoryId,
                        version.Version
                    }
                where root.OrganizationId == organizationId &&
                      root.Code == normalized &&
                      version.Status == (int)EntityStatus.Active
                select new { version.Version, version.Digest })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainValidationException(
                "The spend category is not an active reference of this organization.");
        return new BudgetSpendCategoryRef(normalized, row.Version, row.Digest);
    }

    private Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// Resolves the current reference of a position payload without holding a write lock; the
    /// builder and the adapter use it to attest a demand before any budget is touched (REQ-04).
    /// </summary>
    public Task<BudgetPositionPayload> ResolvePositionAsync(
        Guid organizationId,
        Guid costCenterId,
        int fiscalYear,
        string spendCategoryCode,
        string currency,
        CancellationToken cancellationToken = default) =>
        ResolvePositionPayloadAsync(
            organizationId, costCenterId, fiscalYear, spendCategoryCode, currency, cancellationToken);

    /// <summary>
    /// Serializes every writer of one position on the same application lock, in
    /// <c>position_key_digest</c> ascending order (REQ-02, NFR-02).
    /// </summary>
    public static async Task AcquirePositionLockAsync(
        ProcureToPayDbContext context,
        Guid organizationId,
        string positionKeyDigest,
        CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The budget position lock requires an open transaction that owns the critical section.");
        }

        var resource = $"budget:position:{organizationId:D}:{positionKeyDigest}";
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000",
            [resource, result],
            cancellationToken);
        if (result.Value is not int code || code < 0)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget position critical section could not be acquired.");
        }
    }

    private Task AcquirePositionLockAsync(
        Guid organizationId,
        string positionKeyDigest,
        CancellationToken cancellationToken) =>
        AcquirePositionLockAsync(dbContext, organizationId, positionKeyDigest, cancellationToken);

    /// <summary>
    /// Commits the current transaction. A concurrent writer or a duplicate key violates the unique
    /// constraints and is reported as a contract conflict (409), never as a raw EF failure.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            throw new DomainConflictException(
                $"The budget entry was modified concurrently or violates a unique constraint: {exception.InnerException?.Message}");
        }
    }

    public void AddAudit(
        Guid organizationId,
        BudgetActor actor,
        string action,
        string targetType,
        Guid targetId,
        int? previousVersion,
        int newVersion,
        object deltas,
        string correlationReference,
        DateTimeOffset occurredAt,
        Guid? causeAuditId = null,
        string? causeStream = null)
    {
        dbContext.BudgetAuditRecords.Add(new BudgetAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorJson = JsonSerializer.Serialize(actor),
            CauseStream = causeStream,
            CauseAuditId = causeAuditId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            PreviousVersion = previousVersion,
            NewVersion = newVersion,
            DeltasJson = JsonSerializer.Serialize(deltas),
            CorrelationReference = correlationReference,
            OccurredAt = occurredAt
        });
    }

    /// <summary>
    /// Validates the exclusive lock order of one batch: positions are locked by
    /// <c>position_key_digest</c> ascending, so two instances never deadlock (NFR-02).
    /// </summary>
    public static IReadOnlyList<string> OrderLocks(IEnumerable<string> digests)
    {
        ArgumentNullException.ThrowIfNull(digests);
        return digests.Distinct(StringComparer.Ordinal).OrderBy(digest => digest, StringComparer.Ordinal).ToArray();
    }
}
