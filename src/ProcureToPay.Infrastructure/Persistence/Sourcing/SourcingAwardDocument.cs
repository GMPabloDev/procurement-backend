using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Reader of the persisted <c>award-version/v1</c> document (REQ-12, REQ-14). It rehashes the stored
/// bytes, rejects unknown properties at every level and rebuilds the pieces a consumption response
/// needs: the process reference, the supplier, the candidate lines, the terms, the catalogue snapshots
/// and the policy evidence.
/// </summary>
public static class SourcingAwardDocument
{
    public sealed record AwardContent(
        SourcingEntityRef ProcessRef,
        SourcingEntityRef SupplierRef,
        SourcingContentRef RequestRef,
        SourcingContentRef ProposalRef,
        string ProposalDigest,
        SourcingPolicyEvaluationRef PolicyBundleRef,
        AwardCandidate AwardCandidate,
        CommercialTerms Terms,
        IReadOnlyList<SourcingCatalogSnapshot> CatalogSnapshots);

    private static readonly string[] RootProperties =
    [
        "actor_user_id", "award_lines", "base_amount", "base_currency", "canonicalization_version",
        "catalog_snapshots", "contract_version", "evaluation_ref", "organization_id",
        "policy_approval_refs", "predecessor_version", "process_ref", "proposal_ref", "published_at",
        "quotation_refs", "reason", "request_ref", "source_amount", "source_currency", "supplier_ref",
        "terms", "version", "waiver_ref"
    ];

    private static readonly string[] AwardLineProperties =
    [
        "base_currency", "base_gross_total", "fx_snapshot_ref", "line_ref", "quantity",
        "source_currency", "source_gross_total", "unit_code", "unit_price"
    ];

    private static readonly string[] ApprovalRefProperties = ["case_digest", "case_id", "case_version", "policy_ref"];

    private static readonly string[] PolicyRefProperties =
    [
        "evaluation_sequence", "facts_digest", "id", "input_digest", "manifest_digest",
        "policy_content_digest", "result_digest"
    ];

    private static readonly string[] ContentRefProperties = ["content_digest", "id", "version"];

    private static readonly string[] EntityRefProperties = ["id", "version"];

    private static readonly string[] TermsProperties =
        ["delivery_days", "incoterm_code", "payment_terms_code", "warranty_days"];

    private static readonly string[] SnapshotProperties =
        ["catalog_content_digest", "line_id", "line_version", "snapshot_ref", "supplier_ref"];

