using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public static partial class ApprovalFingerprints
{
    public static string DecisionFingerprint(
        Guid caseId,
        Guid requirementId,
        Guid taskId,
        int requirementVersion,
        int taskVersion,
        IEnumerable<ApprovalTarget> targets,
        ApprovalDecisionAction action,
        string reason,
        Guid actorUserId) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("action", ApprovalCanonicalJson.String(action)),
            ("actor_user_id", ApprovalCanonicalJson.String(actorUserId)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("reason", ApprovalCanonicalJson.String(ApprovalLimits.RequireReason(reason))),
            ("requirement_id", ApprovalCanonicalJson.String(requirementId)),
            ("requirement_version", ApprovalCanonicalJson.Number(requirementVersion)),
            ("targets", ApprovalRequirementDefinition.TargetsValue(targets)),
            ("task_id", ApprovalCanonicalJson.String(taskId)),
            ("task_version", ApprovalCanonicalJson.Number(taskVersion))));

    public static string SignalFingerprint(
        Guid prerequisiteId,
        int prerequisiteVersion,
        ApprovalWorkloadIdentity ownerWorkload,
        bool satisfied,
        string? evidenceReference,
        string? evidenceDigest,
        string signalKey) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("evidence_digest", evidenceDigest is null
                ? ApprovalCanonicalJson.Null()
                : ApprovalCanonicalJson.String(ApprovalLimits.RequireSha256(evidenceDigest, "signal evidence digest"))),
            ("evidence_reference", evidenceReference is null
                ? ApprovalCanonicalJson.Null()
                : ApprovalCanonicalJson.String(evidenceReference)),
            ("owner_client_id", ApprovalCanonicalJson.String(ownerWorkload.ClientId)),
            ("owner_issuer", ApprovalCanonicalJson.String(ownerWorkload.Issuer)),
            ("prerequisite_id", ApprovalCanonicalJson.String(prerequisiteId)),
            ("prerequisite_version", ApprovalCanonicalJson.Number(prerequisiteVersion)),
            ("result", ApprovalCanonicalJson.String(satisfied ? "SATISFIED" : "FAILED")),
            ("signal_key", ApprovalCanonicalJson.String(signalKey))));

    public static string AuthorityEvidenceDigest(string? eligibilityEvidenceJson)
    {
        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            throw new DomainValidationException("Eligibility evidence is required.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("delegation_id", ApprovalCanonicalJson.Null()),
            ("delegation_version", ApprovalCanonicalJson.Null()),
            ("eligibility_evidence", ApprovalCanonicalJson.String(eligibilityEvidenceJson))));
    }

    public static string WorkflowDecisionDigest(
        Guid decisionId,
        int decisionVersion,
        Guid caseId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string sourceSnapshotDigest,
        string workflowRequirementKey,
        string decisionScopeDigest,
        IEnumerable<ApprovalTarget> targets,
        ApprovalDecisionAction action,
        ApprovalDecisionOrigin origin,
        Guid actorUserId,
        DateTimeOffset decidedAt,
        string reason,
        string authorityEvidenceDigest,
        IEnumerable<Guid> excludedUserIds) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("action", ApprovalCanonicalJson.String(action)),
            ("actor_user_id", ApprovalCanonicalJson.String(actorUserId)),
            ("authority_evidence_digest", ApprovalCanonicalJson.String(authorityEvidenceDigest)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("decided_at", ApprovalCanonicalJson.String(decidedAt)),
            ("decision_id", ApprovalCanonicalJson.String(decisionId)),
            ("decision_scope_digest", ApprovalCanonicalJson.String(decisionScopeDigest)),
            ("decision_version", ApprovalCanonicalJson.Number(decisionVersion)),
            ("exclusions", ApprovalCanonicalJson.Set(excludedUserIds.Select(ApprovalCanonicalJson.String))),
            ("origin", ApprovalCanonicalJson.String(origin)),
            ("reason", ApprovalCanonicalJson.String(ApprovalLimits.RequireReason(reason))),
            ("requirement_key", ApprovalCanonicalJson.String(workflowRequirementKey)),
            ("segregation_satisfied", ApprovalCanonicalJson.Bool(true)),
            ("snapshot_digest", ApprovalCanonicalJson.String(sourceSnapshotDigest)),
            ("subject_id", ApprovalCanonicalJson.String(subjectId)),
            ("subject_type", ApprovalCanonicalJson.String(subjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(subjectVersion)),
            ("targets", ApprovalRequirementDefinition.TargetsValue(targets))));
}

