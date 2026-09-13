using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public enum DecisionAuthorityEvidenceStatus
{
    Valid = 1,
    Revoked = 2
}

/// <summary>Closed reason codes of an evidence revocation; the free reason stays in audit (REQ-06).</summary>
public enum EvidenceRevocationReasonCode
{
    OwnerInvalidation = 1,
    IncidentContainment = 2
}

/// <summary>Closed actor union of a revocation: the owning workload or an ADMIN containment (REQ-06).</summary>
public enum EvidenceRevocationActorType
{
    Workload = 1,
    Admin = 2
}

public static class ApprovalEvolutionCodes
{
    public const string LifecycleContractVersion = "approval-case-lifecycle/v1";
    public const string EvidenceRevokedContractVersion = "approval-evidence-revoked/v1";
    public const string ResultContractVersionV3 = "approval-result/v3";
    public const string EvidenceValid = "VALID";
    public const string EvidenceRevoked = "REVOKED";
    public const string ReasonOwnerInvalidation = "OWNER_INVALIDATION";
    public const string ReasonIncidentContainment = "INCIDENT_CONTAINMENT";
    public const string ActorWorkload = "WORKLOAD";
    public const string ActorAdmin = "ADMIN";
    public const string LifecycleSuperseded = "SUPERSEDED";

    public static string Code(DecisionAuthorityEvidenceStatus status) => status switch
    {
        DecisionAuthorityEvidenceStatus.Valid => EvidenceValid,
        DecisionAuthorityEvidenceStatus.Revoked => EvidenceRevoked,
        _ => throw new DomainValidationException("The evidence status is invalid.")
    };

    public static string Code(EvidenceRevocationReasonCode reasonCode) => reasonCode switch
    {
        EvidenceRevocationReasonCode.OwnerInvalidation => ReasonOwnerInvalidation,
        EvidenceRevocationReasonCode.IncidentContainment => ReasonIncidentContainment,
        _ => throw new DomainValidationException("The revocation reason code is invalid.")
    };

    public static string Code(EvidenceRevocationActorType actorType) => actorType switch
    {
        EvidenceRevocationActorType.Workload => ActorWorkload,
        EvidenceRevocationActorType.Admin => ActorAdmin,
        _ => throw new DomainValidationException("The revocation actor type is invalid.")
    };

    public static DecisionAuthorityEvidenceStatus ParseStatus(string? code) => code switch
    {
        EvidenceValid => DecisionAuthorityEvidenceStatus.Valid,
        EvidenceRevoked => DecisionAuthorityEvidenceStatus.Revoked,
        _ => throw new DomainValidationException("The stored evidence status is invalid.")
    };

    public static EvidenceRevocationReasonCode ParseReasonCode(string? code) => code switch
    {
        ReasonOwnerInvalidation => EvidenceRevocationReasonCode.OwnerInvalidation,
        ReasonIncidentContainment => EvidenceRevocationReasonCode.IncidentContainment,
        _ => throw new DomainValidationException("The stored revocation reason code is invalid.")
    };

    public static EvidenceRevocationActorType ParseActorType(string? code) => code switch
    {
        ActorWorkload => EvidenceRevocationActorType.Workload,
        ActorAdmin => EvidenceRevocationActorType.Admin,
        _ => throw new DomainValidationException("The stored revocation actor type is invalid.")
    };
}

/// <summary>
/// Declared materiality of one target replacement inside a supersession (REQ-04): the owning
/// adapter asserts the schema and digest, while equality is decided by the workflow.
/// </summary>
public sealed record ApprovalSupersessionMapping(
    ApprovalTarget Previous,
    ApprovalTarget Replacement,
    string MaterialitySchemaVersion,
    string MaterialityDigest)
{
    public string PreviousIdentity =>
        (Previous ?? throw new DomainValidationException("A mapping requires its previous target."))
        .CanonicalIdentity;

    public string ReplacementIdentity =>
        (Replacement ?? throw new DomainValidationException("A mapping requires its replacement target."))
        .CanonicalIdentity;

    public static ApprovalSupersessionMapping Create(
        ApprovalTarget previous,
        ApprovalTarget replacement,
        string materialitySchemaVersion,
        string materialityDigest) =>
        new(
            previous ?? throw new DomainValidationException("A mapping requires its previous target."),
            replacement ?? throw new DomainValidationException("A mapping requires its replacement target."),
            ApprovalLimits.RequireSchemaVersion(materialitySchemaVersion, "materiality_schema_version"),
            ApprovalLimits.RequireSha256(materialityDigest, "materiality_digest"));
}

