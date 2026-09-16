using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>Operational cycle of a supplier (SPEC 09 REQ-03, §36).</summary>
public enum SupplierOperationalStatus
{
    Draft = 1,
    PendingApproval = 2,
    Active = 3,
    Suspended = 4,
    Blocked = 5
}

/// <summary>Administrated risk classification; never inferred by the system (REQ-04).</summary>
public enum SupplierRiskStatus
{
    Unassessed = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    Critical = 5
}

/// <summary>Lifecycle of one governed change proposal (REQ-03, Datos y contratos).</summary>
public enum SupplierProposalState
{
    Draft = 1,
    Pending = 2,
    Approved = 3,
    Rejected = 4,
    ChangesRequested = 5,
    Cancelled = 6
}

/// <summary>Kind of governed change a proposal carries (REQ-02, REQ-03).</summary>
public enum SupplierChangeKind
{
    Create = 1,
    SensitiveUpdate = 2,
    NonSensitiveUpdate = 3,
    StatusChange = 4
}

/// <summary>Bank account modality of one banking version (REQ-05).</summary>
public enum SupplierBankingAccountType
{
    Checking = 1,
    Savings = 2,
    Interbank = 3
}

/// <summary>Published state of one approved catalog version (REQ-06).</summary>
public enum ApprovedCatalogEntryStatus
{
    Active = 1,
    Inactive = 2
}

/// <summary>Physical state of one agreement attachment (REQ-07).</summary>
public enum SupplierAgreementAttachmentState
{
    Staged = 1,
    Confirmed = 2
}

/// <summary>
/// Closed vocabulary of the Supplier domain (SPEC 09 Datos y contratos): contract versions,
/// identity codes and limits. These are contract identities, not credentials.
/// </summary>
public static class SupplierCodes
{
    public const string SupplierVersionContract = "supplier-version/v1";
    public const string SupplierIdentityKeyContract = "supplier-identity-key/v1";
    public const string SupplierBankingDetailsContract = "supplier-banking-details/v1";
    public const string SupplierBankingMaskedContract = "supplier-banking-masked/v1";
    public const string SupplierReferenceContract = "supplier-reference/v1";
    public const string ApprovedSupplierReferenceContract = "approved-supplier-reference/v1";
    public const string ApprovedCatalogEntryContract = "approved-supplier-catalog-entry/v1";
    public const string SupplierAgreementAttachmentContract = "supplier-agreement-attachment/v1";
    public const string SupplierPolicyFactSnapshotContract = "supplier-policy-fact-snapshot/v1";
    public const string SupplierFactSnapshotsContract = "purchase-request-supplier-fact-snapshots/v1";
    public const string SupplierStatusChangedContract = "supplier-status-changed/v1";
    public const string ActiveSupplierEvidenceContract = "active-supplier-evidence/v1";

    public const string SupplierOwnerId = "supplier-domain";
    public const string SupplierOwnerContractVersion = "supplier-db/v1";
    public const string ApprovedSupplierFactOwnerId = "approved-supplier-catalog-domain";
    public const string ApprovedSupplierFactOwnerContractVersion = "approved-supplier-catalog-db/v1";
    public const string ActiveSupplierOwnerAdapterId = "active-supplier-owner";
    public const string ActiveSupplierOwnerAdapterVersion = "v1";
    /// <summary>Stable processor identity registered for the active-supplier owner (REQ-10).</summary>
    public const string ActiveSupplierProcessorId = "supplier-domain";

    /// <summary>Catalog identifier of the SPEC 02/07 supplier reference family.</summary>
    public const string SupplierCatalog = "SUPPLIER";

    public const string ApprovalSubjectType = "SUPPLIER";
    public const string ApprovalOperation = "SUPPLIER_CHANGE";
    public const string CatalogApprovalSubjectType = "SUPPLIER_CATALOG_CHANGE";
    public const string CatalogApprovalOperation = "APPROVE_SUPPLIER_CATALOG_CHANGE";
    public const string SupplierApprovalOperation = "APPROVE_SUPPLIER_CHANGE";
    public const string ApprovalTargetEntityType = "SUPPLIER";
    public const string ApprovalTargetType = "SUPPLIER_VERSION";
    public const string CatalogApprovalTargetType = "APPROVED_SUPPLIER_CATALOG_VERSION";

