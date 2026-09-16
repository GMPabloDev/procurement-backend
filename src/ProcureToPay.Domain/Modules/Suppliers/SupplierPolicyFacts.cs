using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>Stable reference to one Purchase Request line version (SPEC 06).</summary>
public sealed record SupplierFactSourceLine(Guid Id, int Version, string ContentDigest)
{
    public Guid Id { get; } = Id == Guid.Empty
        ? throw new DomainValidationException("A supplier fact snapshot requires its source line.")
        : Id;

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A supplier fact snapshot requires a positive line version.");

    public string ContentDigest { get; } = SupplierCodes.Digest(ContentDigest, "Line content digest");
}

/// <summary>Stable reference to one approved catalog version.</summary>
public sealed record ApprovedCatalogEntryRef(Guid Id, int Version)
{
    public Guid Id { get; } = Id == Guid.Empty
        ? throw new DomainValidationException("A catalog entry reference requires its identity.")
        : Id;

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A catalog entry reference requires a positive version.");
}

/// <summary>
/// Frozen evidence of one catalogue lookup (REQ-08, <c>supplier-policy-fact-snapshot/v1</c>). It is
/// produced once per Purchase Request line when the version is attested, so the two Policy facts it
/// carries stay reproducible even if the catalogue changes afterwards. It never contains banking
/// data, contacts, tax ids or the attachment itself.
/// </summary>
public sealed record SupplierPolicyFactSnapshot
{
    public const string ContractVersion = SupplierCodes.SupplierPolicyFactSnapshotContract;

    private static readonly IReadOnlySet<string> AgreementStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        ApprovedSupplierCatalogMatcher.AgreementNone,
        ApprovedSupplierCatalogMatcher.AgreementActive,
        ApprovedSupplierCatalogMatcher.AgreementInactive,
        ApprovedSupplierCatalogMatcher.AgreementExpired
    };

    public SupplierPolicyFactSnapshot(
        Guid organizationId,
        SupplierFactSourceLine sourceLine,
        VersionedEntityRef? supplierRef,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef? productRef,
        ApprovedCatalogEntryRef? catalogEntryRef,
        string? catalogContentDigest,
        bool preferredSupplier,
        string agreementStatus,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);
        ArgumentNullException.ThrowIfNull(spendCategoryRef);
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A supplier fact snapshot requires its organization.");
        }

        if (!string.Equals(spendCategoryRef.Catalog, "SPEND_CATEGORY", StringComparison.Ordinal))
        {
            throw new DomainValidationException("A supplier fact snapshot requires a SPEND_CATEGORY reference.");
        }

        if (supplierRef is not null && supplierRef.EntityType != SupplierCodes.SupplierCatalog)
        {
            throw new DomainValidationException("A supplier fact snapshot requires a SUPPLIER reference.");
        }

        if (agreementStatus is null || !AgreementStatuses.Contains(agreementStatus))
        {
            throw new DomainValidationException("The agreement status of a supplier fact snapshot is invalid.");
        }

        var hasEntry = catalogEntryRef is not null;
        if (hasEntry != (catalogContentDigest is not null))
        {
            throw new DomainValidationException(
                "A supplier fact snapshot carries the entry and its digest together or neither.");
        }

        if (hasEntry && supplierRef is null)
        {
            throw new DomainValidationException("Only a declared supplier can have a catalogue entry.");
        }

        if (preferredSupplier && !string.Equals(
                agreementStatus, ApprovedSupplierCatalogMatcher.AgreementActive, StringComparison.Ordinal))
        {
            throw new DomainValidationException("A preferred supplier requires an active agreement status.");
        }

        OrganizationId = organizationId;
        SourceLine = sourceLine;
        SupplierRef = supplierRef;
        SpendCategoryRef = spendCategoryRef;
        ProductRef = productRef;
        CatalogEntryRef = catalogEntryRef;
        CatalogContentDigest = catalogContentDigest is null
            ? null
            : SupplierCodes.Digest(catalogContentDigest, "Catalog content digest");
        PreferredSupplier = preferredSupplier;
        AgreementStatus = agreementStatus;
        EvaluatedAt = evaluatedAt.ToUniversalTime();
        Digest = ComputeDigest();
    }

    public Guid OrganizationId { get; }
    public SupplierFactSourceLine SourceLine { get; }
    public VersionedEntityRef? SupplierRef { get; }
    public VersionedCodeRef SpendCategoryRef { get; }
    public VersionedEntityRef? ProductRef { get; }
    public ApprovedCatalogEntryRef? CatalogEntryRef { get; }
    public string? CatalogContentDigest { get; }
    public bool PreferredSupplier { get; }
    public string AgreementStatus { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public string Digest { get; }

    /// <summary>
    /// <c>supplier_policy_fact_snapshot_digest</c>: the published preimage of REQ-08. The digest of
    /// this record must reproduce it, so a tampered row is detectable without trusting the caller.
    /// </summary>
    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agreement_status"] = AgreementStatus,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["catalog_content_digest"] = CatalogContentDigest,
            ["catalog_entry_ref"] = CatalogEntryRef is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = CatalogEntryRef.Id.ToString("D"),
                    ["version"] = CatalogEntryRef.Version
                },
            ["contract_version"] = ContractVersion,
            ["evaluated_at"] = SupplierCodes.FormatUtc(EvaluatedAt),
            ["preferred_supplier"] = PreferredSupplier,
            ["product_ref"] = ProductRef is null ? null : Reference(ProductRef),
            ["source_line"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["content_digest"] = SourceLine.ContentDigest,
                ["id"] = SourceLine.Id.ToString("D"),
                ["version"] = SourceLine.Version
            },
            ["spend_category_ref"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalog"] = SpendCategoryRef.Catalog,
                ["code"] = SpendCategoryRef.Code,
                ["digest"] = SpendCategoryRef.Digest,
                ["version"] = SpendCategoryRef.Version
            },
            ["supplier_ref"] = SupplierRef is null ? null : Reference(SupplierRef)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    private static SortedDictionary<string, object?> Reference(VersionedEntityRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["entity_type"] = reference.EntityType,
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };
}

