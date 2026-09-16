using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Closed vocabulary of the Sourcing domain (SPEC 10 Datos y contratos): contract versions, owner
/// identities and limits. These are contract identities, not credentials.
/// </summary>
public static class SourcingCodes
{
    public const string ProcessLineContract = "sourcing-process-line/v1";
    public const string RfqLineContract = "rfq-line/v1";
    public const string RfqVersionContract = "rfq-version/v1";
    public const string CommercialTermsContract = "commercial-terms/v1";
    public const string EvaluationWeightContract = "evaluation-weight/v1";
    public const string QuotationVersionContract = "quotation-version/v1";
    public const string QuotationLineContract = "quotation-line/v1";
    public const string QuotationReviewContract = "quotation-review/v1";
    public const string AttachmentRefContract = "sourcing-attachment-ref/v1";
    public const string AttachmentContract = "sourcing-attachment/v1";
    public const string QuotationWaiverFactsContract = "quotation-waiver-facts/v1";
    public const string FxSnapshotContract = "sourcing-fx-snapshot/v1";
    public const string QuoteEvaluationContract = "quote-evaluation-version/v1";
    public const string CatalogSnapshotContract = "catalog-snapshot/v1";
    public const string CatalogSnapshotsDigestContract = "catalog-snapshots/v1";
    public const string ProposalVersionContract = "sourcing-proposal-version/v1";
    public const string AwardVersionContract = "award-version/v1";
    public const string AwardLineContract = "award-line/v1";
    public const string AwardCandidateContract = "award-candidate/v1";
    public const string CompletenessManifestContract = "sourcing-completeness-manifest/v1";
    public const string PolicyFactEnvelopeContract = "sourcing-policy-fact-envelope/v1";
    public const string PolicyEvaluationRefContract = "policy-evaluation-ref/v1";
    public const string QuotationStatusEvidenceContract = "quotation-status-evidence/v1";
    public const string ProcurementStageEvidenceContract = "procurement-stage-evidence/v1";
    public const string AwardConsumptionContract = "award-consumption/v1";

    /// <summary>Adapter/processor identities of the two SPEC 05 owners implemented here.</summary>
    public const string QuotationStatusOwnerAdapterId = "quotation-status-owner";
    public const string ProcurementStageOwnerAdapterId = "procurement-stage-owner";
    public const string OwnerAdapterVersion = "v1";
    public const string QuotationStatusProcessorId = "sourcing-domain";
    public const string ProcurementStageProcessorId = "sourcing-domain";

    /// <summary>Typed owner/provider identities of the sourcing Policy facts.</summary>
    public const string PolicyFactProviderId = "sourcing-domain";
    public const string PolicyFactProviderContractVersion = "sourcing-policy-facts/v1";
    public const string ApprovalAdapterId = "sourcing-policy-approval-adapter";
    public const string ApprovalAdapterVersion = "v1";
    public const string ApprovalSubjectType = "SOURCING_PROPOSAL";
    public const string ApprovalOperation = "SUBMIT_SOURCING_PROPOSAL";
    public const string PolicyOperation = "SOURCING_PO";

    /// <summary>Catalog family and dependency identifiers reused by the sourcing contract.</summary>
    public const string SupplierCatalog = "SUPPLIER";
    public const string ApprovalTargetType = "PURCHASE_REQUEST_LINE";
    public const string RowVersionEntityType = "SOURCING_PROCESS";

    public const string ActionProcessCreated = "SOURCING_PROCESS_CREATED";
    public const string ActionProcessActivated = "SOURCING_PROCESS_ACTIVATED";
    public const string ActionProcessCancelled = "SOURCING_PROCESS_CANCELLED";
    public const string ActionProcessAwarded = "SOURCING_PROCESS_AWARDED";
    public const string ActionRfqOpened = "SOURCING_RFQ_OPENED";
    public const string ActionRfqExtended = "SOURCING_RFQ_EXTENDED";
    public const string ActionRfqClosed = "SOURCING_RFQ_CLOSED";
    public const string ActionRfqCancelled = "SOURCING_RFQ_CANCELLED";
    public const string ActionQuotationRegistered = "SOURCING_QUOTATION_REGISTERED";
    public const string ActionQuotationReviewed = "SOURCING_QUOTATION_REVIEWED";
    public const string ActionQuotationWithdrawn = "SOURCING_QUOTATION_WITHDRAWN";
    public const string ActionAttachmentStaged = "SOURCING_ATTACHMENT_STAGED";
    public const string ActionAttachmentConfirmed = "SOURCING_ATTACHMENT_CONFIRMED";
    public const string ActionAttachmentDownloaded = "SOURCING_ATTACHMENT_DOWNLOADED";
    public const string ActionFxRegistered = "SOURCING_FX_SNAPSHOT_REGISTERED";
    public const string ActionManualScoreRecorded = "SOURCING_MANUAL_SCORE_RECORDED";
    public const string ActionEvaluationCreated = "SOURCING_RFQ_EVALUATED";
    public const string ActionLineSelected = "SOURCING_LINE_SELECTED";

    public const string TargetProcess = "SourcingProcess";
    public const string TargetRfq = "RfqVersion";
    public const string TargetQuotation = "QuotationVersion";
    public const string TargetAttachment = "SourcingAttachment";
    public const string TargetSelection = "SourcingSelection";