    public static AwardContent Read(string documentJson, string expectedDigest)
    {
        if (!string.Equals(
                PolicyCanonicalizer.Hash(documentJson), expectedDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored award is corrupted.");
        }

        var root = Parse(documentJson);
        SourcingSerialization.RequireExactObject(root, RootProperties);
        var approvals = root.GetProperty("policy_approval_refs").EnumerateArray().ToArray();
        if (approvals.Length == 0)
        {
            throw new SourcingDependencyUnavailableException("The stored award has no approval evidence.");
        }

        SourcingPolicyEvaluationRef? policy = null;
        foreach (var approval in approvals)
        {
            SourcingSerialization.RequireExactObject(approval, ApprovalRefProperties);
            var reference = ReadPolicyRef(approval.GetProperty("policy_ref"));
            policy ??= reference;
        }
        // References the consumption response rebuilds are validated too, so a tampered document with a
        // recomputed digest cannot hide an unknown property inside them (REQ-14).
        _ = ReadOptionalContentRef(root.GetProperty("evaluation_ref"));
        foreach (var reference in root.GetProperty("quotation_refs").EnumerateArray())
        {
            _ = ReadContentRef(reference);
        }

        return new AwardContent(
            ReadEntityRef(root.GetProperty("process_ref")),
            ReadEntityRef(root.GetProperty("supplier_ref")),
            ReadContentRef(root.GetProperty("request_ref")),
            ReadContentRef(root.GetProperty("proposal_ref")),
            root.GetProperty("proposal_ref").GetProperty("content_digest").GetString()!,
            policy!,
            ReadCandidate(root),
            ReadTerms(root.GetProperty("terms")),
            root.GetProperty("catalog_snapshots").EnumerateArray().Select(ReadSnapshot).ToArray());
    }

    private static AwardCandidate ReadCandidate(JsonElement root) =>
        new(
            ReadEntityRef(root.GetProperty("supplier_ref")),
            root.GetProperty("source_currency").GetString()!,
            root.GetProperty("base_currency").GetString()!,
            ReadTerms(root.GetProperty("terms")),
            root.GetProperty("award_lines").EnumerateArray().Select(ReadLine).ToArray());

    private static AwardLine ReadLine(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, AwardLineProperties);
        return new AwardLine(
            ReadContentRef(element.GetProperty("line_ref")),
            Decimal(element, "quantity"),
            element.GetProperty("unit_code").GetString()!,
            Decimal(element, "unit_price"),
            element.GetProperty("source_currency").GetString()!,
            Decimal(element, "source_gross_total"),
            element.GetProperty("base_currency").GetString()!,
            Decimal(element, "base_gross_total"),
            element.GetProperty("fx_snapshot_ref") is { ValueKind: JsonValueKind.Object } fx
                ? ReadContentRef(fx)
                : element.GetProperty("fx_snapshot_ref").ValueKind == JsonValueKind.Null
                    ? null
                    : throw new SourcingDependencyUnavailableException("The stored award is corrupted."));
    }

    private static CommercialTerms ReadTerms(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, TermsProperties);
        return new CommercialTerms(
            element.GetProperty("delivery_days").GetInt32(),
            element.GetProperty("incoterm_code") is { ValueKind: JsonValueKind.String } incoterm
                ? incoterm.GetString()
                : null,
            element.GetProperty("payment_terms_code").GetString()!,
            element.GetProperty("warranty_days").GetInt32());
    }

    private static SourcingCatalogSnapshot ReadSnapshot(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, SnapshotProperties);
        return new SourcingCatalogSnapshot(
            ReadContentRef(element.GetProperty("snapshot_ref")),
            element.GetProperty("line_id").GetGuid(),
            element.GetProperty("line_version").GetInt32(),
            ReadEntityRef(element.GetProperty("supplier_ref")),
            element.GetProperty("catalog_content_digest").GetString()!);
    }

    private static SourcingPolicyEvaluationRef ReadPolicyRef(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, PolicyRefProperties);
        return new SourcingPolicyEvaluationRef(
            element.GetProperty("evaluation_sequence").GetInt64(),
            element.GetProperty("id").GetGuid(),
            element.GetProperty("facts_digest").GetString()!,
            element.GetProperty("input_digest").GetString()!,
            element.GetProperty("manifest_digest").GetString()!,
            element.GetProperty("policy_content_digest").GetString()!,
            element.GetProperty("result_digest").GetString()!);
    }

    private static SourcingContentRef ReadContentRef(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, ContentRefProperties);
        return new SourcingContentRef(
            element.GetProperty("id").GetGuid(),
            element.GetProperty("version").GetInt32(),
            element.GetProperty("content_digest").GetString()!);
    }

    private static SourcingContentRef? ReadOptionalContentRef(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ReadContentRef(element),
            JsonValueKind.Null => null,
            _ => throw new SourcingDependencyUnavailableException("The stored award is corrupted.")
        };

    private static SourcingEntityRef ReadEntityRef(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, EntityRefProperties);
        return new SourcingEntityRef(
            element.GetProperty("id").GetGuid(),
            element.GetProperty("version").GetInt32());
    }

    private static decimal Decimal(JsonElement element, string name) =>
        decimal.Parse(
            element.GetProperty(name).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement Parse(string documentJson)
    {
        try
        {
            return JsonDocument.Parse(documentJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SourcingDependencyUnavailableException("The stored award is corrupted.");
        }
    }
}
