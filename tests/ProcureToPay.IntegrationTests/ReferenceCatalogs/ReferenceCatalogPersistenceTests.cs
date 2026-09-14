using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

namespace ProcureToPay.IntegrationTests.ReferenceCatalogs;

/// <summary>
/// SQL Server evidence for the reference catalogs (SPEC 07 REQ-01..REQ-05, CA-01, CA-03, CA-04):
/// append-only versions, case-insensitive code uniqueness, concurrency, scope blocking and the
/// organization-bound Policy/Approval resolution.
/// </summary>
public sealed class ReferenceCatalogPersistenceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOrganizationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DepartmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherDepartmentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Create_update_deactivate_and_reactivate_keep_every_version_and_audit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "cc-it-dev", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        Assert.Equal("CC-IT-DEV", created.Code);
        Assert.Equal(1, created.Version);

        var renamed = await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 1, "Development tools", null, null, "Rename", "corr-2", cancellationToken);
        Assert.Equal(2, renamed.Version);
        Assert.Equal("Development tools", renamed.Name);

        var reassigned = await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 2, null, OtherDepartmentId, null, "Reassign", "corr-3", cancellationToken);
        Assert.Equal(3, reassigned.Version);
        Assert.Equal(OtherDepartmentId, reassigned.DepartmentId);

        var deactivated = await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 3, null, null, EntityStatus.Inactive, "Retire", "corr-4", cancellationToken);
        Assert.Equal(EntityStatus.Inactive, await harness.StatusOfAsync(created.Id, 4, cancellationToken));
        Assert.Equal(4, deactivated.Version);
        Assert.Empty(await harness.Service.ListActiveCostCentersAsync(OrganizationId, cancellationToken));

        var reactivated = await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 4, null, null, EntityStatus.Active, "Back", "corr-5", cancellationToken);
        Assert.Equal(5, reactivated.Version);
        Assert.Single(await harness.Service.ListActiveCostCentersAsync(OrganizationId, cancellationToken));

        var history = await harness.Service.ReadCostCenterHistoryAsync(OrganizationId, created.Id, cancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], history.Select(version => version.Version).ToArray());
        Assert.Equal([null, 1, 2, 3, 4], history.Select(version => version.PredecessorVersion).ToArray());
        Assert.Equal(
            ["ACTIVE", "ACTIVE", "ACTIVE", "INACTIVE", "ACTIVE"],
            history.Select(version => version.Status).ToArray());
        await using var verification = harness.CreateContext();
        Assert.Equal(5, await verification.AdministrativeAuditRecords
            .CountAsync(record => record.TargetId == created.Id, cancellationToken));
        Assert.Equal(1, await verification.CostCenters.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Duplicate_codes_and_stale_versions_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "cc-it-dev", "Duplicate", DepartmentId, "Again", "corr-2", cancellationToken));
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 99, "Stale", null, null, "Stale", "corr-3", cancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() => harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "1-CC", "Bad code", DepartmentId, "Bad", "corr-4", cancellationToken));

        var category = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", "Hardware", "Initial", "corr-5", cancellationToken);
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "hardware", "Duplicate", "Again", "corr-6", cancellationToken));
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Service.UpdateSpendCategoryAsync(
            OrganizationId, ActorId, category.Code, 7, "Stale", null, "Stale", "corr-7", cancellationToken));

        // The same code is independent in another organization: the unique index is per organization.
        await harness.Service.CreateCostCenterAsync(
            OtherOrganizationId, ActorId, "CC-IT-DEV", "Development", harness.OtherOrganizationDepartmentId,
            "Other org", "corr-8", cancellationToken);
    }

    [Fact]
    public async Task Concurrent_updates_produce_exactly_one_successor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);

        await using var first = harness.CreateContext();
        await using var second = harness.CreateContext();
        var firstService = new ReferenceCatalogPersistenceService(first);
        var secondService = new ReferenceCatalogPersistenceService(second);
        var outcomes = await Task.WhenAll(
            CaptureAsync(() => firstService.UpdateCostCenterAsync(
                OrganizationId, ActorId, created.Id, 1, "First", null, null, "Race", "corr-a", cancellationToken)),
            CaptureAsync(() => secondService.UpdateCostCenterAsync(
                OrganizationId, ActorId, created.Id, 1, "Second", null, null, "Race", "corr-b", cancellationToken)));
        Assert.True(
            outcomes.Count(outcome => outcome is null) == 1,
            string.Join(
                " | ",
                outcomes.Select(outcome => outcome is null ? "null" : $"{outcome.GetType().Name}: {outcome.Message}")));
        Assert.Equal(1, outcomes.Count(outcome => outcome is DomainConflictException));

        await using var verification = harness.CreateContext();
        Assert.Equal(2, await verification.CostCenterVersions
            .CountAsync(version => version.CostCenterId == created.Id, cancellationToken));
        var root = await verification.CostCenters.SingleAsync(
            record => record.Id == created.Id, cancellationToken);
        Assert.Equal(2, root.CurrentVersion);
    }

    [Fact]
    public async Task Deactivation_is_blocked_by_active_scopes_and_allowed_after_the_revoke()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);

        await using (var seeding = harness.CreateContext())
        {
            seeding.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = await harness.EnsureUserAsync(cancellationToken),
                Role = (int)SystemRole.FinanceApprover,
                ScopeJson = "[{\"dimension\":\"COST_CENTER\",\"reference\":\"cc-it-dev\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ActorId,
                Version = 1
            });
            await seeding.SaveChangesAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 1, null, null, EntityStatus.Inactive, "Retire", "corr-2", cancellationToken));

        await using (var revoking = harness.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = DateTimeOffset.UtcNow;
            assignment.RevokedBy = ActorId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var deactivated = await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 1, null, null, EntityStatus.Inactive, "Retire", "corr-3", cancellationToken);
        Assert.Equal(2, deactivated.Version);
    }

    [Fact]
    public async Task Reactivation_requires_an_active_owning_department()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        await harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 1, null, null, EntityStatus.Inactive, "Retire", "corr-2", cancellationToken);
        await using (var retiring = harness.CreateContext())
        {
            retiring.Departments.Single(record => record.Id == DepartmentId).Status = (int)EntityStatus.Inactive;
            await retiring.SaveChangesAsync(cancellationToken);
        }

        // Reactivating without an active owning department is an invalid relation, not a success.
        await Assert.ThrowsAsync<DomainValidationException>(() => harness.Service.UpdateCostCenterAsync(
            OrganizationId, ActorId, created.Id, 2, null, null, EntityStatus.Active, "Reactivate", "corr-3", cancellationToken));
        Assert.Empty(await harness.Service.ListActiveCostCentersAsync(OrganizationId, cancellationToken));
    }

    [Fact]
    public async Task Invalid_lookup_shapes_never_resolve()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var costCenter = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        var category = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", "Hardware", "Initial", "corr-2", cancellationToken);

        await using var context = harness.CreateContext();
        var costCenterCatalog = new CostCenterPolicyReferenceCatalog(context);
        // COST_CENTER only accepts id/version; code, digest and value_kind must be null.
        Assert.False(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OrganizationId, costCenter.Id, 1, null, "CC-IT-DEV", null),
            cancellationToken));
        Assert.False(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OrganizationId, costCenter.Id, 1, category.Digest, null, null),
            cancellationToken));
        Assert.False(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OrganizationId, costCenter.Id, 1, null, null, "BOOLEAN"),
            cancellationToken));

        var categoryCatalog = new SpendCategoryPolicyReferenceCatalog(context);
        // SPEND_CATEGORY only accepts code/version/digest; id and value_kind must be null.
        Assert.False(await categoryCatalog.ExistsAsync(
            new PolicyReferenceLookup(
                "SPEND_CATEGORY", OrganizationId, costCenter.Id, 1, category.Digest, "HARDWARE", null),
            cancellationToken));
        Assert.False(await categoryCatalog.ExistsAsync(
            new PolicyReferenceLookup(
                "SPEND_CATEGORY", OrganizationId, null, 1, category.Digest, "HARDWARE", "ENUM_CODE"),
            cancellationToken));
    }

    [Fact]
    public async Task Append_only_history_rejects_direct_sql_mutations_and_version_jumps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var costCenter = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        var category = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", "Hardware", "Initial", "corr-2", cancellationToken);

        await using var context = harness.CreateContext();
        // A skipped version or a wrong predecessor violates the monotonic check constraint.
        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [ReferenceCatalog].[CostCenterVersions] " +
            "([CostCenterId],[Version],[OrganizationId],[Name],[DepartmentId],[Status],[PredecessorVersion]," +
            "[ActorUserId],[OccurredAt],[Reason]) VALUES " +
            $"('{costCenter.Id}',3,'{OrganizationId}','Jump','{DepartmentId}',1,1,'{ActorId}',SYSDATETIMEOFFSET(),'Jump')",
            cancellationToken));
        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"UPDATE [ReferenceCatalog].[CostCenterVersions] SET [Name] = 'Rewritten' " +
            $"WHERE [CostCenterId] = '{costCenter.Id}' AND [Version] = 1",
            cancellationToken));
        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM [ReferenceCatalog].[SpendCategoryVersions] WHERE [SpendCategoryId] = " +
            $"(SELECT TOP(1) [Id] FROM [ReferenceCatalog].[SpendCategories] WHERE [Code] = '{category.Code}')",
            cancellationToken));
        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"UPDATE [ReferenceCatalog].[CostCenters] SET [Code] = 'CC-OTHER' WHERE [Id] = '{costCenter.Id}'",
            cancellationToken));
        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"UPDATE [ReferenceCatalog].[CostCenters] SET [CurrentVersion] = 0 WHERE [Id] = '{costCenter.Id}'",
            cancellationToken));
    }

    [Fact]
    public async Task Deactivation_and_scope_creation_cannot_race_into_an_invalid_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        var actor = await harness.EnsureUserAsync(cancellationToken);

        var deactivation = CaptureAsync(async () =>
        {
            await using var context = harness.CreateContext();
            await new ReferenceCatalogPersistenceService(context).UpdateCostCenterAsync(
                OrganizationId, ActorId, created.Id, 1, null, null, EntityStatus.Inactive, "Retire", "corr-2", cancellationToken);
        });
        var scopeCreation = CaptureAsync(async () =>
        {
            await using var context = harness.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);
            // Same validation the ADMIN path performs before persisting an assignment.
            var resolved = await new ReferenceCatalogPersistenceService(context)
                .FindActiveCostCenterByCodeAsync("CC-IT-DEV", OrganizationId, cancellationToken);
            if (resolved is null)
            {
                throw new DomainValidationException("The scoped reference is not active.");
            }

            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = actor,
                Role = (int)SystemRole.FinanceApprover,
                ScopeJson = "[{\"dimension\":\"COST_CENTER\",\"reference\":\"CC-IT-DEV\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ActorId,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        await Task.WhenAll(deactivation, scopeCreation);

        await using var verification = harness.CreateContext();
        var status = await verification.CostCenterVersions
            .Where(record => record.CostCenterId == created.Id)
            .OrderByDescending(record => record.Version)
            .Select(record => record.Status)
            .FirstAsync(cancellationToken);
        var activeScope = await verification.RoleAssignments.AnyAsync(
            record => record.Status == (int)AssignmentStatus.Active,
            cancellationToken);
        // The only two valid outcomes: an active scope keeps the catalog active, or the retired
        // catalog rejected the scope. A live scope must never point at an inactive cost center.
        Assert.False(
            status == (int)EntityStatus.Inactive && activeScope,
            "A live COST_CENTER scope points at an inactive cost center.");
    }

    [Fact]
    public async Task Spend_category_digests_reproduce_for_every_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", "Hardware", "Initial", "corr-1", cancellationToken);
        Assert.Equal(
            ReferenceCatalogCanonicalizer.SpendCategoryDigest("HARDWARE", "Hardware", OrganizationId, 1),
            created.Digest);

        var renamed = await harness.Service.UpdateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", 1, "Hardware and peripherals", null, "Rename", "corr-2", cancellationToken);
        Assert.Equal(2, renamed.Version);
        Assert.Equal(
            ReferenceCatalogCanonicalizer.SpendCategoryDigest(
                "HARDWARE", "Hardware and peripherals", OrganizationId, 2),
            renamed.Digest);

        await using var verification = harness.CreateContext();
        var stored = await verification.SpendCategoryVersions
            .OrderBy(version => version.Version)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, version => Assert.True(ReferenceCatalogCanonicalizer.MatchesSpendCategoryDigest(
            version.Code, version.Name, version.OrganizationId, version.Version, version.Digest)));
    }

    [Fact]
    public async Task Cost_center_projection_resolves_the_current_department_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var created = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        await using (var renaming = harness.CreateContext())
        {
            await renaming.Departments
                .Where(record => record.Id == DepartmentId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(record => record.Version, record => record.Version + 1),
                    cancellationToken);
        }

        var projected = await harness.Service.FindActiveCostCenterByCodeAsync(
            "cc-it-dev", OrganizationId, cancellationToken);
        Assert.NotNull(projected);
        Assert.Equal(2, projected!.DepartmentVersion);
        Assert.Equal(created.Id, projected.Id);
    }

    [Fact]
    public async Task Policy_and_approval_resolution_is_organization_bound_and_fail_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);
        var costCenter = await harness.Service.CreateCostCenterAsync(
            OrganizationId, ActorId, "CC-IT-DEV", "Development", DepartmentId, "Initial", "corr-1", cancellationToken);
        var category = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, ActorId, "HARDWARE", "Hardware", "Initial", "corr-2", cancellationToken);

        await using var context = harness.CreateContext();
        var costCenterCatalog = new CostCenterPolicyReferenceCatalog(context);
        var categoryCatalog = new SpendCategoryPolicyReferenceCatalog(context);
        Assert.True(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OrganizationId, costCenter.Id, 1, null, null, null),
            cancellationToken));
        Assert.False(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OrganizationId, costCenter.Id, 2, null, null, null),
            cancellationToken));
        Assert.False(await costCenterCatalog.ExistsAsync(
            new PolicyReferenceLookup("COST_CENTER", OtherOrganizationId, costCenter.Id, 1, null, null, null),
            cancellationToken));
        Assert.True(await categoryCatalog.ExistsAsync(
            new PolicyReferenceLookup(
                "SPEND_CATEGORY", OrganizationId, null, 1, category.Digest, "HARDWARE", null),
            cancellationToken));
        Assert.False(await categoryCatalog.ExistsAsync(
            new PolicyReferenceLookup(
                "SPEND_CATEGORY", OrganizationId, null, 1, new string('a', 64), "HARDWARE", null),
            cancellationToken));
        Assert.False(await categoryCatalog.ExistsAsync(
            new PolicyReferenceLookup(
                "SPEND_CATEGORY", OrganizationId, null, 2, category.Digest, "HARDWARE", null),
            cancellationToken));

        // A frozen decision scope resolves to the stable code; a stale version fails closed.
        var resolver = new ApprovalScopeResolver(context);
        var descriptor = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.CostCenter, costCenter.Id, 1)]);
        var resolved = await resolver.ResolveAsync(descriptor, OrganizationId, cancellationToken);
        var scope = Assert.Single(resolved.Scopes);
        Assert.Equal(ScopeDimension.CostCenter, scope.Dimension);
        Assert.Equal("CC-IT-DEV", scope.Reference);

        var stale = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.CostCenter, costCenter.Id, 2)]);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            resolver.ResolveAsync(stale, OrganizationId, cancellationToken));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