    public const int MaxLinesPerProcess = 500;
    public const int MaxSuppliersPerRfq = 100;
    public const int MaxQuotationsPerRfq = 200;
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const long MaxCanonicalDocumentBytes = 5L * 1024 * 1024;
    public const int MaxKeyLength = 128;
    public const int MaxReasonScalars = 1_000;
    public const int MaxMotiveScalars = 1_000;
    public const int MaxJustificationScalars = 1_000;
    public const int MaxTechnicalResponseScalars = 2_000;
    public const int MaxFileNameScalars = 200;
    public const int MaxUnitCodeLength = 64;
    public const int MaxSourceReferenceScalars = 300;
    public const int MaxDeliveryDays = 3_650;
    public const int MaxRfqVersions = 500;
    public const int MaxQuotationVersions = 500;
    public const int MaxEvaluationVersions = 500;

    private static readonly Regex CodePattern =
        new("^[A-Z][A-Z0-9_.:-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyPattern =
        new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DecimalPattern =
        new("^(0|[1-9][0-9]*)(\\.[0-9]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/pdf", "image/png", "image/jpeg", "message/rfc822"
    };

    public static string Key(string? value, string field) =>
        value is null || !KeyPattern.IsMatch(value)
            ? throw new DomainValidationException(
                $"{field} must contain 1-128 characters of [A-Za-z0-9._:-].")
            : value;

    public static string Code(string? value, string field)
    {
        var normalized = Required(value, field).ToUpperInvariant();
        return CodePattern.IsMatch(normalized)
            ? normalized
            : throw new DomainValidationException(
                $"{field} must match [A-Z][A-Z0-9_.:-]* with at most 64 characters.");
    }

    public static string Currency(string? value, string field)
    {
        var normalized = Required(value, field).ToUpperInvariant();
        return CurrencyPattern.IsMatch(normalized)
            ? normalized
            : throw new DomainValidationException($"{field} must be an ISO 4217 code.");
    }

    public static string Digest(string? value, string field)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException($"{field} must be a 64-character SHA-256 value.");
        }

        return value.ToLowerInvariant();
    }

    public static string Reason(string? value) => Scalars(value, "Reason", MaxReasonScalars);

    public static string Motive(string? value) => Scalars(value, "Motive", MaxMotiveScalars);

    public static string Justification(string? value) =>
        Scalars(value, "Justification", MaxJustificationScalars);

    public static string TechnicalResponse(string? value) =>
        Scalars(value, "Technical response", MaxTechnicalResponseScalars);

    public static string FileName(string? value) => Scalars(value, "File name", MaxFileNameScalars);

    public static string SourceReference(string? value) =>
        Scalars(value, "Source reference", MaxSourceReferenceScalars);

    public static string UnitCode(string? value, string field) => Code(value, field);

    public static string Scalars(string? value, string field, int maxScalars)
    {
        var normalized = Required(value, field).Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        return scalars < 1 || scalars > maxScalars
            ? throw new DomainValidationException(
                $"{field} must contain between 1 and {maxScalars} Unicode scalars.")
            : normalized;
    }

    public static string ContentType(string? value)
    {
        var normalized = Required(value, "Content type").ToLowerInvariant();
        return AllowedContentTypes.Contains(normalized)
            ? normalized
            : throw new DomainValidationException("The attachment content type is not allowed.");
    }

    /// <summary>Canonical decimal string: no sign, no exponent and at most twelve decimals.</summary>
    public static string CanonicalDecimal(string? value, string field)
    {
        if (value is null || !DecimalPattern.IsMatch(value))
        {
            throw new DomainValidationException($"{field} must be a canonical non-negative decimal string.");
        }

        var separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator >= 0 && value.Length - separator - 1 > 12)
        {
            throw new DomainValidationException($"{field} admits at most twelve decimal places.");
        }

        return value;
    }

    public static decimal PositiveDecimal(decimal value, string field) =>
        value > 0
            ? value
            : throw new DomainValidationException($"{field} must be strictly positive.");

    public static decimal NonNegativeDecimal(decimal value, string field) =>
        value >= 0
            ? value
            : throw new DomainValidationException($"{field} cannot be negative.");

    public static int Days(int value, string field) =>
        value is >= 0 and <= MaxDeliveryDays
            ? value
            : throw new DomainValidationException($"{field} must be between 0 and {MaxDeliveryDays} days.");

    public static int Weight(decimal value, string field)
    {
        if (value != decimal.Truncate(value) || value is < 0 or > 100)
        {
            throw new DomainValidationException($"{field} must be a whole number between 0 and 100.");
        }

        return (int)value;
    }

    /// <summary>UTC instant with the contractual seven-decimal format.</summary>
    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Canonical decimal string used by digests and persisted rows.</summary>
    public static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Rounds to the contractual twelve-decimal scale of normalized amounts (REQ-08).</summary>
    public static decimal Decimal12(decimal value) =>
        decimal.Round(value, 12, MidpointRounding.ToEven);

    /// <summary>Rounds criterion and total scores to four decimals, half to even (REQ-07).</summary>
    public static decimal Score(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.ToEven);

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim();
}