/// <summary>
/// <c>purchase-request-supplier-fact-snapshots/v1</c>: the exact set of one snapshot per line of a
/// presented Purchase Request version. Its digest enters the v2 completeness manifest (REQ-09).
/// </summary>
public sealed record SupplierFactSnapshotSet
{
    public const string ContractVersion = SupplierCodes.SupplierFactSnapshotsContract;

    public SupplierFactSnapshotSet(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        IEnumerable<SupplierPolicyFactSnapshot> snapshots)
    {
        if (organizationId == Guid.Empty || requestId == Guid.Empty || requestVersion < 1)
        {
            throw new DomainValidationException("A supplier fact snapshot set requires its request identity.");
        }

        var materialized = (snapshots ?? []).ToImmutableArray();
        if (materialized.Length == 0 ||
            materialized.Select(snapshot => $"{snapshot.SourceLine.Id:D}:{snapshot.SourceLine.Version}")
                .Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new DomainValidationException(
                "A supplier fact snapshot set requires exactly one snapshot per line.");
        }

        OrganizationId = organizationId;
        RequestId = requestId;
        RequestVersion = requestVersion;
        Snapshots = materialized
            .OrderBy(snapshot => snapshot.SourceLine.Id)
            .ThenBy(snapshot => snapshot.SourceLine.Version)
            .ToImmutableArray();
        Digest = ComputeDigest();
    }

    public Guid OrganizationId { get; }
    public Guid RequestId { get; }
    public int RequestVersion { get; }
    public IReadOnlyList<SupplierPolicyFactSnapshot> Snapshots { get; }
    public string Digest { get; }

    /// <summary>
    /// <c>supplier_fact_snapshots_digest</c>: reproduced from the persisted snapshots before serving
    /// or approving anything, so a partially written set cannot reach Policy or Approval.
    /// </summary>
    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ContractVersion,
            ["organization_id"] = OrganizationId.ToString("D"),
            ["request_id"] = RequestId.ToString("D"),
            ["request_version"] = RequestVersion,
            // Canonical set: ordered by the bytes of each element, never by enumeration order.
            ["snapshots"] = Snapshots
                .Select(snapshot => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["line_id"] = snapshot.SourceLine.Id.ToString("D"),
                    ["line_version"] = snapshot.SourceLine.Version,
                    ["snapshot_digest"] = snapshot.Digest
                })
                .OrderBy(
                    value => PolicyCanonicalizer.SerializeCanonical(value!),
                    Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
                .ToArray()
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}
