using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// One pending task of the signed-in assignee (REQ-09). It carries only what is needed to
/// decide: no originator identity, no eligibility evidence and no source document.
/// </summary>
public sealed record ApprovalInboxItemView(
    Guid TaskId,
    int TaskVersion,
    Guid CaseId,
    Guid RequirementId,
    string WorkflowRequirementKey,
    string StageCode,
    string Role,
    string DecisionScopeJson,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    DateTimeOffset AssignedAt,
    IReadOnlyList<ApprovalTargetView> Targets);

/// <summary>Decision already taken by the signed-in actor (REQ-09), including its own reason.</summary>
public sealed record ApprovalDecisionHistoryView(
    Guid DecisionId,
    Guid CaseId,
    Guid RequirementId,
    string Action,
    string Origin,
    DateTimeOffset DecidedAt,
    string Reason,
    string DecisionDigest,
    string AuthorityEvidenceDigest,
    IReadOnlyList<ApprovalTargetView> Targets);

/// <summary>Minimized decision record for an organizational AUDITOR or ADMIN (REQ-09).</summary>
public sealed record ApprovalCaseDecisionView(
    Guid DecisionId,
    Guid RequirementId,
    Guid? TaskId,
    string Action,
    Guid ActorUserId,
    DateTimeOffset DecidedAt,
    string DecisionDigest,
    string AuthorityEvidenceDigest,
    int TargetCount);

