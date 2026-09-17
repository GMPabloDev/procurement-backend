using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Reader of the persisted <c>award-version/v1</c> document (REQ-12, REQ-14). It rehashes the stored
/// bytes before trusting them and rebuilds the pieces a consumption response needs: the process
/// reference, the supplier, the candidate lines, the terms, the catalogue snapshots and the policy
/// evidence.
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

    public static AwardContent Read(string documentJson, string expectedDigest)
    {
        if (!string.Equals(
                PolicyCanonicalizer.Hash(documentJson), expectedDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored award is corrupted.");
        }

        var root = Parse(documentJson);
        var approvals = root.GetProperty("policy_approval_refs").EnumerateArray().ToArray();
        if (approvals.Length == 0)
        {
            throw new SourcingDependencyUnavailableException("The stored award has no approval evidence.");
        }

        var policy = ReadPolicyRef(approvals[0].GetProperty("policy_ref"));
        return new AwardContent(
            ReadEntityRef(root.GetProperty("process_ref")),
            ReadEntityRef(root.GetProperty("supplier_ref")),
            ReadContentRef(root.GetProperty("request_ref")),
            ReadContentRef(root.GetProperty("proposal_ref")),
            root.GetProperty("proposal_ref").GetProperty("content_digest").GetString()!,
            policy,
            ReadCandidate(root),
            ReadTerms(root.GetProperty("terms")),
            root.GetProperty("catalog_snapshots").EnumerateArray().Select(ReadSnapshot).ToArray());
    }

    private static AwardCandidate ReadCandidate(JsonElement element) =>
        new(
            ReadEntityRef(element.GetProperty("supplier_ref")),
            element.GetProperty("source_currency").GetString()!,
            element.GetProperty("base_currency").GetString()!,
            ReadTerms(element.GetProperty("terms")),
            element.GetProperty("award_lines").EnumerateArray().Select(ReadLine).ToArray());

    private static AwardLine ReadLine(JsonElement element) => new(
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
            : null);

    private static CommercialTerms ReadTerms(JsonElement element) => new(
        element.GetProperty("delivery_days").GetInt32(),
        element.GetProperty("incoterm_code") is { ValueKind: JsonValueKind.String } incoterm
            ? incoterm.GetString()
            : null,
        element.GetProperty("payment_terms_code").GetString()!,
        element.GetProperty("warranty_days").GetInt32());

    private static SourcingCatalogSnapshot ReadSnapshot(JsonElement element) => new(
        ReadContentRef(element.GetProperty("snapshot_ref")),
        element.GetProperty("line_id").GetGuid(),
        element.GetProperty("line_version").GetInt32(),
        ReadEntityRef(element.GetProperty("supplier_ref")),
        element.GetProperty("catalog_content_digest").GetString()!);

    private static SourcingPolicyEvaluationRef ReadPolicyRef(JsonElement element) => new(
        element.GetProperty("evaluation_sequence").GetInt64(),
        element.GetProperty("id").GetGuid(),
        element.GetProperty("facts_digest").GetString()!,
        element.GetProperty("input_digest").GetString()!,
        element.GetProperty("manifest_digest").GetString()!,
        element.GetProperty("policy_content_digest").GetString()!,
        element.GetProperty("result_digest").GetString()!);

    private static SourcingContentRef ReadContentRef(JsonElement element) => new(
        element.GetProperty("id").GetGuid(),
        element.GetProperty("version").GetInt32(),
        element.GetProperty("content_digest").GetString()!);

    private static SourcingEntityRef ReadEntityRef(JsonElement element) => new(
        element.GetProperty("id").GetGuid(),
        element.GetProperty("version").GetInt32());

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
