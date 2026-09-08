// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Organization;

public sealed class EligibilityResolverTests
{
    [Fact]
    public void Department_approver_requires_business_authority()
    {
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]);
        var requestWithoutAuthority = EligibilityRequest.Create(
            SystemRole.DepartmentApprover,
            scope,
            AuthorityRequirement.None,
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainValidationException>(() => EligibilityResolver.Resolve([], [], [], requestWithoutAuthority));
    }

    [Fact]
    public void It_reviewer_is_eligible_by_scoped_role_without_authority()
    {
        var user = CreateActiveUser("it-reviewer", Guid.NewGuid());
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]);
        var assignment = RoleAssignment.Create(
            Guid.NewGuid(),
            user.Id,
            SystemRole.ItReviewer,
            scope,
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        var request = EligibilityRequest.Create(
            SystemRole.ItReviewer,
            scope,
            AuthorityRequirement.None,
            DateTimeOffset.UtcNow);

        var candidates = EligibilityResolver.Resolve([user], [assignment], [], request);

        var candidate = Assert.Single(candidates);
        Assert.Null(candidate.AuthorityGrant);
        Assert.Equal(user.Version, candidate.Evidence.UserProfileVersion);
    }

    [Fact]
    public void Finance_candidate_requires_one_grant_covering_all_requirements()
    {
        var user = CreateActiveUser("finance", Guid.NewGuid());
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]);
        var assignment = RoleAssignment.Create(
            Guid.NewGuid(),
            user.Id,
            SystemRole.FinanceApprover,
            scope,
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        var level = new AuthorityLevelVersion(
            Guid.NewGuid(),
            ApprovalAuthorityType.Financial,
            "FINANCE_LEVEL_2",
            2,
            1);
        var evaluatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var grant = ApprovalAuthorityGrant.Create(
            Guid.NewGuid(),
            user.Id,
            level,
            20_000m,
            "PEN",
            scope,
            evaluatedAt.AddDays(-1),
            evaluatedAt.AddDays(1),
            evaluatedAt.AddDays(-1),
            Guid.NewGuid());
        var request = EligibilityRequest.Create(
            SystemRole.FinanceApprover,
            scope,
            AuthorityRequirement.Required(
                ApprovalAuthorityType.Financial,
                2,
                20_000m,
                "PEN"),
            evaluatedAt);

        var candidates = EligibilityResolver.Resolve([user], [assignment], [grant], request);

        var candidate = Assert.Single(candidates);
        Assert.Equal(grant.Id, candidate.AuthorityGrant!.Id);
        Assert.Equal(level.Version, candidate.Evidence.AuthorityLevelVersion);
    }

    [Fact]
    public void Resolver_does_not_combine_partial_grants_or_include_excluded_users()
    {
        var user = CreateActiveUser("finance", Guid.NewGuid());
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.Global()]);
        var assignment = RoleAssignment.Create(
            Guid.NewGuid(),
            user.Id,
            SystemRole.FinanceApprover,
            scope,
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        var level = new AuthorityLevelVersion(
            Guid.NewGuid(),
            ApprovalAuthorityType.Financial,
            "FINANCE_LEVEL_1",
            1,
            1);
        var at = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var grant = ApprovalAuthorityGrant.Create(
            Guid.NewGuid(),
            user.Id,
            level,
            10_000m,
            "PEN",
            scope,
            at.AddDays(-1),
            null,
            at.AddDays(-1),
            Guid.NewGuid());
        var request = EligibilityRequest.Create(
            SystemRole.FinanceApprover,
            scope,
            AuthorityRequirement.Required(ApprovalAuthorityType.Financial, 1, 20_000m, "PEN"),
            at,
            [user.Id]);

        var candidates = EligibilityResolver.Resolve([user], [assignment], [grant], request);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Eligibility_evidence_is_a_snapshot_of_versions()
    {
        var user = CreateActiveUser("finance", Guid.NewGuid());
        var scope = AuthorizationScopeSet.Create([AuthorizationScope.Global()]);
        var assignment = RoleAssignment.Create(
            Guid.NewGuid(),
            user.Id,
            SystemRole.FinanceApprover,
            scope,
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        var level = new AuthorityLevelVersion(
            Guid.NewGuid(),
            ApprovalAuthorityType.Financial,
            "FINANCE_LEVEL_1",
            1,
            1);
        var at = DateTimeOffset.UtcNow;
        var grant = ApprovalAuthorityGrant.Create(
            Guid.NewGuid(),
            user.Id,
            level,
            null,
            "PEN",
            scope,
            at.AddMinutes(-1),
            null,
            at.AddMinutes(-1),
            Guid.NewGuid());
        var request = EligibilityRequest.Create(
            SystemRole.FinanceApprover,
            scope,
            AuthorityRequirement.Required(ApprovalAuthorityType.Financial, 1, null, null),
            at);

        var evidence = Assert.Single(EligibilityResolver.Resolve([user], [assignment], [grant], request)).Evidence;

        var capturedVersion = evidence.UserProfileVersion;
        user.RefreshIdentityClaims("changed@acme.test", "Changed");
        Assert.Equal(capturedVersion, evidence.UserProfileVersion);
        Assert.NotEqual(user.Version, evidence.UserProfileVersion);
    }

    private static UserProfile CreateActiveUser(string subject, Guid departmentId)
    {
        // pi-lens-ignore: lsp:CS0246
        var user = new UserProfile(Guid.NewGuid(), "https://issuer", subject, null, subject);
        user.CompleteSetup(departmentId, "Approver");
        user.Activate(true);
        return user;
    }
}