/// <summary>
/// One audit record of a case for an organizational AUDITOR (REQ-09): the closed actor union and
/// the causal link of an automatic effect, so the chain root -> effect stays readable.
/// </summary>
public sealed record ApprovalCaseAuditView(
    Guid AuditId,
    string ActorType,
    Guid? ActorUserId,
    string? ActorWorkloadIssuer,
    string? ActorWorkloadClientId,
    string? ActorSystemId,
    string? CausedByAuditStream,
    Guid? CausedByAuditId,
    string? AutomaticEffectKey,
    string Action,
    string TargetType,
    Guid TargetId,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>Assignment history of a case, including the frozen eligibility evidence (REQ-09).</summary>
public sealed record ApprovalCaseAssignmentView(
    Guid AssignmentId,
    Guid TaskId,
    Guid AssigneeUserId,
    string Cause,
    int Load,
    DateTimeOffset AssignedAt,
    DateTimeOffset? ReleasedAt,
    string EligibilityEvidenceJson);

/// <summary>
/// Minimized read surfaces of the approval workflow (REQ-09, NFR-05): the assignee inbox, the
/// actor decision history and the organizational audit reads. Every query is organization
/// scoped, so anything outside the visible scope is simply absent.
/// </summary>
public sealed class ApprovalInboxQueryService(ProcureToPayDbContext dbContext)
{
    public async Task<IReadOnlyList<ApprovalInboxItemView>> GetInboxAsync(
        Guid organizationId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from task in dbContext.ApprovalTasks.AsNoTracking()
                join requirement in dbContext.ApprovalRequirements.AsNoTracking()
                    on task.RequirementId equals requirement.Id
                join approvalCase in dbContext.ApprovalCases.AsNoTracking()
                    on task.CaseId equals approvalCase.Id
                where task.OrganizationId == organizationId &&
                      task.CurrentAssigneeUserId == actorUserId &&
                      task.Status == (int)ApprovalTaskStatus.Pending
                select new { Task = task, Requirement = requirement, Case = approvalCase })
            .ToArrayAsync(cancellationToken);

        var assignments = await dbContext.ApprovalAssignments
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.AssigneeUserId == actorUserId &&
                record.ReleasedAt == null)
            .Select(record => new { record.TaskId, record.AssignedAt })
            .ToArrayAsync(cancellationToken);
        var assignedAtByTask = assignments.ToDictionary(entry => entry.TaskId, entry => entry.AssignedAt);

        return rows
            .OrderBy(row => row.Task.Id.ToString("D"), StringComparer.Ordinal)
            .Select(row => new ApprovalInboxItemView(
                row.Task.Id,
                row.Task.Version,
                row.Case.Id,
                row.Requirement.Id,
                row.Requirement.WorkflowRequirementKey,
                row.Requirement.StageCode,
                ((SystemRole)row.Requirement.Role).ToString().ToUpperInvariant(),
                row.Requirement.DecisionScopeJson,
                row.Case.SubjectType,
                row.Case.SubjectId,
                row.Case.SubjectVersion,
                row.Case.Operation,
                assignedAtByTask.TryGetValue(row.Task.Id, out var assignedAt) ? assignedAt : row.Case.CreatedAt,
                Targets(row.Requirement.TargetsJson)))
            .ToArray();
    }

    public async Task<IReadOnlyList<ApprovalDecisionHistoryView>> GetDecisionHistoryAsync(
        Guid organizationId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var decisions = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId && record.ActorUserId == actorUserId)
            .OrderByDescending(record => record.DecidedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        if (decisions.Length == 0)
        {
            return [];
        }

        var decisionIds = decisions.Select(record => record.Id).ToArray();
        var targets = await dbContext.ApprovalDecisionTargets
            .AsNoTracking()
            .Where(record => decisionIds.Contains(record.DecisionId))
            .ToArrayAsync(cancellationToken);

        return decisions
            .Select(record => new ApprovalDecisionHistoryView(
                record.Id,
                record.CaseId,
                record.RequirementId,
                ((ApprovalDecisionAction)record.Action).ToString().ToUpperInvariant(),
                ((ApprovalDecisionOrigin)record.Origin).ToString().ToUpperInvariant(),
                record.DecidedAt,
                // The actor's own justification is not minimized away: it is their own record.
                record.Reason,
                record.DecisionDigest,
                record.AuthorityEvidenceDigest,
                targets
                    .Where(target => target.DecisionId == record.Id)
                    .OrderBy(target => target.TargetType, StringComparer.Ordinal)
                    .ThenBy(target => target.TargetId)
                    .Select(target => new ApprovalTargetView(
                        target.TargetType, target.TargetId, target.TargetVersion,
                        target.MaterialSnapshotDigest))
                    .ToArray()))
            .ToArray();
    }

    /// <summary>Organizational audit read of the decisions of a case; unknown cases stay invisible.</summary>
    public async Task<IReadOnlyList<ApprovalCaseDecisionView>> GetCaseDecisionsAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCaseVisibleAsync(organizationId, caseId, cancellationToken);
        var decisions = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.CaseId == caseId)
            .OrderBy(record => record.DecidedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var decisionIds = decisions.Select(record => record.Id).ToArray();
        var counts = (await dbContext.ApprovalDecisionTargets
                .AsNoTracking()
                .Where(record => decisionIds.Contains(record.DecisionId))
                .Select(record => record.DecisionId)
                .ToArrayAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        return decisions
            .Select(record => new ApprovalCaseDecisionView(
                record.Id,
                record.RequirementId,
                record.TaskId,
                ((ApprovalDecisionAction)record.Action).ToString().ToUpperInvariant(),
                record.ActorUserId,
                record.DecidedAt,
                record.DecisionDigest,
                record.AuthorityEvidenceDigest,
                counts.TryGetValue(record.Id, out var count) ? count : 0))
            .ToArray();
    }

    /// <summary>Append-only assignment history of a case with its frozen eligibility evidence.</summary>
    public async Task<IReadOnlyList<ApprovalCaseAssignmentView>> GetCaseAssignmentsAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCaseVisibleAsync(organizationId, caseId, cancellationToken);
        var assignments = await dbContext.ApprovalAssignments
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.CaseId == caseId)
            .OrderBy(record => record.AssignedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        return assignments
            .Select(record => new ApprovalCaseAssignmentView(
                record.Id,
                record.TaskId,
                record.AssigneeUserId,
                record.Cause,
                record.Load,
                record.AssignedAt,
                record.ReleasedAt,
                record.EligibilityEvidenceJson))
            .ToArray();
    }

    /// <summary>
    /// Organizational audit read of a case (REQ-09): every record keeps its actor variant and, for
    /// automatic effects, the immutable link to the root audit that caused it.
    /// </summary>
    public async Task<IReadOnlyList<ApprovalCaseAuditView>> GetCaseAuditAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCaseVisibleAsync(organizationId, caseId, cancellationToken);
        var records = await dbContext.ApprovalAuditEntries
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.CaseId == caseId)
            .OrderBy(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        return records
            .Select(record => new ApprovalCaseAuditView(
                record.Id,
                record.ActorType,
                record.ActorUserId,
                record.ActorWorkloadIssuer,
                record.ActorWorkloadClientId,
                record.ActorSystemId,
                record.CausedByAuditStream,
                record.CausedByAuditId,
                record.AutomaticEffectKey,
                record.Action,
                record.TargetType,
                record.TargetId,
                record.Reason,
                record.OccurredAt))
            .ToArray();
    }

    private async Task EnsureCaseVisibleAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var visible = await dbContext.ApprovalCases
            .AsNoTracking()
            .AnyAsync(
                record => record.Id == caseId && record.OrganizationId == organizationId,
                cancellationToken);
        if (!visible)
        {
            throw new ProcureToPay.Domain.SharedKernel.DomainNotFoundException(
                "The approval case is not visible.");
        }
    }

    private static ApprovalTargetView[] Targets(string targetsJson) =>
        ApprovalJsonPersistence.DeserializeTargets(targetsJson)
            .Select(target => new ApprovalTargetView(
                target.Type, target.Id, target.Version, target.MaterialSnapshotDigest))
            .ToArray();
}
