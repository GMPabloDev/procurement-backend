using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Reproducible command fingerprints of the Sourcing domain. The clock, the correlation reference
/// and every server-owned value stay outside so a replay with the same key is byte-identical
/// (SPEC 10 NFR-03); the fingerprint is persisted before the effect and a different payload with the
/// same key is refused instead of producing a second artefact.
/// </summary>
public static class SourcingCommandFingerprints
{
    public const string CreateProcess = "CREATE_PROCESS";
    public const string CreateRfq = "CREATE_RFQ";
    public const string OpenRfq = "OPEN_RFQ";
    public const string ExtendRfqDeadline = "EXTEND_RFQ_DEADLINE";
    public const string CloseRfq = "CLOSE_RFQ";
    public const string CancelRfq = "CANCEL_RFQ";
    public const string CancelProcess = "CANCEL_PROCESS";
    public const string RegisterQuotation = "REGISTER_QUOTATION";
    public const string ReviewQuotation = "REVIEW_QUOTATION";

    public static string Process(
        Guid organizationId,
        Guid actorUserId,
        Guid requestId,
        int requestVersion,
        IEnumerable<SourcingProcessLine> lines,
        string commandKey)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["command"] = CreateProcess,
            ["command_key"] = SourcingCodes.Key(commandKey, "Command key"),
            ["lines"] = SourcingSet((lines ?? []).Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                ["requested_quantity"] = SourcingCodes.Decimal(line.RequestedQuantity),
                ["unit_code"] = line.UnitCode
            })),
            ["organization_id"] = organizationId.ToString("D"),
            ["request_id"] = requestId.ToString("D"),
            ["request_version"] = requestVersion
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string Rfq(
        Guid organizationId,
        Guid actorUserId,
        Guid processId,
        int expectedProcessVersion,
        string currency,
        CommercialTerms terms,
        EvaluationWeightSet weights,
        DateTimeOffset responseDeadline,
        string commandKey)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(weights);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["command"] = CreateRfq,
            ["command_key"] = SourcingCodes.Key(commandKey, "Command key"),
            ["currency"] = currency,
            ["evaluation_weights"] = SourcingSet(weights.Weights.Select(weight => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["criterion"] = SourcingStateCodes.Criterion(weight.Criterion),
                ["weight"] = weight.Weight
            })),
            ["expected_process_version"] = expectedProcessVersion,
            ["organization_id"] = organizationId.ToString("D"),
            ["process_id"] = processId.ToString("D"),
            ["response_deadline"] = SourcingCodes.FormatUtc(responseDeadline),
            ["terms"] = SourcingCanonicalizer.Terms(terms)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string Transition(
        string command,
        Guid organizationId,
        Guid actorUserId,
        Guid subjectId,
        int expectedVersion,
        string? reason,
        DateTimeOffset? newDeadline,
        string commandKey)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["command"] = command,
            ["command_key"] = SourcingCodes.Key(commandKey, "Command key"),
            ["expected_version"] = expectedVersion,
            ["new_deadline"] = newDeadline is null ? null : SourcingCodes.FormatUtc(newDeadline.Value),
            ["organization_id"] = organizationId.ToString("D"),
            ["reason"] = reason,
            ["subject_id"] = subjectId.ToString("D")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string Quotation(
        Guid organizationId,
        Guid actorUserId,
        Guid rfqId,
        Guid supplierId,
        int? expectedQuotationVersion,
        string currency,
        CommercialTerms terms,
        DateTimeOffset receivedAt,
        IEnumerable<QuotationLine> lines,
        IEnumerable<SourcingAttachmentRef> attachments,
        string commandKey)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["attachments"] = SourcingSet((attachments ?? []).Select(attachment => (object?)SourcingCanonicalizer.Attachment(attachment))),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["command"] = RegisterQuotation,
            ["command_key"] = SourcingCodes.Key(commandKey, "Command key"),
            ["currency"] = currency,
            ["expected_quotation_version"] = expectedQuotationVersion,
            ["lines"] = SourcingSet((lines ?? []).Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["additional_charges"] = line.AdditionalCharges,
                ["discounts"] = line.Discounts,
                ["gross_total"] = line.GrossTotal,
                ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                ["quantity"] = line.Quantity,
                ["subtotal"] = line.Subtotal,
                ["taxes"] = line.Taxes,
                ["technical_response"] = line.TechnicalResponse,
                ["unit_code"] = line.UnitCode,
                ["unit_price"] = line.UnitPrice
            })),
            ["organization_id"] = organizationId.ToString("D"),
            ["received_at"] = SourcingCodes.FormatUtc(receivedAt),
            ["rfq_id"] = rfqId.ToString("D"),
            ["supplier_id"] = supplierId.ToString("D"),
            ["terms"] = SourcingCanonicalizer.Terms(terms)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    public static string QuotationReviewCommand(
        Guid organizationId,
        Guid actorUserId,
        Guid quotationId,
        int expectedVersion,
        QuotationReviewStatus status,
        IEnumerable<QuotationReviewCode> codes,
        string? motive,
        string commandKey)
    {
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["codes"] = SourcingSet((codes ?? []).Select(code => (object?)SourcingStateCodes.ReviewCode(code))),
            ["command"] = ReviewQuotation,
            ["command_key"] = SourcingCodes.Key(commandKey, "Command key"),
            ["expected_version"] = expectedVersion,
            ["motive"] = motive,
            ["organization_id"] = organizationId.ToString("D"),
            ["quotation_id"] = quotationId.ToString("D"),
            ["status"] = SourcingStateCodes.ReviewStatus(status)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    private static object?[] SourcingSet(IEnumerable<object?> values) => SourcingCanonicalizer.Set(values);
}
