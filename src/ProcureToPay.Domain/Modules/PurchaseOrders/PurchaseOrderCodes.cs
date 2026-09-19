using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>
/// Closed vocabulary of the Purchase Orders domain (SPEC 11 Datos y contratos): contract versions,
/// owner/workload identities, state codes and the contractual limits of REQ-11. These are contract
/// identities, not credentials.
/// </summary>
public static class PurchaseOrderCodes
{
    public const string PurchaseOrderVersionContract = "purchase-order-version/v1";
    public const string PurchaseOrderLineContract = "purchase-order-line/v1";
    public const string DeliveryCommitmentContract = "delivery-commitment/v1";
    public const string AcceptanceResponsibilityContract = "acceptance-responsibility/v1";
    public const string VendorTermsSnapshotContract = "vendor-terms-snapshot/v1";
    public const string AwardConsumptionClaimContract = "award-consumption-claim/v1";
    public const string AwardRecoveryContract = "award-recovery/v1";
    public const string AmendmentContract = "purchase-order-amendment/v1";
    public const string AmendmentLineDeltaContract = "amendment-line-delta/v1";
    public const string ResponsibilityChangeContract = "responsibility-change/v1";
    public const string DirectPurchaseCommandContract = "direct-purchase-command/v1";
    public const string DirectPurchaseAuthorizationContract = "direct-purchase-authorization/v1";
    public const string SupportingDocumentContract = "procurement-supporting-document/v1";
    public const string SupportingDocumentEvidenceContract = "supporting-document-evidence/v1";
    public const string TakeoverContract = "purchase-request-line-takeover/v1";
    public const string OrderingEvidenceContract = "purchase-request-ordering-evidence/v1";
    public const string ApprovalAdapterId = "purchase-order-approval-adapter";
    public const string ApprovalAdapterVersion = "v1";
    public const string SupportingDocumentOwnerAdapterId = "supporting-document-owner";
    public const string OwnerAdapterVersion = "v1";
    public const string BudgetTransitionCommandContract = "budget-transition-command/v1";

    /// <summary>Approval subject types and operations published by this domain (REQ-03).</summary>
    public const string PurchaseOrderSubjectType = "PURCHASE_ORDER";
    public const string AmendmentSubjectType = "PURCHASE_ORDER_AMENDMENT";
    public const string IssueOperation = "ISSUE_PURCHASE_ORDER";
    public const string ApplyAmendmentOperation = "APPLY_PURCHASE_ORDER_AMENDMENT";

    /// <summary>Workload identity of the productive COMMIT+ PURCHASE_ORDER producer (REQ-04).</summary>
    public const string DomainWorkloadIssuer = "internal://procure-to-pay";
    public const string DomainWorkloadClientId = "purchase-order-domain";
    public const string DomainProducerId = "purchase-order-domain";
    public const string BudgetSourceType = "PURCHASE_ORDER";

    /// <summary>Owner values of one line takeover (REQ-10).</summary>
    public const string OwnerSourcing = "SOURCING";
    public const string OwnerPurchaseOrder = "PURCHASE_ORDER";
    public const string OwnerDirectPurchase = "DIRECT_PURCHASE";
    public const string OwnerFulfillment = "FULFILLMENT";
    public const string OwnerInvoice = "INVOICE";

    /// <summary>Acceptance responsibility kinds (REQ-06).</summary>
    public const string KindGood = "GOOD";
    public const string KindService = "SERVICE";
    public const string KindSubscription = "SUBSCRIPTION";
    public const string ResponsibilityGoodsReceipt = "GOODS_RECEIPT";
    public const string ResponsibilityServiceAcceptance = "SERVICE_ACCEPTANCE";
    public const string ResponsibilitySubscriptionProvisioning = "SUBSCRIPTION_PROVISIONING";
    public const string ResponsibilitySubscriptionAccessConfirmation = "SUBSCRIPTION_ACCESS_CONFIRMATION";
    public const string PrincipalTypeUser = "USER";

    /// <summary>Supporting document business types (REQ-08).</summary>
    public const string DocumentTypeInvoice = "INVOICE";
    public const string DocumentTypeReceipt = "RECEIPT";
    public const string DocumentTypeOther = "OTHER";

    /// <summary>Amendment change kinds (REQ-05).</summary>
    public const string ChangeReduce = "REDUCE";
    public const string ChangeCancel = "CANCEL";
    public const string ChangeCommercial = "COMMERCIAL";

    /// <summary>Vendor terms source kinds (REQ-09).</summary>
    public const string TermsSourceAward = "AWARD";
    public const string TermsSourceDirectPurchase = "DIRECT_PURCHASE";

