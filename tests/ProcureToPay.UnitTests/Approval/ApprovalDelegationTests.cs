using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Domain rules of SPEC 04: bounded delegation intervals and lifecycle, scheduled transition jobs,
/// full-case supersession terminal states and the taskless carry-forward transition.
/// </summary>
public sealed class ApprovalDelegationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Delegator = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Delegatee = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-10-01T00:00:00.0000000Z");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-10-08T00:00:00.0000000Z");

    [Fact]
    public void Delegation_creation_validates_interval_and_identity()
    {
        Assert.Throws<DomainValidationException>(() => Create(delegator: Delegator, delegatee: Delegator));
        Assert.Throws<DomainValidationException>(() => Create(validTo: From));
        Assert.Throws<DomainValidationException>(() => Create(role: SystemRole.Admin));
        Assert.Throws<DomainValidationException>(() => Create(role: SystemRole.Auditor));
    }

    [Fact]
    public void Delegation_lifecycle_is_strict_and_irreversible()
    {
        var delegation = Create();
        Assert.Equal(ApprovalDelegationStatus.Scheduled, delegation.Status);
        Assert.False(delegation.IsEffectiveAt(From));

        delegation.Activate(From);
        Assert.Equal(ApprovalDelegationStatus.Active, delegation.Status);
        Assert.True(delegation.IsEffectiveAt(From));
        Assert.True(delegation.IsEffectiveAt(To.AddTicks(-1)));
        Assert.False(delegation.IsEffectiveAt(To));
        Assert.Throws<DomainConflictException>(() => delegation.Activate(From));

        delegation.Expire(To);
        Assert.Equal(ApprovalDelegationStatus.Expired, delegation.Status);
        Assert.Throws<DomainConflictException>(() => delegation.Revoke(To, Delegator));
        Assert.Throws<DomainConflictException>(() => delegation.Expire(To));
    }

    [Fact]
    public void Delegation_revocation_is_irreversible_and_requires_an_actor()
    {
        var delegation = Create();
        Assert.Throws<DomainValidationException>(() => delegation.Revoke(From, Guid.Empty));
        delegation.Revoke(From, Delegator);
        Assert.Equal(ApprovalDelegationStatus.Revoked, delegation.Status);
        Assert.Throws<DomainConflictException>(() => delegation.Revoke(From, Delegator));
    }

    [Fact]
    public void Transition_job_is_claimed_with_fencing_and_never_reopens()
    {
        var job = ApprovalDelegationTransitionJob.Create(
            Guid.NewGuid(), OrganizationId, Guid.NewGuid(), 1, ApprovalDelegationTransition.Expire, To);

        job.ApplyClaim("worker-1", From, TimeSpan.FromSeconds(30));
        Assert.Equal(1, job.FencingToken);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.IsClaimableAt(From.AddSeconds(10)));
        Assert.True(job.IsClaimableAt(From.AddSeconds(31)));

        job.Complete(From.AddSeconds(31));
        Assert.Equal(ApprovalDelegationJobStatus.Completed, job.Status);
        Assert.Throws<DomainConflictException>(() => job.Cancel(From.AddSeconds(32)));
        Assert.False(job.IsClaimableAt(From.AddMinutes(5)));
    }

    [Fact]
    public void Supersede_makes_every_non_terminal_node_terminal()
    {
        var approvalCase = CaseWithUnassignedNode();

        approvalCase.Supersede(From);

        Assert.Equal(ApprovalCaseStatus.Superseded, approvalCase.Status);
        Assert.Equal(ApprovalRequirementStatus.Superseded, approvalCase.Requirements.Single().Status);
        Assert.Equal(ApprovalTaskStatus.Superseded, approvalCase.Tasks.Single().Status);
        Assert.Throws<DomainConflictException>(() => approvalCase.Supersede(From));
    }

    [Fact]
    public void Supersede_keeps_terminal_decisions_untouched()
    {
        var caseId = Guid.NewGuid();
        var requirement = Requirement(ApprovalRequirementStatus.Approved, caseId);
        var task = ApprovalTask.Restore(
            Guid.NewGuid(), caseId, requirement.Id, ApprovalTaskStatus.Approved, null, 2);
        var approvalCase = ApprovalCase.Restore(
            caseId, OrganizationId, "PURCHASE_REQUEST", Guid.NewGuid(), 1, "SUBMIT", new string('a', 64),
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter"), "submission", new string('e', 64),
            Delegatee, null, From, "correlation", ApprovalCaseStatus.Open, 4, null, null,
            [requirement], [task], []);

        approvalCase.Supersede(From);

        Assert.Equal(ApprovalRequirementStatus.Approved, requirement.Status);
        Assert.Equal(ApprovalTaskStatus.Approved, task.Status);
    }

    [Fact]
    public void Carry_forward_approves_without_task_and_activates_dependents()
    {
        var approvalCase = CaseWithUnassignedNode();
        var requirement = approvalCase.Requirements.Single();

        approvalCase.ApplyCarryForward(requirement);

        Assert.Equal(ApprovalRequirementStatus.Approved, requirement.Status);
        Assert.Empty(approvalCase.Tasks);
        Assert.Throws<DomainConflictException>(() => approvalCase.ApplyCarryForward(requirement));
    }

    private static ApprovalDelegation Create(
        Guid? delegator = null,
        Guid? delegatee = null,
        SystemRole role = SystemRole.DepartmentApprover,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null)
    {
        var scope = DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);
        var from = validFrom ?? From;
        var to = validTo ?? To;
        var actor = delegator ?? Delegator;
        return ApprovalDelegation.Create(
            Guid.NewGuid(),
            OrganizationId,
            actor,
            delegatee ?? Delegatee,
            role,
            scope.ToCanonicalJson(),
            from,
            to,
            ApprovalDelegationActorType.Delegator,
            actor,
            "Vacation coverage",
            "delegation-1",
            new string('f', 64),
            Guid.NewGuid(),
            From);
    }

    private static ApprovalRequirement Requirement(ApprovalRequirementStatus status, Guid caseId)
    {
        var scope = DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);
        return ApprovalRequirement.Restore(
            Guid.NewGuid(),
            caseId,
            "DEPARTMENT_REQ",
            "WR-00000000000000000000000000000001",
            "DEPARTMENT",
            SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            scope.ToCanonicalJson(),
            [],
            [ApprovalDecisionAction.Approve],
            [new ApprovalTarget("PURCHASE_REQUEST", Guid.NewGuid(), 1, new string('c', 64))],
            [],
            status,
            1);
    }

    private static ApprovalCase CaseWithUnassignedNode()
    {
        var caseId = Guid.NewGuid();
        var requirement = Requirement(ApprovalRequirementStatus.Unassigned, caseId);
        var task = ApprovalTask.Restore(
            Guid.NewGuid(), caseId, requirement.Id, ApprovalTaskStatus.Unassigned, null, 1);
        return ApprovalCase.Restore(
            caseId, OrganizationId, "PURCHASE_REQUEST", Guid.NewGuid(), 1, "SUBMIT", new string('a', 64),
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter"), "submission", new string('e', 64),
            Delegatee, null, From, "correlation", ApprovalCaseStatus.Blocked, 3, null, null,
            [requirement], [task], []);
    }
}