public sealed class ApprovalAssignment
{
    private ApprovalAssignment(
        Guid id,
        Guid caseId,
        Guid taskId,
        Guid organizationId,
        Guid assigneeUserId,
        DateTimeOffset assignedAt,
        int load,
        string cause,
        string eligibilityEvidenceJson)
    {
        Id = id;
        CaseId = caseId;
        TaskId = taskId;
        OrganizationId = organizationId;
        AssigneeUserId = assigneeUserId;
        AssignedAt = assignedAt.ToUniversalTime();
        Load = load;
        Cause = cause;
        EligibilityEvidenceJson = eligibilityEvidenceJson;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public Guid TaskId { get; }
    public Guid OrganizationId { get; }
    public Guid AssigneeUserId { get; }
    public DateTimeOffset AssignedAt { get; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public int Load { get; }
    public string Cause { get; }
    public string EligibilityEvidenceJson { get; }

    public static ApprovalAssignment Create(
        Guid id,
        Guid caseId,
        Guid taskId,
        Guid organizationId,
        Guid assigneeUserId,
        DateTimeOffset assignedAt,
        int load,
        ApprovalAssignmentCause cause,
        string eligibilityEvidenceJson)
    {
        if (id == Guid.Empty || taskId == Guid.Empty || organizationId == Guid.Empty || assigneeUserId == Guid.Empty)
        {
            throw new DomainValidationException("An assignment requires complete identities.");
        }

        if (load < 0)
        {
            throw new DomainValidationException("An observed load cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            throw new DomainValidationException("An assignment requires EligibilityEvidence.");
        }

        return new ApprovalAssignment(
            id, caseId, taskId, organizationId, assigneeUserId, assignedAt, load, cause.ToString(),
            eligibilityEvidenceJson);
    }

    public static ApprovalAssignment Restore(
        Guid id,
        Guid caseId,
        Guid taskId,
        Guid organizationId,
        Guid assigneeUserId,
        DateTimeOffset assignedAt,
        DateTimeOffset? releasedAt,
        int load,
        string cause,
        string eligibilityEvidenceJson)
    {
        var assignment = new ApprovalAssignment(
            id, caseId, taskId, organizationId, assigneeUserId, assignedAt, load, cause, eligibilityEvidenceJson)
        {
            ReleasedAt = releasedAt
        };
        return assignment;
    }

    public void Release(DateTimeOffset releasedAt) => ReleasedAt = releasedAt.ToUniversalTime();
}

public sealed class ApprovalDecision
{
    private ApprovalDecision(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid requirementId,
        Guid? taskId,
        ApprovalDecisionAction action,
        ApprovalDecisionOrigin origin,
        Guid actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string decisionKey,
        string fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference)
    {
        Id = id;
        CaseId = caseId;
        OrganizationId = organizationId;
        RequirementId = requirementId;
        TaskId = taskId;
        Action = action;
        Origin = origin;
        ActorUserId = actorUserId;
        Reason = reason;
        DecidedAt = decidedAt.ToUniversalTime();
        DecisionKey = decisionKey;
        Fingerprint = fingerprint;
        DecisionDigest = decisionDigest;
        AuthorityEvidenceDigest = authorityEvidenceDigest;
        EligibilityEvidenceJson = eligibilityEvidenceJson;
        Targets = targets.ToImmutableArray();
        CorrelationReference = correlationReference;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public Guid OrganizationId { get; }
    public Guid RequirementId { get; }
    public Guid? TaskId { get; }
    public ApprovalDecisionAction Action { get; }
    public ApprovalDecisionOrigin Origin { get; }
    public Guid ActorUserId { get; }
    public string Reason { get; }
    public DateTimeOffset DecidedAt { get; }
    public string DecisionKey { get; }
    public string Fingerprint { get; }
    public string DecisionDigest { get; }
    public string AuthorityEvidenceDigest { get; }
    public string EligibilityEvidenceJson { get; }
    public IReadOnlyList<ApprovalTarget> Targets { get; }
    public string CorrelationReference { get; }
    public int Version { get; private set; } = 1;

    public static ApprovalDecision Create(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid requirementId,
        Guid? taskId,
        ApprovalDecisionAction action,
        ApprovalDecisionOrigin origin,
        Guid actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string decisionKey,
        string fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || requirementId == Guid.Empty ||
            actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A decision requires complete identities.");
        }

        var materialized = (targets ?? throw new DomainValidationException("A decision needs its targets."))
            .ToImmutableArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("A decision needs at least one target.");
        }

        return new ApprovalDecision(
            id, caseId, organizationId, requirementId, taskId, action, origin, actorUserId,
            ApprovalLimits.RequireReason(reason, "Decision reason"),
            decidedAt,
            ApprovalLimits.RequireKey(decisionKey, "decision_key"),
            ApprovalLimits.RequireSha256(fingerprint, "decision fingerprint"),
            ApprovalLimits.RequireSha256(decisionDigest, "decision digest"),
            ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"),
            eligibilityEvidenceJson ?? throw new DomainValidationException("A decision requires EligibilityEvidence."),
            materialized,
            correlationReference);
    }

    public static ApprovalDecision Restore(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid requirementId,
        Guid? taskId,
        ApprovalDecisionAction action,
        ApprovalDecisionOrigin origin,
        Guid actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string decisionKey,
        string fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference,
        int version)
    {
        var decision = Create(
            id, caseId, organizationId, requirementId, taskId, action, origin, actorUserId, reason, decidedAt,
            decisionKey, fingerprint, decisionDigest, authorityEvidenceDigest, eligibilityEvidenceJson, targets,
            correlationReference);
        decision.Version = version;
        return decision;
    }
}

public static class ApprovalOutboxPolicy
{
    /// <summary>Only <c>approval-result/v2</c> is publishable by this revision (REQ-08).</summary>
    public const string ContractVersion = "approval-result/v2";
    public const int MaxAttempts = 10;

    public static TimeSpan DelayForAttempt(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(30),
        4 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10)
    };

    public static void ValidateResultCode(string result)
    {
        if (result is not ("APPROVED" or "REJECTED" or "CHANGES_REQUESTED" or "CANCELLED" or "SATISFIED" or "FAILED"))
        {
            throw new DomainValidationException("The outbox result code is invalid.");
        }
    }

    /// <summary>
    /// <c>result_source.type</c> and <c>result</c> must form a valid combination (REQ-08):
    /// requirements admit APPROVED/REJECTED/CHANGES_REQUESTED/CANCELLED and prerequisites admit
    /// SATISFIED/FAILED/CANCELLED.
    /// </summary>
    public static void ValidateResultCombination(ApprovalEntitySourceType type, string result)
    {
        ValidateResultCode(result);
        var valid = type switch
        {
            ApprovalEntitySourceType.ApprovalRequirement =>
                result is "APPROVED" or "REJECTED" or "CHANGES_REQUESTED" or "CANCELLED",
            ApprovalEntitySourceType.ExternalPrerequisite => result is "SATISFIED" or "FAILED" or "CANCELLED",
            _ => false
        };
        if (!valid)
        {
            throw new DomainValidationException(
                "The result source type and result code are not a valid combination.");
        }
    }
}

public sealed class ApprovalOutboxEvent
{
    private ApprovalOutboxEvent(
        Guid id,
        Guid caseId,
        Guid organizationId,
        ApprovalEntitySource resultSource,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference,
        Guid? sourceCommandId)
    {
        Id = id;
        CaseId = caseId;
        OrganizationId = organizationId;
        ResultSource = resultSource;
        Target = target;
        Result = result;
        PayloadJson = payloadJson;
        CreatedAt = createdAt.ToUniversalTime();
        CorrelationReference = correlationReference;
        SourceCommandId = sourceCommandId;
        State = ApprovalOutboxState.Pending;
        ContractVersion = ApprovalOutboxPolicy.ContractVersion;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public Guid OrganizationId { get; }
    public ApprovalEntitySource ResultSource { get; }
    public ApprovalTarget Target { get; }
    public string Result { get; }
    public string ContractVersion { get; }
    public string PayloadJson { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CorrelationReference { get; }
    /// <summary>Internal link to the decision or signal that produced the event, for replay evidence.</summary>
    public Guid? SourceCommandId { get; }
    public ApprovalOutboxState State { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public string? LastError { get; private set; }
    public int Version { get; private set; } = 1;

    public static ApprovalOutboxEvent Create(
        Guid id,
        Guid caseId,
        Guid organizationId,
        ApprovalEntitySource resultSource,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference,
        Guid? sourceCommandId = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resultSource);
        ApprovalOutboxPolicy.ValidateResultCombination(resultSource.Type, result);
        if (id == Guid.Empty || caseId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DomainValidationException("An outbox event requires complete identities.");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new DomainValidationException("An outbox event requires a payload.");
        }

        return new ApprovalOutboxEvent(
            id, caseId, organizationId, resultSource, target, result, payloadJson, createdAt,
            correlationReference, sourceCommandId);
    }

    public static ApprovalOutboxEvent Restore(
        Guid id,
        Guid caseId,
        Guid organizationId,
        ApprovalEntitySource resultSource,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference,
        Guid? sourceCommandId,
        ApprovalOutboxState state,
        int attempts,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset? deliveredAt,
        string? lastError,
        int version)
    {
        var outboxEvent = Create(
            id, caseId, organizationId, resultSource, target, result, payloadJson, createdAt,
            correlationReference, sourceCommandId);
        outboxEvent.State = state;
        outboxEvent.Attempts = attempts;
        outboxEvent.NextAttemptAt = nextAttemptAt;
        outboxEvent.DeliveredAt = deliveredAt;
        outboxEvent.LastError = lastError;
        outboxEvent.Version = version;
        return outboxEvent;
    }

    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        if (State != ApprovalOutboxState.Pending)
        {
            throw new DomainConflictException("Only a pending outbox event can be delivered.");
        }

        State = ApprovalOutboxState.Delivered;
        DeliveredAt = deliveredAt.ToUniversalTime();
        NextAttemptAt = null;
        Version++;
    }

    public void RegisterFailure(string error, DateTimeOffset occurredAt)
    {
        if (State != ApprovalOutboxState.Pending)
        {
            throw new DomainConflictException("Only a pending outbox event can fail.");
        }

        Attempts++;
        NextAttemptAt = occurredAt.ToUniversalTime() + ApprovalOutboxPolicy.DelayForAttempt(Attempts);
        LastError = error.Length > 400 ? error[..400] : error;
        if (Attempts >= ApprovalOutboxPolicy.MaxAttempts)
        {
            State = ApprovalOutboxState.DeadLetter;
        }

        Version++;
    }

    public void Replay(DateTimeOffset occurredAt)
    {
        if (State == ApprovalOutboxState.Delivered)
        {
            throw new DomainConflictException("A delivered event does not need a replay.");
        }

        State = ApprovalOutboxState.Pending;
        Attempts = 0;
        NextAttemptAt = occurredAt.ToUniversalTime();
        LastError = null;
        Version++;
    }

    /// <summary>
    /// Payload of <c>approval-result/v2</c> with exactly its declared properties (REQ-08); a
    /// consumer deduplicates by <c>event_id + contract_version</c>. <c>decision_digest</c> is the
    /// workflow decision digest when the result comes from a decision and null otherwise.
    /// </summary>
    public static string BuildPayload(
        Guid eventId,
        string contractVersion,
        ApprovalCase approvalCase,
        ApprovalEntitySource resultSource,
        ApprovalTarget target,
        string result,
        Guid? decisionId,
        string? decisionDigest,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(approvalCase);
        ArgumentNullException.ThrowIfNull(resultSource);
        ApprovalOutboxPolicy.ValidateResultCombination(resultSource.Type, result);
        var decisionResult = result is "APPROVED" or "REJECTED" or "CHANGES_REQUESTED";
        if (decisionResult)
        {
            if (decisionId is null || decisionId == Guid.Empty || decisionDigest is null)
            {
                throw new DomainValidationException(
                    "A decision result requires its decision id and workflow decision digest.");
            }

            _ = ApprovalLimits.RequireSha256(decisionDigest, "decision digest");
        }
        else if (decisionId is not null || decisionDigest is not null)
        {
            throw new DomainValidationException(
                "A non-decision result must publish a null decision id and digest.");
        }

        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("case_id", ApprovalCanonicalJson.String(approvalCase.Id)),
            ("contract_version", ApprovalCanonicalJson.String(contractVersion)),
            ("decision_digest", decisionDigest is null ? ApprovalCanonicalJson.Null() : ApprovalCanonicalJson.String(decisionDigest)),
            ("decision_id", ApprovalCanonicalJson.StringOrNull(decisionId)),
            ("event_id", ApprovalCanonicalJson.String(eventId)),
            ("occurred_at", ApprovalCanonicalJson.String(occurredAt)),
            ("organization_id", ApprovalCanonicalJson.String(approvalCase.OrganizationId)),
            ("result", ApprovalCanonicalJson.String(result)),
            ("result_source", resultSource.ToCanonicalValue()),
            ("subject_id", ApprovalCanonicalJson.String(approvalCase.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(approvalCase.SubjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(approvalCase.SubjectVersion)),
            ("target", ApprovalRequirementDefinition.TargetValue(target))));
    }
}

/// <summary>
/// Domain view of an append-only audit record (REQ-08): closed actor union, optional immutable
/// causal link to a root audit and the automatic effect key of a workflow-executed change.
/// </summary>
public sealed class ApprovalAuditRecord
{
    public ApprovalAuditRecord(
        Guid id,
        Guid organizationId,
        Guid? caseId,
        Guid? requirementId,
        ApprovalAuditActor actor,
        ApprovalCausalLink? causedBy,
        string? automaticEffectKey,
        DateTimeOffset occurredAt,
        string action,
        string targetType,
        Guid targetId,
        string scopeJson,
        string reason,
        string? beforeJson,
        string? afterJson,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Id = id;
        OrganizationId = organizationId;
        CaseId = caseId;
        RequirementId = requirementId;
        Actor = actor;
        CausedBy = causedBy;
        AutomaticEffectKey = automaticEffectKey is null
            ? null
            : ApprovalLimits.RequireSha256(automaticEffectKey, "automatic_effect_key");
        if (actor.Type == ApprovalAuditActorType.System)
        {
            if (causedBy is null || this.AutomaticEffectKey is null)
            {
                throw new DomainValidationException(
                    "A system effect requires a causal link and its automatic effect key.");
            }
        }
        else if (causedBy is not null || this.AutomaticEffectKey is not null)
        {
            throw new DomainValidationException(
                "A root audit keeps a null cause and a null automatic effect key.");
        }

        OccurredAt = occurredAt.ToUniversalTime();
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        ScopeJson = scopeJson;
        Reason = reason;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
        CorrelationReference = correlationReference;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid? CaseId { get; }
    public Guid? RequirementId { get; }
    public ApprovalAuditActor Actor { get; }
    public ApprovalCausalLink? CausedBy { get; }
    public string? AutomaticEffectKey { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Action { get; }
    public string TargetType { get; }
    public Guid TargetId { get; }
    public string ScopeJson { get; }
    public string Reason { get; }
    public string? BeforeJson { get; }
    public string? AfterJson { get; }
    public string CorrelationReference { get; }
}