    /// <summary>Audited actions of the module.</summary>
    public const string ActionClaimCreated = "PURCHASE_ORDER_CLAIMED";
    public const string ActionClaimReleased = "PURCHASE_ORDER_CLAIM_RELEASED";
    public const string ActionClaimConsumedCancelled = "PURCHASE_ORDER_CLAIM_CONSUMED_CANCELLED";
    public const string ActionDraftUpdated = "PURCHASE_ORDER_DRAFT_UPDATED";
    public const string ActionSubmitted = "PURCHASE_ORDER_SUBMITTED";
    public const string ActionApproved = "PURCHASE_ORDER_APPROVED";
    public const string ActionChangesRequested = "PURCHASE_ORDER_CHANGES_REQUESTED";
    public const string ActionRejected = "PURCHASE_ORDER_REJECTED";
    public const string ActionIssued = "PURCHASE_ORDER_ISSUED";
    public const string ActionCancelled = "PURCHASE_ORDER_CANCELLED";
    public const string ActionAwardReopened = "PURCHASE_ORDER_AWARD_REOPENED";
    public const string ActionAwardCancelled = "PURCHASE_ORDER_AWARD_CANCELLED";
    public const string ActionAmendmentCreated = "PURCHASE_ORDER_AMENDMENT_CREATED";
    public const string ActionAmendmentSubmitted = "PURCHASE_ORDER_AMENDMENT_SUBMITTED";
    public const string ActionAmendmentApproved = "PURCHASE_ORDER_AMENDMENT_APPROVED";
    public const string ActionAmendmentChangesRequested = "PURCHASE_ORDER_AMENDMENT_CHANGES_REQUESTED";
    public const string ActionAmendmentRejected = "PURCHASE_ORDER_AMENDMENT_REJECTED";
    public const string ActionAmendmentApplied = "PURCHASE_ORDER_AMENDMENT_APPLIED";
    public const string ActionAmendmentCancelled = "PURCHASE_ORDER_AMENDMENT_CANCELLED";
    public const string ActionDirectPurchaseAuthorized = "DIRECT_PURCHASE_AUTHORIZED";
    public const string ActionDirectPurchaseCancelled = "DIRECT_PURCHASE_CANCELLED";
    public const string ActionDocumentStaged = "SUPPORTING_DOCUMENT_STAGED";
    public const string ActionDocumentConfirmed = "SUPPORTING_DOCUMENT_CONFIRMED";
    public const string ActionDocumentDownloaded = "SUPPORTING_DOCUMENT_DOWNLOADED";
    public const string ActionTakeoverAcquired = "PURCHASE_REQUEST_LINE_TAKEOVER_ACQUIRED";
    public const string ActionTakeoverReleased = "PURCHASE_REQUEST_LINE_TAKEOVER_RELEASED";

    public const string TargetPurchaseOrder = "PurchaseOrder";

    /// <summary>Consumer type a Purchase Order uses to hold the per-line takeover (REQ-10).</summary>
    public const string TakeoverConsumerPurchaseOrder = "PURCHASE_ORDER";

    /// <summary>Consumer type a Direct Purchase authorization uses to hold its line takeovers (REQ-07).</summary>
    public const string TakeoverConsumerDirectPurchase = "DIRECT_PURCHASE_AUTHORIZATION";
    public const string TargetAmendment = "PurchaseOrderAmendment";
    public const string TargetDirectPurchase = "DirectPurchaseAuthorization";
    public const string TargetSupportingDocument = "ProcurementSupportingDocument";

    /// <summary>Approval target type of one covered Purchase Request line (REQ-03).</summary>
    public const string ApprovalTargetType = "PURCHASE_REQUEST_LINE";

    /// <summary>Limits of REQ-11.</summary>
    public const int MaxLines = 500;
    public const int MaxDocumentsPerSubject = 100;
    public const long MaxDocumentBytes = 20L * 1024 * 1024;
    public const long MaxCanonicalDocumentBytes = 5L * 1024 * 1024;
    public const int MaxKeyLength = 128;
    public const int MaxReasonScalars = 1_000;
    public const int MaxLocationScalars = 1_000;
    public const int MaxFileNameScalars = 200;
    public const int MaxPoNumberLength = 32;
    public const int MaxVersions = 500;
    public const int MaxProcessorAttempts = 25;
    public const int MaxPaymentTermDays = 3_650;
    public const int MaxDeliveryDays = 3_650;

