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

    public static string AuthorityEvidenceDigest(string? eligibilityEvidenceJson) =>
        AuthorityEvidenceDigest(eligibilityEvidenceJson, delegationId: null, delegationVersion: null);

    /// <summary>
    /// <c>authority_evidence_digest</c> of SPEC 03: <c>delegation_id</c> and
    /// <c>delegation_version</c> were reserved as nulls and become the real values when the
    /// decision was routed under a delegation (SPEC 04 REQ-02, CA-02); the schema does not change.
    /// </summary>
    public static string AuthorityEvidenceDigest(
        string? eligibilityEvidenceJson,
        Guid? delegationId,
        int? delegationVersion)
    {
        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            throw new DomainValidationException("Eligibility evidence is required.");
        }

        if ((delegationId is null) != (delegationVersion is null))
        {
            throw new DomainValidationException(
                "The delegation id and version of an authority evidence digest are both present or both null.");
        }

        if (delegationId is not null && (delegationId == Guid.Empty || delegationVersion < 1))
        {
            throw new DomainValidationException("A delegation evidence digest needs real identities.");
        }

        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("delegation_id", ApprovalCanonicalJson.StringOrNull(delegationId)),
            ("delegation_version", ApprovalCanonicalJson.NumberOrNull(delegationVersion)),
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
        string eligibilityEvidenceJson,
        Guid? delegationId,
        int? delegationVersion)
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
        DelegationId = delegationId;
        DelegationVersion = delegationVersion;
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
    /// <summary>Real delegation applied to the routing decision, if any (SPEC 04 REQ-02).</summary>
    public Guid? DelegationId { get; }
    public int? DelegationVersion { get; }

    public static ApprovalAssignment Create(
        Guid id,
        Guid caseId,
        Guid taskId,
        Guid organizationId,
        Guid assigneeUserId,
        DateTimeOffset assignedAt,
        int load,
        ApprovalAssignmentCause cause,
        string eligibilityEvidenceJson,
        Guid? delegationId = null,
        int? delegationVersion = null)
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

        if ((delegationId is null) != (delegationVersion is null) ||
            (delegationId is not null && (delegationId == Guid.Empty || delegationVersion < 1)))
        {
            throw new DomainValidationException("An assignment delegation id and version are both present or both null.");
        }

        return new ApprovalAssignment(
            id, caseId, taskId, organizationId, assigneeUserId, assignedAt, load, cause.ToString(),
            eligibilityEvidenceJson, delegationId, delegationVersion);
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
        string eligibilityEvidenceJson,
        Guid? delegationId = null,
        int? delegationVersion = null)
    {
        var assignment = new ApprovalAssignment(
            id, caseId, taskId, organizationId, assigneeUserId, assignedAt, load, cause, eligibilityEvidenceJson,
            delegationId, delegationVersion)
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
        ApprovalDecisionActorType actorType,
        Guid? actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string? decisionKey,
        string? fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        Guid evidenceId,
        int evidenceVersion,
        Guid? sourceDecisionId,
        Guid? rootHumanDecisionId,
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
        ActorType = actorType;
        ActorUserId = actorUserId;
        Reason = reason;
        DecidedAt = decidedAt.ToUniversalTime();
        DecisionKey = decisionKey;
        Fingerprint = fingerprint;
        DecisionDigest = decisionDigest;
        AuthorityEvidenceDigest = authorityEvidenceDigest;
        EligibilityEvidenceJson = eligibilityEvidenceJson;
        EvidenceId = evidenceId;
        EvidenceVersion = evidenceVersion;
        SourceDecisionId = sourceDecisionId;
        RootHumanDecisionId = rootHumanDecisionId;
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
    public ApprovalDecisionActorType ActorType { get; }
    /// <summary>Null for a derive SYSTEM decision; never an empty identity (REQ-05, REQ-08).</summary>
    public Guid? ActorUserId { get; }
    public string Reason { get; }
    public DateTimeOffset DecidedAt { get; }
    /// <summary>Null for a carry-forward decision: it is not a command a caller can replay.</summary>
    public string? DecisionKey { get; }
    public string? Fingerprint { get; }
    public string DecisionDigest { get; }
    public string AuthorityEvidenceDigest { get; }
    public string EligibilityEvidenceJson { get; }
    public Guid EvidenceId { get; }
    public int EvidenceVersion { get; }
    public Guid? SourceDecisionId { get; }
    public Guid? RootHumanDecisionId { get; }
    public IReadOnlyList<ApprovalTarget> Targets { get; }
    public string CorrelationReference { get; }
    public int Version { get; private set; } = 1;

    /// <summary>Human decision of the current assignee (SPEC 03 REQ-06).</summary>
    public static ApprovalDecision Create(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid requirementId,
        Guid taskId,
        ApprovalDecisionAction action,
        Guid actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string decisionKey,
        string fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        Guid evidenceId,
        int evidenceVersion,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference)
    {
        RequireIdentities(id, organizationId, requirementId);
        if (taskId == Guid.Empty)
        {
            throw new DomainValidationException("A human decision requires its task.");
        }

        if (actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A human decision requires its acting user.");
        }

        RequireEvidence(evidenceId, evidenceVersion, eligibilityEvidenceJson);
        return new ApprovalDecision(
            id,
            caseId,
            organizationId,
            requirementId,
            taskId,
            action,
            ApprovalDecisionOrigin.Human,
            ApprovalDecisionActorType.Human,
            actorUserId,
            ApprovalLimits.RequireReason(reason, "Decision reason"),
            decidedAt,
            ApprovalLimits.RequireKey(decisionKey, "decision_key"),
            ApprovalLimits.RequireSha256(fingerprint, "decision fingerprint"),
            ApprovalLimits.RequireSha256(decisionDigest, "decision digest"),
            ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"),
            eligibilityEvidenceJson,
            evidenceId,
            evidenceVersion,
            sourceDecisionId: null,
            rootHumanDecisionId: id,
            RequireTargets(targets),
            correlationReference);
    }

    /// <summary>
    /// Derived decision created by carry-forward (SPEC 04 REQ-05): SYSTEM/APPROVAL_WORKFLOW,
    /// no task, no decision key and an immutable cause towards the human root decision.
    /// </summary>
    public static ApprovalDecision CreateCarryForward(
        Guid id,
        Guid caseId,
        Guid organizationId,
        Guid requirementId,
        string reason,
        DateTimeOffset decidedAt,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        Guid evidenceId,
        int evidenceVersion,
        Guid sourceDecisionId,
        Guid rootHumanDecisionId,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference)
    {
        RequireIdentities(id, organizationId, requirementId);
        if (sourceDecisionId == Guid.Empty || rootHumanDecisionId == Guid.Empty)
        {
            throw new DomainValidationException(
                "A carry-forward decision requires its source and root human decisions.");
        }

        if (sourceDecisionId == id || rootHumanDecisionId == id)
        {
            throw new DomainValidationException("A carry-forward decision cannot derive from itself.");
        }

        RequireEvidence(evidenceId, evidenceVersion, eligibilityEvidenceJson);
        return new ApprovalDecision(
            id,
            caseId,
            organizationId,
            requirementId,
            taskId: null,
            ApprovalDecisionAction.Approve,
            ApprovalDecisionOrigin.CarryForward,
            ApprovalDecisionActorType.System,
            actorUserId: null,
            ApprovalLimits.RequireReason(reason, "Decision reason"),
            decidedAt,
            decisionKey: null,
            fingerprint: null,
            ApprovalLimits.RequireSha256(decisionDigest, "decision digest"),
            ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"),
            eligibilityEvidenceJson,
            evidenceId,
            evidenceVersion,
            sourceDecisionId,
            rootHumanDecisionId,
            RequireTargets(targets),
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
        ApprovalDecisionActorType actorType,
        Guid? actorUserId,
        string reason,
        DateTimeOffset decidedAt,
        string? decisionKey,
        string? fingerprint,
        string decisionDigest,
        string authorityEvidenceDigest,
        string eligibilityEvidenceJson,
        Guid evidenceId,
        int evidenceVersion,
        Guid? sourceDecisionId,
        Guid? rootHumanDecisionId,
        IEnumerable<ApprovalTarget> targets,
        string correlationReference,
        int version)
    {
        RequireIdentities(id, organizationId, requirementId);
        RequireEvidence(evidenceId, evidenceVersion, eligibilityEvidenceJson);
        var decision = new ApprovalDecision(
            id,
            caseId,
            organizationId,
            requirementId,
            taskId,
            action,
            origin,
            actorType,
            actorUserId,
            ApprovalLimits.RequireReason(reason, "Decision reason"),
            decidedAt,
            decisionKey,
            fingerprint,
            ApprovalLimits.RequireSha256(decisionDigest, "decision digest"),
            ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"),
            eligibilityEvidenceJson,
            evidenceId,
            evidenceVersion,
            sourceDecisionId,
            rootHumanDecisionId,
            RequireTargets(targets),
            correlationReference)
        {
            Version = version
        };
        return decision;
    }

    private static void RequireIdentities(Guid id, Guid organizationId, Guid requirementId)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || requirementId == Guid.Empty)
        {
            throw new DomainValidationException("A decision requires complete identities.");
        }
    }

    private static void RequireEvidence(Guid evidenceId, int evidenceVersion, string eligibilityEvidenceJson)
    {
        if (evidenceId == Guid.Empty)
        {
            throw new DomainValidationException("A decision requires its authority evidence identity.");
        }

        if (evidenceVersion < 1)
        {
            throw new DomainValidationException("A decision requires a positive evidence version.");
        }

        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            throw new DomainValidationException("A decision requires EligibilityEvidence.");
        }
    }

    private static ApprovalTarget[] RequireTargets(IEnumerable<ApprovalTarget>? targets)
    {
        var materialized = (targets ?? throw new DomainValidationException("A decision needs its targets."))
            .ToArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("A decision needs at least one target.");
        }

        return materialized;
    }
}

