using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>Contract identities of the persisted policy exception extension (SPEC 05).</summary>
public static class PolicyExceptionContract
{
    public const string SubmissionContractVersion = "v1";
    public const string RequestSchemaVersion = "policy-exception-request/v1";
    public const string VerificationRequestVersion = "workflow-verification-request/v1";
    public const string VerificationResponseVersion = "workflow-verification-response/v1";
    public const string VerifierId = "approval-workflow";
    /// <summary>Registry key of the SPEC 02 verifier this service implements.</summary>
    public const string VerifierType = "APPROVAL_WORKFLOW";
    public const string TargetType = "PURCHASE_REQUEST_LINE";
    public const string VerifierReferenceScheme = "approval-exception";
    public const string ExceptionCaseOperation = "POLICY_EXCEPTION";
    public const string ExceptionRequirementStage = "PRE_PROCUREMENT";
    public const string ExceptionRequirementKey = "POLICY_EXCEPTION";
}

/// <summary>Immutable versioned coverage line of a policy exception (SPEC 05 Datos y contratos).</summary>
public sealed record PolicyExceptionTarget
{
    public PolicyExceptionTarget(string type, Guid id, int version, string materialSnapshotDigest)
    {
        Type = ApprovalLimits.RequireCode(type, "exception target type");
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("An exception target requires its immutable identity.");
        }

        Id = id;
        Version = version;
        MaterialSnapshotDigest = ApprovalLimits.RequireSha256(
            materialSnapshotDigest, "exception target material digest");
    }

    public string Type { get; }
    public Guid Id { get; }
    public int Version { get; }
    public string MaterialSnapshotDigest { get; }

    public string CanonicalIdentity => $"{Type}:{Id:D}:{Version}:{MaterialSnapshotDigest}";

    public static CanonicalValue Canonicalize(PolicyExceptionTarget target) => ApprovalCanonicalJson.Object(
        ("id", ApprovalCanonicalJson.String(target.Id)),
        ("material_snapshot_digest", ApprovalCanonicalJson.String(target.MaterialSnapshotDigest)),
        ("type", ApprovalCanonicalJson.String(target.Type)),
        ("version", ApprovalCanonicalJson.Number(target.Version)));

    public static CanonicalValue CanonicalizeSet(IEnumerable<PolicyExceptionTarget> targets) =>
        ApprovalCanonicalJson.Set((targets ?? []).Select(Canonicalize));

    /// <summary>Strict parse of the canonical covered-lines array persisted with an exception.</summary>
    public static IReadOnlyList<PolicyExceptionTarget> ParseCanonicalSet(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException("The persisted policy exception has no covered lines.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new DomainValidationException("The covered lines must be a canonical JSON array.");
            }

            var targets = document.RootElement.EnumerateArray().Select(element =>
            {
                var names = element.EnumerateObject().Select(property => property.Name).ToArray();
                var expected = new[] { "id", "material_snapshot_digest", "type", "version" };
                if (names.Length != expected.Length ||
                    !names.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
                {
                    throw new DomainValidationException(
                        "Every covered line must declare exactly id, material_snapshot_digest, type and version.");
                }

                return new PolicyExceptionTarget(
                    element.GetProperty("type").GetString()!,
                    element.GetProperty("id").GetGuid(),
                    element.GetProperty("version").GetInt32(),
                    element.GetProperty("material_snapshot_digest").GetString()!);
            }).ToArray();
            var canonical = ApprovalCanonicalJson.Serialize(CanonicalizeSet(targets));
            if (!string.Equals(canonical, json, StringComparison.Ordinal))
            {
                throw new DomainValidationException("The covered lines are not canonical JSON.");
            }

            return targets;
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException($"The covered lines are not valid JSON: {exception.Message}");
        }
    }
}

