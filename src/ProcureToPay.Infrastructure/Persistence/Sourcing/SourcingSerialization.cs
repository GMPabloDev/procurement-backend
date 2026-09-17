using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcureToPay.Domain.Modules.Sourcing;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Exact JSON shapes of the Sourcing persistence rows. Readers reject unknown properties so a
/// corrupted or tampered row can never be reinterpreted as valid content (SPEC 10 NFR-01).
/// </summary>
public static class SourcingSerialization
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Terms(CommercialTerms terms) => new JsonObject
    {
        ["delivery_days"] = terms.DeliveryDays,
        ["incoterm_code"] = terms.IncotermCode,
        ["payment_terms_code"] = terms.PaymentTermsCode,
        ["warranty_days"] = terms.WarrantyDays
    }.ToJsonString(Options);

    public static CommercialTerms ReadTerms(string json)
    {
        var item = RequireObject(JsonNode.Parse(json), ["delivery_days", "incoterm_code", "payment_terms_code", "warranty_days"]);
        return new CommercialTerms(
            item["delivery_days"]!.GetValue<int>(),
            Optional(item["incoterm_code"]),
            item["payment_terms_code"]!.GetValue<string>(),
            item["warranty_days"]!.GetValue<int>());
    }

    public static string Weights(EvaluationWeightSet set) => new JsonArray(
        set.Weights.Select(weight => (JsonNode)new JsonObject
        {
            ["criterion"] = SourcingStateCodes.Criterion(weight.Criterion),
            ["weight"] = weight.Weight
        }).ToArray()).ToJsonString(Options);

    public static EvaluationWeightSet ReadWeights(string json)
    {
        var array = Root(json).AsArray();
        var weights = new List<EvaluationWeight>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["criterion", "weight"]);
            weights.Add(new EvaluationWeight(
                Criterion(item["criterion"]!.GetValue<string>()),
                item["weight"]!.GetValue<int>()));
        }

        return new EvaluationWeightSet(weights);
    }

    public static string RfqLines(IEnumerable<RfqLine> lines) => new JsonArray(
        (lines ?? []).Select(line => (JsonNode)JsonNode.Parse(SourcingCanonicalizer.RfqLineDocument(line))!).ToArray())
        .ToJsonString(Options);

    public static IReadOnlyList<RfqLine> ReadRfqLines(string json)
    {
        var array = Root(json).AsArray();
        var lines = new List<RfqLine>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["line_ref", "requested_quantity", "unit_code"]);
            lines.Add(new RfqLine(
                ReadContentRef(item["line_ref"]),
                ParseDecimal(item["requested_quantity"]!.GetValue<string>()),
                item["unit_code"]!.GetValue<string>()));
        }

        return lines;
    }

    public static string QuotationLines(IEnumerable<QuotationLine> lines) => new JsonArray(
        (lines ?? []).Select(line => (JsonNode)new JsonObject
        {
            ["additional_charges"] = line.AdditionalCharges,
            ["discounts"] = line.Discounts,
            ["gross_total"] = line.GrossTotal,
            ["line_ref"] = ContentRefNode(line.LineRef),
            ["quantity"] = line.Quantity,
            ["subtotal"] = line.Subtotal,
            ["taxes"] = line.Taxes,
            ["technical_response"] = line.TechnicalResponse,
            ["unit_code"] = line.UnitCode,
            ["unit_price"] = line.UnitPrice
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<QuotationLine> ReadQuotationLines(string json)
    {
        var array = Root(json).AsArray();
        var lines = new List<QuotationLine>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "additional_charges", "discounts", "gross_total", "line_ref", "quantity", "subtotal",
                "taxes", "technical_response", "unit_code", "unit_price"
            ]);
            lines.Add(new QuotationLine(
                item["additional_charges"]!.GetValue<string>(),
                item["discounts"]!.GetValue<string>(),
                item["gross_total"]!.GetValue<string>(),
                ReadContentRef(item["line_ref"]),
                item["quantity"]!.GetValue<string>(),
                item["subtotal"]!.GetValue<string>(),
                item["taxes"]!.GetValue<string>(),
                item["technical_response"]!.GetValue<string>(),
                item["unit_code"]!.GetValue<string>(),
                item["unit_price"]!.GetValue<string>()));
        }

        return lines;
    }

    public static string Attachments(IEnumerable<SourcingAttachmentRef> attachments) => new JsonArray(
        (attachments ?? []).Select(attachment => (JsonNode)new JsonObject
        {
            ["content_type"] = attachment.ContentType,
            ["file_id"] = attachment.FileId.ToString("D"),
            ["file_name"] = attachment.FileName,
            ["length"] = attachment.Length,
            ["sha256"] = attachment.Sha256,
            ["version"] = attachment.Version
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingAttachmentRef> ReadAttachments(string json)
    {
        var array = Root(json).AsArray();
        var attachments = new List<SourcingAttachmentRef>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["content_type", "file_id", "file_name", "length", "sha256", "version"]);
            attachments.Add(new SourcingAttachmentRef(
                item["content_type"]!.GetValue<string>(),
                Guid.Parse(item["file_id"]!.GetValue<string>()),
                item["file_name"]!.GetValue<string>(),
                item["length"]!.GetValue<long>(),
                item["sha256"]!.GetValue<string>(),
                item["version"]!.GetValue<int>()));
        }

        return attachments;
    }

    public static string Review(QuotationReview review) => new JsonObject
    {
        ["codes"] = new JsonArray(review.Codes
            .Select(code => (JsonNode)JsonValue.Create(SourcingStateCodes.ReviewCode(code))!).ToArray()),
        ["motive"] = review.Motive,
        ["reviewed_at"] = review.ReviewedAt is null ? null : SourcingCodes.FormatUtc(review.ReviewedAt.Value),
        ["reviewer_id"] = review.ReviewerId?.ToString("D"),
        ["status"] = SourcingStateCodes.ReviewStatus(review.Status)
    }.ToJsonString(Options);

    public static QuotationReview ReadReview(string json)
    {
        var item = RequireObject(JsonNode.Parse(json), ["codes", "motive", "reviewed_at", "reviewer_id", "status"]);
        var status = ReviewStatus(item["status"]!.GetValue<string>());
        if (status == QuotationReviewStatus.Pending)
        {
            return QuotationReview.Pending();
        }

        var codes = item["codes"]!.AsArray().Select(node => ReviewCode(node!.GetValue<string>()));
        var reviewedAt = item["reviewed_at"]!.GetValue<string>();
        return QuotationReview.Decided(
            status,
            codes,
            Optional(item["motive"]),
            Guid.Parse(item["reviewer_id"]!.GetValue<string>()),
            DateTimeOffset.Parse(
                reviewedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
    }

    /// <summary>
    /// Frozen quotation parameters of one RFQ version: the minimum valid quotations of every covered
    /// prerequisite and the persisted floor that limits a waiver (REQ-02, REQ-05).
    /// </summary>
    public static string Parameters(IEnumerable<SourcingQuotationParameter> parameters) => new JsonArray(
        (parameters ?? []).Select(parameter => (JsonNode)new JsonObject
        {
            ["minimum_allowed_quotations"] = parameter.MinimumAllowedQuotations,
            ["minimum_quotations"] = parameter.MinimumQuotations,
            ["prerequisite_id"] = parameter.PrerequisiteId.ToString("D"),
            ["requirement_key"] = parameter.RequirementKey,
            ["targets"] = new JsonArray(parameter.Targets
                .OrderBy(target => target.Id)
                .Select(target => (JsonNode)new JsonObject
                {
                    ["id"] = target.Id.ToString("D"),
                    ["type"] = target.Type,
                    ["version"] = target.Version
                }).ToArray())
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingQuotationParameter> ReadParameters(string json)
    {
        var array = Root(json).AsArray();
        var parameters = new List<SourcingQuotationParameter>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "minimum_allowed_quotations", "minimum_quotations", "prerequisite_id", "requirement_key", "targets"
            ]);
            var targets = item["targets"]!.AsArray().Select(targetNode =>
            {
                var target = RequireObject(targetNode, ["id", "type", "version"]);
                return new SourcingQuotationTarget(
                    Guid.Parse(target["id"]!.GetValue<string>()),
                    target["type"]!.GetValue<string>(),
                    target["version"]!.GetValue<int>());
            }).ToArray();
            parameters.Add(new SourcingQuotationParameter(
                Guid.Parse(item["prerequisite_id"]!.GetValue<string>()),
                item["requirement_key"]!.GetValue<string>(),
                OptionalInt(item["minimum_quotations"]),
                OptionalInt(item["minimum_allowed_quotations"]),
                targets));
        }

        return parameters;
    }

    public static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Catalogue snapshots of one governed route, stored as their canonical elements.</summary>
    public static string CatalogSnapshots(IEnumerable<SourcingCatalogSnapshot> snapshots) => new JsonArray(
        (snapshots ?? []).OrderBy(snapshot => snapshot.LineId).Select(snapshot => (JsonNode)new JsonObject
        {
            ["catalog_content_digest"] = snapshot.CatalogContentDigest,
            ["line_id"] = snapshot.LineId.ToString("D"),
            ["line_version"] = snapshot.LineVersion,
            ["snapshot_ref"] = ContentRefNode(snapshot.SnapshotRef),
            ["supplier_ref"] = new JsonObject
            {
                ["id"] = snapshot.SupplierRef.Id.ToString("D"),
                ["version"] = snapshot.SupplierRef.Version
            }
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingCatalogSnapshot> ReadCatalogSnapshots(string json)
    {
        var array = Root(json).AsArray();
        var snapshots = new List<SourcingCatalogSnapshot>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "catalog_content_digest", "line_id", "line_version", "snapshot_ref", "supplier_ref"
            ]);
            var supplier = RequireObject(item["supplier_ref"], ["id", "version"]);
            snapshots.Add(new SourcingCatalogSnapshot(
                ReadContentRef(item["snapshot_ref"]),
                item["line_id"]!.GetValue<Guid>(),
                item["line_version"]!.GetValue<int>(),
                new SourcingEntityRef(
                    supplier["id"]!.GetValue<Guid>(), supplier["version"]!.GetValue<int>()),
                item["catalog_content_digest"]!.GetValue<string>()));
        }

        return snapshots;
    }

    /// <summary>Targets of one quotation waiver: line reference, count and exact quotation versions.</summary>
    public static string WaiverTargets(IEnumerable<SourcingWaiverTarget> targets) => new JsonArray(
        (targets ?? []).OrderBy(target => target.LineRef.Id).Select(target => (JsonNode)new JsonObject
        {
            ["line_ref"] = ContentRefNode(target.LineRef),
            ["quotation_refs"] = new JsonArray(target.QuotationRefs
                .OrderBy(reference => reference.Id)
                .Select(reference => (JsonNode)ContentRefNode(reference)).ToArray()),
            ["valid_quotations"] = target.ValidQuotations
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingWaiverTarget> ReadWaiverTargets(string json)
    {
        var array = Root(json).AsArray();
        var targets = new List<SourcingWaiverTarget>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["line_ref", "quotation_refs", "valid_quotations"]);
            targets.Add(new SourcingWaiverTarget(
                ReadContentRef(item["line_ref"]),
                item["quotation_refs"]!.AsArray().Select(ReadContentRef).ToArray()));
        }

        return targets;
    }

    /// <summary>
    /// Rehydrates <c>quotation-waiver-facts/v1</c> from its stored canonical document and recomputes
    /// the digest, so a tampered row cannot be interpreted as a valid waiver (REQ-05).
    /// </summary>
    public static SourcingWaiverFacts ReadWaiverFacts(string json)
    {
        var item = RequireObject(JsonNode.Parse(json), [
            "base_bundle_id", "base_result_digest", "canonicalization_version", "computed_at",
            "contract_version", "floor", "from", "manifest_digest", "organization_id",
            "policy_content_digest", "policy_version_id", "prerequisite_id", "process_id",
            "requirement_key", "rfq_ref", "targets", "to"
        ]);
        var targetNodes = item["targets"]!.AsArray();
        var wrapper = new JsonObject
        {
            ["targets"] = new JsonArray(targetNodes.Select(node => node!.DeepClone()).ToArray())
        };
        return new SourcingWaiverFacts(
            Guid.Parse(item["organization_id"]!.GetValue<string>()),
            Guid.Parse(item["process_id"]!.GetValue<string>()),
            ReadContentRef(item["rfq_ref"]),
            Guid.Parse(item["prerequisite_id"]!.GetValue<string>()),
            item["requirement_key"]!.GetValue<string>(),
            Guid.Parse(item["base_bundle_id"]!.GetValue<string>()),
            item["base_result_digest"]!.GetValue<string>(),
            Guid.Parse(item["policy_version_id"]!.GetValue<string>()),
            item["policy_content_digest"]!.GetValue<string>(),
            item["manifest_digest"]!.GetValue<string>(),
            item["from"]!.GetValue<int>(),
            item["to"]!.GetValue<int>(),
            item["floor"]!.GetValue<int>(),
            ReadWaiverTargets(wrapper.ToJsonString(Options)),
            DateTimeOffset.Parse(
                item["computed_at"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
    }

    /// <summary>
    /// Rehydrates <c>sourcing-completeness-manifest/v1</c> from its canonical document and recomputes
    /// the digest, so a tampered manifest cannot be served to Policy (REQ-10).
    /// </summary>
    public static SourcingCompletenessManifest ReadCompletenessManifest(string json)
    {
        var item = RequireObject(JsonNode.Parse(json), [
            "award_candidate_digest", "canonicalization_version", "catalog_snapshots_digest",
            "contract_version", "covered_lines", "evaluation_digest", "provider_contract_version",
            "provider_id", "quotation_set_digest", "request_bundle_id", "request_id",
            "request_manifest_digest", "request_result_digest", "request_version", "sourcing_proposal_id",
            "sourcing_proposal_version", "terms_digest"
        ]);
        var manifest = new SourcingCompletenessManifest(
            item["provider_id"]!.GetValue<string>(),
            item["provider_contract_version"]!.GetValue<string>(),
            Guid.Parse(item["request_id"]!.GetValue<string>()),
            item["request_version"]!.GetValue<int>(),
            item["request_manifest_digest"]!.GetValue<string>(),
            item["request_result_digest"]!.GetValue<string>(),
            Guid.Parse(item["request_bundle_id"]!.GetValue<string>()),
            Guid.Parse(item["sourcing_proposal_id"]!.GetValue<string>()),
            item["sourcing_proposal_version"]!.GetValue<int>(),
            item["covered_lines"]!.AsArray().Select(ReadContentRef).ToArray(),
            item["evaluation_digest"] is null ||
            item["evaluation_digest"]!.GetValueKind() == JsonValueKind.Null
                ? null
                : item["evaluation_digest"]!.GetValue<string>(),
            item["quotation_set_digest"]!.GetValue<string>(),
            item["catalog_snapshots_digest"]!.GetValue<string>(),
            item["award_candidate_digest"]!.GetValue<string>(),
            item["terms_digest"]!.GetValue<string>());
        if (!string.Equals(manifest.CanonicalDocument(), json, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored completeness manifest is corrupted.");
        }

        return manifest;
    }

    /// <summary>
    /// Rehydrates <c>policy-evaluation-ref/v1</c> of the request bundle the proposal was built against
    /// and checks it against the manifest that publishes the request digests (REQ-10).
    /// </summary>
    public static SourcingPolicyEvaluationRef ReadPolicyEvaluationRef(
        string json,
        SourcingCompletenessManifest manifest)
    {
        var item = RequireObject(JsonNode.Parse(json), [
            "evaluation_sequence", "facts_digest", "id", "input_digest", "manifest_digest",
            "policy_content_digest", "result_digest"
        ]);
        var reference = new SourcingPolicyEvaluationRef(
            item["evaluation_sequence"]!.GetValue<long>(),
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["facts_digest"]!.GetValue<string>(),
            item["input_digest"]!.GetValue<string>(),
            item["manifest_digest"]!.GetValue<string>(),
            item["policy_content_digest"]!.GetValue<string>(),
            item["result_digest"]!.GetValue<string>());
        if (reference.Id != manifest.RequestBundleId ||
            !string.Equals(reference.ResultDigest, manifest.RequestResultDigest, StringComparison.Ordinal) ||
            !string.Equals(reference.ManifestDigest, manifest.RequestManifestDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException(
                "The stored policy evaluation reference does not match the manifest.");
        }

        return reference;
    }

    /// <summary>Set of versioned entity references, stored as its canonical elements.</summary>
    public static string EntityRefs(IEnumerable<SourcingEntityRef> references) =>
        new JsonArray((references ?? [])
            .OrderBy(reference => reference.Id)
            .Select(reference => (JsonNode)new JsonObject
            {
                ["id"] = reference.Id.ToString("D"),
                ["version"] = reference.Version
            }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingEntityRef> ReadEntityRefs(string json)
    {
        var array = Root(json).AsArray();
        var references = new List<SourcingEntityRef>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["id", "version"]);
            references.Add(new SourcingEntityRef(
                Guid.Parse(item["id"]!.GetValue<string>()), item["version"]!.GetValue<int>()));
        }

        return references;
    }

    /// <summary>Set of versioned content references, stored as its canonical elements.</summary>
    public static string ContentRefs(IEnumerable<SourcingContentRef> references) =>
        new JsonArray((references ?? [])
            .OrderBy(reference => reference.Id)
            .Select(reference => (JsonNode)ContentRefNode(reference)).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<SourcingContentRef> ReadContentRefs(string json)
    {
        var array = Root(json).AsArray();
        var references = new List<SourcingContentRef>(array.Count);
        foreach (var node in array)
        {
            references.Add(ReadContentRef(node));
        }

        return references;
    }

    /// <summary>Set of FX snapshot references used by one evaluation (REQ-08).</summary>
    public static string FxRefs(IEnumerable<SourcingFxSnapshot> snapshots) =>
        new JsonArray((snapshots ?? [])
            .OrderBy(snapshot => snapshot.Id)
            .Select(snapshot => (JsonNode)new JsonObject
            {
                ["base_currency"] = snapshot.BaseCurrency,
                ["digest"] = snapshot.Digest,
                ["effective_at"] = SourcingCodes.FormatUtc(snapshot.EffectiveAt),
                ["id"] = snapshot.Id.ToString("D"),
                ["rate"] = Decimal(snapshot.Rate),
                ["source_currency"] = snapshot.SourceCurrency,
                ["source_reference"] = snapshot.SourceReference,
                ["version"] = snapshot.Version
            }).ToArray()).ToJsonString(Options);

    public static decimal ParseDecimal(string value) =>
        decimal.Parse(value, CultureInfo.InvariantCulture);

    private static JsonObject ContentRefNode(SourcingContentRef reference) => new()
    {
        ["content_digest"] = reference.ContentDigest,
        ["id"] = reference.Id.ToString("D"),
        ["version"] = reference.Version
    };

    private static SourcingContentRef ReadContentRef(JsonNode? node)
    {
        var item = RequireObject(node, ["content_digest", "id", "version"]);
        return new SourcingContentRef(
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["version"]!.GetValue<int>(),
            item["content_digest"]!.GetValue<string>());
    }

    private static SourcingEvaluationCriterion Criterion(string value) => value switch
    {
        "PRICE" => SourcingEvaluationCriterion.Price,
        "DELIVERY_TIME" => SourcingEvaluationCriterion.DeliveryTime,
        "WARRANTY" => SourcingEvaluationCriterion.Warranty,
        "PAYMENT_TERMS" => SourcingEvaluationCriterion.PaymentTerms,
        "TECHNICAL_COMPLIANCE" => SourcingEvaluationCriterion.TechnicalCompliance,
        "SUPPLIER_PERFORMANCE" => SourcingEvaluationCriterion.SupplierPerformance,
        _ => throw new JsonException("The stored evaluation criterion is unknown.")
    };

    private static QuotationReviewStatus ReviewStatus(string value) => value switch
    {
        "PENDING" => QuotationReviewStatus.Pending,
        "VALID" => QuotationReviewStatus.Valid,
        "INVALID" => QuotationReviewStatus.Invalid,
        "WITHDRAWN" => QuotationReviewStatus.Withdrawn,
        _ => throw new JsonException("The stored review status is unknown.")
    };

    private static QuotationReviewCode ReviewCode(string value) => value switch
    {
        "MISSING_REQUIRED_LINE" => QuotationReviewCode.MissingRequiredLine,
        "MONEY_MISMATCH" => QuotationReviewCode.MoneyMismatch,
        "TERMS_NONCOMPLIANT" => QuotationReviewCode.TermsNoncompliant,
        "TECHNICAL_NONCOMPLIANT" => QuotationReviewCode.TechnicalNoncompliant,
        "EVIDENCE_INVALID" => QuotationReviewCode.EvidenceInvalid,
        "OTHER" => QuotationReviewCode.Other,
        _ => throw new JsonException("The stored review code is unknown.")
    };

    private static JsonNode Root(string json) =>
        JsonNode.Parse(json) ?? throw new JsonException("A stored sourcing row is empty.");

    private static string? Optional(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null ? null : node.GetValue<string>();

    private static int? OptionalInt(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null ? null : node.GetValue<int>();

    private static JsonObject RequireObject(JsonNode? node, string[] expected)
    {
        if (node is not JsonObject value)
        {
            throw new JsonException("The stored sourcing document is not an object.");
        }

        var names = value.Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new JsonException("The stored sourcing document has an unexpected property set.");
        }

        return value;
    }
}

/// <summary>One frozen quotation prerequisite of an RFQ version (REQ-02, REQ-05).</summary>
public sealed record SourcingQuotationParameter(
    Guid PrerequisiteId,
    string RequirementKey,
    int? MinimumQuotations,
    int? MinimumAllowedQuotations,
    IReadOnlyList<SourcingQuotationTarget> Targets);

/// <summary>One target of a frozen quotation prerequisite.</summary>
public sealed record SourcingQuotationTarget(Guid Id, string Type, int Version);
