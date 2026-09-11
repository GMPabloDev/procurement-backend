using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public enum ApprovalAssignmentOutcomeKind
{
    Assigned,
    Reassigned,
    Unassigned,
    Unchanged
}

public sealed record ApprovalAssignmentOutcome(
    Guid RequirementId,
    Guid TaskId,
    ApprovalAssignmentOutcomeKind Kind,
    Guid? AssigneeUserId,
    int Load);

/// <summary>
/// Deterministic routing of activation and reconciliation (REQ-04, REQ-05, NFR-01, NFR-03):
/// converts the stored decision scope, asks the SPEC 01 resolver, picks the lowest current
/// load with a canonical-UUID tie-break, and never falls back when no candidate exists.
/// Selection, load reservation and assignment stay inside the caller's transaction.
/// </summary>
public sealed class ApprovalAssignmentEngine(
    ProcureToPayDbContext dbContext,
    IOrganizationEligibilityService eligibility,
    ApprovalScopeResolver scopeResolver)
{
    /// <summary>Assigns every activated requirement that has no assignee yet (REQ-04).</summary>
    public async Task<IReadOnlyList<ApprovalAssignmentOutcome>> AssignUnassignedAsync(
        ApprovalCase approvalCase,
        DateTimeOffset occurredAt,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvalCase);
        var utcNow = occurredAt.ToUniversalTime();
        var loads = await CurrentLoadsAsync(approvalCase.OrganizationId, cancellationToken);
        var outcomes = new List<ApprovalAssignmentOutcome>();

        foreach (var requirement in Ordered(approvalCase.Requirements)
                     .Where(item => item.Status == ApprovalRequirementStatus.Unassigned))
        {
            var task = approvalCase.TaskFor(requirement.Id);
            var candidates = await ResolveCandidatesAsync(approvalCase, requirement, utcNow, cancellationToken);
            var chosen = Select(candidates, loads);
            if (chosen is null)
            {
                ApprovalTelemetry.RecordUnassigned();
                // No candidate: the single current task stays UNASSIGNED and the case BLOCKED,
                // with no fallback (REQ-04). Recorded as evidence, not as an assignment.
                dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
                    approvalCase,
                    "SYSTEM",
                    Guid.Empty,
                    "REQUIREMENT_UNASSIGNED",
                    nameof(ApprovalRequirement),
                    requirement.Id,
                    requirement.DecisionScopeJson,
                    "No eligible candidate for the requirement.",
                    null,
                    null,
                    utcNow,
                    correlationReference,
                    requirement.Id));
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unassigned, null, 0));
                continue;
            }

            var load = LoadOf(loads, chosen.User.Id);
            var evidence = ApprovalEligibilityEvidence.Canonicalize(chosen.Evidence);
            approvalCase.AssignTask(
                task, chosen.User.Id, utcNow, load, evidence, ApprovalAssignmentCause.Initial.ToString());
            loads[chosen.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Initial, utcNow));
            ApprovalTelemetry.RecordAssignment("ASSIGNED");
            dbContext.ApprovalAuditEntries.Add(ApprovalAudit(
                approvalCase, requirement, chosen, "TASK_ASSIGNED", load, utcNow, correlationReference));
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Assigned, chosen.User.Id, load));
        }

        return outcomes;
    }

    /// <summary>
    /// Idempotent reconciliation (REQ-04, NFR-03): a PENDING requirement whose current
    /// assignee stopped being eligible is reassigned to the new best candidate, or released
    /// back to UNASSIGNED when nobody is eligible. A decided task never changes.
    /// </summary>
    public async Task<IReadOnlyList<ApprovalAssignmentOutcome>> ReconcilePendingAsync(
        ApprovalCase approvalCase,
        IEnumerable<ApprovalAssignmentRecord> assignmentRecords,
        DateTimeOffset occurredAt,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvalCase);
        var utcNow = occurredAt.ToUniversalTime();
        var loads = await CurrentLoadsAsync(approvalCase.OrganizationId, cancellationToken);
        var current = assignmentRecords
            .Where(record => record.ReleasedAt is null)
            .ToDictionary(record => record.TaskId);
        var outcomes = new List<ApprovalAssignmentOutcome>();

        foreach (var requirement in Ordered(approvalCase.Requirements)
                     .Where(item => item.Status == ApprovalRequirementStatus.Pending))
        {
            var task = approvalCase.TaskFor(requirement.Id);
            var assignee = task.CurrentAssigneeUserId;
            if (assignee is null)
            {
                continue;
            }

            var candidates = await ResolveCandidatesAsync(approvalCase, requirement, utcNow, cancellationToken);
            var stillEligible = candidates
                .FirstOrDefault(candidate => candidate.User.Id == assignee.Value);
            if (stillEligible is not null)
            {
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unchanged, assignee, 0));
                continue;
            }

            // The previous assignee is no longer a candidate: release the held load first.
            loads[assignee.Value] = Math.Max(0, LoadOf(loads, assignee.Value) - 1);
            var previous = current.GetValueOrDefault(task.Id);
            if (previous is not null)
            {
                previous.ReleasedAt = utcNow;
            }

            var chosen = Select(candidates, loads);
            if (chosen is null)
            {
                ApprovalTelemetry.RecordUnassigned();
                approvalCase.UnassignTask(task, utcNow, ApprovalAssignmentCause.Unassigned.ToString());
                dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
                    approvalCase,
                    "SYSTEM",
                    Guid.Empty,
                    "TASK_UNASSIGNED",
                    nameof(ApprovalTask),
                    task.Id,
                    requirement.DecisionScopeJson,
                    "The assignee lost eligibility and no candidate remains.",
                    null,
                    null,
                    utcNow,
                    correlationReference,
                    requirement.Id));
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unassigned, null, 0));
                continue;
            }

            var load = LoadOf(loads, chosen.User.Id);
            task.ReassignTo(chosen.User.Id);
            loads[chosen.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Reassigned, utcNow));
            ApprovalTelemetry.RecordAssignment("REASSIGNED");
            dbContext.ApprovalAuditEntries.Add(ApprovalAudit(
                approvalCase, requirement, chosen, "TASK_REASSIGNED", load, utcNow, correlationReference));
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Reassigned, chosen.User.Id, load));
        }

        return outcomes;
    }

    /// <summary>True when the pending assignee is still an eligible candidate (REQ-06).</summary>
    public async Task<EligibleCandidate?> FindEligibleCandidateAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        Guid actorUserId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        var candidates = await ResolveCandidatesAsync(
            approvalCase, requirement, evaluatedAt.ToUniversalTime(), cancellationToken);
        return candidates.FirstOrDefault(candidate => candidate.User.Id == actorUserId);
    }

    private async Task<IReadOnlyList<EligibleCandidate>> ResolveCandidatesAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var descriptor = DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson);
        var scope = await scopeResolver.ResolveAsync(descriptor, approvalCase.OrganizationId, cancellationToken);
        return await eligibility.ResolveAsync(
            EligibilityRequest.Create(
                requirement.Role, scope, requirement.Authority, evaluatedAt, requirement.ExcludedUserIds),
            cancellationToken);
    }

    /// <summary>Lowest current PENDING load, tie-broken by canonical ascending UUID (DEC-02).</summary>
    private static EligibleCandidate? Select(
        IReadOnlyList<EligibleCandidate> candidates,
        IReadOnlyDictionary<Guid, int> loads) =>
        // pi-lens-ignore: lsp:CS0103
        ApprovalRoutingPolicy.SelectLowestLoad(candidates, loads);

    private static int LoadOf(IReadOnlyDictionary<Guid, int> loads, Guid userId) =>
        // pi-lens-ignore: lsp:CS0103
        ApprovalRoutingPolicy.LoadOf(loads, userId);

    private static IEnumerable<ApprovalRequirement> Ordered(IEnumerable<ApprovalRequirement> requirements) =>
        requirements.OrderBy(requirement => requirement.WorkflowRequirementKey, StringComparer.Ordinal);

    private async Task<Dictionary<Guid, int>> CurrentLoadsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.ApprovalTasks
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.Status == (int)ApprovalTaskStatus.Pending &&
                record.CurrentAssigneeUserId != null)
            .GroupBy(record => record.CurrentAssigneeUserId!.Value)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        return pending.ToDictionary(entry => entry.UserId, entry => entry.Count);
    }

    private static ApprovalAssignmentRecord Assignment(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        ApprovalTask task,
        EligibleCandidate candidate,
        int load,
        ApprovalAssignmentCause cause,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            CaseId = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            TaskId = task.Id,
            AssigneeUserId = candidate.User.Id,
            AssignedAt = occurredAt,
            Load = load,
            Cause = cause.ToString(),
            // pi-lens-ignore: lsp:CS0103
            EligibilityEvidenceJson = ApprovalEligibilityEvidence.Canonicalize(candidate.Evidence)
        };

    private static ApprovalAuditEntryRecord ApprovalAudit(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        EligibleCandidate candidate,
        string action,
        int load,
        DateTimeOffset occurredAt,
        string correlationReference) =>
        ApprovalEvidence.Audit(
            approvalCase,
            "SYSTEM",
            candidate.User.Id,
            action,
            nameof(ApprovalTask),
            approvalCase.TaskFor(requirement.Id).Id,
            requirement.DecisionScopeJson,
            action == "TASK_ASSIGNED" ? "Deterministic lowest-load assignment." : "Reassignment after authority change.",
            null,
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                ("assignee_user_id", ApprovalCanonicalJson.String(candidate.User.Id)),
                ("load", ApprovalCanonicalJson.Number(load)),
                ("role_assignment_id", ApprovalCanonicalJson.String(candidate.RoleAssignment.Id)))),
            occurredAt,
            correlationReference,
            requirement.Id);
}
