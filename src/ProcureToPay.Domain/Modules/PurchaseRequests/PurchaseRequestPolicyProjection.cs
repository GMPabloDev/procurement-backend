using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseRequests;

/// <summary>
/// Exact projection of one Purchase Request line into the closed SPEC 02 fact catalog, plus the
/// provenance entries and the <c>purchase-request-materiality/v1</c> preimage derived from the
/// persisted Policy snapshot (REQ-05, REQ-08).
/// </summary>
public static class PurchaseRequestPolicyProjection
{
    public const string ContractVersion = PurchaseRequestCodes.MaterialityVersion;

    /// <summary>Typed facts of one line. Text, summaries and amounts outside the catalog never enter.</summary>
    public static IReadOnlyDictionary<string, PolicyValue> LineFacts(
        PurchaseRequestLineContent content,
        bool costCenterActive,
        bool costCenterDepartmentActive)
    {
        ArgumentNullException.ThrowIfNull(content);
        var facts = new SortedDictionary<string, PolicyValue>(StringComparer.Ordinal)
        {
            ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(content.BaseAmount, content.BaseCurrency),
            ["PURCHASE_TYPE"] = PolicyValue.Code(content.PurchaseType),
            ["SPEND_CATEGORY"] = PolicyValue.VersionedCode(
                content.SpendCategoryRef.Catalog,
                content.SpendCategoryRef.Code,
                content.SpendCategoryRef.Version,
                content.SpendCategoryRef.Digest),
            ["BENEFICIARY_DEPARTMENT"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.Department),
                content.BeneficiaryDepartmentRef.Id,
                content.BeneficiaryDepartmentRef.Version),
            ["COST_CENTER"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.CostCenter),
                content.CostCenterRef.Id,
                content.CostCenterRef.Version),
            ["COST_CENTER_DEPARTMENT"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.Department),
                content.CostCenterDepartmentRef.Id,
                content.CostCenterDepartmentRef.Version),
            ["COST_CENTER_ACTIVE"] = PolicyValue.Boolean(costCenterActive),
            ["COST_CENTER_DEPARTMENT_ACTIVE"] = PolicyValue.Boolean(costCenterDepartmentActive),
            // v1 payload carries no supplier preference or agreement revision: the closed catalog
            // is projected with its non-informative value instead of inventing a fact.
            ["PREFERRED_SUPPLIER"] = PolicyValue.Boolean(false),
            ["CONTRACT_REQUIRED"] = PolicyValue.Boolean(content.ContractRequired),
            ["NON_STANDARD_TERMS"] = PolicyValue.Boolean(content.NonStandardTerms),
            ["EXTERNAL_AGREEMENT_STATUS"] = PolicyValue.Code("NONE")
        };
        if (content.SupplierRef is not null)
        {
            facts["SUPPLIER"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.Supplier),
                content.SupplierRef.Id,
                content.SupplierRef.Version);
        }

        if (content.PreferredProductRef is not null)
        {
            facts["PREFERRED_PRODUCT"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.Product),
                content.PreferredProductRef.Id,
                content.PreferredProductRef.Version);
        }

        if (content.RequiredProductRef is not null)
        {
            facts["REQUIRED_PRODUCT"] = PolicyValue.EntityRef(
                PurchaseRequestCodes.Code(PurchaseRequestReferenceType.Product),
                content.RequiredProductRef.Id,
                content.RequiredProductRef.Version);
        }

        if (content.RiskAnswers.Count > 0)
        {
            facts["RISK_ANSWER"] = PolicyValue.Set(content.RiskAnswers
                .Select(answer => PolicyValue.TypedAnswer(
                    answer.QuestionCode, answer.SchemaVersion, answer.ValueKind, answer.Value))
                .ToArray());
        }

        return facts;
    }

    /// <summary>Request-level facts of release 1: the engine derives the total from the lines.</summary>
    public static IReadOnlyDictionary<string, PolicyValue> RequestFacts() =>
        new SortedDictionary<string, PolicyValue>(StringComparer.Ordinal);

    /// <summary>
    /// Provenance of one line: every fact points to the exact request version, line version and
    /// source field, or to the owner attestation that answered it (REQ-05).
    /// </summary>
    public static IReadOnlyDictionary<string, string> LineProvenance(
        Guid requestId,
        int requestVersion,
        Guid lineId,
        int lineVersion,
        IReadOnlyDictionary<string, PolicyValue> facts,
        string referenceAttestationDigest)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var prefix = $"pr://{requestId:D}/versions/{requestVersion}/lines/{lineId:D}/versions/{lineVersion}";
        var provenance = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in facts.Keys)
        {
            provenance[key] = key switch
            {
                "GROSS_AMOUNT_BASE" => $"{prefix}#base_amount",
                "PURCHASE_TYPE" => $"{prefix}#purchase_type",
                "SPEND_CATEGORY" => $"{prefix}#spend_category_ref",
                "BENEFICIARY_DEPARTMENT" => $"{prefix}#beneficiary_department_ref",
                "COST_CENTER" => $"{prefix}#cost_center_ref",
                "COST_CENTER_DEPARTMENT" => $"{prefix}#cost_center_department_ref",
                "SUPPLIER" => $"{prefix}#supplier_ref",
                "PREFERRED_PRODUCT" => $"{prefix}#preferred_product_ref",
                "REQUIRED_PRODUCT" => $"{prefix}#required_product_ref",
                "CONTRACT_REQUIRED" => $"{prefix}#contract_required",
                "NON_STANDARD_TERMS" => $"{prefix}#non_standard_terms",
                "PREFERRED_SUPPLIER" => $"{prefix}#preferred_supplier",
                "EXTERNAL_AGREEMENT_STATUS" => $"{prefix}#external_agreement_status",
                "COST_CENTER_ACTIVE" =>
                    $"attestations/{referenceAttestationDigest}#{PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization)}",
                "COST_CENTER_DEPARTMENT_ACTIVE" =>
                    $"attestations/{referenceAttestationDigest}#{PurchaseRequestCodes.Code(PurchaseRequestAssertionType.CostCenterOwnedByDepartment)}",
                "RISK_ANSWER" => $"{prefix}#risk_answers",
                _ => $"{prefix}#{key.ToLowerInvariant()}"
            };
        }

        return provenance;
    }

    /// <summary>
    /// <c>purchase-request-materiality/v1</c> of one covered line, derived from the persisted
    /// Policy snapshot and its persisted provenance only (REQ-08, DEC-03). No provider is called
    /// again and no identity outside the typed facts participates.
    /// </summary>
    public static string MaterialityDigest(
        string canonicalRequestJson,
        Guid lineId,
        IReadOnlyDictionary<string, string> provenance)
    {
        if (string.IsNullOrWhiteSpace(canonicalRequestJson))
        {
            throw new DomainValidationException(
                "The persisted policy snapshot is required to derive materiality.");
        }

        if (lineId == Guid.Empty)
        {
            throw new DomainValidationException("Materiality requires the covered line identity.");
        }

        using var document = JsonDocument.Parse(canonicalRequestJson);
        var root = document.RootElement;
        var lineFacts = FindLineFacts(root, lineId);
        var requestFacts = root.TryGetProperty("facts", out var factsElement) && factsElement.ValueKind == JsonValueKind.Object
            ? factsElement
            : throw new DomainValidationException("The persisted policy snapshot has no request facts.");
        var lineProvenance = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        AddProvenance(lineProvenance, lineFacts, provenance);
        AddProvenance(lineProvenance, requestFacts, provenance);

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_currency"] = root.GetProperty("base_currency").GetString(),
            ["canonicalization_version"] = PurchaseRequestCanonicalizer.CanonicalizationVersion,
            ["contract_version"] = ContractVersion,
            ["facts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line"] = SortFacts(lineFacts),
                ["request"] = SortFacts(requestFacts)
            },
            ["legal_entity_id"] = root.GetProperty("legal_entity_id").GetString(),
            ["organization_id"] = root.GetProperty("organization_id").GetString(),
            ["provenance"] = lineProvenance,
            ["target_id"] = lineId.ToString("D")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// Key-sorted copy of one fact map. The persisted snapshot is already canonical; sorting here
    /// keeps the digest invariant to the enumeration order of the stored keys (NFR-02).
    /// </summary>
    private static SortedDictionary<string, object?> SortFacts(JsonElement facts)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var fact in facts.EnumerateObject())
        {
            sorted[fact.Name] = fact.Value;
        }

        return sorted;
    }

    private static JsonElement FindLineFacts(JsonElement root, Guid lineId)
    {
        var matches = new List<JsonElement>();
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            if (line.GetProperty("subject").GetProperty("id").GetGuid() == lineId)
            {
                matches.Add(line.GetProperty("facts"));
            }
        }

        return matches.Count == 1
            ? matches[0]
            : throw new DomainValidationException(
                "The persisted policy snapshot does not contain exactly one covered line.");
    }

    private static void AddProvenance(
        IDictionary<string, object?> target,
        JsonElement facts,
        IReadOnlyDictionary<string, string> provenance)
    {
        foreach (var fact in facts.EnumerateObject())
        {
            if (provenance.TryGetValue(fact.Name, out var reference))
            {
                target[fact.Name] = reference;
            }
        }
    }
}