    public const string GovernanceAdapterId = "supplier-governance-approval-adapter";
    public const string GovernanceAdapterVersion = "v1";
    public const string GovernanceAdapterIdentity = "supplier-governance-approval-adapter/v1";

    public const string ActionCreated = "SUPPLIER_CREATED";
    public const string ActionUpdated = "SUPPLIER_UPDATED";
    public const string ActionSubmitted = "SUPPLIER_SUBMITTED";
    public const string ActionApproved = "SUPPLIER_APPROVED";
    public const string ActionRejected = "SUPPLIER_REJECTED";
    public const string ActionChangesRequested = "SUPPLIER_CHANGES_REQUESTED";
    public const string ActionBankingRevealed = "SUPPLIER_BANKING_REVEALED";
    public const string ActionCatalogPublished = "APPROVED_SUPPLIER_CATALOG_PUBLISHED";
    public const string ActionCatalogSubmitted = "APPROVED_SUPPLIER_CATALOG_SUBMITTED";
    public const string ActionAttachmentStaged = "SUPPLIER_AGREEMENT_ATTACHMENT_STAGED";
    public const string ActionAttachmentDownloaded = "SUPPLIER_AGREEMENT_ATTACHMENT_DOWNLOADED";
    public const string ActionPrerequisiteChecked = "SUPPLIER_PREREQUISITE_CHECKED";

    public const string TargetSupplier = "Supplier";
    public const string TargetCatalogEntry = "ApprovedSupplierCatalogEntry";
    public const string TargetAttachment = "SupplierAgreementAttachment";

    public const int MaxLegalNameScalars = 300;
    public const int MaxTradeNameScalars = 300;
    public const int MaxTaxIdScalars = 64;
    public const int MaxReasonScalars = 1_000;
    public const int MaxKeyLength = 128;
    public const int MaxDigestLength = 64;
    public const int MaxAddresses = 20;
    public const int MaxContacts = 20;
    public const int MaxBankingVersions = 20;
    public const int MaxBankingAccountsPerCurrency = 20;
    public const int MaxCatalogVersions = 200;
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;
    public const int MaxAttachmentNameScalars = 200;
    public const int MaxAgreementReferenceScalars = 200;
    public const int MaxUnitCodeLength = 64;
    public const int MaxExternalReferenceScalars = 200;

