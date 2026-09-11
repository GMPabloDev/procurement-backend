using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>Minimized UNASSIGNED read model for ADMIN and AUDITOR (REQ-09).</summary>
public sealed record ApprovalUnassignedView(
    Guid CaseId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    DateTimeOffset CaseCreatedAt,
    Guid RequirementId,
    string WorkflowRequirementKey,
    string StageCode,
    string Role,
    string DecisionScopeJson,
    IReadOnlyList<ApprovalTargetView> Targets);

public sealed record ApprovalReconciliationStateView(
    Guid OrganizationId,
    DateTimeOffset? LastReconciliationCompletedAt,
    bool IsDue);

/// <summary>
/// Administrative reads (REQ-04, REQ-09): ADMIN sees the requirements with no eligible
/// candidate and the reconciliation state, and can never pick an assignee from here.
/// </summary>
public sealed class ApprovalOperationsQueryService(ProcureToPayDbContext dbContext)
{
    public async Task<IReadOnlyList<ApprovalUnassignedView>> GetUnassignedAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from requirement in dbContext.ApprovalRequirements.AsNoTracking()
                join approvalCase in dbContext.ApprovalCases.AsNoTracking()
                    on requirement.CaseId equals approvalCase.Id
                where requirement.OrganizationId == organizationId &&
                      requirement.Status == (int)ApprovalRequirementStatus.Unassigned &&
                      (approvalCase.Status == (int)ApprovalCaseStatus.Open ||
                       approvalCase.Status == (int)ApprovalCaseStatus.Blocked)
                select new { Requirement = requirement, Case = approvalCase })
            .ToArrayAsync(cancellationToken);

        return rows
            .OrderBy(row => row.Case.CreatedAt)
            .ThenBy(row => row.Requirement.WorkflowRequirementKey, StringComparer.Ordinal)
            .Select(row => new ApprovalUnassignedView(
                row.Case.Id,
                row.Case.SubjectType,
                row.Case.SubjectId,
                row.Case.SubjectVersion,
                row.Case.Operation,
                row.Case.CreatedAt,
                row.Requirement.Id,
                row.Requirement.WorkflowRequirementKey,
                row.Requirement.StageCode,
                ((ProcureToPay.Domain.Modules.Organization.SystemRole)row.Requirement.Role)
                    .ToString().ToUpperInvariant(),
                row.Requirement.DecisionScopeJson,
                ApprovalJsonPersistence.DeserializeTargets(row.Requirement.TargetsJson)
                    .Select(target => new ApprovalTargetView(
                        target.Type, target.Id, target.Version, target.MaterialSnapshotDigest))
                    .ToArray()))
            .ToArray();
    }

    public async Task<ApprovalReconciliationStateView> GetReconciliationStateAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var last = await dbContext.ApprovalWorkflowStates
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId)
            .Select(record => (DateTimeOffset?)record.LastReconciliationCompletedAt)
            .SingleOrDefaultAsync(cancellationToken);
        return new ApprovalReconciliationStateView(organizationId, last, IsDue(last, now));
    }

    private static bool IsDue(DateTimeOffset? last, DateTimeOffset now) =>
        last is null || now.ToUniversalTime() - last.Value >= ApprovalReconciliationService.MaxReconciliationAge;
}
