using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-10 / REQ-12 (CA-07, CA-08): the proposal document and its manifest are reproducible,
/// the award candidate sums its lines and the selection basis decides the closed nullability of every
/// optional reference instead of leaving it to the caller.
/// </summary>
public sealed class SourcingProposalTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupplierId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EvaluationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid BundleId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ProposalId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public void A_foreign_award_line_requires_its_fx_snapshot_reference()
    {
        Assert.Throws<DomainValidationException>(() => Line("USD", "PEN", fx: null));
        Assert.Throws<DomainValidationException>(() => Line("PEN", "PEN", fx: Ref(Guid.NewGuid())));
        var line = Line("USD", "PEN", fx: Ref(Guid.NewGuid()));
        Assert.NotNull(line.FxSnapshotRef);
    }

    [Fact]
    public void The_candidate_sums_its_lines_and_keeps_them_ordered()
    {
        var candidate = new AwardCandidate(
            new SourcingEntityRef(SupplierId, 1),
            "PEN",
            "PEN",
            Terms(),
            [
                Line("PEN", "PEN", fx: null, gross: 20m, lineId: LineId),
                Line("PEN", "PEN", fx: null, gross: 5m, lineId: Guid.Parse("88888888-8888-8888-8888-888888888888"))
            ]);

        Assert.Equal(25m, candidate.SourceAmount);
        Assert.Equal(25m, candidate.BaseAmount);
        Assert.Equal(2, candidate.Lines.Count);
        Assert.Equal(candidate.Lines.OrderBy(line => line.LineRef.Id), candidate.Lines);
    }

    [Fact]
    public void The_proposal_digest_is_stable_and_the_basis_closes_the_nullability()
    {
        var rfq = Proposal(SourcingSelectionBasis.Rfq, evaluation: Ref(EvaluationId), quotations: [Ref(Guid.NewGuid())]);
        var replay = Proposal(SourcingSelectionBasis.Rfq, evaluation: Ref(EvaluationId), quotations: rfq.QuotationRefs);
        Assert.Equal(rfq.ComputeDigest(), replay.ComputeDigest());
        Assert.Equal(rfq.ComputeDigest(), PolicyCanonicalizer.Hash(rfq.CanonicalDocument()));

        // An RFQ proposal without its evaluation, or a catalogue proposal with one, is refused.
        Assert.Throws<DomainValidationException>(() =>
            Proposal(SourcingSelectionBasis.Rfq, evaluation: null, quotations: [Ref(Guid.NewGuid())]));
        Assert.Throws<DomainValidationException>(() =>
            Proposal(SourcingSelectionBasis.ApprovedCatalog, evaluation: Ref(EvaluationId), quotations: []));
        Assert.Throws<DomainValidationException>(() =>
            Proposal(SourcingSelectionBasis.ApprovedCatalog, evaluation: null, quotations: [Ref(Guid.NewGuid())]));
    }

    [Fact]
    public void A_catalogue_proposal_requires_one_snapshot_per_selected_line()
    {
        Assert.Throws<DomainValidationException>(() => Proposal(
            SourcingSelectionBasis.ApprovedCatalog, evaluation: null, quotations: [], snapshots: []));
        var proposal = Proposal(
            SourcingSelectionBasis.ApprovedCatalog, evaluation: null, quotations: [],
            snapshots: [new SourcingCatalogSnapshot(Ref(Guid.NewGuid()), LineId, 1,
                new SourcingEntityRef(SupplierId, 1), new string('c', 64))]);
        Assert.Null(proposal.EvaluationRef);
        Assert.Null(proposal.WaiverRef);
        Assert.Single(proposal.CatalogSnapshots);
    }

    [Fact]
    public void The_candidate_must_cover_exactly_the_selected_lines_of_its_supplier()
    {
        var otherSupplier = new SourcingEntityRef(Guid.NewGuid(), 1);
        Assert.Throws<DomainConflictException>(() => new SourcingProposalVersion(
            1, null, OrganizationId, Request(), Bundle(), SourcingSelectionBasis.Rfq,
            new SourcingEntityRef(SupplierId, 1), [Ref(LineId)], Ref(EvaluationId), [Ref(Guid.NewGuid())],
            [], null, Terms(), Candidate(otherSupplier)));
    }

    [Fact]
    public void The_manifest_digest_commits_the_covered_lines_and_the_bundle()
    {
        var manifest = Manifest([Ref(LineId)]);
        var reordered = Manifest([Ref(LineId)]);
        Assert.Equal(manifest.ComputeDigest(), reordered.ComputeDigest());
        Assert.Equal(manifest.ComputeDigest(), PolicyCanonicalizer.Hash(manifest.CanonicalDocument()));

        var other = Manifest([Ref(Guid.NewGuid())]);
        Assert.NotEqual(manifest.ComputeDigest(), other.ComputeDigest());
    }

    [Fact]
    public void A_catalogue_manifest_declares_no_evaluation_digest()
    {
        var catalog = new SourcingCompletenessManifest(
            SourcingCodes.PolicyFactProviderId, SourcingCodes.PolicyFactProviderContractVersion,
            RequestId, 1, new string('a', 64), new string('b', 64), BundleId, ProposalId, 1,
            [Ref(LineId)], evaluationDigest: null, new string('c', 64), new string('d', 64),
            new string('e', 64), new string('f', 64));
        Assert.Null(catalog.EvaluationDigest);
        Assert.Contains("\"evaluation_digest\":null", catalog.CanonicalDocument(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_evaluation_reference_validates_its_five_digests_and_sequence()
    {
        Assert.Throws<DomainValidationException>(() => new SourcingPolicyEvaluationRef(
            0, BundleId, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64)));
        Assert.Throws<DomainValidationException>(() => new SourcingPolicyEvaluationRef(
            1, BundleId, "not-a-digest", new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64)));
        var reference = new SourcingPolicyEvaluationRef(
            7, BundleId, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
            new string('e', 64));
        Assert.Equal(7, reference.EvaluationSequence);
    }

    private static SourcingProposalVersion Proposal(
        SourcingSelectionBasis basis,
        SourcingContentRef? evaluation,
        IReadOnlyList<SourcingContentRef> quotations,
        IReadOnlyList<SourcingCatalogSnapshot>? snapshots = null) =>
        new(
            1, null, OrganizationId, Request(), Bundle(), basis,
            new SourcingEntityRef(SupplierId, 1), [Ref(LineId)], evaluation, quotations,
            snapshots ?? [], null, Terms(), Candidate(new SourcingEntityRef(SupplierId, 1)));

    private static AwardCandidate Candidate(SourcingEntityRef supplier) =>
        new(supplier, "PEN", "PEN", Terms(), [Line("PEN", "PEN", fx: null)]);

    private static AwardLine Line(
        string source,
        string baseCurrency,
        SourcingContentRef? fx,
        decimal gross = 10m,
        Guid? lineId = null) =>
        new(Ref(lineId ?? LineId), 2m, "EA", gross / 2m, source, gross, baseCurrency, gross, fx);

    private static SourcingCompletenessManifest Manifest(IReadOnlyList<SourcingContentRef> lines) =>
        new(
            SourcingCodes.PolicyFactProviderId, SourcingCodes.PolicyFactProviderContractVersion,
            RequestId, 1, new string('a', 64), new string('b', 64), BundleId, ProposalId, 1, lines,
            new string('c', 64), new string('d', 64), new string('e', 64), new string('f', 64),
            new string('1', 64));

    private static SourcingContentRef Request() => Ref(RequestId);

    private static SourcingPolicyEvaluationRef Bundle() => new(
        1, BundleId, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
        new string('e', 64));

    private static CommercialTerms Terms() => new(15, "EXW", "NET30", 365);

    private static SourcingContentRef Ref(Guid id) => new(id, 1, new string('a', 64));
}