/// <summary>
/// Binding inputs of a policy exception: exactly the properties of the <c>binding</c> preimage
/// (SPEC 05 REQ-08). Policy computes the digest before calling the workflow verifier and the
/// workflow recomputes it from what it persisted; either side rejects a mismatch.
/// </summary>
public sealed record PolicyExceptionBindingRequest
{
    public PolicyExceptionBindingRequest(
        Guid organizationId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        Guid baseBundleId,
        string baseResultDigest,
        Guid policyVersionId,
        string policyContentDigest,
        string manifestDigest,
        string targetRequirementKey,
        IEnumerable<PolicyExceptionTarget> coveredLines,
        int from,
        int to,
        int floor,
        Guid referenceId,
        Guid? requesterId,
        Guid originatorId,
        Guid workloadSubjectId,
        DateTimeOffset requestedAt,
        DateTimeOffset requestedValidTo,
        string nonce)
    {
        if (organizationId == Guid.Empty || subjectId == Guid.Empty || subjectVersion < 1 ||
            baseBundleId == Guid.Empty || policyVersionId == Guid.Empty)
        {
            throw new DomainValidationException("A policy exception binding requires its complete identities.");
        }

        SubjectType = ApprovalLimits.RequireCode(subjectType, "exception subject type");
        BaseResultDigest = ApprovalLimits.RequireSha256(baseResultDigest, "exception base result digest");
        PolicyContentDigest = ApprovalLimits.RequireSha256(policyContentDigest, "exception policy content digest");
        ManifestDigest = ApprovalLimits.RequireSha256(manifestDigest, "exception manifest digest");
        TargetRequirementKey = ApprovalLimits.RequireKey(targetRequirementKey, "exception target requirement key");
        var targets = (coveredLines ?? []).ToArray();
        if (targets.Length == 0)
        {
            throw new DomainValidationException("A policy exception requires at least one covered line.");
        }

        if (targets.Length != targets.Select(target => target.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("A policy exception cannot repeat covered lines.");
        }

        CoveredLines = targets.ToImmutableArray();
        if (from < 1 || floor < 1 || to < floor || to >= from)
        {
            throw new DomainValidationException("The quotation reduction must satisfy 1 <= floor <= to < from.");
        }

        From = from;
        To = to;
        Floor = floor;
        if (referenceId == Guid.Empty || originatorId == Guid.Empty || workloadSubjectId == Guid.Empty)
        {
            throw new DomainValidationException("A policy exception requires its reference, originator and workload subject.");
        }

        if (requesterId == Guid.Empty)
        {
            throw new DomainValidationException("A declared requester cannot be empty.");
        }

        ReferenceId = referenceId;
        RequesterId = requesterId;
        OriginatorId = originatorId;
        WorkloadSubjectId = workloadSubjectId;
        RequestedAt = requestedAt.ToUniversalTime();
        RequestedValidTo = requestedValidTo.ToUniversalTime();
        if (RequestedValidTo <= RequestedAt)
        {
            throw new DomainValidationException("A policy exception validity must be a positive UTC interval.");
        }

        OrganizationId = organizationId;
        SubjectId = subjectId;
        SubjectVersion = subjectVersion;
        BaseBundleId = baseBundleId;
        PolicyVersionId = policyVersionId;
        Nonce = ApprovalLimits.RequireKey(nonce, "exception nonce");
    }

    public Guid OrganizationId { get; }
    public string SubjectType { get; }
    public Guid SubjectId { get; }
    public int SubjectVersion { get; }
    public Guid BaseBundleId { get; }
    public string BaseResultDigest { get; }
    public Guid PolicyVersionId { get; }
    public string PolicyContentDigest { get; }
    public string ManifestDigest { get; }
    public string TargetRequirementKey { get; }
    public ImmutableArray<PolicyExceptionTarget> CoveredLines { get; }
    public int From { get; }
    public int To { get; }
    public int Floor { get; }
    public Guid ReferenceId { get; }
    public Guid? RequesterId { get; }
    public Guid OriginatorId { get; }
    public Guid WorkloadSubjectId { get; }
    public DateTimeOffset RequestedAt { get; }
    public DateTimeOffset RequestedValidTo { get; }
    public string Nonce { get; }

    public string ComputeBinding() => PolicyExceptionDigests.ComputeBinding(this);
}

/// <summary>Exact preimages of <c>binding</c> and <c>evidence_digest</c> (SPEC 05 REQ-08).</summary>
public static class PolicyExceptionDigests
{
    /// <summary>
    /// <c>binding</c> preimage. <c>correlation_reference</c> is deliberately absent: it is a
    /// tracing value and never part of identity or idempotency (SPEC 05 Datos y contratos).
    /// </summary>
    public static CanonicalValue BindingValue(PolicyExceptionBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApprovalCanonicalJson.Object(
            ("base_bundle_id", ApprovalCanonicalJson.String(request.BaseBundleId)),
            ("base_result_digest", ApprovalCanonicalJson.String(request.BaseResultDigest)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("contract_version", ApprovalCanonicalJson.String(PolicyExceptionContract.VerificationRequestVersion)),
            ("covered_lines", PolicyExceptionTarget.CanonicalizeSet(request.CoveredLines)),
            ("floor", ApprovalCanonicalJson.Number(request.Floor)),
            ("from", ApprovalCanonicalJson.Number(request.From)),
            ("manifest_digest", ApprovalCanonicalJson.String(request.ManifestDigest)),
            ("nonce", ApprovalCanonicalJson.String(request.Nonce)),
            ("organization_id", ApprovalCanonicalJson.String(request.OrganizationId)),
            ("originator_id", ApprovalCanonicalJson.String(request.OriginatorId)),
            ("policy_content_digest", ApprovalCanonicalJson.String(request.PolicyContentDigest)),
            ("policy_version_id", ApprovalCanonicalJson.String(request.PolicyVersionId)),
            ("reference_id", ApprovalCanonicalJson.String(request.ReferenceId)),
            ("requested_at", ApprovalCanonicalJson.String(request.RequestedAt)),
            ("requested_valid_to", ApprovalCanonicalJson.String(request.RequestedValidTo)),
            ("requester_id", ApprovalCanonicalJson.StringOrNull(request.RequesterId)),
            ("subject_id", ApprovalCanonicalJson.String(request.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(request.SubjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(request.SubjectVersion)),
            ("target_requirement_key", ApprovalCanonicalJson.String(request.TargetRequirementKey)),
            ("to", ApprovalCanonicalJson.Number(request.To)),
            ("workload_subject_id", ApprovalCanonicalJson.String(request.WorkloadSubjectId)));
    }

    public static string ComputeBinding(PolicyExceptionBindingRequest request) =>
        ApprovalCanonicalJson.Digest(BindingValue(request));

    /// <summary>
    /// <c>evidence_digest</c> preimage: every binding property plus the decision facts the
    /// workflow reads from its own persistence (SPEC 05 REQ-06, REQ-08).
    /// </summary>
    public static CanonicalValue EvidenceValue(
        PolicyExceptionBindingRequest request,
        Guid caseId,
        Guid requirementId,
        string requirementKey,
        Guid workflowDecisionId,
        int workflowDecisionVersion,
        string workflowDecisionDigest,
        Guid approverId,
        string approverRole,
        string authorityType,
        string eligibilityEvidenceJson,
        string eligibilityEvidenceDigest,
        string authorityEvidenceDigest,
        DecisionScopeDescriptor decisionScope,
        bool segregationSatisfied,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset? revokedAt,
        string verifierReference)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decisionScope);
        if (caseId == Guid.Empty || requirementId == Guid.Empty || workflowDecisionId == Guid.Empty ||
            approverId == Guid.Empty || workflowDecisionVersion < 1)
        {
            throw new DomainValidationException("Policy exception evidence requires complete decision identities.");
        }

        var binding = request.ComputeBinding();
        return ApprovalCanonicalJson.Object(
            ("approver_id", ApprovalCanonicalJson.String(approverId)),
            ("approver_role", ApprovalCanonicalJson.String(ApprovalLimits.RequireCode(approverRole, "approver role"))),
            ("authority_evidence_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(authorityEvidenceDigest, "authority evidence digest"))),
            ("authority_type", ApprovalCanonicalJson.String(ApprovalLimits.RequireCode(authorityType, "authority type"))),
            ("base_bundle_id", ApprovalCanonicalJson.String(request.BaseBundleId)),
            ("base_result_digest", ApprovalCanonicalJson.String(request.BaseResultDigest)),
            ("binding", ApprovalCanonicalJson.String(binding)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("case_id", ApprovalCanonicalJson.String(caseId)),
            ("contract_version", ApprovalCanonicalJson.String(PolicyExceptionContract.VerificationRequestVersion)),
            ("covered_lines", PolicyExceptionTarget.CanonicalizeSet(request.CoveredLines)),
            ("decision_scope", decisionScope.ToCanonicalValue()),
            ("eligibility_evidence", ApprovalCanonicalJson.String(eligibilityEvidenceJson)),
            ("eligibility_evidence_digest", ApprovalCanonicalJson.String(eligibilityEvidenceDigest)),
            ("floor", ApprovalCanonicalJson.Number(request.Floor)),
            ("from", ApprovalCanonicalJson.Number(request.From)),
            ("manifest_digest", ApprovalCanonicalJson.String(request.ManifestDigest)),
            ("nonce", ApprovalCanonicalJson.String(request.Nonce)),
            ("organization_id", ApprovalCanonicalJson.String(request.OrganizationId)),
            ("originator_id", ApprovalCanonicalJson.String(request.OriginatorId)),
            ("policy_content_digest", ApprovalCanonicalJson.String(request.PolicyContentDigest)),
            ("policy_version_id", ApprovalCanonicalJson.String(request.PolicyVersionId)),
            ("reference_id", ApprovalCanonicalJson.String(request.ReferenceId)),
            ("requested_at", ApprovalCanonicalJson.String(request.RequestedAt)),
            ("requested_valid_to", ApprovalCanonicalJson.String(request.RequestedValidTo)),
            ("requester_id", ApprovalCanonicalJson.StringOrNull(request.RequesterId)),
            ("requirement_id", ApprovalCanonicalJson.String(requirementId)),
            ("requirement_key", ApprovalCanonicalJson.String(requirementKey)),
            ("revoked_at", ApprovalCanonicalJson.StringOrNull(revokedAt)),
            ("segregation_satisfied", ApprovalCanonicalJson.Bool(segregationSatisfied)),
            ("subject_id", ApprovalCanonicalJson.String(request.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(request.SubjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(request.SubjectVersion)),
            ("target_requirement_key", ApprovalCanonicalJson.String(request.TargetRequirementKey)),
            ("to", ApprovalCanonicalJson.Number(request.To)),
            ("valid_from", ApprovalCanonicalJson.String(validFrom)),
            ("valid_to", ApprovalCanonicalJson.String(validTo)),
            ("verifier_contract_version", ApprovalCanonicalJson.String(PolicyExceptionContract.VerificationResponseVersion)),
            ("verifier_id", ApprovalCanonicalJson.String(PolicyExceptionContract.VerifierId)),
            ("verifier_reference", ApprovalCanonicalJson.String(verifierReference)),
            ("workflow_decision_digest", ApprovalCanonicalJson.String(
                ApprovalLimits.RequireSha256(workflowDecisionDigest, "workflow decision digest"))),
            ("workflow_decision_id", ApprovalCanonicalJson.String(workflowDecisionId)),
            ("workflow_decision_version", ApprovalCanonicalJson.Number(workflowDecisionVersion)),
            ("workload_subject_id", ApprovalCanonicalJson.String(request.WorkloadSubjectId)));
    }

    public static string ComputeEvidence(
        PolicyExceptionBindingRequest request,
        Guid caseId,
        Guid requirementId,
        string requirementKey,
        Guid workflowDecisionId,
        int workflowDecisionVersion,
        string workflowDecisionDigest,
        Guid approverId,
        string approverRole,
        string authorityType,
        string eligibilityEvidenceJson,
        string eligibilityEvidenceDigest,
        string authorityEvidenceDigest,
        DecisionScopeDescriptor decisionScope,
        bool segregationSatisfied,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset? revokedAt,
        string verifierReference) =>
        ApprovalCanonicalJson.Digest(EvidenceValue(
            request, caseId, requirementId, requirementKey, workflowDecisionId, workflowDecisionVersion,
            workflowDecisionDigest, approverId, approverRole, authorityType, eligibilityEvidenceJson,
            eligibilityEvidenceDigest, authorityEvidenceDigest, decisionScope, segregationSatisfied,
            validFrom, validTo, revokedAt, verifierReference));

    /// <summary>SHA-256 of the exact persisted SPEC 01 eligibility evidence bytes.</summary>
    public static string ComputeEligibilityEvidenceDigest(string eligibilityEvidenceJson)
    {
        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            throw new DomainValidationException("Eligibility evidence is required.");
        }

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(eligibilityEvidenceJson)))
            .ToLowerInvariant();
    }

    public static string VerifierReference(Guid caseId, Guid requirementId) =>
        $"{PolicyExceptionContract.VerifierReferenceScheme}://{caseId:D}/{requirementId:D}";
}

/// <summary>
/// <c>workflow-verification-request/v1</c>: exactly the contractual properties, all present.
/// Unknown members are rejected so a legacy or extended payload cannot interop silently.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowVerificationRequest
{
    [JsonPropertyName("contract_version")] public string? ContractVersion { get; init; }
    [JsonPropertyName("reference_id")] public Guid ReferenceId { get; init; }
    [JsonPropertyName("organization_id")] public Guid OrganizationId { get; init; }
    [JsonPropertyName("subject_type")] public string? SubjectType { get; init; }
    [JsonPropertyName("subject_id")] public Guid SubjectId { get; init; }
    [JsonPropertyName("subject_version")] public int SubjectVersion { get; init; }
    [JsonPropertyName("base_bundle_id")] public Guid BaseBundleId { get; init; }
    [JsonPropertyName("base_result_digest")] public string? BaseResultDigest { get; init; }
    [JsonPropertyName("policy_version_id")] public Guid PolicyVersionId { get; init; }
    [JsonPropertyName("policy_content_digest")] public string? PolicyContentDigest { get; init; }
    [JsonPropertyName("manifest_digest")] public string? ManifestDigest { get; init; }
    [JsonPropertyName("target_requirement_key")] public string? TargetRequirementKey { get; init; }
    [JsonPropertyName("covered_lines")] public IReadOnlyList<PolicyExceptionTarget>? CoveredLines { get; init; }
    [JsonPropertyName("from")] public int From { get; init; }
    [JsonPropertyName("to")] public int To { get; init; }
    [JsonPropertyName("floor")] public int Floor { get; init; }
    [JsonPropertyName("requester_id")] public Guid? RequesterId { get; init; }
    [JsonPropertyName("originator_id")] public Guid OriginatorId { get; init; }
    [JsonPropertyName("workload_subject_id")] public Guid WorkloadSubjectId { get; init; }
    [JsonPropertyName("requested_at")] public DateTimeOffset RequestedAt { get; init; }
    [JsonPropertyName("requested_valid_to")] public DateTimeOffset RequestedValidTo { get; init; }
    [JsonPropertyName("binding")] public string? Binding { get; init; }
    [JsonPropertyName("nonce")] public string? Nonce { get; init; }
    [JsonPropertyName("workflow_decision_id")] public Guid WorkflowDecisionId { get; init; }
    [JsonPropertyName("workflow_decision_version")] public int WorkflowDecisionVersion { get; init; }
    [JsonPropertyName("evidence_digest")] public string? EvidenceDigest { get; init; }
    [JsonPropertyName("correlation_reference")] public string? CorrelationReference { get; init; }
}

/// <summary>
/// <c>workflow-verification-response/v1</c>: exactly the contractual properties, all present.
/// A non-verified response keeps the authority/evidence fields as <c>null</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowVerificationResponse
{
    [JsonPropertyName("contract_version")] public string? ContractVersion { get; init; }
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("verifier_id")] public string? VerifierId { get; init; }
    [JsonPropertyName("verifier_contract_version")] public string? VerifierContractVersion { get; init; }
    [JsonPropertyName("verifier_reference")] public string? VerifierReference { get; init; }
    [JsonPropertyName("case_id")] public Guid? CaseId { get; init; }
    [JsonPropertyName("requirement_id")] public Guid? RequirementId { get; init; }
    [JsonPropertyName("requirement_key")] public string? RequirementKey { get; init; }
    [JsonPropertyName("evidence_digest")] public string? EvidenceDigest { get; init; }
    [JsonPropertyName("binding")] public string? Binding { get; init; }
    [JsonPropertyName("nonce")] public string? Nonce { get; init; }
    [JsonPropertyName("workflow_decision_id")] public Guid? WorkflowDecisionId { get; init; }
    [JsonPropertyName("workflow_decision_version")] public int? WorkflowDecisionVersion { get; init; }
    [JsonPropertyName("workflow_decision_digest")] public string? WorkflowDecisionDigest { get; init; }
    [JsonPropertyName("approver_id")] public Guid? ApproverId { get; init; }
    [JsonPropertyName("approver_role")] public string? ApproverRole { get; init; }
    [JsonPropertyName("authority_type")] public string? AuthorityType { get; init; }
    [JsonPropertyName("eligibility_evidence")] public string? EligibilityEvidence { get; init; }
    [JsonPropertyName("eligibility_evidence_digest")] public string? EligibilityEvidenceDigest { get; init; }
    [JsonPropertyName("authority_evidence_digest")] public string? AuthorityEvidenceDigest { get; init; }
    [JsonPropertyName("decision_scope")] public DecisionScopeDescriptor? DecisionScope { get; init; }
    [JsonPropertyName("covered_lines")] public IReadOnlyList<PolicyExceptionTarget>? CoveredLines { get; init; }
    [JsonPropertyName("segregation_satisfied")] public bool SegregationSatisfied { get; init; }
    [JsonPropertyName("valid_from")] public DateTimeOffset? ValidFrom { get; init; }
    [JsonPropertyName("valid_to")] public DateTimeOffset? ValidTo { get; init; }
    [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Workload command that opens a policy exception case (SPEC 05 REQ-05).</summary>
public sealed record PolicyExceptionSubmission(
    ApprovalWorkloadIdentity Workload,
    string SubmissionKey,
    Guid OrganizationId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string TargetRequirementKey,
    ImmutableArray<PolicyExceptionTarget> CoveredLines,
    int From,
    int To,
    int Floor,
    Guid ReferenceId,
    Guid? RequesterId,
    Guid OriginatorId,
    Guid WorkloadSubjectId,
    DateTimeOffset RequestedAt,
    DateTimeOffset RequestedValidTo,
    string Nonce,
    string CorrelationReference,
    Guid BaseBundleId,
    string BaseResultDigest,
    Guid PolicyVersionId,
    string PolicyContentDigest,
    string ManifestDigest);

/// <summary>Outcome of an exception submission: the case and the persisted extension identity.</summary>
public sealed record PolicyExceptionSubmissionOutcome(
    Guid CaseId,
    Guid RequirementId,
    string RequirementKey,
    Guid RequestId,
    string Binding,
    string Status,
    int Version,
    bool Replayed);

/// <summary>Decision facts the verifier compares against its own persistence (SPEC 05 REQ-06).</summary>
public sealed record PolicyExceptionDecisionFacts(
    Guid CaseId,
    Guid RequirementId,
    string RequirementKey,
    Guid WorkflowDecisionId,
    int WorkflowDecisionVersion,
    string WorkflowDecisionDigest,
    ApprovalDecisionAction Action,
    Guid ApproverId,
    string ApproverRole,
    string AuthorityType,
    string EligibilityEvidenceJson,
    string AuthorityEvidenceDigest,
    DecisionScopeDescriptor DecisionScope,
    ImmutableArray<PolicyExceptionTarget> Targets,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    DateTimeOffset? RevokedAt);

/// <summary>Persisted outcome of a verified policy exception (immutable, SPEC 05 REQ-06/08).</summary>
public sealed record PolicyExceptionVerificationOutcome(
    Guid VerificationId,
    Guid RequestId,
    bool Replayed,
    WorkflowVerificationResponse Response);
