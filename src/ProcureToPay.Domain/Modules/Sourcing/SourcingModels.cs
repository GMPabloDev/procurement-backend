using System.Collections.Immutable;
using System.Globalization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>Lifecycle of one sourcing process (REQ-01).</summary>
public enum SourcingProcessState
{
    Draft = 1,
    Active = 2,
    Awarded = 3,
    Cancelled = 4
}

/// <summary>Lifecycle of one RFQ root (REQ-02).</summary>
public enum RfqStatus
{
    Draft = 1,
    Open = 2,
    Closed = 3,
    Cancelled = 4
}

/// <summary>Timeliness of one quotation version against the deadline in force when it was received (REQ-04).</summary>
public enum QuotationTimeliness
{
    OnTime = 1,
    Late = 2
}

/// <summary>Review state of one quotation version (REQ-04).</summary>
public enum QuotationReviewStatus
{
    Pending = 1,
    Valid = 2,
    Invalid = 3,
    Withdrawn = 4
}

/// <summary>Closed review codes of <c>quotation-review/v1</c>.</summary>
public enum QuotationReviewCode
{
    MissingRequiredLine = 1,
    MoneyMismatch = 2,
    TermsNoncompliant = 3,
    TechnicalNoncompliant = 4,
    EvidenceInvalid = 5,
    Other = 6
}

/// <summary>Physical state of one sourcing attachment (REQ-03).</summary>
public enum SourcingAttachmentState
{
    Staged = 1,
    Confirmed = 2
}

/// <summary>Route that produced one proposal (REQ-06, REQ-10).</summary>
public enum SourcingSelectionBasis
{
    Rfq = 1,
    ApprovedCatalog = 2
}

/// <summary>The six weighted criteria of one evaluation (REQ-07).</summary>
public enum SourcingEvaluationCriterion
{
    Price = 1,
    DeliveryTime = 2,
    Warranty = 3,
    PaymentTerms = 4,
    TechnicalCompliance = 5,
    SupplierPerformance = 6
}

/// <summary>
/// Reference to one versioned Sourcing artefact or one Purchase Request line version
/// (<c>{content_digest,id,version}</c>). Both carry the digest of the version they point at, so a
/// stale reference is detectable without reading the referenced row.
/// </summary>
public sealed record SourcingContentRef
{
    public SourcingContentRef(Guid id, int version, string contentDigest)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("A sourcing reference requires its identity.")
            : id;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A sourcing reference requires a positive version.");
        ContentDigest = SourcingCodes.Digest(contentDigest, "Reference content digest");
    }

    public Guid Id { get; }
    public int Version { get; }
    public string ContentDigest { get; }
}

/// <summary>
/// Reference to one versioned entity of another domain (<c>{id,version}</c>): the supplier and the
/// catalogue entry are re-verified server-side at selection and publication, so they carry no digest.
/// </summary>
public sealed record SourcingEntityRef
{
    public SourcingEntityRef(Guid id, int version)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("An entity reference requires its identity.")
            : id;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("An entity reference requires a positive version.");
    }

    public Guid Id { get; }
    public int Version { get; }
}

/// <summary>
/// One line attached to a sourcing process (<c>sourcing-process-line/v1</c>). The Buyer declares the
/// quantity and unit; the Purchase Request line itself is never modified (REQ-01).
/// </summary>
public sealed record SourcingProcessLine
{
    public const string ContractVersion = SourcingCodes.ProcessLineContract;

    public SourcingProcessLine(SourcingContentRef lineRef, decimal requestedQuantity, string unitCode)
    {
        LineRef = lineRef ?? throw new DomainValidationException("A sourcing line requires its request line.");
        RequestedQuantity = SourcingCodes.PositiveDecimal(requestedQuantity, "Requested quantity");
        UnitCode = SourcingCodes.UnitCode(unitCode, "Unit code");
    }

    public SourcingContentRef LineRef { get; }
    public decimal RequestedQuantity { get; }
    public string UnitCode { get; }
}

/// <summary>
/// One RFQ line (<c>rfq-line/v1</c>). REQ-02 requires byte-equivalence with the
/// <c>sourcing-process-line/v1</c> of its process, so both documents share one shape.
/// </summary>
public sealed record RfqLine
{
    public const string ContractVersion = SourcingCodes.RfqLineContract;