/// <summary>
/// Append-only record of one full-case supersession (REQ-04, NFR-02): the previous case and its
/// non-terminal nodes became terminal SUPERSEDED and never reopen.
/// </summary>
public sealed class CaseSupersession
{
    private CaseSupersession(
        Guid id,
        Guid organizationId,
        Guid previousCaseId,
        int previousCaseVersion,
        Guid newCaseId,
        string supersessionKey,
        string fingerprint,
        ApprovalWorkloadIdentity workload,
        IEnumerable<ApprovalSupersessionMapping> targetMapping,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        PreviousCaseId = previousCaseId;
        PreviousCaseVersion = previousCaseVersion;
        NewCaseId = newCaseId;
        SupersessionKey = supersessionKey;
        Fingerprint = fingerprint;
        WorkloadIssuer = workload.Issuer;
        WorkloadClientId = workload.ClientId;
        TargetMapping = targetMapping.ToImmutableArray();
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid PreviousCaseId { get; }
    public int PreviousCaseVersion { get; }
    public Guid NewCaseId { get; }
    public string SupersessionKey { get; }
    public string Fingerprint { get; }
    public string WorkloadIssuer { get; }
    public string WorkloadClientId { get; }
    public IReadOnlyList<ApprovalSupersessionMapping> TargetMapping { get; }
    public DateTimeOffset CreatedAt { get; }

    public static CaseSupersession Create(
        Guid id,
        Guid organizationId,
        Guid previousCaseId,
        int previousCaseVersion,
        Guid newCaseId,
        string supersessionKey,
        string fingerprint,
        ApprovalWorkloadIdentity workload,
        IEnumerable<ApprovalSupersessionMapping> targetMapping,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (id == Guid.Empty || organizationId == Guid.Empty || previousCaseId == Guid.Empty ||
            newCaseId == Guid.Empty || previousCaseId == newCaseId)
        {
            throw new DomainValidationException("A supersession requires distinct, complete case identities.");
        }

        if (previousCaseVersion < 1)
        {
            throw new DomainValidationException("A supersession requires the expected previous case version.");
        }

        var mapping = (targetMapping ?? throw new DomainValidationException("A supersession needs its mapping."))
            .ToArray();
        if (mapping.Length == 0)
        {
            throw new DomainValidationException("A supersession needs at least one target mapping.");
        }

        var previous = mapping.Select(entry => entry.PreviousIdentity).ToArray();
        var replacements = mapping.Select(entry => entry.ReplacementIdentity).ToArray();
        if (previous.Length != previous.Distinct(StringComparer.Ordinal).Count() ||
            replacements.Length != replacements.Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException(
                "A supersession mapping must be bijective: every previous and replacement target appears once.");
        }

        return new CaseSupersession(
            id,
            organizationId,
            previousCaseId,
            previousCaseVersion,
            newCaseId,
            ApprovalLimits.RequireKey(supersessionKey, "supersession_key"),
            ApprovalLimits.RequireSha256(fingerprint, "supersession fingerprint"),
            workload,
            mapping,
            createdAt);
    }

    public static CaseSupersession Restore(
        Guid id,
        Guid organizationId,
        Guid previousCaseId,
        int previousCaseVersion,
        Guid newCaseId,
        string supersessionKey,
        string fingerprint,
        ApprovalWorkloadIdentity workload,
        IEnumerable<ApprovalSupersessionMapping> targetMapping,
        DateTimeOffset createdAt) =>
        Create(
            id, organizationId, previousCaseId, previousCaseVersion, newCaseId, supersessionKey, fingerprint,
            workload, targetMapping, createdAt);
}

/// <summary>
/// Stable identity of the authority evidence of a human root decision (REQ-06, DEC-08):
/// carry-forward decisions reference this row instead of copying or rewriting the evidence.
/// </summary>
public sealed class DecisionAuthorityEvidence
{
    private DecisionAuthorityEvidence(
        Guid id,
        Guid organizationId,
        Guid rootHumanDecisionId,
        string digest,
        string evidenceJson,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        RootHumanDecisionId = rootHumanDecisionId;
        Digest = digest;
        EvidenceJson = evidenceJson;
        CreatedAt = createdAt.ToUniversalTime();
        Status = DecisionAuthorityEvidenceStatus.Valid;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid RootHumanDecisionId { get; }
    /// <summary>The <c>authority_evidence_digest</c> v2 preimage of the human decision.</summary>
    public string Digest { get; }
    public string EvidenceJson { get; }
    public int Version { get; private set; } = 1;
    public DecisionAuthorityEvidenceStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static DecisionAuthorityEvidence Create(
        Guid id,
        Guid organizationId,
        Guid rootHumanDecisionId,
        string digest,
        string evidenceJson,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || rootHumanDecisionId == Guid.Empty)
        {
            throw new DomainValidationException("Decision authority evidence requires complete identities.");
        }

        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            throw new DomainValidationException("Decision authority evidence requires its canonical JSON.");
        }

