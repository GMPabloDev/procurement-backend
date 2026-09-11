using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public static class ApprovalFingerprints
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
    public const string ContractVersion = "approval-result/v1";
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
}

public sealed class ApprovalOutboxEvent
{
    private ApprovalOutboxEvent(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid? requirementId,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference)
    {
        Id = id;
        CaseId = caseId;
        OrganizationId = organizationId;
        RequirementId = requirementId;
        Target = target;
        Result = result;
        PayloadJson = payloadJson;
        CreatedAt = createdAt.ToUniversalTime();
        CorrelationReference = correlationReference;
        State = ApprovalOutboxState.Pending;
        ContractVersion = ApprovalOutboxPolicy.ContractVersion;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public Guid OrganizationId { get; }
    public Guid? RequirementId { get; }
    public ApprovalTarget Target { get; }
    public string Result { get; }
    public string ContractVersion { get; }
    public string PayloadJson { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CorrelationReference { get; }
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
        Guid? requirementId,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(target);
        ApprovalOutboxPolicy.ValidateResultCode(result);
        if (id == Guid.Empty || caseId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DomainValidationException("An outbox event requires complete identities.");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new DomainValidationException("An outbox event requires a payload.");
        }

        return new ApprovalOutboxEvent(
            id, caseId, organizationId, requirementId, target, result, payloadJson, createdAt, correlationReference);
    }

    public static ApprovalOutboxEvent Restore(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid? requirementId,
        ApprovalTarget target,
        string result,
        string payloadJson,
        DateTimeOffset createdAt,
        string correlationReference,
        ApprovalOutboxState state,
        int attempts,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset? deliveredAt,
        string? lastError,
        int version)
    {
        var outboxEvent = Create(
            id, caseId, organizationId, requirementId, target, result, payloadJson, createdAt, correlationReference);
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

    /// <summary>Payload that a consumer deduplicates by <c>event_id + contract_version</c>.</summary>
    public static string BuildPayload(
        Guid eventId,
        string contractVersion,
        ApprovalCase approvalCase,
        ApprovalRequirement? requirement,
        ApprovalTarget target,
        string result,
        Guid? decisionId,
        string? decisionDigest,
        DateTimeOffset occurredAt) =>
        ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("case_id", ApprovalCanonicalJson.String(approvalCase.Id)),
            ("contract_version", ApprovalCanonicalJson.String(contractVersion)),
            ("decision_digest", decisionDigest is null ? ApprovalCanonicalJson.Null() : ApprovalCanonicalJson.String(decisionDigest)),
            ("decision_id", ApprovalCanonicalJson.StringOrNull(decisionId)),
            ("event_id", ApprovalCanonicalJson.String(eventId)),
            ("occurred_at", ApprovalCanonicalJson.String(occurredAt)),
            ("organization_id", ApprovalCanonicalJson.String(approvalCase.OrganizationId)),
            ("requirement_key", requirement is null
                ? ApprovalCanonicalJson.Null()
                : ApprovalCanonicalJson.String(requirement.WorkflowRequirementKey)),
            ("result", ApprovalCanonicalJson.String(result)),
            ("subject_id", ApprovalCanonicalJson.String(approvalCase.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(approvalCase.SubjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(approvalCase.SubjectVersion)),
            ("target", ApprovalRequirementDefinition.TargetValue(target))));
}

public sealed class ApprovalAuditRecord
{
    public ApprovalAuditRecord(
        Guid id,
        Guid organizationId,
        Guid? caseId,
        Guid? requirementId,
        string actorType,
        Guid actorId,
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
        Id = id;
        OrganizationId = organizationId;
        CaseId = caseId;
        RequirementId = requirementId;
        ActorType = actorType;
        ActorId = actorId;
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
    public string ActorType { get; }
    public Guid ActorId { get; }
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