    public RfqLine(SourcingContentRef lineRef, decimal requestedQuantity, string unitCode)
    {
        LineRef = lineRef ?? throw new DomainValidationException("An RFQ line requires its request line.");
        RequestedQuantity = SourcingCodes.PositiveDecimal(requestedQuantity, "Requested quantity");
        UnitCode = SourcingCodes.UnitCode(unitCode, "Unit code");
    }

    public SourcingContentRef LineRef { get; }
    public decimal RequestedQuantity { get; }
    public string UnitCode { get; }
}

/// <summary>
/// Commercial terms of one RFQ, quotation or award (<c>commercial-terms/v1</c>). The RFQ fixes
/// expected minima; quotations and the award keep the offered/selected values in full (REQ-02).
/// </summary>
public sealed record CommercialTerms
{
    public const string ContractVersion = SourcingCodes.CommercialTermsContract;

    public CommercialTerms(int deliveryDays, string? incotermCode, string paymentTermsCode, int warrantyDays)
    {
        DeliveryDays = SourcingCodes.Days(deliveryDays, "Delivery days");
        IncotermCode = string.IsNullOrWhiteSpace(incotermCode)
            ? null
            : SourcingCodes.Code(incotermCode, "Incoterm code");
        PaymentTermsCode = SourcingCodes.Code(paymentTermsCode, "Payment terms code");
        WarrantyDays = SourcingCodes.Days(warrantyDays, "Warranty days");
    }

    public int DeliveryDays { get; }
    public string? IncotermCode { get; }
    public string PaymentTermsCode { get; }
    public int WarrantyDays { get; }
}

/// <summary>One weighted criterion of an RFQ (<c>evaluation-weight/v1</c>).</summary>
public sealed record EvaluationWeight
{
    public const string ContractVersion = SourcingCodes.EvaluationWeightContract;

    public EvaluationWeight(SourcingEvaluationCriterion criterion, int weight)
    {
        if (!Enum.IsDefined(criterion))
        {
            throw new DomainValidationException("The evaluation criterion is invalid.");
        }

        Criterion = criterion;
        Weight = weight is >= 0 and <= 100
            ? weight
            : throw new DomainValidationException("An evaluation weight must be between 0 and 100.");
    }

    public SourcingEvaluationCriterion Criterion { get; }
    public int Weight { get; }
}

/// <summary>
/// The six weights frozen when the RFQ was opened (REQ-07): each between 0 and 100, the sum exactly
/// 100 and <c>PRICE</c> strictly positive.
/// </summary>
public sealed record EvaluationWeightSet
{
    public EvaluationWeightSet(IEnumerable<EvaluationWeight> weights)
    {
        var materialized = (weights ?? []).ToImmutableArray();
        if (materialized.Length != 6 ||
            materialized.Select(weight => weight.Criterion).Distinct().Count() != 6)
        {
            throw new DomainValidationException(
                "An evaluation weight set must cover the six criteria exactly once.");
        }

        if (materialized.Sum(weight => weight.Weight) != 100)
        {
            throw new DomainValidationException("The evaluation weights must add up to 100.");
        }

        if (materialized.Single(weight => weight.Criterion == SourcingEvaluationCriterion.Price).Weight <= 0)
        {
            throw new DomainValidationException("The price criterion requires a positive weight.");
        }

        Weights = materialized
            .OrderBy(weight => weight.Criterion)
            .ToImmutableArray();
    }

    public IReadOnlyList<EvaluationWeight> Weights { get; }

    public int WeightOf(SourcingEvaluationCriterion criterion) =>
        Weights.Single(weight => weight.Criterion == criterion).Weight;
}

/// <summary>
/// One line of a quotation (<c>quotation-line/v1</c>). Every amount is a canonical decimal string of
/// at most twelve decimals and the totals must reconcile exactly under <c>decimal(38,12)</c> (REQ-03).
/// </summary>
public sealed record QuotationLine
{
    public const string ContractVersion = SourcingCodes.QuotationLineContract;