        return new DecisionAuthorityEvidence(
            id,
            organizationId,
            rootHumanDecisionId,
            ApprovalLimits.RequireSha256(digest, "authority evidence digest"),
            evidenceJson,
            createdAt);
    }

    public static DecisionAuthorityEvidence Restore(
        Guid id,
        Guid organizationId,
        Guid rootHumanDecisionId,
        string digest,
        string evidenceJson,
        DateTimeOffset createdAt,
        int version,
        DecisionAuthorityEvidenceStatus status,
        DateTimeOffset? revokedAt)
    {
        var evidence = Create(id, organizationId, rootHumanDecisionId, digest, evidenceJson, createdAt);
        evidence.Version = version;
        evidence.Status = status;
        evidence.RevokedAt = revokedAt;
        return evidence;
    }

    /// <summary>Irreversible: a revoked evidence never becomes valid again (REQ-06).</summary>
    public void Revoke(DateTimeOffset occurredAt)
    {
        if (Status == DecisionAuthorityEvidenceStatus.Revoked)
        {
            throw new DomainConflictException("The decision authority evidence is already revoked.");
        }

        Status = DecisionAuthorityEvidenceStatus.Revoked;
        RevokedAt = occurredAt.ToUniversalTime();
        Version++;
    }
}

/// <summary>
/// Provenance of one carry-forward decision (REQ-05): source and root human decisions, the shared
/// evidence, the target mapping and the canonical proof of equality.
/// </summary>
public sealed class DecisionCarryForwardRecord
{
    private DecisionCarryForwardRecord(
        Guid id,
        Guid organizationId,
        Guid sourceDecisionId,
        Guid rootHumanDecisionId,
        Guid newDecisionId,
        Guid evidenceId,
        int evidenceVersion,
        Guid newCaseId,
        Guid newRequirementId,
        string newRequirementKey,
        string sourceRequirementContractDigest,
        string newRequirementContractDigest,
        string proofDigest,
        ApprovalTarget sourceTarget,
        ApprovalTarget newTarget,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        SourceDecisionId = sourceDecisionId;
        RootHumanDecisionId = rootHumanDecisionId;
        NewDecisionId = newDecisionId;
        EvidenceId = evidenceId;
        EvidenceVersion = evidenceVersion;
        NewCaseId = newCaseId;
        NewRequirementId = newRequirementId;
        NewRequirementKey = newRequirementKey;
        SourceRequirementContractDigest = sourceRequirementContractDigest;
        NewRequirementContractDigest = newRequirementContractDigest;
        ProofDigest = proofDigest;
        SourceTarget = sourceTarget;
        NewTarget = newTarget;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid SourceDecisionId { get; }
    public Guid RootHumanDecisionId { get; }
    public Guid NewDecisionId { get; }
    public Guid EvidenceId { get; }
    public int EvidenceVersion { get; }
    public Guid NewCaseId { get; }
    public Guid NewRequirementId { get; }
    public string NewRequirementKey { get; }
    public string SourceRequirementContractDigest { get; }
    public string NewRequirementContractDigest { get; }
    public string ProofDigest { get; }
    public ApprovalTarget SourceTarget { get; }
    public ApprovalTarget NewTarget { get; }
    public DateTimeOffset CreatedAt { get; }

    public static DecisionCarryForwardRecord Create(
        Guid id,
        Guid organizationId,
        Guid sourceDecisionId,
        Guid rootHumanDecisionId,
        Guid newDecisionId,
        Guid evidenceId,
        int evidenceVersion,
        Guid newCaseId,
        Guid newRequirementId,
        string newRequirementKey,
        string sourceRequirementContractDigest,
        string newRequirementContractDigest,
        string proofDigest,
        ApprovalTarget sourceTarget,
        ApprovalTarget newTarget,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || sourceDecisionId == Guid.Empty ||
            rootHumanDecisionId == Guid.Empty || newDecisionId == Guid.Empty || evidenceId == Guid.Empty ||
            newCaseId == Guid.Empty || newRequirementId == Guid.Empty)
        {
            throw new DomainValidationException("A carry-forward record requires complete identities.");
        }

        if (evidenceVersion < 1)
        {
            throw new DomainValidationException("A carry-forward record requires the evidence version.");
        }

        return new DecisionCarryForwardRecord(
            id,
            organizationId,
            sourceDecisionId,
            rootHumanDecisionId,
            newDecisionId,
            evidenceId,
            evidenceVersion,
            newCaseId,
            newRequirementId,
            ApprovalLimits.RequireKey(newRequirementKey, "workflow requirement key"),
            ApprovalLimits.RequireSha256(sourceRequirementContractDigest, "source requirement contract digest"),
            ApprovalLimits.RequireSha256(newRequirementContractDigest, "new requirement contract digest"),
            ApprovalLimits.RequireSha256(proofDigest, "carry-forward proof digest"),
            sourceTarget ?? throw new DomainValidationException("A carry-forward record needs its source target."),
            newTarget ?? throw new DomainValidationException("A carry-forward record needs its new target."),
            createdAt);
    }

