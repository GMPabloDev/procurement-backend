using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Hydrates the approval aggregate from persisted rows (no business validation beyond
/// structural integrity; the schema is the source of truth for status).
/// </summary>
/// <remarks>Part of the Approval persistence boundary; receives already-validated records.</remarks>
public static class ApprovalCaseHydrator
{
    public static ApprovalCase Hydrate(
        ApprovalCaseRecord caseRecord,
        IEnumerable<ApprovalRequirementRecord> requirementRecords,
        IEnumerable<ApprovalPrerequisiteRecord> prerequisiteRecords,
        IEnumerable<ApprovalTaskRecord> taskRecords)
    {
        var requirements = requirementRecords
            .Select(record => ApprovalRequirement.Restore(
                record.Id,
                record.CaseId,
                record.SourceRequirementKey,
                record.WorkflowRequirementKey,
                record.StageCode,
                (SystemRole)record.Role,
                ApprovalJsonPersistence.DeserializeAuthority(record.AuthorityJson),
                record.DecisionScopeJson,
                ApprovalJsonPersistence.DeserializeGuids(record.ExcludedUserIdsJson),
                ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson),
                ApprovalJsonPersistence.DeserializeDependencies(record.DependenciesJson),
                (ApprovalRequirementStatus)record.Status,
                record.Version))
            .ToArray();
        var prerequisites = prerequisiteRecords
            .Select(record => ExternalPrerequisite.Restore(
                record.Id,
                record.CaseId,
                record.Key,
                record.OwnerAdapterId,
                record.OwnerAdapterVersion,
                record.SourceControlType,
                record.SourceControlDigest,
                record.ParametersJson,
                ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson),
                (PrerequisiteStatus)record.Status,
                record.Version,
                record.SignalKey,
                record.SignalFingerprint,
                record.ResolvedAt))
            .ToArray();
        var tasks = taskRecords
            .Select(record => ApprovalTask.Restore(
                record.Id,
                record.CaseId,
                record.RequirementId,
                (ApprovalTaskStatus)record.Status,
                record.CurrentAssigneeUserId,
                record.Version))
            .ToArray();

        return ApprovalCase.Restore(
            caseRecord.Id,
            caseRecord.OrganizationId,
            caseRecord.SubjectType,
            caseRecord.SubjectId,
            caseRecord.SubjectVersion,
            caseRecord.Operation,
            caseRecord.SourceSnapshotDigest,
            new ApprovalWorkloadIdentity(caseRecord.WorkloadIssuer, caseRecord.WorkloadClientId),
            caseRecord.SubmissionKey,
            caseRecord.SubmissionFingerprint,
            caseRecord.OriginatorId,
            caseRecord.RequesterId,
            caseRecord.CreatedAt,
            caseRecord.CorrelationReference,
            (ApprovalCaseStatus)caseRecord.Status,
            caseRecord.Version,
            caseRecord.CancelledAt,
            caseRecord.CancellationReason,
            requirements,
            tasks,
            prerequisites);
    }

    public static async Task<ApprovalCase> LoadAsync(
        ProcureToPayDbContext dbContext,
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var caseRecord = await dbContext.ApprovalCases
            .SingleOrDefaultAsync(record => record.Id == caseId && record.OrganizationId == organizationId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        return await HydrateAsync(dbContext, caseRecord, cancellationToken);
    }

    public static async Task<ApprovalCase> HydrateAsync(
        ProcureToPayDbContext dbContext,
        ApprovalCaseRecord caseRecord,
        CancellationToken cancellationToken)
    {
        var requirementRecords = await dbContext.ApprovalRequirements
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        var prerequisiteRecords = await dbContext.ApprovalPrerequisites
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        var taskRecords = await dbContext.ApprovalTasks
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        return Hydrate(caseRecord, requirementRecords, prerequisiteRecords, taskRecords);
    }
}

/// <summary>Audit and outbox writers shared by the approval services.</summary>
public static class ApprovalEvidence
{
    public static ApprovalAuditEntryRecord Audit(
        ApprovalCase approvalCase,
        string actorType,
        Guid actorId,
        string action,
        string targetType,
        Guid targetId,
        string scopeJson,
        string reason,
        string? beforeJson,
        string? afterJson,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid? requirementId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = approvalCase.OrganizationId,
            CaseId = approvalCase.Id,
            RequirementId = requirementId,
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = occurredAt.ToUniversalTime(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            ScopeJson = scopeJson,
            Reason = reason,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            CorrelationReference = correlationReference
        };

    public static ApprovalOutboxEventRecord Outbox(
        ApprovalCase approvalCase,
        ApprovalRequirement? requirement,
        ApprovalTarget target,
        string result,
        Guid? decisionId,
        string? decisionDigest,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        ApprovalOutboxPolicy.ValidateResultCode(result);
        var eventId = Guid.NewGuid();
        return new ApprovalOutboxEventRecord
        {
            Id = eventId,
            CaseId = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            RequirementId = requirement?.Id,
            TargetType = target.Type,
            TargetId = target.Id,
            TargetVersion = target.Version,
            MaterialSnapshotDigest = target.MaterialSnapshotDigest,
            Result = result,
            ContractVersion = ApprovalOutboxPolicy.ContractVersion,
            PayloadJson = ApprovalOutboxEvent.BuildPayload(
                eventId,
                ApprovalOutboxPolicy.ContractVersion,
                approvalCase,
                requirement,
                target,
                result,
                decisionId,
                decisionDigest,
                occurredAt),
            State = (int)ApprovalOutboxState.Pending,
            Attempts = 0,
            NextAttemptAt = occurredAt.ToUniversalTime(),
            CreatedAt = occurredAt.ToUniversalTime(),
            CorrelationReference = correlationReference,
            Version = 1
        };
    }
}
