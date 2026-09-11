using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalTargetView(string Type, Guid Id, int Version, string MaterialSnapshotDigest);

public sealed record ApprovalTaskView(
    Guid Id,
    Guid RequirementId,
    string Status,
    Guid? AssigneeUserId,
    IReadOnlyList<ApprovalTargetView> Targets);

public sealed record ApprovalRequirementView(
    Guid Id,
    string SourceRequirementKey,
    string WorkflowRequirementKey,
    string StageCode,
    string Role,
    string Status,
    string DecisionScopeJson,
    IReadOnlyList<ApprovalTargetView> Targets);

public sealed record ApprovalCaseView(
    Guid Id,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    Guid OriginatorId,
    Guid? RequesterId,
    string SourceSnapshotDigest,
    IReadOnlyList<ApprovalRequirementView> Requirements,
    IReadOnlyList<ApprovalTaskView> Tasks,
    IReadOnlyList<ApprovalPrerequisiteView> Prerequisites);

public sealed record ApprovalPrerequisiteView(
    Guid Id,
    string Key,
    string OwnerAdapterId,
    string Status,
    IReadOnlyList<ApprovalTargetView> Targets);

/// <summary>Minimized read model (REQ-09): no source documents, balances or personal data.</summary>
public sealed class ApprovalQueryService(ProcureToPayDbContext dbContext)
{
    public async Task<ApprovalCaseView> GetCaseAsync(
        Guid? organizationId,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == caseId &&
                          (organizationId == null || record.OrganizationId == organizationId),
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        return await BuildViewAsync(caseRecord, cancellationToken);
    }

    private async Task<ApprovalCaseView> BuildViewAsync(
        ApprovalCaseRecord caseRecord,
        CancellationToken cancellationToken)
    {
        var caseId = caseRecord.Id;

        var requirements = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .OrderBy(record => record.WorkflowRequirementKey)
            .ToArrayAsync(cancellationToken);
        var tasks = await dbContext.ApprovalTasks
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var prerequisites = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == caseId)
            .OrderBy(record => record.Key)
            .ToArrayAsync(cancellationToken);

        return new ApprovalCaseView(
            caseRecord.Id,
            caseRecord.SubjectType,
            caseRecord.SubjectId,
            caseRecord.SubjectVersion,
            caseRecord.Operation,
            ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
            caseRecord.Version,
            caseRecord.CreatedAt,
            caseRecord.OriginatorId,
            caseRecord.RequesterId,
            caseRecord.SourceSnapshotDigest,
            requirements.Select(record => new ApprovalRequirementView(
                record.Id,
                record.SourceRequirementKey,
                record.WorkflowRequirementKey,
                record.StageCode,
                ((SystemRole)record.Role).ToString().ToUpperInvariant(),
                ((ApprovalRequirementStatus)record.Status).ToString().ToUpperInvariant(),
                record.DecisionScopeJson,
                ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson).Select(ToView).ToArray())).ToArray(),
            tasks.Select(record => new ApprovalTaskView(
                record.Id,
                record.RequirementId,
                ((ApprovalTaskStatus)record.Status).ToString().ToUpperInvariant(),
                record.CurrentAssigneeUserId,
                [])).ToArray(),
            prerequisites.Select(record => new ApprovalPrerequisiteView(
                record.Id,
                record.Key,
                record.OwnerAdapterId,
                ((PrerequisiteStatus)record.Status).ToString().ToUpperInvariant(),
                ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson).Select(ToView).ToArray())).ToArray());
    }

    private static ApprovalTargetView ToView(ApprovalTarget target) =>
        new(target.Type, target.Id, target.Version, target.MaterialSnapshotDigest);

    /// <summary>Case read restricted to the workload that owns it (REQ-09).</summary>
    public async Task<ApprovalCaseView> GetCaseForWorkloadAsync(
        ApprovalWorkloadIdentity workload,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == caseId &&
                          record.WorkloadIssuer == workload.Issuer &&
                          record.WorkloadClientId == workload.ClientId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        return await BuildViewAsync(caseRecord, cancellationToken);
    }
}