    public static DecisionCarryForwardRecord Restore(
        Guid id,
        Guid organizationId,
        Guid sourceDecisionId,
        Guid rootHumanDecisionId,
        Guid newDecisionId,
        Guid evidenceId,
        int evidenceVersion,
        Guid newCaseId,
        Guid newRequirementId,
        string newRequirementKey,
        string sourceRequirementContractDigest,
        string newRequirementContractDigest,
        string proofDigest,
        ApprovalTarget sourceTarget,
        ApprovalTarget newTarget,
        DateTimeOffset createdAt) =>
        Create(
            id, organizationId, sourceDecisionId, rootHumanDecisionId, newDecisionId, evidenceId,
            evidenceVersion, newCaseId, newRequirementId, newRequirementKey, sourceRequirementContractDigest,
            newRequirementContractDigest, proofDigest, sourceTarget, newTarget, createdAt);
}

/// <summary>
/// Append-only, irreversible revocation of decision authority evidence (REQ-06). The historical
/// decision stays untouched; only future verification and carry-forward are cut.
/// </summary>
public sealed class DecisionEvidenceRevocation
{
    private DecisionEvidenceRevocation(
        Guid id,
        Guid organizationId,
        Guid caseId,
        Guid evidenceId,
        Guid decisionId,
        string revocationKey,
        string fingerprint,
        EvidenceRevocationActorType actorType,
        Guid? actorUserId,
        ApprovalWorkloadIdentity? actorWorkload,
        EvidenceRevocationReasonCode reasonCode,
        string reason,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        CaseId = caseId;
        EvidenceId = evidenceId;
        DecisionId = decisionId;
        RevocationKey = revocationKey;
        Fingerprint = fingerprint;
        ActorType = actorType;
        ActorUserId = actorUserId;
        ActorWorkloadIssuer = actorWorkload?.Issuer;
        ActorWorkloadClientId = actorWorkload?.ClientId;
        ReasonCode = reasonCode;
        Reason = reason;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid CaseId { get; }
    public Guid EvidenceId { get; }
    /// <summary>The human root decision whose evidence was revoked.</summary>
    public Guid DecisionId { get; }
    public string RevocationKey { get; }
    public string Fingerprint { get; }
    public EvidenceRevocationActorType ActorType { get; }
    public Guid? ActorUserId { get; }
    public string? ActorWorkloadIssuer { get; }
    public string? ActorWorkloadClientId { get; }
    public EvidenceRevocationReasonCode ReasonCode { get; }
    public string Reason { get; }
    public DateTimeOffset CreatedAt { get; }

    public static DecisionEvidenceRevocation Create(
        Guid id,
        Guid organizationId,
        Guid caseId,
        Guid evidenceId,
        Guid decisionId,
        string revocationKey,
        string fingerprint,
        EvidenceRevocationActorType actorType,
        Guid? actorUserId,
        ApprovalWorkloadIdentity? actorWorkload,
        EvidenceRevocationReasonCode reasonCode,
        string reason,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || caseId == Guid.Empty ||
            evidenceId == Guid.Empty || decisionId == Guid.Empty)
        {
            throw new DomainValidationException("A revocation requires complete identities.");
        }

        if (actorType == EvidenceRevocationActorType.Admin && (actorUserId is null || actorUserId == Guid.Empty))
        {
            throw new DomainValidationException("An ADMIN revocation requires the acting user.");
        }

        if (actorType == EvidenceRevocationActorType.Workload &&
            (actorWorkload is null || string.IsNullOrWhiteSpace(actorWorkload.Issuer) ||
             string.IsNullOrWhiteSpace(actorWorkload.ClientId)))
        {
            throw new DomainValidationException("A workload revocation requires its issuer and client id.");
        }

        if (actorType == EvidenceRevocationActorType.Admin && actorWorkload is not null)
        {
            throw new DomainValidationException("An ADMIN revocation cannot carry a workload identity.");
        }

        if (actorType == EvidenceRevocationActorType.Workload && actorUserId is not null)
        {
            throw new DomainValidationException("A workload revocation cannot carry a user identity.");
        }

        return new DecisionEvidenceRevocation(
            id,
            organizationId,
            caseId,
            evidenceId,
            decisionId,
            ApprovalLimits.RequireKey(revocationKey, "revocation_key"),
            ApprovalLimits.RequireSha256(fingerprint, "revocation fingerprint"),
            actorType,
            actorUserId,
            actorWorkload,
            reasonCode,
            ApprovalLimits.RequireReason(reason, "Revocation reason"),
            createdAt);
    }

