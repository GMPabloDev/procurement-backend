using System.Collections.Immutable;
using System.Globalization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseRequests;

/// <summary>
/// Canonical preimages of the Purchase Requests contracts (Datos y contratos). Every digest uses
/// <c>policy-canonical-json/v1</c> bytes: UTF-8 without BOM or whitespace, known properties always
/// present, ordinal key order, NFC strings, lowercase-D UUIDs, UTC timestamps with seven decimals,
/// invariant decimal strings and sets sorted by canonical bytes without duplicates.
/// </summary>
public static class PurchaseRequestCanonicalizer
{
    public const string CanonicalizationVersion = PolicyCanonicalizer.Version;

    public static string LineContentDigest(
        Guid organizationId,
        Guid requestId,
        Guid lineId,
        int lineVersion,
        PurchaseRequestLineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (requestId == Guid.Empty || lineId == Guid.Empty || lineVersion < 1)
        {
            throw new DomainValidationException("A line content digest requires its identity and version.");
        }

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_amount"] = Decimal(content.BaseAmount),
            ["base_currency"] = content.BaseCurrency,
            ["beneficiary_department_ref"] = EntityRef(content.BeneficiaryDepartmentRef),
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_required"] = content.ContractRequired,
            ["contract_version"] = PurchaseRequestCodes.LineContentVersion,
            ["cost_center_department_ref"] = EntityRef(content.CostCenterDepartmentRef),
            ["cost_center_ref"] = EntityRef(content.CostCenterRef),
            ["estimated_gross_amount"] = Decimal(content.EstimatedGrossAmount),
            ["fiscal_year"] = content.FiscalYear,
            ["fx_attestation_ref"] = FxRef(content.FxAttestationRef),
            ["line_id"] = CanonicalGuid(lineId),
            ["line_version"] = lineVersion,
            ["need_summary"] = content.NeedSummary,
            ["non_standard_terms"] = content.NonStandardTerms,
            ["organization_id"] = CanonicalGuid(organizationId),
            ["preferred_product_ref"] = EntityRef(content.PreferredProductRef),
            ["purchase_request_id"] = CanonicalGuid(requestId),
            ["purchase_type"] = content.PurchaseType,
            ["requested_for_user_ref"] = EntityRef(content.RequestedForUserRef),
            ["required_product_ref"] = EntityRef(content.RequiredProductRef),
            ["risk_answers"] = content.RiskAnswers.Select(AnswerRef).ToArray(),
            ["spend_category_ref"] = CodeRef(content.SpendCategoryRef),
            ["supplier_ref"] = EntityRef(content.SupplierRef),
            ["transaction_currency"] = content.TransactionCurrency
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string RequestContentDigest(PurchaseRequestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["business_justification"] = snapshot.BusinessJustification,
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_version"] = PurchaseRequestCodes.RequestContentVersion,
            ["legal_entity_ref"] = EntityRef(snapshot.LegalEntityRef),
            ["line_refs"] = snapshot.LineRefs.Select(LineRef).ToArray(),
            ["organization_id"] = CanonicalGuid(snapshot.OrganizationId),
            ["predecessor_version"] = snapshot.PredecessorVersion,
            ["request_id"] = CanonicalGuid(snapshot.RequestId),
            ["request_version"] = snapshot.Version,
            ["requester_id"] = CanonicalGuid(snapshot.RequesterId)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string SerializeAttestation(PurchaseRequestReferenceAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        return PolicyCanonicalizer.SerializeCanonical(AttestationValue(
            attestation.RequestId,
            attestation.RequestVersion,
            attestation.OrganizationId,
            attestation.AttestedAt,
            attestation.Assertions));
    }

    public static string ReferenceAttestationDigest(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        DateTimeOffset attestedAt,
        IEnumerable<PurchaseRequestReferenceAssertion> assertions) =>
        PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(
            AttestationValue(requestId, requestVersion, organizationId, attestedAt, assertions)));

    internal static object AttestationValue(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        DateTimeOffset attestedAt,
        IEnumerable<PurchaseRequestReferenceAssertion> assertions)
    {
        // Assertions are a set: the digest must not depend on the enumeration order (NFR-02).
        var materialized = (assertions ?? [])
            .OrderBy(assertion => assertion.SlotIdentity, StringComparer.Ordinal)
            .ToArray();
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assertions"] = materialized
                .Select(assertion => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["assertion_type"] = PurchaseRequestCodes.Code(assertion.AssertionType),
                    ["owner_contract_version"] = assertion.OwnerContractVersion,
                    ["owner_id"] = assertion.OwnerId,
                    ["source_ref"] = AttestedRef(assertion.SourceRef),
                    ["status"] = assertion.Status,
                    ["target_ref"] = assertion.TargetRef is null ? null : AttestedRef(assertion.TargetRef)
                })
                .ToArray(),
            ["attested_at"] = Timestamp(attestedAt),
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_version"] = PurchaseRequestCodes.AttestationVersion,
            ["organization_id"] = CanonicalGuid(organizationId),
            ["request_id"] = CanonicalGuid(requestId),
            ["request_version"] = requestVersion
        };
    }

    public static string ReferenceAttestationDigest(PurchaseRequestReferenceAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        return ReferenceAttestationDigest(
            attestation.RequestId,
            attestation.RequestVersion,
            attestation.OrganizationId,
            attestation.AttestedAt,
            attestation.Assertions);
    }

    /// <summary>
    /// Policy completeness manifest digest of SPEC 02: it deliberately excludes the digest from
    /// its own preimage and keeps the exact property set the engine recomputes.
    /// </summary>
    public static string PolicyManifestDigest(Guid requestId, int requestVersion, IEnumerable<PurchaseRequestLineRef> lines)
    {
        var materialized = (lines ?? []).ToArray();
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = CanonicalizationVersion,
            ["line_count"] = materialized.Length,
            ["lines"] = materialized
                .Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = CanonicalGuid(line.Id),
                    ["version"] = line.Version
                })
                .OrderBy(value => PolicyCanonicalizer.SerializeCanonical(value!), StringComparer.Ordinal)
                .ToArray(),
            ["request_id"] = CanonicalGuid(requestId),
            ["request_version"] = requestVersion
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string DomainAttestationDigest(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        string requestContentDigest,
        string referenceAttestationDigest,
        string policyManifestDigest,
        IEnumerable<PurchaseRequestLineRef> lines)
    {
        var materialized = (lines ?? []).ToArray();
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_version"] = PurchaseRequestCodes.ManifestVersion,
            ["line_count"] = materialized.Length,
            ["lines"] = materialized
                .Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["content_digest"] = line.ContentDigest,
                    ["id"] = CanonicalGuid(line.Id),
                    ["version"] = line.Version
                })
                .OrderBy(value => PolicyCanonicalizer.SerializeCanonical(value!), StringComparer.Ordinal)
                .ToArray(),
            ["organization_id"] = CanonicalGuid(organizationId),
            ["policy_manifest_digest"] = PurchaseRequestLineRef.RequireDigest(
                policyManifestDigest, "Policy manifest digest"),
            ["provider_contract_version"] = PurchaseRequestCodes.ProviderContractVersion,
            ["provider_id"] = PurchaseRequestCodes.ProviderId,
            ["reference_attestation_digest"] = PurchaseRequestLineRef.RequireDigest(
                referenceAttestationDigest, "Reference attestation digest"),
            ["request_content_digest"] = PurchaseRequestLineRef.RequireDigest(
                requestContentDigest, "Request content digest"),
            ["request_id"] = CanonicalGuid(requestId),
            ["request_version"] = requestVersion
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string SubmissionFingerprint(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        Guid actorUserId,
        string requestContentDigest,
        string referenceAttestationDigest,
        string policyManifestDigest,
        string domainAttestationDigest,
        string submissionKey,
        string reason)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = CanonicalGuid(actorUserId),
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_version"] = PurchaseRequestCodes.SubmissionVersion,
            ["domain_attestation_digest"] = PurchaseRequestLineRef.RequireDigest(
                domainAttestationDigest, "Domain attestation digest"),
            ["organization_id"] = CanonicalGuid(organizationId),
            ["policy_manifest_digest"] = PurchaseRequestLineRef.RequireDigest(
                policyManifestDigest, "Policy manifest digest"),
            ["reason"] = PurchaseRequestLimits.RequireReason(reason, "Submission reason"),
            ["reference_attestation_digest"] = PurchaseRequestLineRef.RequireDigest(
                referenceAttestationDigest, "Reference attestation digest"),
            ["request_content_digest"] = PurchaseRequestLineRef.RequireDigest(
                requestContentDigest, "Request content digest"),
            ["request_id"] = CanonicalGuid(requestId),
            ["request_version"] = requestVersion,
            ["submission_key"] = PurchaseRequestLimits.RequireKey(submissionKey, "submission_key")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string CreateFingerprint(
        Guid organizationId,
        Guid actorUserId,
        VersionedEntityRef legalEntityRef,
        string businessJustification,
        string revisionKey,
        string reason,
        IEnumerable<PurchaseRequestLineDraft> drafts)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = CanonicalGuid(actorUserId),
            ["business_justification"] = businessJustification,
            ["canonicalization_version"] = CanonicalizationVersion,
            ["command_version"] = PurchaseRequestCodes.CreateCommandVersion,
            ["legal_entity_ref"] = EntityRef(legalEntityRef),
            ["line_drafts"] = (drafts ?? [])
                .Select(draft => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["client_line_key"] = draft.Key,
                    ["content"] = LineContentValue(draft.Content)
                })
                .ToArray(),
            ["organization_id"] = CanonicalGuid(organizationId),
            ["reason"] = PurchaseRequestLimits.RequireReason(reason, "Create reason"),
            ["revision_key"] = PurchaseRequestLimits.RequireKey(revisionKey, "revision_key")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string RevisionFingerprint(
        Guid organizationId,
        Guid requestId,
        Guid actorUserId,
        int expectedRequestVersion,
        string businessJustification,
        string revisionKey,
        string reason,
        IEnumerable<PurchaseRequestLineRef> retained,
        IEnumerable<PurchaseRequestLineChange> changed,
        IEnumerable<PurchaseRequestLineDraft> added,
        IEnumerable<PurchaseRequestLineRef> removed)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = CanonicalGuid(actorUserId),
            ["added"] = (added ?? [])
                .Select(draft => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["client_line_key"] = draft.Key,
                    ["content"] = LineContentValue(draft.Content)
                })
                .ToArray(),
            ["business_justification"] = businessJustification,
            ["canonicalization_version"] = CanonicalizationVersion,
            ["changed"] = (changed ?? [])
                .Select(entry => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["content"] = LineContentValue(entry.Content),
                    ["expected_content_digest"] = entry.ExpectedContentDigest,
                    ["expected_version"] = entry.ExpectedVersion,
                    ["id"] = CanonicalGuid(entry.LineId)
                })
                .ToArray(),
            ["command_version"] = PurchaseRequestCodes.RevisionCommandVersion,
            ["expected_request_version"] = expectedRequestVersion,
            ["organization_id"] = CanonicalGuid(organizationId),
            ["reason"] = PurchaseRequestLimits.RequireReason(reason, "Revision reason"),
            ["removed"] = (removed ?? []).Select(reference => (object?)LineRef(reference)).ToArray(),
            ["request_id"] = CanonicalGuid(requestId),
            ["retained"] = (retained ?? []).Select(reference => (object?)LineRef(reference)).ToArray(),
            ["revision_key"] = PurchaseRequestLimits.RequireKey(revisionKey, "revision_key")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    /// <summary>
    /// <c>purchase-request-materiality/v1</c> (REQ-08): the typed facts and provenance of one line
    /// exactly as Policy persisted them, without snapshot or manifest identities.
    /// </summary>
    public static string MaterialityDigest(
        Guid organizationId,
        Guid legalEntityId,
        string baseCurrency,
        Guid targetId,
        IReadOnlyDictionary<string, object?> lineFacts,
        IReadOnlyDictionary<string, object?> requestFacts,
        IReadOnlyDictionary<string, string> provenance)
    {
        if (organizationId == Guid.Empty || legalEntityId == Guid.Empty || targetId == Guid.Empty)
        {
            throw new DomainValidationException("A materiality digest requires its complete identity.");
        }

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_currency"] = PolicyValue.NormalizeCurrency(baseCurrency),
            ["canonicalization_version"] = CanonicalizationVersion,
            ["contract_version"] = PurchaseRequestCodes.MaterialityVersion,
            ["facts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line"] = new SortedDictionary<string, object?>(
                    (lineFacts ?? new Dictionary<string, object?>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal),
                ["request"] = new SortedDictionary<string, object?>(
                    (requestFacts ?? new Dictionary<string, object?>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal)
            },
            ["legal_entity_id"] = CanonicalGuid(legalEntityId),
            ["organization_id"] = CanonicalGuid(organizationId),
            ["provenance"] = new SortedDictionary<string, object?>(
                (provenance ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            ["target_id"] = CanonicalGuid(targetId)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    internal static object LineContentValue(PurchaseRequestLineContent content) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_amount"] = Decimal(content.BaseAmount),
            ["base_currency"] = content.BaseCurrency,
            ["beneficiary_department_ref"] = EntityRef(content.BeneficiaryDepartmentRef),
            ["contract_required"] = content.ContractRequired,
            ["cost_center_department_ref"] = EntityRef(content.CostCenterDepartmentRef),
            ["cost_center_ref"] = EntityRef(content.CostCenterRef),
            ["estimated_gross_amount"] = Decimal(content.EstimatedGrossAmount),
            ["fiscal_year"] = content.FiscalYear,
            ["fx_attestation_ref"] = FxRef(content.FxAttestationRef),
            ["need_summary"] = content.NeedSummary,
            ["non_standard_terms"] = content.NonStandardTerms,
            ["preferred_product_ref"] = EntityRef(content.PreferredProductRef),
            ["purchase_type"] = content.PurchaseType,
            ["requested_for_user_ref"] = EntityRef(content.RequestedForUserRef),
            ["required_product_ref"] = EntityRef(content.RequiredProductRef),
            ["risk_answers"] = content.RiskAnswers.Select(AnswerRef).ToArray(),
            ["spend_category_ref"] = CodeRef(content.SpendCategoryRef),
            ["supplier_ref"] = EntityRef(content.SupplierRef),
            ["transaction_currency"] = content.TransactionCurrency
        };

    internal static object LineRef(PurchaseRequestLineRef reference) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content_digest"] = reference.ContentDigest,
            ["id"] = CanonicalGuid(reference.Id),
            ["version"] = reference.Version
        };

    internal static object EntityRef(VersionedEntityRef? reference) => reference is null
        ? null!
        : new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity_type"] = reference.EntityType,
            ["id"] = CanonicalGuid(reference.Id),
            ["version"] = reference.Version
        };

    internal static object CodeRef(VersionedCodeRef reference) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["catalog"] = reference.Catalog,
            ["code"] = reference.Code,
            ["digest"] = reference.Digest,
            ["version"] = reference.Version
        };

    internal static object AnswerRef(TypedAnswerRef answer) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["question_code"] = answer.QuestionCode,
            ["schema_version"] = answer.SchemaVersion,
            ["value"] = answer.Value,
            ["value_kind"] = answer.ValueKind == TypedAnswerValueKind.Boolean ? "BOOLEAN" : "ENUM_CODE"
        };

    internal static object FxRef(PurchaseRequestFxRef? reference) => reference is null
        ? null!
        : new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attestation_id"] = CanonicalGuid(reference.AttestationId),
            ["attestation_version"] = reference.AttestationVersion,
            ["base_currency"] = reference.BaseCurrency,
            ["effective_rate"] = Decimal(reference.EffectiveRate),
            ["rate_date"] = reference.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["transaction_currency"] = reference.TransactionCurrency
        };

    internal static object AttestedRef(PurchaseRequestAttestedRef reference) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = reference.Code,
            ["digest"] = reference.Digest,
            ["id"] = reference.Id is Guid id ? CanonicalGuid(id) : null,
            ["kind"] = reference.KindCode,
            ["type"] = PurchaseRequestCodes.Code(reference.Type),
            ["version"] = reference.Version
        };

    internal static string CanonicalGuid(Guid value) => value.ToString("D").ToLowerInvariant();

    internal static string Decimal(decimal value) =>
        value.ToString("0.##############################", CultureInfo.InvariantCulture);

    internal static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