    /// <summary>Codes of one supplier field, address, contact, currency or unit.</summary>
    private static readonly Regex CodePattern =
        new("^[A-Z][A-Z0-9_.:-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyPattern =
        new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CountryPattern =
        new("^[A-Z]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DecimalPattern =
        new("^-?(0|[1-9][0-9]*)(\\.[0-9]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/pdf", "image/png", "image/jpeg"
    };

    public static string Code(string? value, string field)
    {
        var normalized = Normalize(value, field).ToUpperInvariant();
        if (!CodePattern.IsMatch(normalized))
        {
            throw new DomainValidationException(
                $"{field} must match [A-Z][A-Z0-9_.:-]* with at most 64 characters.");
        }

        return normalized;
    }

    public static string Key(string? value, string field) =>
        value is null || !KeyPattern.IsMatch(value)
            ? throw new DomainValidationException(
                $"{field} must contain 1-128 characters of [A-Za-z0-9._:-].")
            : value;

    public static string Currency(string? value, string field)
    {
        var normalized = Normalize(value, field).ToUpperInvariant();
        return CurrencyPattern.IsMatch(normalized)
            ? normalized
            : throw new DomainValidationException($"{field} must be an ISO 4217 code.");
    }

    /// <summary>The only catalog family accepted by supplier categories (SPEC 07).</summary>
    public static string Catalog(string? value, string expected)
    {
        var normalized = Normalize(value, "Catalog").ToUpperInvariant();
        return string.Equals(normalized, expected, StringComparison.Ordinal)
            ? normalized
            : throw new DomainValidationException($"The reference catalog must be {expected}.");
    }

    public static string CountryCode(string? value, string field)
    {
        var normalized = Normalize(value, field).ToUpperInvariant();
        return CountryPattern.IsMatch(normalized)
            ? normalized
            : throw new DomainValidationException($"{field} must be an ISO 3166-1 alpha-2 code.");
    }

    /// <summary>
    /// Public Tax ID: NFC, trimmed and uppercased with the invariant culture so the fiscal identity
    /// key is stable across case and accent-composition differences (REQ-01).
    /// </summary>
    public static string TaxId(string? value)
    {
        var normalized = Normalize(value, "Tax ID").Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var scalars = new StringInfo(normalized).LengthInTextElements;
        return scalars is < 1 or > MaxTaxIdScalars
            ? throw new DomainValidationException(
                $"Tax ID must contain between 1 and {MaxTaxIdScalars} Unicode scalars.")
            : normalized;
    }

    public static string DisplayName(string? value, string field, int maxScalars)
    {
        var normalized = Normalize(value, field).Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        return scalars < 1 || scalars > maxScalars
            ? throw new DomainValidationException(
                $"{field} must contain between 1 and {maxScalars} Unicode scalars.")
            : normalized;
    }

    public static string Reason(string? value)
    {
        var normalized = Normalize(value, "Reason").Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        return scalars is < 1 or > MaxReasonScalars
            ? throw new DomainValidationException(
                $"Reason must contain between 1 and {MaxReasonScalars} Unicode scalars.")
            : normalized;
    }

    public static string Digest(string? value, string field)
    {
        if (value is null || value.Length != MaxDigestLength || !value.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException($"{field} must be a 64-character SHA-256 value.");
        }

        return value.ToLowerInvariant();
    }

    public static string CanonicalDecimal(string? value, string field)
    {
        if (value is null || !DecimalPattern.IsMatch(value))
        {
            throw new DomainValidationException($"{field} must be a canonical decimal string.");
        }

        return value;
    }

    public static string ContentType(string? value)
    {
        var normalized = Normalize(value, "Content type").ToLowerInvariant();
        return AllowedContentTypes.Contains(normalized)
            ? normalized
            : throw new DomainValidationException("The attachment content type is not allowed.");
    }

    public static string EffectiveStatus(ApprovedCatalogEntryStatus status, DateTimeOffset validFrom, DateTimeOffset validTo, DateTimeOffset at)
    {
        if (at < validFrom)
        {
            return "INACTIVE";
        }

        if (status == ApprovedCatalogEntryStatus.Inactive)
        {
            return "INACTIVE";
        }

        return validTo <= at ? "EXPIRED" : "ACTIVE";
    }

    /// <summary>UTC instant with the contractual seven-decimal format.</summary>
    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Normalize(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim();
}

/// <summary>One physical address of a supplier version (REQ-04).</summary>
public sealed record SupplierAddress
{
    public SupplierAddress(
        Guid id,
        string label,
        string line1,
        string? line2,
        string city,
        string? region,
        string? postalCode,
        string countryCode)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("An address requires its stable identity.")
            : id;
        Label = SupplierCodes.DisplayName(label, "Address label", 60);
        Line1 = SupplierCodes.DisplayName(line1, "Address line", 200);
        Line2 = string.IsNullOrWhiteSpace(line2) ? null : SupplierCodes.DisplayName(line2, "Address line 2", 200);
        City = SupplierCodes.DisplayName(city, "City", 100);
        Region = string.IsNullOrWhiteSpace(region) ? null : SupplierCodes.DisplayName(region, "Region", 100);
        PostalCode = string.IsNullOrWhiteSpace(postalCode)
            ? null
            : SupplierCodes.DisplayName(postalCode, "Postal code", 20);
        CountryCode = SupplierCodes.CountryCode(countryCode, "Address country");
    }

    public Guid Id { get; }
    public string Label { get; }
    public string Line1 { get; }
    public string? Line2 { get; }
    public string City { get; }
    public string? Region { get; }
    public string? PostalCode { get; }
    public string CountryCode { get; }
}

/// <summary>One business contact of a supplier version (REQ-04): a name plus email or phone.</summary>
public sealed record SupplierContact
{
    public SupplierContact(Guid id, string name, string? email, string? phone, string? jobTitle)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("A contact requires its stable identity.")
            : id;
        Name = SupplierCodes.DisplayName(name, "Contact name", 200);
        Email = string.IsNullOrWhiteSpace(email) ? null : SupplierCodes.DisplayName(email, "Contact email", 254);
        Phone = string.IsNullOrWhiteSpace(phone) ? null : SupplierCodes.DisplayName(phone, "Contact phone", 40);
        if (Email is null && Phone is null)
        {
            throw new DomainValidationException("A contact requires an email or a phone number.");
        }

        if (Email is not null && (!Email.Contains('@') || Email.StartsWith('@') || Email.EndsWith('@')))
        {
            throw new DomainValidationException("The contact email is not a valid mailbox.");
        }

        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : SupplierCodes.DisplayName(jobTitle, "Contact job title", 120);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? Email { get; }
    public string? Phone { get; }
    public string? JobTitle { get; }
}

/// <summary>Payment terms of one supplier version (REQ-04).</summary>
public sealed record SupplierPaymentTerms
{
    public SupplierPaymentTerms(string code, int netDays)
    {
        Code = SupplierCodes.Code(code, "Payment terms code");
        NetDays = netDays is >= 0 and <= 365
            ? netDays
            : throw new DomainValidationException("Payment terms net days must be between 0 and 365.");
    }

    public string Code { get; }
    public int NetDays { get; }
}

/// <summary>Administrated performance score with its provenance (REQ-04).</summary>
public sealed record SupplierPerformanceScore
{
    public SupplierPerformanceScore(decimal score, string source, DateTimeOffset measuredAt)
    {
        Score = score is >= 0 and <= 100
            ? decimal.Round(score, 4)
            : throw new DomainValidationException("A performance score must be between 0 and 100.");
        if (decimal.Round(score, 4) != score)
        {
            throw new DomainValidationException("A performance score admits at most four decimal places.");
        }

        Source = SupplierCodes.DisplayName(source, "Performance source", 120);
        MeasuredAt = measuredAt.ToUniversalTime();
    }

    public decimal Score { get; }
    public string Source { get; }
    public DateTimeOffset MeasuredAt { get; }
}

/// <summary>Stable reference to one banking detail version (REQ-05).</summary>
public sealed record SupplierBankingRef(Guid Id, int Version)
{
    public Guid Id { get; } = Id == Guid.Empty
        ? throw new DomainValidationException("A banking reference requires its identity.")
        : Id;

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A banking reference requires a positive version.");
}

/// <summary>
/// Immutable content of one supplier version (REQ-02, REQ-04, <c>supplier-version/v1</c>). Banking
/// plaintext never lives here: the version only references encrypted banking versions.
/// </summary>
public sealed record SupplierVersionContent
{
    public SupplierVersionContent(
        string legalName,
        string? tradeName,
        string countryCode,
        string taxId,
        IEnumerable<SupplierAddress> addresses,
        IEnumerable<SupplierContact> contacts,
        SupplierPaymentTerms paymentTerms,
        IEnumerable<string> supportedCurrencies,
        IEnumerable<VersionedCodeRef> categoriesSupplied,
        SupplierOperationalStatus status,
        SupplierRiskStatus riskStatus,
        SupplierPerformanceScore? performanceScore,
        IEnumerable<SupplierBankingRef> bankingRefs)
    {
        ArgumentNullException.ThrowIfNull(paymentTerms);
        LegalName = SupplierCodes.DisplayName(legalName, "Legal name", SupplierCodes.MaxLegalNameScalars);
        TradeName = string.IsNullOrWhiteSpace(tradeName)
            ? null
            : SupplierCodes.DisplayName(tradeName, "Trade name", SupplierCodes.MaxTradeNameScalars);
        CountryCode = SupplierCodes.CountryCode(countryCode, "Country");
        TaxId = SupplierCodes.TaxId(taxId);
        var addressList = (addresses ?? []).OrderBy(address => address.Id).ToImmutableArray();
        if (addressList.Length > SupplierCodes.MaxAddresses ||
            addressList.Select(address => address.Id).Distinct().Count() != addressList.Length)
        {
            throw new DomainValidationException(
                $"A supplier version admits at most {SupplierCodes.MaxAddresses} distinct addresses.");
        }

        Addresses = addressList;
        var contactList = (contacts ?? []).OrderBy(contact => contact.Id).ToImmutableArray();
        if (contactList.Length is 0 || contactList.Length > SupplierCodes.MaxContacts ||
            contactList.Select(contact => contact.Id).Distinct().Count() != contactList.Length)
        {
            throw new DomainValidationException(
                $"A supplier version requires 1 to {SupplierCodes.MaxContacts} distinct contacts.");
        }

        Contacts = contactList;
        PaymentTerms = paymentTerms;
        var currencies = (supportedCurrencies ?? [])
            .Select(currency => SupplierCodes.Currency(currency, "Supported currency"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(currency => currency, StringComparer.Ordinal)
            .ToImmutableArray();
        if (currencies.Length == 0)
        {
            throw new DomainValidationException("A supplier version requires at least one supported currency.");
        }

        SupportedCurrencies = currencies;
        CategoriesSupplied = (categoriesSupplied ?? [])
            .Select(reference => new VersionedCodeRef(
                SupplierCodes.Catalog(reference.Catalog, "SPEND_CATEGORY"),
                reference.Code,
                reference.Version,
                reference.Digest))
            .OrderBy(reference => $"{reference.Code}:{reference.Version}", StringComparer.Ordinal)
            .ToImmutableArray();
        if (CategoriesSupplied.Count == 0)
        {
            throw new DomainValidationException("A supplier version requires at least one spend category.");
        }

        if (CategoriesSupplied.Select(reference => $"{reference.Code}|{reference.Version}")
                .Distinct(StringComparer.Ordinal).Count() != CategoriesSupplied.Count)
        {
            throw new DomainConflictException("A supplier version cannot repeat a spend category reference.");
        }

        Status = status;
        RiskStatus = riskStatus;
        PerformanceScore = performanceScore;
        var banking = (bankingRefs ?? []).OrderBy(reference => reference.Id).ThenBy(reference => reference.Version)
            .ToImmutableArray();
        if (banking.Select(reference => reference.Id).Distinct().Count() != banking.Length)
        {
            throw new DomainConflictException("A supplier version cannot reference the same banking detail twice.");
        }

        BankingRefs = banking;
    }

    public string LegalName { get; }
    public string? TradeName { get; }
    public string CountryCode { get; }
    public string TaxId { get; }
    public IReadOnlyList<SupplierAddress> Addresses { get; }
    public IReadOnlyList<SupplierContact> Contacts { get; }
    public SupplierPaymentTerms PaymentTerms { get; }
    public IReadOnlyList<string> SupportedCurrencies { get; }
    public IReadOnlyList<VersionedCodeRef> CategoriesSupplied { get; }
    public SupplierOperationalStatus Status { get; }
    public SupplierRiskStatus RiskStatus { get; }
    public SupplierPerformanceScore? PerformanceScore { get; }
    public IReadOnlyList<SupplierBankingRef> BankingRefs { get; }

    /// <summary>
    /// Rebuilds the same content with another operational status. The status is a governed field, so
    /// the server decides which value a candidate or a materialized version carries (REQ-02, REQ-03).
    /// </summary>
    public SupplierVersionContent WithStatus(SupplierOperationalStatus status) => new(
        LegalName,
        TradeName,
        CountryCode,
        TaxId,
        Addresses,
        Contacts,
        PaymentTerms,
        SupportedCurrencies,
        CategoriesSupplied,
        status,
        RiskStatus,
        PerformanceScore,
        BankingRefs);

    /// <summary>
    /// Sensitive governance fields of this content. Changing any of them requires an approved
    /// proposal; the classification is computed server-side from the persisted diff (REQ-02).
    /// </summary>
    public IReadOnlySet<string> SensitiveFieldNames() => new HashSet<string>(StringComparer.Ordinal)
    {
        "legal_name", "country_code", "tax_id", "payment_terms", "supported_currencies",
        "categories_supplied", "risk_status", "performance_score", "status"
    };
}

/// <summary>
/// Administrative view of one append-only supplier version (REQ-02, REQ-11). Banking is published
/// masked through its own projection; the version only carries opaque references.
/// </summary>
public sealed record SupplierVersionView(
    Guid SupplierId,
    int Version,
    SupplierVersionContent Content,
    string ContentDigest,
    int? PredecessorVersion,
    Guid ActorUserId,
    DateTimeOffset OccurredAt,
    string Reason)
{
    public string Status => SupplierStatusCodes.Code(Content.Status);
}

/// <summary>Public minimal supplier projection used to build a Purchase Request (REQ-11).</summary>
public sealed record SupplierReferenceView(
    Guid Id,
    int Version,
    string LegalName,
    string? TradeName,
    IReadOnlyList<string> SupportedCurrencies,
    IReadOnlyList<VersionedCodeRef> CategoriesSupplied);

/// <summary>Masked banking projection: never carries the account number (REQ-05).</summary>
public sealed record SupplierBankingMaskedView(
    Guid BankingDetailId,
    int Version,
    string BankName,
    string BankCountryCode,
    string Currency,
    SupplierBankingAccountType AccountType,
    bool IsDefault,
    string MaskedAccount);

/// <summary>Effective, reproducible projection of one approved catalog version (REQ-06, REQ-11).</summary>
public sealed record ApprovedSupplierReferenceView(
    Guid CatalogEntryId,
    int Version,
    VersionedEntityRef SupplierRef,
    VersionedCodeRef SpendCategoryRef,
    VersionedEntityRef? ProductRef,
    decimal NegotiatedPrice,
    string Currency,
    string UnitCode,
    string ExternalContractReference,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string EffectiveStatus);

/// <summary>Published agreement attachment metadata (REQ-07).</summary>
public sealed record SupplierAgreementAttachmentView(
    Guid Id,
    int Version,
    string FileName,
    string ContentType,
    long Length,
    string Sha256);

/// <summary>Event published atomically with one approved supplier status change (REQ-10).</summary>
public sealed record SupplierStatusChangedEvent(
    string ContractVersion,
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid OrganizationId,
    SupplierOperationalStatus? PreviousStatus,
    Guid SupplierId,
    int SupplierVersion,
    SupplierOperationalStatus Status)
{
    public string CanonicalJson()
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contract_version"] = SupplierCodes.SupplierStatusChangedContract,
            ["event_id"] = EventId.ToString("D"),
            ["occurred_at"] = SupplierCodes.FormatUtc(OccurredAt),
            ["organization_id"] = OrganizationId.ToString("D"),
            ["previous_status"] = PreviousStatus is null ? null : SupplierStatusCodes.Code(PreviousStatus.Value),
            ["status"] = SupplierStatusCodes.Code(Status),
            ["supplier_id"] = SupplierId.ToString("D"),
            ["supplier_version"] = SupplierVersion
        };
        return ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.SerializeCanonical(root);
    }
}

/// <summary>Contractual status codes of the supplier cycle.</summary>
public static class SupplierStatusCodes
{
    public static string Code(SupplierOperationalStatus status) => status switch
    {
        SupplierOperationalStatus.Draft => "DRAFT",
        SupplierOperationalStatus.PendingApproval => "PENDING_APPROVAL",
        SupplierOperationalStatus.Active => "ACTIVE",
        SupplierOperationalStatus.Suspended => "SUSPENDED",
        SupplierOperationalStatus.Blocked => "BLOCKED",
        _ => throw new DomainValidationException("The supplier status is invalid.")
    };

    public static string Code(SupplierRiskStatus status) => status switch
    {
        SupplierRiskStatus.Unassessed => "UNASSESSED",
        SupplierRiskStatus.Low => "LOW",
        SupplierRiskStatus.Medium => "MEDIUM",
        SupplierRiskStatus.High => "HIGH",
        SupplierRiskStatus.Critical => "CRITICAL",
        _ => throw new DomainValidationException("The supplier risk status is invalid.")
    };

    /// <summary>Reference usable by owners and catalogs: only the approved active version counts.</summary>
    public static bool IsUsable(SupplierOperationalStatus status) => status == SupplierOperationalStatus.Active;
}