    public static DecisionEvidenceRevocation Restore(
        Guid id,
        Guid organizationId,
        Guid caseId,
        Guid evidenceId,
        Guid decisionId,
        string revocationKey,
        string fingerprint,
        EvidenceRevocationActorType actorType,
        Guid? actorUserId,
        ApprovalWorkloadIdentity? actorWorkload,
        EvidenceRevocationReasonCode reasonCode,
        string reason,
        DateTimeOffset createdAt) =>
        Create(
            id, organizationId, caseId, evidenceId, decisionId, revocationKey, fingerprint, actorType,
            actorUserId, actorWorkload, reasonCode, reason, createdAt);
}

/// <summary>
/// Exact canonical payloads of the new SPEC 04 events (REQ-04, REQ-06, REQ-05). Lifecycle and
/// revocation never widen the closed combinations of <c>approval-result/v2</c>.
/// </summary>
public static class ApprovalEvolutionEvents
{
    public static string BuildLifecyclePayload(
        Guid eventId,
        string contractVersion,
        Guid caseId,
        Guid organizationId,
        Guid previousCaseId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        ApprovalTarget target,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (eventId == Guid.Empty || caseId == Guid.Empty || organizationId == Guid.Empty ||
            previousCaseId == Guid.Empty || previousCaseId == caseId)
        {
            throw new DomainValidationException("A lifecycle event requires complete distinct identities.");
        }

        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("contract_version", ApprovalCanonicalJson.String(contractVersion)),
            ("event_id", ApprovalCanonicalJson.String(eventId)),
            ("occurred_at", ApprovalCanonicalJson.String(occurredAt)),
            ("organization_id", ApprovalCanonicalJson.String(organizationId)),
            ("previous_case_id", ApprovalCanonicalJson.String(previousCaseId)),
            ("result", ApprovalCanonicalJson.String(ApprovalEvolutionCodes.LifecycleSuperseded)),
            ("subject_id", ApprovalCanonicalJson.String(subjectId)),
            ("subject_type", ApprovalCanonicalJson.String(subjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(subjectVersion)),
            ("target", ApprovalRequirementDefinition.TargetValue(target))));
    }

    public static string BuildEvidenceRevokedPayload(
        Guid eventId,
        string contractVersion,
        Guid caseId,
        Guid organizationId,
        Guid decisionId,
        Guid evidenceId,
        int evidenceVersion,
        string actorType,
        string reasonCode,
        Guid revocationId,
        DateTimeOffset occurredAt)
    {
        if (eventId == Guid.Empty || caseId == Guid.Empty || organizationId == Guid.Empty ||
            decisionId == Guid.Empty || evidenceId == Guid.Empty || revocationId == Guid.Empty)
        {
            throw new DomainValidationException("An evidence revocation event requires complete identities.");
        }

        if (evidenceVersion < 1)
        {
            throw new DomainValidationException("An evidence revocation event requires the evidence version.");
        }

        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("actor_type", ApprovalCanonicalJson.String(ApprovalLimits.RequireCode(actorType, "actor_type"))),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("contract_version", ApprovalCanonicalJson.String(contractVersion)),
            ("decision_id", ApprovalCanonicalJson.String(decisionId)),
            ("event_id", ApprovalCanonicalJson.String(eventId)),
            ("evidence_id", ApprovalCanonicalJson.String(evidenceId)),
            ("evidence_version", ApprovalCanonicalJson.Number(evidenceVersion)),
            ("occurred_at", ApprovalCanonicalJson.String(occurredAt)),
            ("organization_id", ApprovalCanonicalJson.String(organizationId)),
            ("reason_code", ApprovalCanonicalJson.String(ApprovalLimits.RequireCode(reasonCode, "reason_code"))),
            ("revocation_id", ApprovalCanonicalJson.String(revocationId))));
    }
}