    public QuotationLine(
        string additionalCharges,
        string discounts,
        string grossTotal,
        SourcingContentRef lineRef,
        string quantity,
        string subtotal,
        string taxes,
        string technicalResponse,
        string unitCode,
        string unitPrice)
    {
        LineRef = lineRef ?? throw new DomainValidationException("A quotation line requires its request line.");
        Quantity = SourcingCodes.CanonicalDecimal(quantity, "Quantity");
        UnitPrice = SourcingCodes.CanonicalDecimal(unitPrice, "Unit price");
        Subtotal = SourcingCodes.CanonicalDecimal(subtotal, "Subtotal");
        Taxes = SourcingCodes.CanonicalDecimal(taxes, "Taxes");
        AdditionalCharges = SourcingCodes.CanonicalDecimal(additionalCharges, "Additional charges");
        Discounts = SourcingCodes.CanonicalDecimal(discounts, "Discounts");
        GrossTotal = SourcingCodes.CanonicalDecimal(grossTotal, "Gross total");
        TechnicalResponse = SourcingCodes.TechnicalResponse(technicalResponse);
        UnitCode = SourcingCodes.UnitCode(unitCode, "Unit code");
        Validate();
    }

    public SourcingContentRef LineRef { get; }
    public string Quantity { get; }
    public string UnitPrice { get; }
    public string Subtotal { get; }
    public string Taxes { get; }
    public string AdditionalCharges { get; }
    public string Discounts { get; }
    public string GrossTotal { get; }
    public string TechnicalResponse { get; }
    public string UnitCode { get; }

    public decimal QuantityValue => decimal.Parse(Quantity, CultureInfo.InvariantCulture);
    public decimal UnitPriceValue => decimal.Parse(UnitPrice, CultureInfo.InvariantCulture);
    public decimal GrossTotalValue => decimal.Parse(GrossTotal, CultureInfo.InvariantCulture);

    /// <summary>
    /// REQ-03: quantity, unit price and gross total are strictly positive, the remaining amounts are
    /// non-negative, discounts never drive the total to zero or below and both equations hold at
    /// <c>decimal(38,12)</c> with <c>MidpointRounding.ToEven</c>.
    /// </summary>
    private void Validate()
    {
        var quantity = QuantityValue;
        var unitPrice = UnitPriceValue;
        var taxes = decimal.Parse(Taxes, CultureInfo.InvariantCulture);
        var charges = decimal.Parse(AdditionalCharges, CultureInfo.InvariantCulture);
        var discounts = decimal.Parse(Discounts, CultureInfo.InvariantCulture);
        var subtotal = decimal.Parse(Subtotal, CultureInfo.InvariantCulture);
        var grossTotal = GrossTotalValue;
        if (quantity <= 0 || unitPrice <= 0 || grossTotal <= 0)
        {
            throw new DomainValidationException(
                "A quotation line requires positive quantity, unit price and gross total.");
        }

        if (taxes < 0 || charges < 0 || discounts < 0)
        {
            throw new DomainValidationException("Quotation taxes, charges and discounts cannot be negative.");
        }

        if (SourcingCodes.Decimal12(quantity * unitPrice) != subtotal)
        {
            throw new DomainValidationException("The quotation subtotal does not match quantity times unit price.");
        }

        var expected = SourcingCodes.Decimal12(subtotal + taxes + charges - discounts);
        if (expected != grossTotal || grossTotal <= 0)
        {
            throw new DomainValidationException("The quotation gross total does not reconcile its components.");
        }
    }
}

/// <summary>
/// Review of one quotation version (<c>quotation-review/v1</c>). Only the Buyer reviews, with an
/// expected version; <c>VALID</c> requires an empty code set (REQ-04).
/// </summary>
public sealed record QuotationReview
{
    public const string ContractVersion = SourcingCodes.QuotationReviewContract;

    private QuotationReview(QuotationReviewStatus status)
    {
        Status = status;
    }

    public QuotationReviewStatus Status { get; }
    public IReadOnlyList<QuotationReviewCode> Codes { get; private init; } = [];
    public string? Motive { get; private init; }
    public Guid? ReviewerId { get; private init; }
    public DateTimeOffset? ReviewedAt { get; private init; }

    public static QuotationReview Pending() => new(QuotationReviewStatus.Pending);

