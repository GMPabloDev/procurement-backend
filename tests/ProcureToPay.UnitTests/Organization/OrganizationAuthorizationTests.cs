using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using OrganizationEntity = ProcureToPay.Domain.Modules.Organization.Organization;

namespace ProcureToPay.UnitTests.Organization;

public sealed class OrganizationAuthorizationTests
{
    [Fact]
    public void Organization_rejects_invalid_configuration_and_keeps_immutable_fields()
    {
        var id = Guid.NewGuid();
        var organization = new OrganizationEntity(id, "PE", "Acme", "PEN", "America/Lima", 1);

        organization.UpdateProfile("Acme Corp", "UTC");

        Assert.Equal("PE", organization.Code);
        Assert.Equal("PEN", organization.BaseCurrency);
        Assert.Equal(1, organization.FiscalYearStartMonth);
        Assert.Equal("Acme Corp", organization.Name);
        Assert.Equal("UTC", organization.TimeZoneId);
        Assert.Throws<DomainValidationException>(() => new OrganizationEntity(
            id,
            "PE",
            "Acme",
            "PEN",
            "America/Lima",
            13));
    }

    [Fact]
    public void Department_cannot_deactivate_with_active_references_but_can_reactivate()
    {
        var department = new Department(Guid.NewGuid(), "IT", "Software / IT");

        Assert.Throws<DomainConflictException>(() => department.Deactivate(true, false));
        Assert.Throws<DomainConflictException>(() => department.Deactivate(false, true));

        department.Deactivate(false, false);
        Assert.Equal(EntityStatus.Inactive, department.Status);
        department.Reactivate();
        Assert.Equal(EntityStatus.Active, department.Status);
    }

    [Fact]
    public void User_requires_setup_and_returns_without_restoring_privileges()
    {
        var user = new UserProfile(Guid.NewGuid(), "https://issuer", "subject-1", "a@acme.test", "Alice");
        var departmentId = Guid.NewGuid();

        Assert.Equal(UserProfileStatus.PendingSetup, user.Status);
        Assert.Throws<DomainValidationException>(() => user.Activate(true));

        user.CompleteSetup(departmentId, "Finance Analyst");
        user.Activate(true);
        user.Deactivate();
        user.ReturnToSetup();

        Assert.Equal(UserProfileStatus.PendingSetup, user.Status);
        Assert.Equal(departmentId, user.DepartmentId);
        Assert.Equal("Finance Analyst", user.JobTitle);
    }

    [Fact]
    public void User_transitions_reject_setup_or_activation_from_wrong_state()
    {
        var user = new UserProfile(Guid.NewGuid(), "https://issuer", "subject-2", null, null);
        user.CompleteSetup(Guid.NewGuid(), "Analyst");
        user.Activate(true);

        Assert.Throws<DomainConflictException>(() => user.CompleteSetup(Guid.NewGuid(), "Changed"));
        Assert.Throws<DomainConflictException>(() => user.Activate(true));

        user.Deactivate();
        Assert.Throws<DomainConflictException>(() => user.Activate(true));
    }

    [Fact]
    public void Scope_set_requires_explicit_non_empty_scope_and_accepts_cost_centers()
    {
        Assert.Throws<DomainValidationException>(() => AuthorizationScopeSet.Create([]));
        // SPEC 07 REQ-04: COST_CENTER is now a first-class dimension with an exact code.
        var costCenter = AuthorizationScopeSet.Create([
            AuthorizationScope.For(ScopeDimension.CostCenter, "CC-IT-DEV")
        ]);

        var global = AuthorizationScopeSet.Create([AuthorizationScope.Global()]);
        var department = AuthorizationScopeSet.Create([
            AuthorizationScope.For(ScopeDimension.Department, "IT")
        ]);
        var sameCostCenter = AuthorizationScopeSet.Create([
            AuthorizationScope.For(ScopeDimension.CostCenter, "cc-it-dev")
        ]);
        var otherCostCenter = AuthorizationScopeSet.Create([
            AuthorizationScope.For(ScopeDimension.CostCenter, "CC-IT-INFRA")
        ]);

        Assert.True(global.Covers(department));
        Assert.False(department.Covers(global));
        Assert.True(global.Overlaps(department));
        Assert.True(global.Covers(costCenter));
        // Department and Cost Center never cover each other implicitly (DEC-04).
        Assert.False(department.Covers(costCenter));
        Assert.False(costCenter.Covers(department));
        Assert.True(costCenter.Covers(sameCostCenter));
        Assert.False(costCenter.Covers(otherCostCenter));
    }

