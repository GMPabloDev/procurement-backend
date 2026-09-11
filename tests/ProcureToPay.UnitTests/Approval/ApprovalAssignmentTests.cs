using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Deterministic routing and Segregation of Duties contract for assignment (REQ-04, REQ-05,
/// NFR-01, CA-04, CA-05). The selection is a pure function of the SPEC 01 eligibility snapshot
/// and the observed PENDING loads, so the same inputs always choose the same assignee.
/// </summary>
public sealed class ApprovalAssignmentTests
{
    private static readonly DateTimeOffset EvaluatedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly AuthorizationScopeSet DepartmentScope =
        AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.Department, "IT")]);

    [Fact]
    public void Routing_chooses_the_lowest_load_and_breaks_ties_by_canonical_uuid()
    {
        var loaded = Candidate(new Guid("11111111-1111-1111-1111-111111111111"), "approver-a");
        var alsoLoaded = Candidate(new Guid("22222222-2222-2222-2222-222222222222"), "approver-b");
        var idle = Candidate(new Guid("33333333-3333-3333-3333-333333333333"), "approver-c");
        var loads = new Dictionary<Guid, int>
        {
            [loaded.User.Id] = 1,
            [alsoLoaded.User.Id] = 1,
            [idle.User.Id] = 0
        };

        // pi-lens-ignore: lsp:CS0103
        var chosen = ApprovalRoutingPolicy.SelectLowestLoad([loaded, alsoLoaded, idle], loads);

        Assert.NotNull(chosen);
        Assert.Equal(idle.User.Id, chosen.User.Id);
    }

    [Fact]
    public void Routing_breaks_a_load_tie_by_canonical_ascending_uuid()
    {
        var higher = Candidate(new Guid("99999999-9999-9999-9999-999999999999"), "approver-z");
        var lower = Candidate(new Guid("11111111-1111-1111-1111-111111111111"), "approver-a");
        var middle = Candidate(new Guid("55555555-5555-5555-5555-555555555555"), "approver-m");
        var loads = new Dictionary<Guid, int>
        {
            [higher.User.Id] = 2,
            [lower.User.Id] = 2,
            [middle.User.Id] = 2
        };

        // pi-lens-ignore: lsp:CS0103
        var chosen = ApprovalRoutingPolicy.SelectLowestLoad([higher, middle, lower], loads);

        Assert.NotNull(chosen);
        Assert.Equal(lower.User.Id, chosen.User.Id);
    }

    [Fact]
    public void Canonical_uuid_order_is_the_lowercase_d_string_compared_ordinally()
    {
        // The tie-break is the lowercase canonical "D" form compared ordinally, not a numeric
        // or component-wise ordering. The vectors differ in the leading character and, in the
        // last case, only in the final byte, so the whole string participates in the order.
        var first = new Guid("0f000000-0000-0000-0000-000000000000");
        var second = new Guid("f0000000-0000-0000-0000-000000000000");
        var lastByte = new Guid("0f000000-0000-0000-0000-000000000001");

        // pi-lens-ignore: lsp:CS0103
        Assert.Equal("0f000000-0000-0000-0000-000000000000", ApprovalRoutingPolicy.CanonicalUserId(first));
        Assert.True(string.CompareOrdinal(
            // pi-lens-ignore: lsp:CS0103
            ApprovalRoutingPolicy.CanonicalUserId(first),
            // pi-lens-ignore: lsp:CS0103
            ApprovalRoutingPolicy.CanonicalUserId(second)) < 0);
        Assert.True(string.CompareOrdinal(
            // pi-lens-ignore: lsp:CS0103
            ApprovalRoutingPolicy.CanonicalUserId(first),
            // pi-lens-ignore: lsp:CS0103
            ApprovalRoutingPolicy.CanonicalUserId(lastByte)) < 0);

        var loads = new Dictionary<Guid, int> { [first] = 1, [second] = 1, [lastByte] = 1 };
        // pi-lens-ignore: lsp:CS0103
        var chosen = ApprovalRoutingPolicy.SelectLowestLoad(
            [Candidate(second, "approver-b"), Candidate(lastByte, "approver-c"), Candidate(first, "approver-a")],
            loads);

        Assert.NotNull(chosen);
        Assert.Equal(first, chosen.User.Id);
    }

    [Fact]
    public void Routing_is_deterministic_regardless_of_candidate_order()
    {
        var first = Candidate(new Guid("11111111-1111-1111-1111-111111111111"), "approver-a");
        var second = Candidate(new Guid("22222222-2222-2222-2222-222222222222"), "approver-b");
        var third = Candidate(new Guid("33333333-3333-3333-3333-333333333333"), "approver-c");
        var loads = new Dictionary<Guid, int>
        {
            [first.User.Id] = 4,
            [second.User.Id] = 4,
            [third.User.Id] = 4
        };
        EligibleCandidate[] candidates = [first, second, third];

        // pi-lens-ignore: lsp:CS0103
        var forward = ApprovalRoutingPolicy.SelectLowestLoad(candidates, loads);
        // pi-lens-ignore: lsp:CS0103
        var reversed = ApprovalRoutingPolicy.SelectLowestLoad(candidates.Reverse(), loads);
        // pi-lens-ignore: lsp:CS0103
        var shuffled = ApprovalRoutingPolicy.SelectLowestLoad([second, third, first], loads);

        Assert.NotNull(forward);
        Assert.Equal(forward.User.Id, reversed!.User.Id);
        Assert.Equal(forward.User.Id, shuffled!.User.Id);
    }

    [Fact]
    public void Routing_returns_no_candidate_when_nobody_is_eligible()
    {
        // pi-lens-ignore: lsp:CS0103
        Assert.Null(ApprovalRoutingPolicy.SelectLowestLoad([], new Dictionary<Guid, int>()));
    }

    [Fact]
    public void Excluded_actors_are_never_candidates_even_with_full_scope()
    {
        var originatorId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var requesterId = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var eligibleId = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var users = new[]
        {
            ActiveUser(originatorId, "originator"),
            ActiveUser(requesterId, "requester"),
            ActiveUser(eligibleId, "approver")
        };
        var assignments = users.Select(user => Assignment(user.Id)).ToArray();
        var request = EligibilityRequest.Create(
            SystemRole.ItReviewer,
            DepartmentScope,
            AuthorityRequirement.None,
            EvaluatedAt,
            [originatorId, requesterId]);

        var candidates = EligibilityResolver.Resolve(users, assignments, [], request);

        var only = Assert.Single(candidates);
        Assert.Equal(eligibleId, only.User.Id);

        // pi-lens-ignore: lsp:CS0103
        var chosen = ApprovalRoutingPolicy.SelectLowestLoad(
            candidates, new Dictionary<Guid, int> { [originatorId] = 0, [requesterId] = 0 });
        Assert.NotNull(chosen);
        Assert.Equal(eligibleId, chosen.User.Id);
        Assert.DoesNotContain(candidates, candidate =>
            candidate.User.Id == originatorId || candidate.User.Id == requesterId);
    }

    [Fact]
    public void One_user_with_several_matching_assignments_counts_once()
    {
        var userId = new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var user = ActiveUser(userId, "approver");
        var assignments = new[] { Assignment(userId), Assignment(userId) };
        var request = EligibilityRequest.Create(
            SystemRole.ItReviewer,
            DepartmentScope,
            AuthorityRequirement.None,
            EvaluatedAt);

        var candidates = EligibilityResolver.Resolve([user], assignments, [], request);

        Assert.Equal(2, candidates.Count);
        // pi-lens-ignore: lsp:CS0103
        var chosen = ApprovalRoutingPolicy.SelectLowestLoad(
            candidates, new Dictionary<Guid, int> { [userId] = 7 });
        Assert.NotNull(chosen);
        Assert.Equal(userId, chosen.User.Id);
        // pi-lens-ignore: lsp:CS0103
        Assert.Equal(7, ApprovalRoutingPolicy.LoadOf(new Dictionary<Guid, int> { [userId] = 7 }, userId));
        // pi-lens-ignore: lsp:CS0103
        Assert.Equal(0, ApprovalRoutingPolicy.LoadOf(new Dictionary<Guid, int>(), userId));
    }

    private static EligibleCandidate Candidate(Guid userId, string subject)
    {
        var user = ActiveUser(userId, subject);
        var request = EligibilityRequest.Create(
            SystemRole.ItReviewer,
            DepartmentScope,
            AuthorityRequirement.None,
            EvaluatedAt);
        return EligibilityResolver.Resolve([user], [Assignment(userId)], [], request).Single();
    }

    private static UserProfile ActiveUser(Guid userId, string subject) => UserProfile.Restore(
        userId,
        "https://issuer.test/realms/procure-to-pay",
        subject,
        $"{subject}@acme.test",
        subject,
        null,
        null,
        UserProfileStatus.Active,
        1);

    private static RoleAssignment Assignment(Guid userId) => RoleAssignment.Create(
        Guid.NewGuid(),
        userId,
        SystemRole.ItReviewer,
        DepartmentScope,
        EvaluatedAt.AddDays(-1),
        new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));
}