    public static QuotationReview Decided(
        QuotationReviewStatus status,
        IEnumerable<QuotationReviewCode> codes,
        string? motive,
        Guid reviewerId,
        DateTimeOffset reviewedAt)
    {
        if (status == QuotationReviewStatus.Pending)
        {
            throw new DomainValidationException("A decided review cannot be PENDING.");
        }

        if (reviewerId == Guid.Empty)
        {
            throw new DomainValidationException("A decided review requires its reviewer.");
        }

        var materialized = (codes ?? [])
            .Distinct()
            .OrderBy(code => code)
            .ToImmutableArray();
        if (materialized.Any(code => !Enum.IsDefined(code)))
        {
            throw new DomainValidationException("The quotation review carries an unknown code.");
        }

        if (status == QuotationReviewStatus.Valid && materialized.Length != 0)
        {
            throw new DomainValidationException("A VALID review cannot carry review codes.");
        }

        var normalizedMotive = string.IsNullOrWhiteSpace(motive) ? null : SourcingCodes.Motive(motive);
        if (status == QuotationReviewStatus.Valid && normalizedMotive is not null)
        {
            throw new DomainValidationException("A VALID review cannot carry a motive.");
        }

        if (status != QuotationReviewStatus.Valid && normalizedMotive is null)
        {
            throw new DomainValidationException("An INVALID or WITHDRAWN review requires a motive.");
        }

        return new QuotationReview(status)
        {
            Codes = materialized,
            Motive = normalizedMotive,
            ReviewerId = reviewerId,
            ReviewedAt = reviewedAt.ToUniversalTime()
        };
    }

    public string StatusCode => SourcingStateCodes.ReviewStatus(Status);
}

/// <summary>One confirmed or staged attachment of a quotation (<c>sourcing-attachment-ref/v1</c>).</summary>
public sealed record SourcingAttachmentRef
{
    public const string ContractVersion = SourcingCodes.AttachmentRefContract;

    public SourcingAttachmentRef(
        string contentType,
        Guid fileId,
        string fileName,
        long length,
        string sha256,
        int version)
    {
        ContentType = SourcingCodes.ContentType(contentType);
        FileId = fileId == Guid.Empty
            ? throw new DomainValidationException("An attachment reference requires its identity.")
            : fileId;
        FileName = SourcingCodes.FileName(fileName);
        Length = length is > 0 and <= SourcingCodes.MaxAttachmentBytes
            ? length
            : throw new DomainValidationException(
                $"An attachment must contain between 1 and {SourcingCodes.MaxAttachmentBytes} bytes.");
        Sha256 = SourcingCodes.Digest(sha256, "Attachment digest");
        Version = version >= 1
            ? version
            : throw new DomainValidationException("An attachment reference requires a positive version.");
    }

    public string ContentType { get; }
    public Guid FileId { get; }
    public string FileName { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public int Version { get; }
}

/// <summary>
/// Immutable content of one quotation version (<c>quotation-version/v1</c>). A new answer of the same
/// supplier creates a successor; a withdrawal creates a <c>WITHDRAWN</c> successor (REQ-03).
/// </summary>
public sealed record QuotationVersionContent
{
    public QuotationVersionContent(
        Guid organizationId,
        SourcingContentRef rfqRef,
        SourcingEntityRef supplierRef,
        string currency,
        CommercialTerms terms,
        QuotationTimeliness timeliness,
        DateTimeOffset receivedAt,
        DateTimeOffset registeredAt,
        IEnumerable<QuotationLine> lines,
        IEnumerable<SourcingAttachmentRef> attachments,
        QuotationReview review)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A quotation requires its organization.");
        }

        OrganizationId = organizationId;
        RfqRef = rfqRef ?? throw new DomainValidationException("A quotation requires its RFQ.");
        SupplierRef = supplierRef ?? throw new DomainValidationException("A quotation requires its supplier.");
        Currency = SourcingCodes.Currency(currency, "Quotation currency");
        Terms = terms ?? throw new DomainValidationException("A quotation requires its commercial terms.");
        if (!Enum.IsDefined(timeliness))
        {
            throw new DomainValidationException("The quotation timeliness is invalid.");
        }

