using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Exact JSON shapes of the Purchase Orders persistence rows. Readers reject unknown properties so a
/// corrupted or tampered row can never be reinterpreted as valid content (SPEC 11 NFR-01), and every
/// document is rehashed against its stored digest before it is trusted.
/// </summary>
public static class PurchaseOrderSerialization
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Server-owned per-line parameters: the attested purchase type and Requested For candidate.</summary>
    public sealed record LineParameter(string LineId, string PurchaseType, Guid CandidateUserId, int CandidateVersion);

    public static string Terms(VendorTermsSnapshot snapshot) =>
        PurchaseOrderCanonicalizer.VendorTermsDocument(snapshot);

    public static VendorTermsSnapshot ReadTerms(string json) => ReadTerms(JsonNode.Parse(json));

    public static VendorTermsSnapshot ReadTerms(JsonNode? jsonNode)
    {
        var item = RequireObject(jsonNode, [
            "award_ref", "award_terms", "canonicalization_version", "catalog_snapshot_refs", "contract_version",
            "evaluated_at", "payment_terms", "policy_bundle_ref", "request_ref", "source_currency",
            "source_kind", "supplier_content_ref", "supplier_supported_currencies"
        ]);
        RequireCanonicalization(item);
        var sourceKind = item["source_kind"]!.GetValue<string>();
        var supplierContentRef = ReadContentRef(item["supplier_content_ref"]);
        var payment = RequireObject(item["payment_terms"], ["code", "net_days"]);
        var policyBundle = ReadPolicyEvaluationRef(item["policy_bundle_ref"]);
        var awardRef = item["award_ref"] is null ? null : ReadContentRef(item["award_ref"]);
        var awardTerms = item["award_terms"] is null
            ? null
            : new CommercialTerms(
                item["award_terms"]!["delivery_days"]!.GetValue<int>(),
                item["award_terms"]!["incoterm_code"]?.GetValue<string>(),
                item["award_terms"]!["payment_terms_code"]!.GetValue<string>(),
                item["award_terms"]!["warranty_days"]!.GetValue<int>());
        var catalogs = new List<VendorCatalogSnapshotRef>();
        foreach (var node in item["catalog_snapshot_refs"]!.AsArray())
        {
            var entry = RequireObject(node, ["digest", "line_ref"]);
            catalogs.Add(new VendorCatalogSnapshotRef(
                ReadContentRef(entry["line_ref"]),
                entry["digest"]!.GetValue<string>()));
        }

        var currencies = item["supplier_supported_currencies"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        return new VendorTermsSnapshot(
            sourceKind,
            new PurchaseOrderEntityRef(supplierContentRef.Id, supplierContentRef.Version),
            supplierContentRef,
            currencies,
            ReadContentRef(item["request_ref"]),
            policyBundle,
            awardRef,
            awardTerms,
            catalogs,
            new VendorPaymentTerms(payment["code"]!.GetValue<string>(), payment["net_days"]!.GetValue<int>()),
            item["source_currency"]!.GetValue<string>(),
            ParseUtc(item["evaluated_at"]!.GetValue<string>()));
    }

    public static string Delivery(DeliveryCommitment delivery) =>
        PurchaseOrderCanonicalizer.DeliveryDocument(delivery);

    public static DeliveryCommitment ReadDelivery(string json) => ReadDelivery(JsonNode.Parse(json));

    public static DeliveryCommitment ReadDelivery(JsonNode? jsonNode)
    {
        var item = RequireObject(jsonNode, ["delivery_date", "delivery_location"]);
        return new DeliveryCommitment(
            DateOnly.ParseExact(item["delivery_date"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            item["delivery_location"]!.GetValue<string>());
    }

    public static string Responsibilities(IEnumerable<AcceptanceResponsibility> responsibilities) =>
        new JsonArray((responsibilities ?? [])
            .Select(responsibility =>
                (JsonNode)JsonNode.Parse(PurchaseOrderCanonicalizer.ResponsibilityDocument(responsibility))!)
            .ToArray()).ToJsonString(Options);

    public static IReadOnlyList<AcceptanceResponsibility> ReadResponsibilities(string? json) =>
        ReadResponsibilities(string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json));

    public static IReadOnlyList<AcceptanceResponsibility> ReadResponsibilities(JsonNode? jsonNode)
    {
        var json = jsonNode?.ToJsonString(Options);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var array = Root(json).AsArray();
        var responsibilities = new List<AcceptanceResponsibility>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "assigned_at", "assigned_by_user_id", "candidate_user_ref", "kind", "line_ref", "principal",
                "reason", "version"
            ]);
            var principal = RequireObject(item["principal"], ["id", "type", "version"]);
            if (principal["type"]!.GetValue<string>() != PurchaseOrderCodes.PrincipalTypeUser)
            {
                throw new DomainValidationException("Only an explicit USER principal is admitted in v1.");
            }

            responsibilities.Add(new AcceptanceResponsibility(
                item["kind"]!.GetValue<string>(),
                ReadContentRef(item["line_ref"]),
                new PurchaseOrderEntityRef(
                    Guid.Parse(principal["id"]!.GetValue<string>()), principal["version"]!.GetValue<int>()),
                ReadEntityRef(item["candidate_user_ref"]),
                Guid.Parse(item["assigned_by_user_id"]!.GetValue<string>()),
                item["reason"]!.GetValue<string>(),
                ParseUtc(item["assigned_at"]!.GetValue<string>()),
                item["version"]!.GetValue<int>()));
        }

        return responsibilities;
    }

    public static string Lines(IEnumerable<PurchaseOrderLine> lines) =>
        new JsonArray((lines ?? [])
            .Select(line => (JsonNode)JsonNode.Parse(PurchaseOrderCanonicalizer.PurchaseOrderLineDocument(line))!)
            .ToArray()).ToJsonString(Options);

    public static IReadOnlyList<PurchaseOrderLine> ReadLines(string json) => ReadLines(JsonNode.Parse(json));

    public static IReadOnlyList<PurchaseOrderLine> ReadLines(JsonNode? jsonNode)
    {
        var json = jsonNode!.ToJsonString(Options);
        var array = Root(json).AsArray();
        var lines = new List<PurchaseOrderLine>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "acceptance_responsibilities", "additional_charges", "award_line_ref", "base_currency",
                "base_gross_total", "discounts", "fx_snapshot_ref", "gross_total", "line_id", "quantity",
                "request_line_ref", "source_currency", "subtotal", "taxes", "unit_code", "unit_price"
            ]);
            lines.Add(new PurchaseOrderLine(
                Guid.Parse(item["line_id"]!.GetValue<string>()),
                ReadContentRef(item["award_line_ref"]),
                ReadContentRef(item["request_line_ref"]),
                ParseDecimal(item["quantity"]!.GetValue<string>()),
                item["unit_code"]!.GetValue<string>(),
                ParseDecimal(item["unit_price"]!.GetValue<string>()),
                item["source_currency"]!.GetValue<string>(),
                ParseDecimal(item["gross_total"]!.GetValue<string>()),
                item["base_currency"]!.GetValue<string>(),
                ParseDecimal(item["base_gross_total"]!.GetValue<string>()),
                ParseDecimal(item["subtotal"]!.GetValue<string>()),
                ParseDecimal(item["taxes"]!.GetValue<string>()),
                ParseDecimal(item["additional_charges"]!.GetValue<string>()),
                ParseDecimal(item["discounts"]!.GetValue<string>()),
                item["fx_snapshot_ref"] is null ? null : ReadContentRef(item["fx_snapshot_ref"]),
                ReadResponsibilities(item["acceptance_responsibilities"])));
        }

        return lines;
    }

    public static string LineParameters(IEnumerable<LineParameter> parameters) => new JsonArray(
        (parameters ?? []).Select(parameter => (JsonNode)new JsonObject
        {
            ["candidate_user_id"] = parameter.CandidateUserId.ToString("D"),
            ["candidate_version"] = parameter.CandidateVersion,
            ["line_id"] = parameter.LineId,
            ["purchase_type"] = parameter.PurchaseType
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<LineParameter> ReadLineParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var array = Root(json).AsArray();
        var parameters = new List<LineParameter>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["candidate_user_id", "candidate_version", "line_id", "purchase_type"]);
            parameters.Add(new LineParameter(
                item["line_id"]!.GetValue<string>(),
                item["purchase_type"]!.GetValue<string>(),
                Guid.Parse(item["candidate_user_id"]!.GetValue<string>()),
                item["candidate_version"]!.GetValue<int>()));
        }

        return parameters;
    }

    public static string BudgetOperationRefs(IEnumerable<PurchaseOrderBudgetOperationRef> references) =>
        new JsonArray((references ?? []).Select(reference => (JsonNode)new JsonObject
        {
            ["operation"] = reference.Operation,
            ["operation_id"] = reference.OperationId.ToString("D"),
            ["operation_key"] = reference.OperationKey,
            ["source_digest"] = reference.SourceDigest,
            ["source_id"] = reference.SourceId.ToString("D"),
            ["source_version"] = reference.SourceVersion
        }).ToArray()).ToJsonString(Options);

    public static IReadOnlyList<PurchaseOrderBudgetOperationRef> ReadBudgetOperationRefs(string? json) =>
        ReadBudgetOperationRefs(string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json));

    public static IReadOnlyList<PurchaseOrderBudgetOperationRef> ReadBudgetOperationRefs(JsonNode? jsonNode)
    {
        var json = jsonNode?.ToJsonString(Options);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var array = Root(json).AsArray();
        var references = new List<PurchaseOrderBudgetOperationRef>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, [
                "operation", "operation_id", "operation_key", "source_digest", "source_id", "source_version"
            ]);
            references.Add(new PurchaseOrderBudgetOperationRef(
                item["operation"]!.GetValue<string>(),
                Guid.Parse(item["operation_id"]!.GetValue<string>()),
                item["operation_key"]!.GetValue<string>(),
                Guid.Parse(item["source_id"]!.GetValue<string>()),
                item["source_version"]!.GetValue<int>(),
                item["source_digest"]!.GetValue<string>()));
        }

        return references;
    }

    /// <summary>
    /// Rebuilds one published PO version from its own canonical document, rejecting any drift between
    /// the stored bytes and the stored digest (NFR-01, NFR-02).
    /// </summary>
    public static PurchaseOrderVersion ReadDocument(string documentJson, string? expectedDigest = null)
    {
        if (expectedDigest is not null &&
            !string.Equals(
                PurchaseOrderCanonicalizer.Hash(documentJson),
                PurchaseOrderCodes.Digest(expectedDigest, "content digest"),
                StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The stored purchase order document is not reproducible from its digest.");
        }

        var item = RequireObject(JsonNode.Parse(documentJson), [
            "actor_user_id", "amendment_ref", "approval_ref", "award_claim_ref", "award_ref", "base_amount",
            "base_currency", "budget_operation_refs", "canonicalization_version", "contract_version", "delivery",
            "issued_at", "legal_entity_ref", "lines", "ordering_evidence_ref", "organization_id", "po_id",
            "po_number", "predecessor_version", "proposal_ref", "purchase_order_version", "request_ref",
            "source_amount", "source_currency", "state", "supplier_ref", "terms_snapshot"
        ]);
        RequireCanonicalization(item);
        var contract = item["contract_version"]!.GetValue<string>();
        if (!string.Equals(contract, PurchaseOrderVersion.ContractVersion, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The stored purchase order document has an unsupported contract version.");
        }

        return new PurchaseOrderVersion(
            Guid.Parse(item["po_id"]!.GetValue<string>()),
            item["purchase_order_version"]!.GetValue<int>(),
            item["predecessor_version"] is null ? null : item["predecessor_version"]!.GetValue<int>(),
            Guid.Parse(item["organization_id"]!.GetValue<string>()),
            item["po_number"]!.GetValue<string>(),
            PurchaseOrderStateCodes.Parse(item["state"]!.GetValue<string>()),
            ReadEntityRef(item["legal_entity_ref"]),
            ReadEntityRef(item["supplier_ref"]),
            ReadContentRef(item["request_ref"]),
            ReadContentRef(item["proposal_ref"]),
            ReadContentRef(item["award_ref"]),
            ReadContentRef(item["award_claim_ref"]),
            item["ordering_evidence_ref"] is null ? null : ReadContentRef(item["ordering_evidence_ref"]),
            item["approval_ref"] is null ? null : ReadContentRef(item["approval_ref"]),
            item["amendment_ref"] is null ? null : ReadContentRef(item["amendment_ref"]),
            ReadTerms(item["terms_snapshot"]),
            item["delivery"] is null ? null : ReadDelivery(item["delivery"]),
            ReadLines(item["lines"]),
            ReadBudgetOperationRefs(item["budget_operation_refs"]),
            ParseDecimal(item["source_amount"]!.GetValue<string>()),
            item["source_currency"]!.GetValue<string>(),
            ParseDecimal(item["base_amount"]!.GetValue<string>()),
            item["base_currency"]!.GetValue<string>(),
            Guid.Parse(item["actor_user_id"]!.GetValue<string>()),
            DateTimeOffset.UnixEpoch,
            item["issued_at"] is null ? null : ParseUtc(item["issued_at"]!.GetValue<string>()));
    }

    /// <summary>Change kind and line reference of every delta of one amendment document.</summary>
    public static IReadOnlyList<(string ChangeKind, PurchaseOrderContentRef LineRef)> ReadAmendmentDeltaRefs(
        string documentJson)
    {
        var item = RequireObject(JsonNode.Parse(documentJson), [
            "amendment_id", "approval_ref", "base_po_ref", "canonicalization_version", "contract_version",
            "evidence_refs", "expected_po_version", "line_deltas", "predecessor_version", "reason",
            "replacement_delivery", "responsibility_changes", "state", "successor_award_ref", "version"
        ]);
        var deltas = new List<(string, PurchaseOrderContentRef)>();
        foreach (var node in item["line_deltas"]!.AsArray())
        {
            var delta = RequireObject(node, ["change_kind", "line_ref", "previous_line", "replacement_line"]);
            deltas.Add((
                delta["change_kind"]!.GetValue<string>(),
                ReadContentRef(delta["line_ref"])));
        }

        return deltas;
    }

    /// <summary>Persisted evidence targets with their resolved material snapshot digest.</summary>
    public static IReadOnlyList<OrderingEvidenceTarget> ReadTargets(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(element => new OrderingEvidenceTarget(
                element.GetProperty("target_id").GetGuid(),
                element.GetProperty("target_version").GetInt32(),
                element.GetProperty("material_snapshot_digest").GetString()!))
            .ToArray();
    }

    /// <summary>Reservations resolved by the issue saga, kept for the recoverable attempt.</summary>
    public static string Reservations(IEnumerable<PurchaseOrderReservation> reservations) =>
        new JsonArray((reservations ?? []).Select(reservation => (JsonNode)new JsonObject
        {
            ["amount"] = PurchaseOrderCodes.Decimal(reservation.Amount),
            ["movement_id"] = reservation.MovementId.ToString("D"),
            ["position_key_digest"] = reservation.PositionKeyDigest,
            ["remaining"] = PurchaseOrderCodes.Decimal(reservation.Remaining),
            ["target_id"] = reservation.TargetId.ToString("D"),
            ["target_version"] = reservation.TargetVersion
        }).ToArray()).ToJsonString(Options);

    public static string Targets(IEnumerable<OrderingEvidenceTarget> targets) =>
        new JsonArray((targets ?? []).Select(target => (JsonNode)new JsonObject
        {
            ["material_snapshot_digest"] = target.MaterialSnapshotDigest,
            ["target_id"] = target.TargetId.ToString("D"),
            ["target_version"] = target.TargetVersion
        }).ToArray()).ToJsonString(Options);

    public static string Requirements(IEnumerable<OrderingEvidenceRequirement> requirements) =>
        new JsonArray((requirements ?? []).Select(requirement => (JsonNode)new JsonObject
        {
            ["authority"] = new JsonObject
            {
                ["amount_base"] = requirement.AuthorityAmountBase is null
                    ? null
                    : PurchaseOrderCodes.Decimal(requirement.AuthorityAmountBase.Value),
                ["currency"] = requirement.AuthorityCurrency,
                ["level"] = requirement.AuthorityLevel,
                ["type"] = requirement.AuthorityType
            },
            ["key"] = requirement.Key,
            ["role"] = requirement.Role,
            ["scope"] = requirement.Scope,
            ["targets"] = JsonNode.Parse(Targets(requirement.Targets))
        }).ToArray()).ToJsonString(Options);

    public static string ResultRefs(IEnumerable<OrderingEvidenceResultRef> references) =>
        new JsonArray((references ?? []).Select(reference => (JsonNode)new JsonObject
        {
            ["decision_digest"] = reference.DecisionDigest,
            ["decision_id"] = reference.DecisionId?.ToString("D"),
            ["decision_key"] = reference.DecisionKey,
            ["requirement_id"] = reference.RequirementId.ToString("D"),
            ["result"] = reference.Result,
            ["targets"] = JsonNode.Parse(Targets(reference.Targets))
        }).ToArray()).ToJsonString(Options);

    public static string BudgetEvidenceRefs(IEnumerable<OrderingEvidenceBudgetRef> references) =>
        new JsonArray((references ?? []).Select(reference => (JsonNode)new JsonObject
        {
            ["evidence_digest"] = reference.EvidenceDigest,
            ["evidence_reference"] = reference.EvidenceReference,
            ["key"] = reference.Key,
            ["prerequisite_id"] = reference.PrerequisiteId.ToString("D")
        }).ToArray()).ToJsonString(Options);

    public static JsonObject Object(params (string Key, JsonNode? Value)[] properties)
    {
        var node = new JsonObject();
        foreach (var (key, value) in properties)
        {
            node[key] = value;
        }

        return node;
    }

    public static PurchaseOrderContentRef ReadContentRef(JsonNode? node)
    {
        var item = RequireObject(node, ["content_digest", "id", "version"]);
        return new PurchaseOrderContentRef(
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["version"]!.GetValue<int>(),
            item["content_digest"]!.GetValue<string>());
    }

    public static PurchaseOrderEntityRef ReadEntityRef(JsonNode? node)
    {
        var item = RequireObject(node, ["id", "version"]);
        return new PurchaseOrderEntityRef(
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["version"]!.GetValue<int>());
    }

    public static SourcingPolicyEvaluationRef ReadPolicyEvaluationRef(JsonNode? node)
    {
        var item = RequireObject(node, [
            "evaluation_sequence", "facts_digest", "id", "input_digest", "manifest_digest",
            "policy_content_digest", "result_digest"
        ]);
        return new SourcingPolicyEvaluationRef(
            item["evaluation_sequence"]!.GetValue<long>(),
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["facts_digest"]!.GetValue<string>(),
            item["input_digest"]!.GetValue<string>(),
            item["manifest_digest"]!.GetValue<string>(),
            item["policy_content_digest"]!.GetValue<string>(),
            item["result_digest"]!.GetValue<string>());
    }

    public static string WriteContentRef(PurchaseOrderContentRef reference) =>
        ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.SerializeCanonical(
            PurchaseOrderCanonicalizer.ContentRef(reference));

    public static decimal ParseDecimal(string value) =>
        decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void RequireCanonicalization(JsonObject item)
    {
        var version = item["canonicalization_version"]?.GetValue<string>();
        if (!string.Equals(version, ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.Version, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The stored document was canonicalized with an unsupported version.");
        }
    }

    private static JsonNode Root(string json) =>
        JsonNode.Parse(json) ?? throw new DomainValidationException("The stored JSON document is empty.");

    private static JsonObject RequireObject(JsonNode? node, string[] allowed)
    {
        var item = node as JsonObject
            ?? throw new DomainValidationException("The stored JSON document is not an object.");
        foreach (var property in item)
        {
            if (!allowed.Contains(property.Key, StringComparer.Ordinal))
            {
                throw new DomainValidationException(
                    $"The stored JSON document contains an unknown property '{property.Key}'.");
            }
        }

        foreach (var name in allowed)
        {
            if (!item.ContainsKey(name))
            {
                throw new DomainValidationException(
                    $"The stored JSON document is missing the property '{name}'.");
            }
        }

        return item;
    }
}
