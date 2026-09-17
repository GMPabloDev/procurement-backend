using System.Collections.Immutable;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Reader of the persisted <c>sourcing-proposal-version/v1</c> document (REQ-10, REQ-12). It rebuilds
/// the award candidate and the references a publication must reproduce, rejects unknown properties at
/// every level and never trusts the stored bytes without rehashing them first.
/// </summary>
public static class SourcingProposalDocument
{
    /// <summary>Everything a publication needs from the approved proposal, in canonical order.</summary>
    public sealed record ProposalContent(
        SourcingSelectionBasis SelectionBasis,
        SourcingEntityRef SupplierRef,
        IReadOnlyList<SourcingContentRef> SelectedLines,
        SourcingContentRef RequestRef,
        SourcingContentRef? EvaluationRef,
        IReadOnlyList<SourcingContentRef> QuotationRefs,
        IReadOnlyList<SourcingCatalogSnapshot> CatalogSnapshots,
        Guid? WaiverRef,
        CommercialTerms Terms,
        AwardCandidate AwardCandidate);

    private static readonly string[] RootProperties =
    [
        "award_candidate", "canonicalization_version", "catalog_snapshots", "contract_version",
        "evaluation_ref", "organization_id", "predecessor_version", "quotation_refs",
        "request_bundle_ref", "request_ref", "selected_lines", "selection_basis", "supplier_ref",
        "terms", "version", "waiver_ref"
    ];

    private static readonly string[] CandidateProperties =
    [
        "award_lines", "base_amount", "base_currency", "canonicalization_version", "contract_version",
        "source_amount", "source_currency", "supplier_ref", "terms"
    ];

    private static readonly string[] AwardLineProperties =
    [
        "base_currency", "base_gross_total", "fx_snapshot_ref", "line_ref", "quantity",
        "source_currency", "source_gross_total", "unit_code", "unit_price"
    ];

    private static readonly string[] ContentRefProperties = ["content_digest", "id", "version"];

    private static readonly string[] EntityRefProperties = ["id", "version"];

    private static readonly string[] TermsProperties =
        ["delivery_days", "incoterm_code", "payment_terms_code", "warranty_days"];

    private static readonly string[] SnapshotProperties =
        ["catalog_content_digest", "line_id", "line_version", "snapshot_ref", "supplier_ref"];

    public static ProposalContent Read(string documentJson, string expectedDigest)
    {
        if (!string.Equals(
                ProcureToPay.Domain.Modules.Policy.PolicyCanonicalizer.Hash(documentJson),
                expectedDigest,
                StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored proposal is corrupted.");
        }

        var root = Parse(documentJson);
        SourcingSerialization.RequireExactObject(root, RootProperties);
        var candidate = ReadCandidate(root.GetProperty("award_candidate"));
        var terms = ReadTerms(root.GetProperty("terms"));
        return new ProposalContent(
            root.GetProperty("selection_basis").GetString() switch
            {
                "RFQ" => SourcingSelectionBasis.Rfq,
                "APPROVED_CATALOG" => SourcingSelectionBasis.ApprovedCatalog,
                _ => throw new SourcingDependencyUnavailableException("The stored proposal is corrupted.")
            },
            ReadEntityRef(root.GetProperty("supplier_ref")),
            root.GetProperty("selected_lines").EnumerateArray().Select(ReadContentRef).ToArray(),
            ReadContentRef(root.GetProperty("request_ref")),
            ReadOptionalContentRef(root.GetProperty("evaluation_ref")),
            root.GetProperty("quotation_refs").EnumerateArray().Select(ReadContentRef).ToArray(),
            root.GetProperty("catalog_snapshots").EnumerateArray().Select(ReadSnapshot).ToArray(),
            ReadOptionalGuid(root.GetProperty("waiver_ref")),
            terms,
            candidate);
    }

    private static AwardCandidate ReadCandidate(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, CandidateProperties);
        var lines = element.GetProperty("award_lines").EnumerateArray().Select(ReadLine).ToArray();
        return new AwardCandidate(
            ReadEntityRef(element.GetProperty("supplier_ref")),
            element.GetProperty("source_currency").GetString()!,
            element.GetProperty("base_currency").GetString()!,
            ReadTerms(element.GetProperty("terms")),
            lines);
    }

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
            ReadOptionalContentRef(element.GetProperty("fx_snapshot_ref")));
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

    private static SourcingContentRef ReadContentRef(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, ContentRefProperties);
        return new SourcingContentRef(
            element.GetProperty("id").GetGuid(),
            element.GetProperty("version").GetInt32(),
            element.GetProperty("content_digest").GetString()!);
    }

    private static SourcingContentRef? ReadOptionalContentRef(JsonElement element) =>
        element is { ValueKind: JsonValueKind.Object } ? ReadContentRef(element) : null;

    private static SourcingEntityRef ReadEntityRef(JsonElement element)
    {
        SourcingSerialization.RequireExactObject(element, EntityRefProperties);
        return new SourcingEntityRef(
            element.GetProperty("id").GetGuid(),
            element.GetProperty("version").GetInt32());
    }

    private static Guid? ReadOptionalGuid(JsonElement element) =>
        element is { ValueKind: JsonValueKind.String } ? element.GetGuid() : null;

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
            throw new SourcingDependencyUnavailableException("The stored proposal is corrupted.");
        }
    }
}