        Timeliness = timeliness;
        ReceivedAt = receivedAt.ToUniversalTime();
        RegisteredAt = registeredAt.ToUniversalTime();
        if (ReceivedAt > RegisteredAt)
        {
            throw new DomainValidationException("A quotation cannot be received after it was registered.");
        }

        Review = review ?? throw new DomainValidationException("A quotation requires its review state.");

        var materializedLines = (lines ?? []).ToImmutableArray();
        if (materializedLines.Length == 0 ||
            materializedLines.Select(line => line.LineRef.Id).Distinct().Count() != materializedLines.Length)
        {
            throw new DomainValidationException("A quotation requires at least one distinct quoted line.");
        }

        Lines = materializedLines.OrderBy(line => line.LineRef.Id).ToImmutableArray();
        var materializedAttachments = (attachments ?? []).ToImmutableArray();
        if (materializedAttachments.Length == 0)
        {
            throw new DomainValidationException("A quotation requires at least one confirmed attachment.");
        }

        Attachments = materializedAttachments
            .OrderBy(attachment => attachment.FileId)
            .ToImmutableArray();
    }

    public Guid OrganizationId { get; }
    public SourcingContentRef RfqRef { get; }
    public SourcingEntityRef SupplierRef { get; }
    public string Currency { get; }
    public CommercialTerms Terms { get; }
    public QuotationTimeliness Timeliness { get; }
    public DateTimeOffset ReceivedAt { get; }
    public DateTimeOffset RegisteredAt { get; }
    public IReadOnlyList<QuotationLine> Lines { get; }
    public IReadOnlyList<SourcingAttachmentRef> Attachments { get; }
    public QuotationReview Review { get; }
}

/// <summary>Administrative view of one append-only quotation version (REQ-03, REQ-14).</summary>
public sealed record QuotationVersionView(
    Guid QuotationId,
    int Version,
    QuotationVersionContent Content,
    string ContentDigest,
    int? PredecessorVersion,
    Guid ActorUserId,
    DateTimeOffset OccurredAt,
    string Reason)
{
    public string Timeliness => SourcingStateCodes.Timeliness(Content.Timeliness);

    /// <summary>Canonical <c>quotation-version/v1</c> document of this immutable version (SPEC 10).</summary>
    public string CanonicalDocument() => SourcingCanonicalizer.QuotationDocument(
        Version, PredecessorVersion, Content.OrganizationId, Content.RfqRef, Content.SupplierRef,
        Content.Currency, Content.Terms, Content.Timeliness, Content.ReceivedAt, Content.RegisteredAt,
        Content.Lines, Content.Attachments, Content.Review);
}

/// <summary>Administrative view of one RFQ version (REQ-02).</summary>
public sealed record RfqVersionView(
    Guid RfqId,
    int Version,
    Guid OrganizationId,
    Guid ProcessId,
    Guid RequestId,
    int RequestVersion,
    RfqStatus Status,
    string Currency,
    CommercialTerms Terms,
    EvaluationWeightSet Weights,
    DateTimeOffset? OpenedAt,
    DateTimeOffset ResponseDeadline,
    int? PredecessorVersion,
    IReadOnlyList<RfqLine> Lines,
    string ContentDigest,
    Guid ActorUserId,
    DateTimeOffset OccurredAt,
    string? Reason)
{
    public string StatusCode => SourcingStateCodes.RfqStatusCode(Status);

    /// <summary>Canonical <c>rfq-version/v1</c> document of this immutable version (SPEC 10).</summary>
    public string CanonicalDocument() => SourcingCanonicalizer.RfqDocument(
        RfqId, Version, OrganizationId, ProcessId, RequestId, RequestVersion, Status, Currency, Terms,
        Weights, OpenedAt, ResponseDeadline, PredecessorVersion, Lines);
}

/// <summary>
/// One extension of the RFQ deadline (REQ-02). Extensions are records of their own: the previous
/// deadline is preserved so a late answer is never reclassified (DEC-02).
/// </summary>
public sealed record RfqDeadlineExtension(
    Guid RfqId,
    int FromVersion,
    int ToVersion,
    DateTimeOffset PreviousDeadline,
    DateTimeOffset NewDeadline,
    Guid ActorUserId,
    DateTimeOffset OccurredAt,
    string Reason);

