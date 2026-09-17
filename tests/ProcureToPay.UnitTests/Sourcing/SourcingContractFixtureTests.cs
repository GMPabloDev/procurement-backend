using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 "Datos y contratos": the versioned fixtures under <c>tests/ContractFixtures/Sourcing/v1/</c>
/// publish the complete canonical JSON and SHA-256 of RFQ, quotation, FX, evaluation, manifest,
/// proposal, both owner evidence and award. The goldens fix bytes, nullability and set order, and the
/// published readers reject a document whose bytes or property set were tampered with.
/// </summary>
public sealed class SourcingContractFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory, "ContractFixtures", "Sourcing", "v1");

    [Fact]
    public void Every_published_fixture_matches_its_document_bytes_and_sha256()
    {
        foreach (var fixture in SourcingContractFixtures.All())
        {
            var document = File.ReadAllText(Path.Combine(FixtureDirectory, fixture.FileName));
            var published = File.ReadAllText(Path.Combine(FixtureDirectory, fixture.FileName + ".sha256")).Trim();
            Assert.Equal(fixture.Document, document);
            Assert.Equal(fixture.Digest, published);
            Assert.Equal(fixture.Digest, PolicyCanonicalizer.Hash(document));
            Assert.Equal(fixture.Digest, Sha256(document));
            Assert.Contains($"\"contract_version\":\"{fixture.ContractVersion}\"", document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_published_fixture_set_covers_every_contractual_artefact()
    {
        var published = Directory.EnumerateFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = SourcingContractFixtures.All()
            .Select(fixture => fixture.FileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, published);
        var versions = SourcingContractFixtures.All()
            .Select(fixture => fixture.ContractVersion)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                SourcingCodes.RfqVersionContract,
                SourcingCodes.QuotationVersionContract,
                SourcingCodes.FxSnapshotContract,
                SourcingCodes.QuoteEvaluationContract,
                SourcingCodes.CompletenessManifestContract,
                SourcingCodes.ProposalVersionContract,
                SourcingCodes.QuotationStatusEvidenceContract,
                SourcingCodes.ProcurementStageEvidenceContract,
                SourcingCodes.AwardVersionContract
            },
            versions);
    }

    [Fact]
    public void Permuting_a_canonical_set_publishes_the_same_bytes_and_change_is_sensitive()
    {
        var rfq = Load("rfq-version.json");
        var reversed = new RfqVersionView(
            SourcingContractFixtures.RfqId,
            1,
            SourcingContractFixtures.OrganizationId,
            SourcingContractFixtures.ProcessId,
            SourcingContractFixtures.RequestId,
            1,
            RfqStatus.Open,
            "PEN",
            new CommercialTerms(15, "EXW", "NET30", 365),
            new EvaluationWeightSet(SourcingContractFixtures.Weights().Weights.Reverse()),
            SourcingContractFixtures.OpenedAt,
            SourcingContractFixtures.ResponseDeadline,
            null,
            [
                new RfqLine(new SourcingContentRef(SourcingContractFixtures.LineBId, 1, new string('b', 64)), 5m, "EA"),
                new RfqLine(new SourcingContentRef(SourcingContractFixtures.LineAId, 1, new string('a', 64)), 10m, "EA")
            ],
            new string('0', 64),
            SourcingContractFixtures.ActorId,
            SourcingContractFixtures.OpenedAt,
            "Open the RFQ").CanonicalDocument();
        Assert.Equal(rfq, reversed);

        var changed = new RfqVersionView(
            SourcingContractFixtures.RfqId,
            1,
            SourcingContractFixtures.OrganizationId,
            SourcingContractFixtures.ProcessId,
            SourcingContractFixtures.RequestId,
            1,
            RfqStatus.Open,
            "PEN",
            new CommercialTerms(15, "EXW", "NET30", 365),
            SourcingContractFixtures.Weights(),
            SourcingContractFixtures.OpenedAt,
            SourcingContractFixtures.ResponseDeadline,
            null,
            [
                new RfqLine(new SourcingContentRef(SourcingContractFixtures.LineAId, 1, new string('a', 64)), 11m, "EA"),
                new RfqLine(new SourcingContentRef(SourcingContractFixtures.LineBId, 1, new string('b', 64)), 5m, "EA")
            ],
            new string('0', 64),
            SourcingContractFixtures.ActorId,
            SourcingContractFixtures.OpenedAt,
            "Open the RFQ").CanonicalDocument();
        Assert.NotEqual(rfq, changed);
        Assert.NotEqual(PolicyCanonicalizer.Hash(rfq), PolicyCanonicalizer.Hash(changed));
    }

    [Fact]
    public void Extra_properties_are_rejected_by_the_published_readers()
    {
        var manifest = Load("sourcing-completeness-manifest.json");
        Assert.Throws<System.Text.Json.JsonException>(() =>
            SourcingSerialization.ReadCompletenessManifest(WithExtraProperty(manifest)));
        Assert.Equal(
            manifest,
            SourcingSerialization.ReadCompletenessManifest(manifest).CanonicalDocument());

        foreach (var name in new[] { "sourcing-proposal-version.json", "sourcing-proposal-version-catalog.json" })
        {
            var proposal = Load(name);
            Assert.NotNull(SourcingProposalDocument.Read(proposal, PolicyCanonicalizer.Hash(proposal)));
            // A tampered document whose hash is recomputed is still rejected by the closed schema.
            var altered = WithExtraProperty(proposal);
            Assert.Throws<SourcingDependencyUnavailableException>(() =>
                SourcingProposalDocument.Read(altered, PolicyCanonicalizer.Hash(altered)));
        }

        foreach (var name in new[] { "award-version.json", "award-version-catalog.json" })
        {
            var award = Load(name);
            Assert.NotNull(SourcingAwardDocument.Read(award, PolicyCanonicalizer.Hash(award)));
            var altered = WithExtraProperty(award);
            Assert.Throws<SourcingDependencyUnavailableException>(() =>
                SourcingAwardDocument.Read(altered, PolicyCanonicalizer.Hash(altered)));
        }

        // Nested objects are closed too: an extra member inside an award line never reaches the reader.
        var awardDocument = JsonNode.Parse(Load("award-version.json"))!.AsObject();
        ((JsonObject)((JsonArray)awardDocument["award_lines"]!)[0]!)["unexpected"] = 1;
        var nested = awardDocument.ToJsonString();
        Assert.Throws<SourcingDependencyUnavailableException>(() =>
            SourcingAwardDocument.Read(nested, PolicyCanonicalizer.Hash(nested)));
    }

    [Fact]
    public void The_readers_rebuild_the_published_catalogue_and_rfq_routes()
    {
        var rfqProposal = SourcingProposalDocument.Read(
            Load("sourcing-proposal-version.json"),
            PolicyCanonicalizer.Hash(Load("sourcing-proposal-version.json")));
        Assert.Equal(SourcingSelectionBasis.Rfq, rfqProposal.SelectionBasis);
        Assert.NotNull(rfqProposal.EvaluationRef);
        Assert.NotEmpty(rfqProposal.QuotationRefs);
        Assert.Empty(rfqProposal.CatalogSnapshots);
        Assert.Null(rfqProposal.WaiverRef);

        var catalogProposal = SourcingProposalDocument.Read(
            Load("sourcing-proposal-version-catalog.json"),
            PolicyCanonicalizer.Hash(Load("sourcing-proposal-version-catalog.json")));
        Assert.Equal(SourcingSelectionBasis.ApprovedCatalog, catalogProposal.SelectionBasis);
        Assert.Null(catalogProposal.EvaluationRef);
        Assert.Empty(catalogProposal.QuotationRefs);
        Assert.Null(catalogProposal.WaiverRef);
        Assert.Single(catalogProposal.CatalogSnapshots);

        var rfqAward = SourcingAwardDocument.Read(
            Load("award-version.json"), PolicyCanonicalizer.Hash(Load("award-version.json")));
        Assert.Equal(2, rfqAward.AwardCandidate.Lines.Count);
        Assert.Empty(rfqAward.CatalogSnapshots);
        Assert.Equal("USD", rfqAward.AwardCandidate.SourceCurrency);
        Assert.Equal("PEN", rfqAward.AwardCandidate.BaseCurrency);

        var catalogAward = SourcingAwardDocument.Read(
            Load("award-version-catalog.json"), PolicyCanonicalizer.Hash(Load("award-version-catalog.json")));
        Assert.Single(catalogAward.AwardCandidate.Lines);
        Assert.Single(catalogAward.CatalogSnapshots);
        Assert.Equal("PEN", catalogAward.AwardCandidate.SourceCurrency);

        var evaluation = Load("quote-evaluation-version.json");
        var line = SourcingEvaluationDocument.ReadLineResult(evaluation, SourcingContractFixtures.LineAId);
        Assert.Equal(93.75m, Assert.Single(line.QuotationScores).TotalScore);
        Assert.Equal(
            SourcingContractFixtures.FxId,
            SourcingEvaluationDocument.ReadFxRef(evaluation, "USD", "PEN")!.Id);

        Assert.Null(SourcingSerialization.ReadCompletenessManifest(Load("sourcing-completeness-manifest-catalog.json"))
            .EvaluationDigest);
        Assert.NotNull(SourcingSerialization.ReadCompletenessManifest(Load("sourcing-completeness-manifest.json"))
            .EvaluationDigest);
    }

    [Fact]
    public void The_goldens_fix_the_draft_nullability_of_the_rfq()
    {
        var draft = Load("rfq-version-draft.json");
        Assert.Contains("\"opened_at\":null", draft, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"DRAFT\"", draft, StringComparison.Ordinal);
        Assert.Contains("\"predecessor_version\":null", draft, StringComparison.Ordinal);
        var opened = Load("rfq-version.json");
        Assert.DoesNotContain("\"opened_at\":null", opened, StringComparison.Ordinal);
        Assert.Contains("\"opened_at\":\"2026-09-20T15:00:00.0000000Z\"", opened, StringComparison.Ordinal);
    }

    private static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, fileName));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string WithExtraProperty(string document)
    {
        var node = JsonNode.Parse(document)!.AsObject();
        node["unexpected_property"] = "must be rejected";
        return node.ToJsonString();
    }
}
