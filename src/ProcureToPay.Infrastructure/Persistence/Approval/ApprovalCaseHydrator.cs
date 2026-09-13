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
                ApprovalJsonPersistence.DeserializeActions(record.ActionsJson),
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
                new ApprovalWorkloadIdentity(record.OwnerWorkloadIssuer, record.OwnerWorkloadClientId),
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
    /// <summary>
    /// Root audit of a direct command: USER or WORKLOAD actor, no cause and no effect key (REQ-08).
    /// </summary>
    public static ApprovalAuditEntryRecord Root(
        ApprovalCase approvalCase,
        ApprovalAuditActor actor,
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
        Audit(
            approvalCase, actor, causedBy: null, automaticEffectKey: null, action, targetType, targetId,
            scopeJson, reason, beforeJson, afterJson, occurredAt, correlationReference, requirementId);

    /// <summary>
    /// Root audit of a run triggered by an organization change: SYSTEM actor with the immutable
    /// ORGANIZATION causal link and no effect key (it is the cause, not an effect of it).
    /// </summary>
    public static ApprovalAuditEntryRecord OrganizationRoot(
        Guid organizationId,
        Guid triggerAuditId,
        Guid rootAuditId,
        string action,
        Guid targetId,
        string reason,
        DateTimeOffset occurredAt,
        string correlationReference) =>
        new()
        {
            Id = rootAuditId,
            OrganizationId = organizationId,
            CaseId = null,
            RequirementId = null,
            ActorType = "SYSTEM",
            ActorUserId = null,
            ActorWorkloadIssuer = null,
            ActorWorkloadClientId = null,
            ActorSystemId = ApprovalSystemActors.ApprovalWorkflow,
            CausedByAuditStream = "ORGANIZATION",
            CausedByAuditId = triggerAuditId,
            AutomaticEffectKey = null,
            OccurredAt = occurredAt.ToUniversalTime(),
            Action = action,
            TargetType = "RECONCILIATION_RUN",
            TargetId = targetId,
            ScopeJson = "[]",
            Reason = reason,
            BeforeJson = null,
            AfterJson = null,
            CorrelationReference = correlationReference
        };

    /// <summary>
    /// Root audit without a case (run requests): the persistence record is the only consumer, so
    /// the typed union is validated here instead of on <see cref="ApprovalAuditRecord"/>.
    /// </summary>
    public static ApprovalAuditEntryRecord RootForOrganization(
        Guid organizationId,
        ApprovalAuditActor actor,
        Guid rootAuditId,
        string action,
        string targetType,
        Guid targetId,
        string scopeJson,
        string reason,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Type == ApprovalAuditActorType.System)
        {
            throw new DomainValidationException(
                "A case-less root audit cannot be a SYSTEM effect; use the organization root.");
        }

        return new ApprovalAuditEntryRecord
        {
            Id = rootAuditId,
            OrganizationId = organizationId,
            CaseId = null,
            RequirementId = null,
            ActorType = actor.Type == ApprovalAuditActorType.User ? "USER" : "WORKLOAD",
            ActorUserId = actor.UserId,
            ActorWorkloadIssuer = actor.WorkloadIssuer,
            ActorWorkloadClientId = actor.WorkloadClientId,
            ActorSystemId = null,
            CausedByAuditStream = null,
            CausedByAuditId = null,
            AutomaticEffectKey = null,
            OccurredAt = occurredAt.ToUniversalTime(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            ScopeJson = scopeJson,
            Reason = reason,
            BeforeJson = null,
            AfterJson = null,
            CorrelationReference = correlationReference
        };
    }
    /// <summary>
    /// Automatic effect over a case/task: SYSTEM/APPROVAL_WORKFLOW, immutable causal link to the
    /// root audit and the unique effect key of the transition (REQ-08).
    /// </summary>
    public static ApprovalAuditEntryRecord Effect(
        ApprovalCase approvalCase,
        Guid rootAuditId,
        ApprovalAutomaticEffect effect,
        string scopeJson,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid? requirementId = null)
    {
        var causedBy = ApprovalCausalLink.Approval(rootAuditId);
        var effectKey = ApprovalFingerprints.AutomaticEffectKey(
            effect.Action,
            effect.BeforeVersion,
            effect.AfterVersion,
            approvalCase.Id,
            causedBy,
            effect.Source,
            approvalCase.OrganizationId,
            effect.TargetType,
            effect.TargetId);
        return Audit(
            approvalCase,
            ApprovalAuditActor.System(),
            causedBy,
            effectKey,
            effect.Action,
            effect.TargetType,
            effect.TargetId,
            scopeJson,
            effect.Reason,
            effect.BeforeJson,
            effect.AfterJson,
            occurredAt,
            correlationReference,
            requirementId);
    }

    public static ApprovalAuditEntryRecord Audit(
        ApprovalCase approvalCase,
        ApprovalAuditActor actor,
        ApprovalCausalLink? causedBy,
        string? automaticEffectKey,
        string action,
        string targetType,
        Guid targetId,
        string scopeJson,
        string reason,
        string? beforeJson,
        string? afterJson,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid? requirementId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Type == ApprovalAuditActorType.System)
        {
            if (causedBy is null || automaticEffectKey is null)
            {
                throw new DomainValidationException(
                    "A system effect audit requires its causal link and automatic effect key.");
            }
        }
        else if (causedBy is not null || automaticEffectKey is not null)
        {
            throw new DomainValidationException(
                "A root audit cannot carry a causal link or an automatic effect key.");
        }

        return new ApprovalAuditEntryRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = approvalCase.OrganizationId,
            CaseId = approvalCase.Id,
            RequirementId = requirementId,
            ActorType = actor.Type switch
            {
                ApprovalAuditActorType.User => "USER",
                ApprovalAuditActorType.Workload => "WORKLOAD",
                _ => "SYSTEM"
            },
            ActorUserId = actor.UserId,
            ActorWorkloadIssuer = actor.WorkloadIssuer,
            ActorWorkloadClientId = actor.WorkloadClientId,
            ActorSystemId = actor.SystemId,
            CausedByAuditStream = causedBy?.StreamCode,
            CausedByAuditId = causedBy?.AuditId,
            AutomaticEffectKey = automaticEffectKey,
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
    }

    public static ApprovalOutboxEventRecord Outbox(
        ApprovalCase approvalCase,
        ApprovalEntitySource resultSource,
        ApprovalTarget target,
        string result,
        Guid? decisionId,
        string? decisionDigest,
        Guid? sourceCommandId,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(resultSource);
        ApprovalOutboxPolicy.ValidateResultCombination(resultSource.Type, result);
        var eventId = Guid.NewGuid();
        return new ApprovalOutboxEventRecord
        {
            Id = eventId,
            CaseId = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            ResultSourceType = resultSource.Code,
            ResultSourceId = resultSource.Id,
            ResultSourceKey = resultSource.Key,
            SourceCommandId = sourceCommandId,
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
                resultSource,
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
