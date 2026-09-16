using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>
/// Reproducible digests of the Supplier domain (SPEC 09 Canonicalización y digests). Every preimage
/// is an object under <c>policy-canonical-json/v1</c> with exactly the published properties,
/// ordered ordinal, NFC strings, lowercase UUIDs, seven-decimal UTC instants and canonical decimal
/// strings. Banking plaintext never enters any preimage.
/// </summary>
public static class SupplierCanonicalizer
{
    /// <summary>
    /// <c>supplier_identity_key_digest</c> (<c>supplier-identity-key/v1</c>): the reserved fiscal
    /// identity of one supplier root. Unique across roots of the organization (REQ-01).
    /// </summary>
    public static string IdentityKeyDigest(string countryCode, Guid organizationId, string taxId)
    {
        var normalizedCountry = SupplierCodes.CountryCode(countryCode, "Country");
        var normalizedTaxId = SupplierCodes.TaxId(taxId);
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A fiscal identity requires its organization.");
        }

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SupplierCodes.SupplierIdentityKeyContract,
            ["country_code"] = normalizedCountry,
            ["organization_id"] = organizationId.ToString("D"),
            ["tax_id_key"] = normalizedTaxId
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>supplier_content_digest</c> (<c>supplier-version/v1</c>): the immutable content of one
    /// version. Actor, reason, key and timestamps stay outside so a replay is byte-identical.
    /// </summary>
    public static string ContentDigest(Guid supplierId, int version, SupplierVersionContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (supplierId == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("A supplier content digest requires its identity and version.");
        }

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["addresses"] = Set(content.Addresses.Select(DescribeAddress)),
            ["banking_refs"] = Set(content.BankingRefs.Select(reference => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = reference.Id.ToString("D"),
                ["version"] = reference.Version
            })),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["categories_supplied"] = Set(content.CategoriesSupplied.Select(reference => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalog"] = reference.Catalog,
                ["code"] = reference.Code,
                ["digest"] = reference.Digest,
                ["version"] = reference.Version
            })),
            ["contacts"] = Set(content.Contacts.Select(DescribeContact)),
            ["contract_version"] = SupplierCodes.SupplierVersionContract,
            ["country_code"] = content.CountryCode,
            ["legal_name"] = content.LegalName,
            ["payment_terms"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = content.PaymentTerms.Code,
                ["net_days"] = content.PaymentTerms.NetDays
            },
            ["performance_score"] = content.PerformanceScore is null
                ? null
                : CanonicalDecimal(content.PerformanceScore.Score),
            // REQ-04 makes the source and the measurement instant mandatory together with the score.
            // Both travel inside the single published preimage property so no extra property appears.
            ["performance_source"] = content.PerformanceScore is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["measured_at"] = SupplierCodes.FormatUtc(content.PerformanceScore.MeasuredAt),
                    ["source"] = content.PerformanceScore.Source
                },
            ["risk_status"] = SupplierStatusCodes.Code(content.RiskStatus),
            ["status"] = SupplierStatusCodes.Code(content.Status),
            ["supplier_id"] = supplierId.ToString("D"),
            ["supported_currencies"] = Set(content.SupportedCurrencies.Select(currency => (object?)currency)),
            ["tax_id"] = content.TaxId,
            ["trade_name"] = content.TradeName,
            ["version"] = version
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>approved_catalog_content_digest</c> (<c>approved-supplier-catalog-entry/v1</c>). Precio,
    /// acuerdo, vigencia, selector y attachment quedan comprometidos por estos bytes (REQ-06).
    /// </summary>
    public static string ApprovedCatalogContentDigest(
        Guid catalogEntryId,
        int version,
        VersionedEntityRef supplierRef,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef? productRef,
        decimal negotiatedPrice,
        string currency,
        string unitCode,
        string externalContractReference,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovedCatalogEntryStatus status,
        SupplierAgreementAttachmentView attachment)
    {
        ArgumentNullException.ThrowIfNull(supplierRef);
        ArgumentNullException.ThrowIfNull(spendCategoryRef);
        ArgumentNullException.ThrowIfNull(attachment);
        if (catalogEntryId == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("A catalog content digest requires its identity and version.");
        }

        if (negotiatedPrice <= 0)
        {
            throw new DomainValidationException("A negotiated price must be positive.");
        }

        var normalizedCurrency = SupplierCodes.Currency(currency, "Negotiated currency");
        if (validTo <= validFrom)
        {
            throw new DomainValidationException("The agreement validity window is empty.");
        }

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attachment"] = DescribeAttachment(attachment),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SupplierCodes.ApprovedCatalogEntryContract,
            ["external_contract_reference"] = externalContractReference,
            ["negotiated_price"] = CanonicalDecimal(negotiatedPrice),
            ["product_ref"] = productRef is null ? null : DescribeEntityRef(productRef),
            ["spend_category_ref"] = DescribeCodeRef(spendCategoryRef),
            ["status"] = status == ApprovedCatalogEntryStatus.Active ? "ACTIVE" : "INACTIVE",
            ["supplier_ref"] = DescribeEntityRef(supplierRef),
            ["unit_code"] = unitCode,
            ["valid_from"] = SupplierCodes.FormatUtc(validFrom),
            ["valid_to"] = SupplierCodes.FormatUtc(validTo),
            ["version"] = version
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>supplier_change_fingerprint</c> of one governed command. The clock, the correlation and the
    /// banking plaintext stay outside so a replay with the same key is byte-identical (REQ-01).
    /// </summary>
    public static string ChangeFingerprint(
        Guid supplierId,
        Guid organizationId,
        Guid editorId,
        int? baseVersion,
        int? expectedVersion,
        string changeKind,
        string requestedStatus,
        string changeKey,
        string candidateContentDigest,
        string reason)
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_version"] = baseVersion,
            ["candidate_content_digest"] = SupplierCodes.Digest(candidateContentDigest, "Candidate content digest"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["change_key"] = SupplierCodes.Key(changeKey, "Change key"),
            ["change_kind"] = changeKind,
            ["editor_id"] = editorId.ToString("D"),
            ["expected_version"] = expectedVersion,
            ["organization_id"] = organizationId.ToString("D"),
            ["reason"] = SupplierCodes.Reason(reason),
            ["requested_status"] = requestedStatus,
            ["supplier_id"] = supplierId.ToString("D")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>active_supplier_evidence_digest</c> (<c>active-supplier-evidence/v1</c>): the proof that the
    /// owner checked the current operational version before signalling (REQ-10).
    /// </summary>
    public static string ActiveSupplierEvidenceDigest(
        Guid attemptId,
        Guid prerequisiteId,
        Guid organizationId,
        string parametersDigest,
        VersionedEntityRef supplierRef,
        bool satisfied,
        string signalKey,
        DateTimeOffset checkedAt,
        IEnumerable<ApprovalTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(supplierRef);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attempt_id"] = attemptId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["checked_at"] = SupplierCodes.FormatUtc(checkedAt),
            ["contract_version"] = SupplierCodes.ActiveSupplierEvidenceContract,
            ["organization_id"] = organizationId.ToString("D"),
            ["parameters_digest"] = SupplierCodes.Digest(parametersDigest, "Parameters digest"),
            ["prerequisite_id"] = prerequisiteId.ToString("D"),
            ["result"] = satisfied ? "SATISFIED" : "FAILED",
            ["signal_key"] = SupplierCodes.Key(signalKey, "signal_key"),
            ["supplier_ref"] = DescribeEntityRef(supplierRef),
            ["targets"] = TargetSet(targets)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>active_supplier_partition_digest</c> of one supplier partition of a combined control. It
    /// belongs to the Approval contract, so it keeps <c>approval-canonical-json/v2</c> (REQ-10).
    /// </summary>
    public static string PartitionDigest(string requirementKey, VersionedEntityRef supplierRef)
    {
        ArgumentNullException.ThrowIfNull(supplierRef);
        return ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("requirement_key", ApprovalCanonicalJson.String(SupplierCodes.Key(requirementKey, "requirement_key"))),
            ("supplier_ref", ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(supplierRef.Id.ToString("D"))),
                ("version", ApprovalCanonicalJson.Number(supplierRef.Version))))));
    }

    /// <summary>Deterministic prerequisite key of one supplier partition (REQ-10).</summary>
    public static string PartitionKey(string requirementKey, VersionedEntityRef supplierRef) =>
        $"ACTIVE_SUPPLIER:{PartitionDigest(requirementKey, supplierRef)}";

    /// <summary>Canonical parameters of the singular <c>active-supplier-owner/v1</c> contract.</summary>
    public static string SupplierParameters(VersionedEntityRef supplierRef)
    {
        ArgumentNullException.ThrowIfNull(supplierRef);
        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("supplier_ref", ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(supplierRef.Id.ToString("D"))),
                ("version", ApprovalCanonicalJson.Number(supplierRef.Version))))));
    }

    public static bool MatchesContentDigest(SupplierVersionView view) =>
        string.Equals(
            ContentDigest(view.SupplierId, view.Version, view.Content),
            view.ContentDigest,
            StringComparison.Ordinal);

    private static SortedDictionary<string, object?> DescribeAddress(SupplierAddress address) =>
        new(StringComparer.Ordinal)
        {
            ["city"] = address.City,
            ["country_code"] = address.CountryCode,
            ["id"] = address.Id.ToString("D"),
            ["label"] = address.Label,
            ["line1"] = address.Line1,
            ["line2"] = address.Line2,
            ["postal_code"] = address.PostalCode,
            ["region"] = address.Region
        };

    private static SortedDictionary<string, object?> DescribeContact(SupplierContact contact) =>
        new(StringComparer.Ordinal)
        {
            ["email"] = contact.Email,
            ["id"] = contact.Id.ToString("D"),
            ["job_title"] = contact.JobTitle,
            ["name"] = contact.Name,
            ["phone"] = contact.Phone
        };

    private static SortedDictionary<string, object?> DescribeEntityRef(VersionedEntityRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["entity_type"] = reference.EntityType,
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };

    private static SortedDictionary<string, object?> DescribeCodeRef(VersionedCodeRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["catalog"] = reference.Catalog,
            ["code"] = reference.Code,
            ["digest"] = reference.Digest,
            ["version"] = reference.Version
        };

    private static SortedDictionary<string, object?> DescribeAttachment(SupplierAgreementAttachmentView attachment) =>
        new(StringComparer.Ordinal)
        {
            ["content_type"] = attachment.ContentType,
            ["file_id"] = attachment.Id.ToString("D"),
            ["file_name"] = attachment.FileName,
            ["length"] = attachment.Length,
            ["sha256"] = SupplierCodes.Digest(attachment.Sha256, "Attachment digest"),
            ["version"] = attachment.Version
        };

    private static object?[] TargetSet(IEnumerable<ApprovalTarget> targets) => Set((targets ?? [])
        .Select(target => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = target.Id.ToString("D"),
            ["material_snapshot_digest"] = target.MaterialSnapshotDigest,
            ["type"] = target.Type,
            ["version"] = target.Version
        }));

    private static object?[] Set(IEnumerable<object?> values) => values
        .OrderBy(
            value => PolicyCanonicalizer.SerializeCanonical(value!),
            Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
        .ToArray();

    private static string CanonicalDecimal(decimal value) => value.ToString(
        "0.############################", System.Globalization.CultureInfo.InvariantCulture);

    private static string CanonicalDecimal(string value) =>
        CanonicalDecimal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
}