    [Fact]
    public void Role_assignments_are_scoped_and_must_not_overlap()
    {
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var first = RoleAssignment.Create(
            Guid.NewGuid(),
            userId,
            SystemRole.DepartmentApprover,
            AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]),
            DateTimeOffset.UtcNow,
            actorId);
        var overlapping = RoleAssignment.Create(
            Guid.NewGuid(),
            userId,
            SystemRole.DepartmentApprover,
            AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "it")]),
            DateTimeOffset.UtcNow,
            actorId);

        Assert.Throws<DomainConflictException>(() =>
            AuthorizationRules.EnsureNoOverlappingAssignment([first], overlapping));
    }

    [Fact]
    public void Contract_codes_are_explicit_and_reject_numeric_aliases()
    {
        Assert.True(OrganizationContractCodes.TryRole("DEPARTMENT_APPROVER", out var role));
        Assert.Equal(SystemRole.DepartmentApprover, role);
        Assert.False(OrganizationContractCodes.TryRole("2", out _));
        Assert.True(OrganizationContractCodes.TryAuthority("BUSINESS_NEED", out var authority));
        Assert.Equal(ApprovalAuthorityType.BusinessNeed, authority);
        Assert.True(OrganizationContractCodes.TryScope("LEGAL_ENTITY", out var scope));
        Assert.Equal(ScopeDimension.LegalEntity, scope);
        Assert.Equal("PENDING_SETUP", OrganizationContractCodes.Status(UserProfileStatus.PendingSetup));
    }

    [Fact]
    public void Currency_catalog_tracks_current_codes_and_rejects_retired_codes()
    {
        Assert.True(CurrencyCatalog.IsIso4217("PEN"));
        Assert.True(CurrencyCatalog.IsIso4217("ZWG"));
        Assert.True(CurrencyCatalog.IsIso4217("XCG"));
        Assert.False(CurrencyCatalog.IsIso4217("CUC"));
        Assert.False(CurrencyCatalog.IsIso4217("SLL"));
        Assert.False(CurrencyCatalog.IsIso4217("ZWL"));
    }

    [Fact]
    public void Admin_assignment_requires_organization_scope()
    {
        Assert.Throws<DomainValidationException>(() => RoleAssignment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SystemRole.Admin,
            AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]),
            DateTimeOffset.UtcNow,
            Guid.NewGuid()));
    }

    [Fact]
    public void Restored_profiles_preserve_persisted_version_and_state()
    {
        var departmentId = Guid.NewGuid();
        var profile = UserProfile.Restore(Guid.NewGuid(), "https://issuer", "subject", "a@acme.test",
            "Alice", departmentId, "Analyst", UserProfileStatus.Active, 7);

        Assert.Equal(UserProfileStatus.Active, profile.Status);
        Assert.Equal(7, profile.Version);
        Assert.Equal(departmentId, profile.DepartmentId);
    }

    [Fact]
    public void Grant_requires_active_level_and_satisfies_amount_and_validity_together()
    {
        var level = new AuthorityLevelVersion(
            Guid.NewGuid(),
            ApprovalAuthorityType.Financial,
            "FINANCE_LEVEL_2",
            2,
            1);
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var grant = ApprovalAuthorityGrant.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            level,
            20_000m,
            "PEN",
            scope,
            from,
            from.AddYears(1),
            from,
            Guid.NewGuid());

        Assert.True(grant.Covers(
            ApprovalAuthorityType.Financial,
            2,
            20_000m,
            scope,
            from.AddMonths(1)));
        Assert.False(grant.Covers(
            ApprovalAuthorityType.Financial,
            3,
            20_000m,
            scope,
            from.AddMonths(1)));
        Assert.False(grant.Covers(
            ApprovalAuthorityType.Financial,
            2,
            20_001m,
            scope,
            from.AddMonths(1)));
        Assert.False(grant.Covers(
            ApprovalAuthorityType.Financial,
            2,
            20_000m,
            scope,
            from.AddYears(1)));
    }

    [Fact]
    public void Revoked_assignment_and_grant_stop_being_effective()
    {
        var actorId = Guid.NewGuid();
        var assignment = RoleAssignment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SystemRole.FinanceApprover,
            AuthorizationScopeSet.Create([AuthorizationScope.Global()]),
            DateTimeOffset.UtcNow,
            actorId);
        assignment.Revoke(DateTimeOffset.UtcNow, actorId);

        Assert.False(assignment.Covers(AuthorizationScopeSet.Create([AuthorizationScope.Global()])));
    }
}
