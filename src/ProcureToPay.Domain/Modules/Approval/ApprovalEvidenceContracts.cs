using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>Closed vocabulary of entities that can originate a result or an automatic effect.</summary>
public enum ApprovalEntitySourceType
{
    ApprovalRequirement = 1,
    ExternalPrerequisite = 2,
    ApprovalCase = 3,
    /// <summary>Internal source of an evidence revocation event (SPEC 04 REQ-06).</summary>
    ApprovalEvidence = 4
}

/// <summary>
/// Identity of the persisted entity that produced an event or an automatic effect (REQ-08).
/// <c>result_source</c> only admits requirements and prerequisites; <c>effect_source</c> also
/// admits the case. The canonical value is exactly <c>{id,key,type}</c>.
/// </summary>
public sealed record ApprovalEntitySource
{
    private ApprovalEntitySource(ApprovalEntitySourceType type, Guid id, string? key)
    {
        Type = type;
        Id = id;
        Key = key;
    }

    public ApprovalEntitySourceType Type { get; }
    public Guid Id { get; }
    public string? Key { get; }

    public static ApprovalEntitySource Requirement(Guid requirementId, string workflowRequirementKey) =>
        new(
            ApprovalEntitySourceType.ApprovalRequirement,
            RequireId(requirementId),
            ApprovalLimits.RequireKey(workflowRequirementKey, "workflow requirement key"));

    public static ApprovalEntitySource Prerequisite(Guid prerequisiteId, string prerequisiteKey) =>
        new(
            ApprovalEntitySourceType.ExternalPrerequisite,
            RequireId(prerequisiteId),
            ApprovalLimits.RequireKey(prerequisiteKey, "prerequisite key"));

    public static ApprovalEntitySource Case(Guid caseId) =>
        new(ApprovalEntitySourceType.ApprovalCase, RequireId(caseId), key: null);

    public static ApprovalEntitySource Evidence(Guid evidenceId) =>
        new(ApprovalEntitySourceType.ApprovalEvidence, RequireId(evidenceId), key: null);

    public static ApprovalEntitySource Restore(ApprovalEntitySourceType type, Guid id, string? key)
    {
        _ = RequireId(id);
        return type switch
        {
            ApprovalEntitySourceType.ApprovalRequirement or ApprovalEntitySourceType.ExternalPrerequisite =>
                key is null
                    ? throw new DomainValidationException("A result source needs its stable key.")
                    : new ApprovalEntitySource(type, id, ApprovalLimits.RequireKey(key, "entity source key")),
            ApprovalEntitySourceType.ApprovalCase when key is null => new ApprovalEntitySource(type, id, null),
            ApprovalEntitySourceType.ApprovalEvidence when key is null => new ApprovalEntitySource(type, id, null),
            _ => throw new DomainValidationException("The entity source type is invalid.")
        };
    }

    /// <summary>Parses the contract code stored with an event or effect source; fail-closed.</summary>
    public static ApprovalEntitySourceType ParseCode(string? code) => code switch
    {
        "APPROVAL_REQUIREMENT" => ApprovalEntitySourceType.ApprovalRequirement,
        "EXTERNAL_PREREQUISITE" => ApprovalEntitySourceType.ExternalPrerequisite,
        "APPROVAL_CASE" => ApprovalEntitySourceType.ApprovalCase,
        "APPROVAL_EVIDENCE" => ApprovalEntitySourceType.ApprovalEvidence,
        _ => throw new DomainValidationException("The stored entity source code is invalid.")
    };

    /// <summary>Canonical code exposed by the contract, not the enum name.</summary>
    public string Code => Type switch
    {
        ApprovalEntitySourceType.ApprovalRequirement => "APPROVAL_REQUIREMENT",
        ApprovalEntitySourceType.ExternalPrerequisite => "EXTERNAL_PREREQUISITE",
        ApprovalEntitySourceType.ApprovalCase => "APPROVAL_CASE",
        ApprovalEntitySourceType.ApprovalEvidence => "APPROVAL_EVIDENCE",
        _ => throw new DomainValidationException("The entity source type is invalid.")
    };

