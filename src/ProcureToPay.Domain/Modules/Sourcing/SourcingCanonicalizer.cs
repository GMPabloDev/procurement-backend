using System.Globalization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Reproducible digests of the Sourcing domain (SPEC 10 Canonicalización y digests). Every preimage
/// is an object under <c>policy-canonical-json/v1</c> with exactly the published properties, NFC
/// strings, lowercase UUIDs, seven-decimal UTC instants, decimals as invariant strings and sets
/// ordered by their canonical bytes without duplicates.
/// </summary>
public static class SourcingCanonicalizer
{
    /// <summary>
    /// <c>rfq_content_digest</c> (<c>rfq-version/v1</c>): the immutable content of one RFQ version.
    /// In <c>DRAFT</c> the opening instant is null; once opened it never changes (REQ-02).
    /// </summary>
    public static string RfqContentDigest(
        Guid rfqId,
        int version,
        Guid organizationId,
        Guid processId,
        Guid requestId,
        int requestVersion,
        RfqStatus status,
        string currency,
        CommercialTerms terms,
        EvaluationWeightSet weights,
        DateTimeOffset? openedAt,
        DateTimeOffset responseDeadline,
        int? predecessorVersion,
        IEnumerable<RfqLine> lines)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(weights);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SourcingCodes.RfqVersionContract,
            ["currency"] = currency,
            ["evaluation_weights"] = Set(weights.Weights.Select(weight => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["criterion"] = SourcingStateCodes.Criterion(weight.Criterion),
                ["weight"] = weight.Weight
            })),
            ["lines"] = Set((lines ?? []).Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line_ref"] = ContentRef(line.LineRef),
                ["requested_quantity"] = SourcingCodes.Decimal(line.RequestedQuantity),
                ["unit_code"] = line.UnitCode
            })),
            ["opened_at"] = openedAt is null ? null : SourcingCodes.FormatUtc(openedAt.Value),
            ["organization_id"] = organizationId.ToString("D"),
            ["predecessor_version"] = predecessorVersion,
            ["process_id"] = processId.ToString("D"),
            ["request_id"] = requestId.ToString("D"),
            ["request_version"] = requestVersion,
            ["response_deadline"] = SourcingCodes.FormatUtc(responseDeadline),
            ["rfq_id"] = rfqId.ToString("D"),
            ["status"] = SourcingStateCodes.RfqStatusCode(status),
            ["terms"] = Terms(terms),
            ["version"] = version
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>quotation_content_digest</c> (<c>quotation-version/v1</c>): the immutable content of one
    /// supplier answer. The server-owned timeliness and registration instant are part of it, so a
    /// late answer cannot be re-registered as on time (REQ-03, REQ-04).
    /// </summary>
    public static string QuotationContentDigest(
        int version,
        int? predecessorVersion,
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
        ArgumentNullException.ThrowIfNull(rfqRef);
        ArgumentNullException.ThrowIfNull(supplierRef);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(review);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attachments"] = Set((attachments ?? []).Select(attachment => (object?)Attachment(attachment))),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SourcingCodes.QuotationVersionContract,
            ["currency"] = currency,
            ["lines"] = Set((lines ?? []).Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["additional_charges"] = line.AdditionalCharges,
                ["discounts"] = line.Discounts,
                ["gross_total"] = line.GrossTotal,
                ["line_ref"] = ContentRef(line.LineRef),
                ["quantity"] = line.Quantity,
                ["subtotal"] = line.Subtotal,
                ["taxes"] = line.Taxes,
                ["technical_response"] = line.TechnicalResponse,
                ["unit_code"] = line.UnitCode,
                ["unit_price"] = line.UnitPrice
            })),
            ["organization_id"] = organizationId.ToString("D"),
            ["predecessor_version"] = predecessorVersion,
            ["received_at"] = SourcingCodes.FormatUtc(receivedAt),
            ["registered_at"] = SourcingCodes.FormatUtc(registeredAt),
            ["review"] = Review(review),
            ["rfq_ref"] = ContentRef(rfqRef),
            ["supplier_ref"] = EntityRef(supplierRef),
            ["terms"] = Terms(terms),
            ["timeliness"] = SourcingStateCodes.Timeliness(timeliness),
            ["version"] = version
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>sourcing-process-line/v1</c> document of one process line. REQ-02 makes
    /// <c>rfq-line/v1</c> byte-equivalent to it, so both are serialized with this single shape.
    /// </summary>
    public static string ProcessLineDocument(SourcingProcessLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["line_ref"] = ContentRef(line.LineRef),
            ["requested_quantity"] = SourcingCodes.Decimal(line.RequestedQuantity),
            ["unit_code"] = line.UnitCode
        });
    }

    /// <summary><c>rfq-line/v1</c> document; must be byte-equivalent to its process line.</summary>
    public static string RfqLineDocument(RfqLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["line_ref"] = ContentRef(line.LineRef),
            ["requested_quantity"] = SourcingCodes.Decimal(line.RequestedQuantity),
            ["unit_code"] = line.UnitCode
        });
    }

    public static bool ProcessLineMatchesRfqLine(SourcingProcessLine processLine, RfqLine rfqLine) =>
        string.Equals(ProcessLineDocument(processLine), RfqLineDocument(rfqLine), StringComparison.Ordinal);

    /// <summary>
    /// Canonical set digest of one referenced artefact set, ordered by the bytes of its elements.
    /// Sets never repeat an identity.
    /// </summary>
    public static string RefSetDigest(IEnumerable<SourcingContentRef> references)
    {
        var preimage = Set((references ?? []).Select(reference => (object?)ContentRef(reference)));
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    public static SortedDictionary<string, object?> ContentRef(SourcingContentRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["content_digest"] = reference.ContentDigest,
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };

    public static SortedDictionary<string, object?> EntityRef(SourcingEntityRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        };

    public static SortedDictionary<string, object?> Terms(CommercialTerms terms) =>
        new(StringComparer.Ordinal)
        {
            ["delivery_days"] = terms.DeliveryDays,
            ["incoterm_code"] = terms.IncotermCode,
            ["payment_terms_code"] = terms.PaymentTermsCode,
            ["warranty_days"] = terms.WarrantyDays
        };

    public static SortedDictionary<string, object?> Attachment(SourcingAttachmentRef attachment) =>
        new(StringComparer.Ordinal)
        {
            ["content_type"] = attachment.ContentType,
            ["file_id"] = attachment.FileId.ToString("D"),
            ["file_name"] = attachment.FileName,
            ["length"] = attachment.Length,
            ["sha256"] = attachment.Sha256,
            ["version"] = attachment.Version
        };

    public static SortedDictionary<string, object?> Review(QuotationReview review) =>
        new(StringComparer.Ordinal)
        {
            ["codes"] = Set(review.Codes.Select(code => (object?)SourcingStateCodes.ReviewCode(code))),
            ["motive"] = review.Motive,
            ["reviewed_at"] = review.ReviewedAt is null ? null : SourcingCodes.FormatUtc(review.ReviewedAt.Value),
            ["reviewer_id"] = review.ReviewerId?.ToString("D"),
            ["status"] = SourcingStateCodes.ReviewStatus(review.Status)
        };

    /// <summary>Canonical set: ordered by the serialized bytes of each element, duplicates rejected.</summary>
    public static object?[] Set(IEnumerable<object?> values)
    {
        var materialized = (values ?? []).ToArray();
        var ordered = materialized
            .OrderBy(
                value => PolicyCanonicalizer.SerializeCanonical(value!),
                Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
            .ToArray();
        var serialized = ordered
            .Select(value => PolicyCanonicalizer.SerializeCanonical(value!))
            .ToArray();
        if (serialized.Distinct(StringComparer.Ordinal).Count() != serialized.Length)
        {
            throw new DomainConflictException("A canonical set cannot repeat an element.");
        }

        return ordered;
    }

    internal static string Decimal(decimal value) => value.ToString(
        "0.############################", CultureInfo.InvariantCulture);
}