    private static readonly Regex CodePattern =
        new("^[A-Z][A-Z0-9_.:-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyPattern =
        new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ContractIdPattern =
        new("^[A-Za-z0-9._:/-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PoNumberPattern =
        new("^[A-Z0-9][A-Z0-9._:/-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    public static string ContractId(string? value, string field) =>
        value is null || !ContractIdPattern.IsMatch(value)
            ? throw new DomainValidationException(
                $"{field} must contain 1-128 characters of [A-Za-z0-9._:/-].")
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

    public static string PoNumber(string? value) =>
        value is null || !PoNumberPattern.IsMatch(value)
            ? throw new DomainValidationException(
                $"The PO number must match [A-Z0-9][A-Z0-9._:/-]* with at most {MaxPoNumberLength} characters.")
            : value;

    public static string Digest(string? value, string field)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException($"{field} must be a 64-character SHA-256 value.");
        }

        return value.ToLowerInvariant();
    }

    public static string Scalars(string? value, string field, int maxScalars)
    {
        var normalized = Required(value, field).Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        return scalars < 1 || scalars > maxScalars
            ? throw new DomainValidationException(
                $"{field} must contain between 1 and {maxScalars} Unicode scalars.")
            : normalized;
    }

    public static string Reason(string? value) => Scalars(value, "Reason", MaxReasonScalars);

    public static string Location(string? value) => Scalars(value, "Delivery location", MaxLocationScalars);

    public static string FileName(string? value) => Scalars(value, "File name", MaxFileNameScalars);

    public static string ContentType(string? value)
    {
        var normalized = Required(value, "Content type").ToLowerInvariant();
        return AllowedContentTypes.Contains(normalized)
            ? normalized
            : throw new DomainValidationException("The document content type is not allowed.");
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

    public static decimal Positive(decimal value, string field) =>
        value > 0 ? value : throw new DomainValidationException($"{field} must be strictly positive.");

    public static decimal NonNegative(decimal value, string field) =>
        value >= 0 ? value : throw new DomainValidationException($"{field} cannot be negative.");

    /// <summary>Amounts and quantities are persisted and digested at twelve decimals (NFR-06).</summary>
    public static decimal Decimal12(decimal value) => decimal.Round(value, 12, MidpointRounding.ToEven);

    /// <summary>UTC instant with the contractual seven-decimal format.</summary>
    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Calendar date of one delivery commitment (NFR-06).</summary>
    public static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Canonical decimal string used by digests and persisted rows.</summary>
    public static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>
    /// REQ-11: a canonical document admits at most five mebibytes. The check runs before the row is
    /// written, so an oversized document never reaches persistence.
    /// </summary>
    public static void RequireCanonicalDocument(string? document)
    {
        if (document is null || Encoding.UTF8.GetByteCount(document) > MaxCanonicalDocumentBytes)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A canonical purchase order document admits at most {MaxCanonicalDocumentBytes} bytes.");
        }
    }

    /// <summary>Owner code of one takeover, validated against the closed vocabulary (REQ-10).</summary>
    public static string Owner(string? value) => value switch
    {
        OwnerSourcing or OwnerPurchaseOrder or OwnerDirectPurchase or OwnerFulfillment or OwnerInvoice => value,
        _ => throw new DomainValidationException("The takeover owner is not recognized.")
    };

    /// <summary>Reverse lookup of the takeover owner code: the wire never publishes the CLR enum name.</summary>
    public static string OwnerOf(PurchaseRequestLineOwner owner) => owner switch
    {
        PurchaseRequestLineOwner.Sourcing => OwnerSourcing,
        PurchaseRequestLineOwner.PurchaseOrder => OwnerPurchaseOrder,
        PurchaseRequestLineOwner.DirectPurchase => OwnerDirectPurchase,
        PurchaseRequestLineOwner.Fulfillment => OwnerFulfillment,
        PurchaseRequestLineOwner.Invoice => OwnerInvoice,
        _ => throw new DomainValidationException("The takeover owner is not recognized.")
    };

    public static PurchaseRequestLineOwner ParseOwner(string? value) => value switch
    {
        OwnerSourcing => PurchaseRequestLineOwner.Sourcing,
        OwnerPurchaseOrder => PurchaseRequestLineOwner.PurchaseOrder,
        OwnerDirectPurchase => PurchaseRequestLineOwner.DirectPurchase,
        OwnerFulfillment => PurchaseRequestLineOwner.Fulfillment,
        OwnerInvoice => PurchaseRequestLineOwner.Invoice,
        _ => throw new DomainValidationException("The stored takeover owner is not recognized.")
    };

    /// <summary>Business type code of one supporting document (REQ-08).</summary>
    public static string DocumentType(string? value) => value switch
    {
        DocumentTypeInvoice or DocumentTypeReceipt or DocumentTypeOther => value,
        _ => throw new DomainValidationException("The supporting document business type is not recognized.")
    };

    /// <summary>Responsibility kinds required by one purchase type (REQ-06).</summary>
    public static IReadOnlyList<string> RequiredResponsibilities(string purchaseType) => purchaseType switch
    {
        KindGood => [ResponsibilityGoodsReceipt],
        KindService => [ResponsibilityServiceAcceptance],
        KindSubscription =>
            [ResponsibilitySubscriptionProvisioning, ResponsibilitySubscriptionAccessConfirmation],
        _ => throw new DomainValidationException("The purchase type must be GOOD, SERVICE or SUBSCRIPTION.")
    };

    public static string ChangeKind(string? value) => value switch
    {
        ChangeReduce or ChangeCancel or ChangeCommercial => value,
        _ => throw new DomainValidationException("The amendment change kind is not recognized.")
    };

    public static string TermsSourceKind(string? value) => value switch
    {
        TermsSourceAward or TermsSourceDirectPurchase => value,
        _ => throw new DomainValidationException("The vendor terms source kind is not recognized.")
    };

    /// <summary>Payment terms are a bounded business code plus a non-negative net-days interval.</summary>
    public static int NetDays(int value) =>
        value is >= 0 and <= MaxPaymentTermDays
            ? value
            : throw new DomainValidationException(
                $"The payment term net days must be between 0 and {MaxPaymentTermDays}.");

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim();
}