    public static string CodeOf(ApprovalEntitySourceType type) => type switch
    {
        ApprovalEntitySourceType.ApprovalRequirement => "APPROVAL_REQUIREMENT",
        ApprovalEntitySourceType.ExternalPrerequisite => "EXTERNAL_PREREQUISITE",
        ApprovalEntitySourceType.ApprovalCase => "APPROVAL_CASE",
        ApprovalEntitySourceType.ApprovalEvidence => "APPROVAL_EVIDENCE",
        _ => throw new DomainValidationException("The entity source type is invalid.")
    };

    public CanonicalValue ToCanonicalValue() => ApprovalCanonicalJson.Object(
        ("id", ApprovalCanonicalJson.String(Id)),
        ("key", Key is null ? ApprovalCanonicalJson.Null() : ApprovalCanonicalJson.String(Key)),
        ("type", ApprovalCanonicalJson.String(Code)));

    private static Guid RequireId(Guid id) =>
        id == Guid.Empty
            ? throw new DomainValidationException("An entity source needs a non-empty identity.")
            : id;
}

/// <summary>Closed actor union of the audit trail (REQ-08): USER, WORKLOAD or SYSTEM.</summary>
public enum ApprovalAuditActorType
{
    User = 1,
    Workload = 2,
    System = 3
}

/// <summary>Fixed system identity of the workflow executor; it is not a credential.</summary>
public static class ApprovalSystemActors
{
    public const string ApprovalWorkflow = "APPROVAL_WORKFLOW";
}

/// <summary>Immutable audit streams that can be referenced as the cause of an automatic effect.</summary>
public enum ApprovalAuditStream
{
    Approval = 1,
    Organization = 2,
    /// <summary>Cause of a delegation transition run (SPEC 04 REQ-03).</summary>
    Delegation = 3
}

public sealed record ApprovalCausalLink
{
    private ApprovalCausalLink(ApprovalAuditStream stream, Guid auditId)
    {
        Stream = stream;
        AuditId = auditId;
    }

    public ApprovalAuditStream Stream { get; }
    public Guid AuditId { get; }

    public static ApprovalCausalLink Approval(Guid auditId) => new(ApprovalAuditStream.Approval, Require(auditId));
    public static ApprovalCausalLink Organization(Guid auditId) => new(ApprovalAuditStream.Organization, Require(auditId));

    /// <summary>Cause of a delegation transition run: the delegation row that scheduled it.</summary>
    public static ApprovalCausalLink Delegation(Guid auditId) => new(ApprovalAuditStream.Delegation, Require(auditId));

    public string StreamCode => Stream switch
    {
        ApprovalAuditStream.Approval => "APPROVAL",
        ApprovalAuditStream.Organization => "ORGANIZATION",
        ApprovalAuditStream.Delegation => "DELEGATION",
        _ => throw new DomainValidationException("The audit stream is invalid.")
    };

    public CanonicalValue ToCanonicalValue() => ApprovalCanonicalJson.Object(
        ("audit_id", ApprovalCanonicalJson.String(AuditId)),
        ("audit_stream", ApprovalCanonicalJson.String(StreamCode)));

    private static Guid Require(Guid auditId) =>
        auditId == Guid.Empty
            ? throw new DomainValidationException("A causal link needs the identity of an audit record.")
            : auditId;
}

/// <summary>
/// Exactly one variant of the audit actor union is complete and the fields of the other
/// variants are null; an empty UUID is never a valid identity (REQ-08).
/// </summary>
public sealed record ApprovalAuditActor
{
    private ApprovalAuditActor(
        ApprovalAuditActorType type,
        Guid? userId,
        string? workloadIssuer,
        string? workloadClientId,
        string? systemId)
    {
        Type = type;
        UserId = userId;
        WorkloadIssuer = workloadIssuer;
        WorkloadClientId = workloadClientId;
        SystemId = systemId;
    }