/// <summary>Rfq the Buyer sees when reading one RFQ (REQ-14).</summary>
public sealed record RfqView(
    Guid RfqId,
    Guid ProcessId,
    Guid RequestId,
    int RequestVersion,
    RfqStatus Status,
    int CurrentVersion,
    DateTimeOffset ResponseDeadline,
    DateTimeOffset? OpenedAt,
    IReadOnlyList<RfqVersionView> Versions,
    IReadOnlyList<RfqDeadlineExtension> Extensions);

/// <summary>Contractual codes of the sourcing lifecycle.</summary>
public static class SourcingStateCodes
{
    public static string ProcessState(SourcingProcessState state) => state switch
    {
        SourcingProcessState.Draft => "DRAFT",
        SourcingProcessState.Active => "ACTIVE",
        SourcingProcessState.Awarded => "AWARDED",
        SourcingProcessState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The sourcing process state is invalid.")
    };

    public static string RfqStatusCode(RfqStatus status) => status switch
    {
        RfqStatus.Draft => "DRAFT",
        RfqStatus.Open => "OPEN",
        RfqStatus.Closed => "CLOSED",
        RfqStatus.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The RFQ status is invalid.")
    };

    public static string Timeliness(QuotationTimeliness timeliness) => timeliness switch
    {
        QuotationTimeliness.OnTime => "ON_TIME",
        QuotationTimeliness.Late => "LATE",
        _ => throw new DomainValidationException("The quotation timeliness is invalid.")
    };

    public static string ReviewStatus(QuotationReviewStatus status) => status switch
    {
        QuotationReviewStatus.Pending => "PENDING",
        QuotationReviewStatus.Valid => "VALID",
        QuotationReviewStatus.Invalid => "INVALID",
        QuotationReviewStatus.Withdrawn => "WITHDRAWN",
        _ => throw new DomainValidationException("The quotation review status is invalid.")
    };

    public static string ReviewCode(QuotationReviewCode code) => code switch
    {
        QuotationReviewCode.MissingRequiredLine => "MISSING_REQUIRED_LINE",
        QuotationReviewCode.MoneyMismatch => "MONEY_MISMATCH",
        QuotationReviewCode.TermsNoncompliant => "TERMS_NONCOMPLIANT",
        QuotationReviewCode.TechnicalNoncompliant => "TECHNICAL_NONCOMPLIANT",
        QuotationReviewCode.EvidenceInvalid => "EVIDENCE_INVALID",
        QuotationReviewCode.Other => "OTHER",
        _ => throw new DomainValidationException("The quotation review code is invalid.")
    };

    public static string ProposalState(SourcingProposalState state) => state switch
    {
        SourcingProposalState.Draft => "DRAFT",
        SourcingProposalState.Submitted => "SUBMITTED",
        SourcingProposalState.Approved => "APPROVED",
        SourcingProposalState.Rejected => "REJECTED",
        SourcingProposalState.ChangesRequested => "CHANGES_REQUESTED",
        SourcingProposalState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The proposal state is invalid.")
    };

    public static string SelectionBasis(SourcingSelectionBasis basis) => basis switch
    {
        SourcingSelectionBasis.Rfq => "RFQ",
        SourcingSelectionBasis.ApprovedCatalog => "APPROVED_CATALOG",
        _ => throw new DomainValidationException("The selection basis is invalid.")
    };

    public static string Criterion(SourcingEvaluationCriterion criterion) => criterion switch
    {
        SourcingEvaluationCriterion.Price => "PRICE",
        SourcingEvaluationCriterion.DeliveryTime => "DELIVERY_TIME",
        SourcingEvaluationCriterion.Warranty => "WARRANTY",
        SourcingEvaluationCriterion.PaymentTerms => "PAYMENT_TERMS",
        SourcingEvaluationCriterion.TechnicalCompliance => "TECHNICAL_COMPLIANCE",
        SourcingEvaluationCriterion.SupplierPerformance => "SUPPLIER_PERFORMANCE",
        _ => throw new DomainValidationException("The evaluation criterion is invalid.")
    };
}
