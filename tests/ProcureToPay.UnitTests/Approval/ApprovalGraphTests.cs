using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Case graph, dependency propagation, exactly-one task and lifecycle transitions
/// (REQ-03, REQ-07, REQ-10).
/// </summary>
public sealed class ApprovalGraphTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Originator = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly ApprovalAdapterDescriptor Adapter =
        new("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
    private static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", "adapter");

    [Fact]
    public void Root_requirements_activate_and_block_until_assigned()
    {
        var line = Target("LINE", Guid.NewGuid());
        var approvalCase = BuildCase([("DEPARTMENT_REQ", "DEPARTMENT", [line], []), ("FINANCE_REQ", "FINANCE", [line], [])]);

        Assert.Equal(ApprovalCaseStatus.Blocked, approvalCase.Status);
        Assert.All(approvalCase.Requirements, requirement =>
        {
            Assert.Equal(ApprovalRequirementStatus.Unassigned, requirement.Status);
            Assert.Equal(ApprovalTaskStatus.Unassigned, approvalCase.TaskFor(requirement.Id).Status);
        });
        // Exactly one current task per requirement.
        Assert.Equal(2, approvalCase.Tasks.Count);
        Assert.Equal(2, approvalCase.Tasks.Select(task => task.RequirementId).Distinct().Count());

        // A task cannot be assigned twice and cannot be decided before assignment.
        var task = approvalCase.TaskFor(approvalCase.Requirements[0].Id);
        Assert.Throws<DomainConflictException>(() => approvalCase.ApplyDecision(task, ApprovalDecisionAction.Approve));

        approvalCase.AssignTask(
            task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{\"evidence\":true}", ApprovalAssignmentCause.Initial.ToString());
        Assert.Equal(ApprovalTaskStatus.Pending, task.Status);
        Assert.Equal(ApprovalRequirementStatus.Pending, approvalCase.RequireRequirement(task.RequirementId).Status);
        Assert.Equal(ApprovalCaseStatus.Blocked, approvalCase.Status);
        Assert.Throws<DomainConflictException>(() => approvalCase.AssignTask(
            task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{\"evidence\":true}", "Initial"));
    }

    [Fact]
    public void Dependencies_gate_activation_until_the_predecessor_is_approved()
    {
        var line = Target("LINE", Guid.NewGuid());
        var dependency = new ApprovalDependencyRef(
            DependencyPredecessorKind.Approval, "DEPARTMENT_REQ", [line]);
        var approvalCase = BuildCase(
            [("DEPARTMENT_REQ", "DEPARTMENT", [line], []), ("PROCUREMENT_REQ", "PROCUREMENT", [line], [dependency])]);

        var dependent = approvalCase.Requirements.Single(item => item.SourceRequirementKey == "PROCUREMENT_REQ");
        Assert.Equal(ApprovalRequirementStatus.Waiting, dependent.Status);

        var predecessor = approvalCase.Requirements.Single(item => item.SourceRequirementKey == "DEPARTMENT_REQ");
        var task = approvalCase.TaskFor(predecessor.Id);
        approvalCase.AssignTask(task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{}", "Initial");
        approvalCase.ApplyDecision(task, ApprovalDecisionAction.Approve);

        // The approval transition itself enables the edge it satisfies (REQ-07): no extra
        // driver call is needed for the dependent to become activatable.
        Assert.Equal(ApprovalRequirementStatus.Unassigned, dependent.Status);
        Assert.Contains(approvalCase.Requirements, item =>
            item.SourceRequirementKey == "PROCUREMENT_REQ" &&
            item.Status == ApprovalRequirementStatus.Unassigned);
    }

    [Fact]
    public void Reject_cancels_only_linked_descendants()
    {
        var line = Target("LINE", Guid.NewGuid());
        var other = Target("LINE", Guid.NewGuid());
        var dependent = new ApprovalDependencyRef(
            DependencyPredecessorKind.Approval, "DEPARTMENT_REQ", [line]);
        var approvalCase = BuildCase(
            [
                ("DEPARTMENT_REQ", "DEPARTMENT", [line], []),
                ("FINANCE_REQ", "FINANCE", [other], []),
                ("PROCUREMENT_REQ", "PROCUREMENT", [line], [dependent])
            ]);

        var predecessor = approvalCase.Requirements.Single(item => item.SourceRequirementKey == "DEPARTMENT_REQ");
        var task = approvalCase.TaskFor(predecessor.Id);
        approvalCase.AssignTask(task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{}", "Initial");
        approvalCase.ApplyDecision(task, ApprovalDecisionAction.Reject);

        Assert.Equal(
            ApprovalRequirementStatus.Cancelled,
            approvalCase.Requirements.Single(item => item.SourceRequirementKey == "PROCUREMENT_REQ").Status);
        Assert.Equal(
            ApprovalRequirementStatus.Unassigned,
            approvalCase.Requirements.Single(item => item.SourceRequirementKey == "FINANCE_REQ").Status);
        Assert.Equal(ApprovalTaskStatus.Rejected, task.Status);
    }

    [Fact]
    public void Failed_prerequisite_blocks_and_satisfied_prerequisite_enables_the_dependent()
    {
        var line = Target("LINE", Guid.NewGuid());
        var dependency = new ApprovalDependencyRef(
            DependencyPredecessorKind.External, "BUDGET_CHECK", [line]);
        var approvalCase = BuildCase(
            [("DEPARTMENT_REQ", "DEPARTMENT", [line], [dependency])],
            prerequisite: ("BUDGET_CHECK", line));

        var prerequisite = approvalCase.Prerequisites.Single();
        var requirement = approvalCase.Requirements.Single();

        // A pending prerequisite keeps the case blocked before any assignment happens.
        Assert.Equal(ApprovalCaseStatus.Blocked, approvalCase.Status);
        Assert.Equal(ApprovalRequirementStatus.Waiting, requirement.Status);

        approvalCase.ApplyPrerequisiteSignal(
            prerequisite, false, "signal-1", new string('a', 64), DateTimeOffset.UtcNow);
        Assert.Equal(PrerequisiteStatus.Failed, prerequisite.Status);
        Assert.Equal(ApprovalRequirementStatus.Waiting, requirement.Status);
        Assert.Equal(ApprovalCaseStatus.Blocked, approvalCase.Status);
        Assert.Throws<DomainConflictException>(() => approvalCase.ApplyPrerequisiteSignal(
            prerequisite, true, "signal-2", new string('b', 64), DateTimeOffset.UtcNow));

        var satisfiedCase = BuildCase(
            [("DEPARTMENT_REQ", "DEPARTMENT", [line], [dependency])],
            prerequisite: ("BUDGET_CHECK", line));
        satisfiedCase.ApplyPrerequisiteSignal(
            satisfiedCase.Prerequisites.Single(), true, "signal-1", new string('c', 64), DateTimeOffset.UtcNow);
        Assert.Equal(PrerequisiteStatus.Satisfied, satisfiedCase.Prerequisites.Single().Status);
        Assert.Equal(ApprovalRequirementStatus.Unassigned, satisfiedCase.Requirements.Single().Status);
    }

    [Fact]
    public void Case_completes_only_when_every_requirement_is_terminal_and_every_prerequisite_is_satisfied()
    {
        var line = Target("LINE", Guid.NewGuid());
        var approvalCase = BuildCase([("DEPARTMENT_REQ", "DEPARTMENT", [line], [])]);
        var task = approvalCase.TaskFor(approvalCase.Requirements.Single().Id);
        approvalCase.AssignTask(task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{}", "Initial");

        Assert.Equal(ApprovalCaseStatus.Open, approvalCase.Status);

        approvalCase.ApplyDecision(task, ApprovalDecisionAction.Approve);
        Assert.Equal(ApprovalCaseStatus.Completed, approvalCase.Status);
        Assert.Throws<DomainConflictException>(() => approvalCase.Cancel("no longer needed", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancellation_is_terminal_and_preserves_decisions()
    {
        var line = Target("LINE", Guid.NewGuid());
        var approvalCase = BuildCase(
            [("DEPARTMENT_REQ", "DEPARTMENT", [line], []), ("FINANCE_REQ", "FINANCE", [line], [])]);
        approvalCase.Cancel("domain requested cancellation", DateTimeOffset.UtcNow);

        Assert.Equal(ApprovalCaseStatus.Cancelled, approvalCase.Status);
        Assert.All(approvalCase.Requirements, requirement =>
            Assert.Equal(ApprovalRequirementStatus.Cancelled, requirement.Status));
        Assert.All(approvalCase.Tasks, task => Assert.Equal(ApprovalTaskStatus.Cancelled, task.Status));
        Assert.Throws<DomainConflictException>(() => approvalCase.Cancel("again", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Decision_actions_map_to_requirement_and_task_states()
    {
        foreach (var (action, requirementStatus, taskStatus) in new[]
                 {
                     (ApprovalDecisionAction.Approve,
                         ApprovalRequirementStatus.Approved, ApprovalTaskStatus.Approved),
                     (ApprovalDecisionAction.Reject,
                         ApprovalRequirementStatus.Rejected, ApprovalTaskStatus.Rejected),
                     (ApprovalDecisionAction.RequestChanges,
                         ApprovalRequirementStatus.ChangesRequested, ApprovalTaskStatus.ChangesRequested)
                 })
        {
            var line = Target("LINE", Guid.NewGuid());
            var approvalCase = BuildCase([("DEPARTMENT_REQ", "DEPARTMENT", [line], [])]);
            var task = approvalCase.TaskFor(approvalCase.Requirements.Single().Id);
            approvalCase.AssignTask(task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{}", "Initial");
            approvalCase.ApplyDecision(task, action);

            Assert.Equal(requirementStatus, approvalCase.Requirements.Single().Status);
            Assert.Equal(taskStatus, task.Status);
        }
    }

    private static ApprovalTarget Target(string type, Guid id) => new(type, id, 1, new string('c', 64));

    private static ApprovalCase BuildCase(
        (string Key, string Stage, ApprovalTarget[] Targets, ApprovalDependencyRef[] Dependencies)[] requirements,
        (string Key, ApprovalTarget Target)? prerequisite = null)
    {
        var definitions = requirements
            .Select(requirement => BuildRequirement(
                requirement.Key, requirement.Stage, requirement.Targets, requirement.Dependencies))
            .ToArray();

        var prerequisites = prerequisite is null
            ? Array.Empty<ExternalPrerequisiteDefinition>()
            : [new ExternalPrerequisiteDefinition(
                prerequisite.Value.Key, "adapter", "v1", "BUDGET_CHECK", new string('d', 64),
                "{\"minimum\":1}", [prerequisite.Value.Target])];

        var submission = new ApprovalSubmission(
            "submission-key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            Originator,
            definitions,
            prerequisites);
        var prerequisiteOwners = prerequisite is null
            ? new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal)
            : new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal)
            {
                [prerequisite.Value.Key] = Workload
            };
        return ApprovalCase.Create(
            Guid.NewGuid(), submission, Adapter, Workload, prerequisiteOwners, new string('e', 64),
            DateTimeOffset.UtcNow, "correlation");
    }

    [Fact]
    public void A_requirement_rejects_actions_outside_its_declared_set()
    {
        var line = Target("LINE", Guid.NewGuid());
        var definition = new ApprovalRequirementDefinition(
            "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve],
            [Originator],
            [line],
            []);
        var submission = new ApprovalSubmission(
            "submission-key", OrganizationId, "PURCHASE_REQUEST",
            Guid.Parse("33333333-3333-3333-3333-333333333333"), 1, "SUBMIT",
            new string('a', 64), null, Originator, [definition], []);
        var approvalCase = ApprovalCase.Create(
            Guid.NewGuid(), submission, Adapter, Workload,
            new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal),
            new string('e', 64), DateTimeOffset.UtcNow, "correlation");

        var task = approvalCase.TaskFor(approvalCase.Requirements[0].Id);
        approvalCase.AssignTask(
            task, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, "{\"evidence\":true}", "Initial");

        // The declared actions are the only admissible responses (REQ-02): a solo-APPROVE
        // requirement must reject REJECT and REQUEST_CHANGES.
        Assert.Throws<DomainConflictException>(() =>
            approvalCase.ApplyDecision(task, ApprovalDecisionAction.Reject));
        Assert.Throws<DomainConflictException>(() =>
            approvalCase.ApplyDecision(task, ApprovalDecisionAction.RequestChanges));
        approvalCase.ApplyDecision(task, ApprovalDecisionAction.Approve);
        Assert.Equal(
            ApprovalRequirementStatus.Approved,
            approvalCase.RequireRequirement(task.RequirementId).Status);
    }

    private static ApprovalRequirementDefinition BuildRequirement(
        string key,
        string stage,
        IEnumerable<ApprovalTarget> targets,
        IEnumerable<ApprovalDependencyRef> dependencies)
    {
        var role = stage switch
        {
            "FINANCE" => SystemRole.FinanceApprover,
            "PROCUREMENT" => SystemRole.ProcurementApprover,
            _ => SystemRole.DepartmentApprover
        };
        var authorityType = stage switch
        {
            "FINANCE" => ApprovalAuthorityType.Financial,
            "PROCUREMENT" => ApprovalAuthorityType.Procurement,
            _ => ApprovalAuthorityType.BusinessNeed
        };

        return new ApprovalRequirementDefinition(
            key,
            stage,
            role,
            AuthorityRequirement.Required(authorityType, 1, null, null),
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
            [Originator],
            targets,
            dependencies);
    }
}