    public ApprovalAuditActorType Type { get; }
    public Guid? UserId { get; }
    public string? WorkloadIssuer { get; }
    public string? WorkloadClientId { get; }
    public string? SystemId { get; }

    public static ApprovalAuditActor User(Guid userId) =>
        new(
            ApprovalAuditActorType.User,
            userId == Guid.Empty
                ? throw new DomainValidationException("A user audit actor needs a non-empty identity.")
                : userId,
            null, null, null);

    public static ApprovalAuditActor Workload(ApprovalWorkloadIdentity workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (string.IsNullOrWhiteSpace(workload.Issuer) || string.IsNullOrWhiteSpace(workload.ClientId))
        {
            throw new DomainValidationException("A workload audit actor needs its issuer and client id.");
        }

        return new ApprovalAuditActor(ApprovalAuditActorType.Workload, null, workload.Issuer, workload.ClientId, null);
    }

    public static ApprovalAuditActor System() =>
        new(ApprovalAuditActorType.System, null, null, null, ApprovalSystemActors.ApprovalWorkflow);
}

/// <summary>
/// One automatic transition recorded by the aggregate and materialized by the orchestrator as an
/// audit effect: <c>SYSTEM/APPROVAL_WORKFLOW</c>, causal link to the command's root audit and a
/// unique <c>automatic_effect_key</c> (REQ-08).
/// </summary>
public sealed record ApprovalAutomaticEffect(
    string Action,
    ApprovalEntitySource Source,
    string TargetType,
    Guid TargetId,
    int BeforeVersion,
    int AfterVersion,
    string? BeforeJson,
    string? AfterJson,
    string Reason)
{
    public static ApprovalAutomaticEffect Create(
        string action,
        ApprovalEntitySource source,
        string targetType,
        Guid targetId,
        int beforeVersion,
        int afterVersion,
        string? beforeJson,
        string? afterJson,
        string reason)
    {
        _ = ApprovalLimits.RequireCode(action, "effect action");
        _ = ApprovalLimits.RequireCode(targetType, "effect target type");
        if (targetId == Guid.Empty)
        {
            throw new DomainValidationException("An automatic effect needs the identity of its target.");
        }

        if (beforeVersion < 1 || afterVersion < 1)
        {
            throw new DomainValidationException("Automatic effect versions must be positive.");
        }

        return new ApprovalAutomaticEffect(
            action, source ?? throw new DomainValidationException("An automatic effect needs its source."),
            targetType, targetId, beforeVersion, afterVersion, beforeJson, afterJson, reason);
    }
}

public static partial class ApprovalFingerprints
{
    /// <summary>
    /// Effect key of an automatic case/task change (REQ-08): SHA-256 over the exact preimage
    /// <c>{action,actor_system_id,after_version,before_version,canonicalization_version,case_id,
    /// caused_by,effect_source,organization_id,target_id,target_type}</c>. Two different source
    /// entities on the same target and root produce different keys.
    /// </summary>
    public static string AutomaticEffectKey(
        string action,
        int beforeVersion,
        int afterVersion,
        Guid caseId,
        ApprovalCausalLink causedBy,
        ApprovalEntitySource effectSource,
        Guid organizationId,
        string targetType,
        Guid targetId) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("action", ApprovalCanonicalJson.String(action)),
            ("actor_system_id", ApprovalCanonicalJson.String(ApprovalSystemActors.ApprovalWorkflow)),
            ("after_version", ApprovalCanonicalJson.Number(afterVersion)),
            ("before_version", ApprovalCanonicalJson.Number(beforeVersion)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("caused_by", causedBy.ToCanonicalValue()),
            ("effect_source", effectSource.ToCanonicalValue()),
            ("organization_id", ApprovalCanonicalJson.String(organizationId)),
            ("target_id", ApprovalCanonicalJson.String(targetId)),
            ("target_type", ApprovalCanonicalJson.String(targetType))));
}