public static class ApprovalOutboxPolicy
{
    /// <summary>Contract version published by decisions of a human assignee (SPEC 03 REQ-08).</summary>
    public const string ContractVersion = "approval-result/v2";

    /// <summary>
    /// Every contract version this revision may publish: the v2 result, the derived
    /// <c>approval-result/v3</c> and the two SPEC 04 lifecycle/revocation events (REQ-04, REQ-05,
    /// REQ-06). An outbox row with any other version is a legacy row the preflight refuses.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownContractVersions = new HashSet<string>(StringComparer.Ordinal)
    {
        ContractVersion,
        ApprovalEvolutionCodes.ResultContractVersionV3,
        ApprovalEvolutionCodes.LifecycleContractVersion,
        ApprovalEvolutionCodes.EvidenceRevokedContractVersion
    };

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
        if (result is not ("APPROVED" or "REJECTED" or "CHANGES_REQUESTED" or "CANCELLED" or "SATISFIED" or "FAILED"
            or "SUPERSEDED" or "REVOKED"))
        {
            throw new DomainValidationException("The outbox result code is invalid.");
        }
    }

    /// <summary>
    /// <c>result_source.type</c> and <c>result</c> must form a valid combination (REQ-08):
    /// requirements admit APPROVED/REJECTED/CHANGES_REQUESTED/CANCELLED, prerequisites admit
    /// SATISFIED/FAILED/CANCELLED, a superseded case admits SUPERSEDED and a revoked evidence
    /// admits REVOKED (SPEC 04 REQ-04, REQ-06).
    /// </summary>
    public static void ValidateResultCombination(ApprovalEntitySourceType type, string result)
    {
        ValidateResultCode(result);
        var valid = type switch
        {
            ApprovalEntitySourceType.ApprovalRequirement =>
                result is "APPROVED" or "REJECTED" or "CHANGES_REQUESTED" or "CANCELLED",
            ApprovalEntitySourceType.ExternalPrerequisite => result is "SATISFIED" or "FAILED" or "CANCELLED",
            ApprovalEntitySourceType.ApprovalCase => result is "SUPERSEDED",
            ApprovalEntitySourceType.ApprovalEvidence => result is "REVOKED",
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
        Guid? sourceCommandId,
        string contractVersion)
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
        ContractVersion = contractVersion;
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
        Guid? sourceCommandId = null,
        string? contractVersion = null)
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
            correlationReference, sourceCommandId,
            contractVersion is null
                ? ApprovalOutboxPolicy.ContractVersion
                : ApprovalLimits.RequireSchemaVersion(contractVersion, "contract_version"));
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
        int version,
        string? contractVersion = null)
    {
        var outboxEvent = Create(
            id, caseId, organizationId, resultSource, target, result, payloadJson, createdAt,
            correlationReference, sourceCommandId, contractVersion);
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
