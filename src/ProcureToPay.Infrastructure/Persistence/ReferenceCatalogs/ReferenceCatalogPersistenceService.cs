using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

/// <summary>
/// Versioned persistence and administration of the Cost Center and Spend Category catalogs
/// (SPEC 07 REQ-01..REQ-03, REQ-08). Every mutation appends one version, moves the current pointer
/// atomically and writes its administrative audit in the same transaction.
/// </summary>
public sealed class ReferenceCatalogPersistenceService(ProcureToPayDbContext dbContext)
{
    public async Task<CostCenterReference> CreateCostCenterAsync(
        Guid organizationId,
        Guid actorUserId,
        string? code,
        string? name,
        Guid departmentId,
        string? reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = ReferenceCatalogCodes.RequireCode(code, "Code");
        var normalizedName = ReferenceCatalogCodes.RequireName(name, "Name");
        var cleanReason = ReferenceCatalogCodes.RequireReason(reason);
        var department = await RequireActiveDepartmentAsync(organizationId, departmentId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await dbContext.CostCenters.AnyAsync(
                record => record.OrganizationId == organizationId && record.Code == normalizedCode,
                cancellationToken))
        {
            throw new DomainConflictException("The cost center code is already in use.");
        }

        var root = new CostCenterRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Code = normalizedCode,
            CurrentVersion = 1
        };
        var occurredAt = DateTimeOffset.UtcNow;
        dbContext.CostCenters.Add(root);
        dbContext.CostCenterVersions.Add(new CostCenterVersionRecord
        {
            CostCenterId = root.Id,
            Version = 1,
            OrganizationId = organizationId,
            Name = normalizedName,
            DepartmentId = department.Id,
            Status = (int)EntityStatus.Active,
            PredecessorVersion = null,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = cleanReason
        });
        AddAudit(
            actorUserId,
            ReferenceCatalogCodes.ActionCostCenterCreated,
            ReferenceCatalogCodes.TargetCostCenter,
            root.Id,
            null,
            1,
            ScopeJson(ReferenceCatalogCodes.CostCenterCatalog, root.Code),
            null,
            SerializeVersion(normalizedName, department.Id, EntityStatus.Active),
            cleanReason,
            correlationReference,
            occurredAt);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CostCenterReference(root.Id, root.Code, normalizedName, 1, department.Id, department.Version);
    }

    public async Task<CostCenterReference> UpdateCostCenterAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid costCenterId,
        int expectedVersion,
        string? name,
        Guid? departmentId,
        EntityStatus? status,
        string? reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var cleanReason = ReferenceCatalogCodes.RequireReason(reason);
        if (expectedVersion < 1)
        {
            throw new DomainValidationException("ExpectedVersion is required.");
        }

        // SPEC 07 NFR-03/REQ-04: the deactivation invariant and the creation of a scope that
        // references this cost center must serialize on the same root row. Serializable isolation
        // plus a locking read keeps a concurrent assignment from validating a catalog entry that
        // this transaction is retiring.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        // Executes a locking read on the root row; the tracked entity is loaded afterwards.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT [Id] FROM [ReferenceCatalog].[CostCenters] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {costCenterId}",
            cancellationToken);
        var root = await dbContext.CostCenters.SingleOrDefaultAsync(
                record => record.Id == costCenterId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The cost center was not found.");
        if (root.CurrentVersion != expectedVersion)
        {
            throw new DomainConflictException("The cost center version is stale.");
        }

        var current = await dbContext.CostCenterVersions.SingleAsync(
            record => record.CostCenterId == root.Id && record.Version == expectedVersion,
            cancellationToken);
        var successorName = name is null
            ? current.Name
            : ReferenceCatalogCodes.RequireName(name, "Name");
        var successorDepartmentId = current.DepartmentId;
        if (departmentId is Guid requestedDepartmentId && requestedDepartmentId != current.DepartmentId)
        {
            var department = await RequireActiveDepartmentAsync(organizationId, requestedDepartmentId, cancellationToken);
            successorDepartmentId = department.Id;
        }

        var successorStatus = status ?? (EntityStatus)current.Status;
        if (successorStatus == EntityStatus.Inactive && current.Status == (int)EntityStatus.Active)
        {
            // The check runs after the locking read: a scope committed meanwhile is visible here.
            await EnsureCostCenterHasNoActiveScopesAsync(
                root.Code, "The cost center has active scopes and cannot be deactivated.", cancellationToken);
        }

        // A Cost Center can only be Active while its owning Department is current and active
        // (SPEC 07 REQ-02): reactivating after the Department was retired must fail closed.
        var successorDepartment = await dbContext.Departments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == successorDepartmentId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainValidationException("The owning department is not active in this organization.");
        if (successorStatus == EntityStatus.Active && successorDepartment.Status != (int)EntityStatus.Active)
        {
            throw new DomainValidationException("The owning department is not active in this organization.");
        }

        var departmentVersion = successorDepartment.Version;
        var occurredAt = DateTimeOffset.UtcNow;
        var nextVersion = expectedVersion + 1;
        dbContext.CostCenterVersions.Add(new CostCenterVersionRecord
        {
            CostCenterId = root.Id,
            Version = nextVersion,
            OrganizationId = organizationId,
            Name = successorName,
            DepartmentId = successorDepartmentId,
            Status = (int)successorStatus,
            PredecessorVersion = expectedVersion,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = cleanReason
        });
        root.CurrentVersion = nextVersion;
        AddAudit(
            actorUserId,
            ReferenceCatalogCodes.ActionCostCenterUpdated,
            ReferenceCatalogCodes.TargetCostCenter,
            root.Id,
            expectedVersion,
            nextVersion,
            ScopeJson(ReferenceCatalogCodes.CostCenterCatalog, root.Code),
            SerializeVersion(current.Name, current.DepartmentId, current.Status),
            SerializeVersion(successorName, successorDepartmentId, successorStatus),
            cleanReason,
            correlationReference,
            occurredAt);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CostCenterReference(
            root.Id, root.Code, successorName, nextVersion, successorDepartmentId, departmentVersion);
    }

    public async Task<SpendCategoryReference> CreateSpendCategoryAsync(
        Guid organizationId,
        Guid actorUserId,
        string? code,
        string? name,
        string? reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = ReferenceCatalogCodes.RequireCode(code, "Code");
        var normalizedName = ReferenceCatalogCodes.RequireName(name, "Name");
        var cleanReason = ReferenceCatalogCodes.RequireReason(reason);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await dbContext.SpendCategories.AnyAsync(
                record => record.OrganizationId == organizationId && record.Code == normalizedCode,
                cancellationToken))
        {
            throw new DomainConflictException("The spend category code is already in use.");
        }

        var root = new SpendCategoryRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Code = normalizedCode,
            CurrentVersion = 1
        };
        var digest = ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            normalizedCode, normalizedName, organizationId, 1);
        var occurredAt = DateTimeOffset.UtcNow;
        dbContext.SpendCategories.Add(root);
        dbContext.SpendCategoryVersions.Add(new SpendCategoryVersionRecord
        {
            SpendCategoryId = root.Id,
            Version = 1,
            OrganizationId = organizationId,
            Code = normalizedCode,
            Name = normalizedName,
            Digest = digest,
            Status = (int)EntityStatus.Active,
            PredecessorVersion = null,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = cleanReason
        });
        AddAudit(
            actorUserId,
            ReferenceCatalogCodes.ActionSpendCategoryCreated,
            ReferenceCatalogCodes.TargetSpendCategory,
            root.Id,
            null,
            1,
            ScopeJson(ReferenceCatalogCodes.SpendCategoryCatalog, normalizedCode),
            null,
            SerializeVersion(normalizedName, digest, EntityStatus.Active),
            cleanReason,
            correlationReference,
            occurredAt);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SpendCategoryReference(normalizedCode, normalizedName, 1, digest);
    }

    public async Task<SpendCategoryReference> UpdateSpendCategoryAsync(
        Guid organizationId,
        Guid actorUserId,
        string? code,
        int expectedVersion,
        string? name,
        EntityStatus? status,
        string? reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = ReferenceCatalogCodes.RequireCode(code, "Code");
        var cleanReason = ReferenceCatalogCodes.RequireReason(reason);
        if (expectedVersion < 1)
        {
            throw new DomainValidationException("ExpectedVersion is required.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var root = await dbContext.SpendCategories.SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId && record.Code == normalizedCode,
                cancellationToken)
            ?? throw new DomainNotFoundException("The spend category was not found.");
        if (root.CurrentVersion != expectedVersion)
        {
            throw new DomainConflictException("The spend category version is stale.");
        }

        var current = await dbContext.SpendCategoryVersions.SingleAsync(
            record => record.SpendCategoryId == root.Id && record.Version == expectedVersion,
            cancellationToken);
        var successorName = name is null
            ? current.Name
            : ReferenceCatalogCodes.RequireName(name, "Name");
        var successorStatus = status ?? (EntityStatus)current.Status;
        var nextVersion = expectedVersion + 1;
        var digest = ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            normalizedCode, successorName, organizationId, nextVersion);
        var occurredAt = DateTimeOffset.UtcNow;
        dbContext.SpendCategoryVersions.Add(new SpendCategoryVersionRecord
        {
            SpendCategoryId = root.Id,
            Version = nextVersion,
            OrganizationId = organizationId,
            Code = normalizedCode,
            Name = successorName,
            Digest = digest,
            Status = (int)successorStatus,
            PredecessorVersion = expectedVersion,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = cleanReason
        });
        root.CurrentVersion = nextVersion;
        AddAudit(
            actorUserId,
            ReferenceCatalogCodes.ActionSpendCategoryUpdated,
            ReferenceCatalogCodes.TargetSpendCategory,
            root.Id,
            expectedVersion,
            nextVersion,
            ScopeJson(ReferenceCatalogCodes.SpendCategoryCatalog, normalizedCode),
            SerializeVersion(current.Name, current.Digest, (EntityStatus)current.Status),
            SerializeVersion(successorName, digest, successorStatus),
            cleanReason,
            correlationReference,
            occurredAt);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SpendCategoryReference(normalizedCode, successorName, nextVersion, digest);
    }

    public async Task<IReadOnlyList<CostCenterReference>> ListActiveCostCentersAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ActiveCostCenters(organizationId).ToArrayAsync(cancellationToken);
        return rows
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .Select(row => new CostCenterReference(
                row.Id, row.Code, row.Name, row.Version, row.DepartmentId, row.DepartmentVersion))
            .ToArray();
    }

    public async Task<IReadOnlyList<SpendCategoryReference>> ListActiveSpendCategoriesAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ActiveSpendCategories(organizationId).ToArrayAsync(cancellationToken);
        return rows
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .Select(row => new SpendCategoryReference(row.Code, row.Name, row.Version, row.Digest))
            .ToArray();
    }

    /// <summary>Active and current Cost Center resolved by its stable code, used to validate scopes.</summary>
    public async Task<CostCenterReference?> FindActiveCostCenterByCodeAsync(
        string? code,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = ReferenceCatalogCodes.RequireCode(code, "Code");
        var row = await (
                from root in dbContext.CostCenters.AsNoTracking()
                join version in dbContext.CostCenterVersions.AsNoTracking()
                    on new { root.Id, Version = root.CurrentVersion } equals new
                    {
                        Id = version.CostCenterId,
                        version.Version
                    }
                join department in dbContext.Departments.AsNoTracking()
                    on version.DepartmentId equals department.Id
                where root.OrganizationId == organizationId &&
                      root.Code == normalized &&
                      version.Status == (int)EntityStatus.Active
                select new CostCenterRow(
                    root.Id, root.Code, version.Name, version.Version, department.Id, department.Version))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new CostCenterReference(
                row.Id, row.Code, row.Name, row.Version, row.DepartmentId, row.DepartmentVersion);
    }

    public async Task<IReadOnlyList<CostCenterVersionView>> ReadCostCenterHistoryAsync(
        Guid organizationId,
        Guid costCenterId,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.CostCenters.AnyAsync(
            root => root.Id == costCenterId && root.OrganizationId == organizationId,
            cancellationToken);
        if (!exists)
        {
            throw new DomainNotFoundException("The cost center was not found.");
        }

        var rows = await dbContext.CostCenterVersions
            .AsNoTracking()
            .Where(version => version.CostCenterId == costCenterId)
            .OrderBy(version => version.Version)
            .ToArrayAsync(cancellationToken);
        return rows.Select(version => new CostCenterVersionView(
            version.Version,
            version.Name,
            version.DepartmentId,
            OrganizationContractCodes.Status((EntityStatus)version.Status),
            version.PredecessorVersion,
            version.OccurredAt,
            version.ActorUserId,
            version.Reason)).ToArray();
    }

    public async Task<IReadOnlyList<SpendCategoryVersionView>> ReadSpendCategoryHistoryAsync(
        Guid organizationId,
        string? code,
        CancellationToken cancellationToken = default)
    {
        var normalized = ReferenceCatalogCodes.RequireCode(code, "Code");
        var root = await dbContext.SpendCategories
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId && record.Code == normalized,
                cancellationToken)
            ?? throw new DomainNotFoundException("The spend category was not found.");
        var rows = await dbContext.SpendCategoryVersions
            .AsNoTracking()
            .Where(version => version.SpendCategoryId == root.Id)
            .OrderBy(version => version.Version)
            .ToArrayAsync(cancellationToken);
        return rows.Select(version => new SpendCategoryVersionView(
            version.Version,
            version.Code,
            version.Name,
            version.Digest,
            OrganizationContractCodes.Status((EntityStatus)version.Status),
            version.PredecessorVersion,
            version.OccurredAt,
            version.ActorUserId,
            version.Reason)).ToArray();
    }

    /// <summary>
    /// True when a non-revoked assignment or grant declares this Cost Center code; such a catalog
    /// entry cannot be deactivated until those scopes are revoked (REQ-04).
    /// </summary>
    public async Task<bool> HasActiveScopeReferencesAsync(
        string? code,
        CancellationToken cancellationToken = default)
    {
        var normalized = ReferenceCatalogCodes.RequireCode(code, "Code");
        var assignmentScopes = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(record => record.Status == (int)AssignmentStatus.Active)
            .Select(record => record.ScopeJson)
            .ToArrayAsync(cancellationToken);
        var grantScopes = await dbContext.AuthorityGrants
            .AsNoTracking()
            .Where(record => record.Status == (int)GrantStatus.Active)
            .Select(record => record.ScopeJson)
            .ToArrayAsync(cancellationToken);
        return assignmentScopes.Concat(grantScopes).Any(scope => ReferencesCode(scope, normalized));
    }

    private async Task EnsureCostCenterHasNoActiveScopesAsync(
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        if (await HasActiveScopeReferencesAsync(code, cancellationToken))
        {
            throw new DomainConflictException(message);
        }
    }

    private IQueryable<CostCenterRow> ActiveCostCenters(Guid organizationId) =>
        from root in dbContext.CostCenters.AsNoTracking()
        join version in dbContext.CostCenterVersions.AsNoTracking()
            on new { root.Id, Version = root.CurrentVersion } equals new { Id = version.CostCenterId, version.Version }
        join department in dbContext.Departments.AsNoTracking() on version.DepartmentId equals department.Id
        where root.OrganizationId == organizationId && version.Status == (int)EntityStatus.Active
        select new CostCenterRow(
            root.Id, root.Code, version.Name, version.Version, department.Id, department.Version);

    private IQueryable<SpendCategoryRow> ActiveSpendCategories(Guid organizationId) =>
        from root in dbContext.SpendCategories.AsNoTracking()
        join version in dbContext.SpendCategoryVersions.AsNoTracking()
            on new { root.Id, Version = root.CurrentVersion } equals new
            {
                Id = version.SpendCategoryId,
                version.Version
            }
        where root.OrganizationId == organizationId && version.Status == (int)EntityStatus.Active
        select new SpendCategoryRow(version.Code, version.Name, version.Version, version.Digest);

    /// <summary>
    /// Commits the current transaction. A concurrent writer or a duplicate code violates the
    /// unique constraints and is reported as a contract conflict (409), never as a raw EF failure.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new DomainConflictException(
                "The reference catalog entry was modified concurrently or violates a unique constraint.");
        }
    }

    private async Task<DepartmentRecord> RequireActiveDepartmentAsync(
        Guid organizationId,
        Guid departmentId,
        CancellationToken cancellationToken)
    {
        if (departmentId == Guid.Empty)
        {
            throw new DomainValidationException("A cost center requires an owning department.");
        }

        var department = await dbContext.Departments.SingleOrDefaultAsync(
                record => record.Id == departmentId &&
                          record.OrganizationId == organizationId &&
                          record.Status == (int)EntityStatus.Active,
                cancellationToken)
            ?? throw new DomainValidationException("The owning department is not active in this organization.");
        return department;
    }

    private void AddAudit(
        Guid actorUserId,
        string action,
        string targetType,
        Guid targetId,
        int? previousVersion,
        int newVersion,
        string scopeJson,
        string? beforeJson,
        string afterJson,
        string reason,
        string correlationReference,
        DateTimeOffset occurredAt)
    {
        dbContext.AdministrativeAuditRecords.Add(new AdministrativeAuditRecord
        {
            Id = Guid.NewGuid(),
            ActorType = "USER",
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            PreviousVersion = previousVersion,
            NewVersion = newVersion,
            ScopeJson = scopeJson,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            CorrelationReference = correlationReference
        });
    }

    private static bool ReferencesCode(string scopeJson, string code)
    {
        try
        {
            return (JsonSerializer.Deserialize<ScopeEntry[]>(scopeJson, ScopeJsonOptions) ?? [])
                .Any(entry =>
                    string.Equals(entry.Dimension, ReferenceCatalogCodes.CostCenterCatalog,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Reference, code, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ScopeJson(string dimension, string reference) =>
        JsonSerializer.Serialize(new[] { new { dimension, reference } });

    private static string SerializeVersion(string? name, Guid departmentId, EntityStatus status) =>
        JsonSerializer.Serialize(new
        {
            name,
            department_id = departmentId,
            status = OrganizationContractCodes.Status(status)
        });

    private static string SerializeVersion(string? name, string digest, EntityStatus status) =>
        JsonSerializer.Serialize(new
        {
            name,
            digest,
            status = OrganizationContractCodes.Status(status)
        });

    private static string SerializeVersion(string name, Guid departmentId, int status) =>
        SerializeVersion(name, departmentId, (EntityStatus)status);

    private static string SerializeVersion(string name, string digest, int status) =>
        SerializeVersion(name, digest, (EntityStatus)status);

    private sealed record ScopeEntry(string? Dimension, string? Reference);

    private sealed record CostCenterRow(
        Guid Id,
        string Code,
        string Name,
        int Version,
        Guid DepartmentId,
        int DepartmentVersion);

    private sealed record SpendCategoryRow(string Code, string Name, int Version, string Digest);

    private static readonly JsonSerializerOptions ScopeJsonOptions = new(JsonSerializerDefaults.Web);
}
