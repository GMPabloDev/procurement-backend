using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-12 (CA-08): the award document is reproducible, repeats the candidate sums, keeps the
/// closed nullability of its basis and refuses to publish without approval evidence.
/// </summary>
public sealed class SourcingAwardTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupplierId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ProcessId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ProposalId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid RequestId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid BundleId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid CaseId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid QuoteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid EvaluationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-16T12:00:00.0000000Z");

    [Fact]
    public void An_award_needs_approval_case_evidence()
    {
        Assert.Throws<DomainValidationException>(() => Award(policyApprovalRefs: []));
        Assert.Throws<DomainValidationException>(() => new SourcingPolicyApprovalRef(
            EvaluationRef(), Guid.Empty, 1, new string('a', 64)));
        Assert.Throws<DomainValidationException>(() => new SourcingPolicyApprovalRef(
            EvaluationRef(), CaseId, 0, new string('a', 64)));
    }

    [Fact]
    public void The_award_digest_is_stable_and_changes_with_its_lines()
    {
        var first = Award();
        var replay = Award();
        Assert.Equal(first.ComputeDigest(), replay.ComputeDigest());
        Assert.Equal(first.ComputeDigest(), PolicyCanonicalizer.Hash(first.CanonicalDocument()));

        var other = Award(quantity: 5m);
        Assert.NotEqual(first.ComputeDigest(), other.ComputeDigest());
    }

    [Fact]
    public void The_award_repeats_the_candidate_sums_and_its_supplier()
    {
        var award = Award(quantity: 3m, unitPrice: 4m);
        Assert.Equal(12m, award.AwardCandidate.SourceAmount);
        Assert.Equal(12m, award.AwardCandidate.BaseAmount);
        Assert.Equal(SupplierId, award.SupplierRef.Id);
        Assert.Single(award.Lines);
        Assert.Equal(LineId, award.Lines[0].Id);

        Assert.Throws<DomainConflictException>(() => new SourcingAwardVersion(
            1, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.Rfq, new SourcingEntityRef(Guid.NewGuid(), 1), Ref(EvaluationId),
            [Ref(QuoteId)], [], null, [ApprovalRef()], Terms(), Candidate(), "reason"));
    }

    [Fact]
    public void Only_the_first_award_version_lacks_a_predecessor()
    {
        Assert.Throws<DomainValidationException>(() => new SourcingAwardVersion(
            2, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.Rfq, new SourcingEntityRef(SupplierId, 1), Ref(EvaluationId),
            [Ref(QuoteId)], [], null, [ApprovalRef()], Terms(), Candidate(), "reason"));
        Assert.Throws<DomainValidationException>(() => new SourcingAwardVersion(
            1, 1, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.Rfq, new SourcingEntityRef(SupplierId, 1), Ref(EvaluationId),
            [Ref(QuoteId)], [], null, [ApprovalRef()], Terms(), Candidate(), "reason"));
    }

    [Fact]
    public void The_basis_closes_the_nullability_of_every_optional_reference()
    {
        Assert.Throws<DomainValidationException>(() => new SourcingAwardVersion(
            1, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.Rfq, new SourcingEntityRef(SupplierId, 1), evaluationRef: null,
            [], [], null, [ApprovalRef()], Terms(), Candidate(), "reason"));
        Assert.Throws<DomainValidationException>(() => new SourcingAwardVersion(
            1, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.ApprovedCatalog, new SourcingEntityRef(SupplierId, 1), Ref(EvaluationId),
            [Ref(QuoteId)], [], null, [ApprovalRef()], Terms(), Candidate(), "reason"));
        var catalogue = new SourcingAwardVersion(
            1, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.ApprovedCatalog, new SourcingEntityRef(SupplierId, 1), evaluationRef: null,
            [], [Snapshot()], null, [ApprovalRef()], Terms(), Candidate(), "reason");
        Assert.Null(catalogue.EvaluationRef);
        Assert.Null(catalogue.WaiverRef);
        Assert.Single(catalogue.CatalogSnapshots);
    }

    private static SourcingAwardVersion Award(
        decimal quantity = 2m,
        decimal unitPrice = 10m,
        IReadOnlyList<SourcingPolicyApprovalRef>? policyApprovalRefs = null) =>
        new(
            1, null, OrganizationId, ActorUserId, Clock, Process(), Proposal(), Request(),
            SourcingSelectionBasis.Rfq, new SourcingEntityRef(SupplierId, 1), Ref(EvaluationId),
            [Ref(QuoteId)], [], null, policyApprovalRefs ?? [ApprovalRef()], Terms(),
            Candidate(quantity, unitPrice), "awarded");

    private static AwardCandidate Candidate(decimal quantity = 2m, decimal unitPrice = 10m) =>
        new(
            new SourcingEntityRef(SupplierId, 1),
            "PEN",
            "PEN",
            Terms(),
            [new AwardLine(
                Ref(LineId), quantity, "EA", unitPrice, "PEN", quantity * unitPrice, "PEN",
                quantity * unitPrice, fxSnapshotRef: null)]);

    private static SourcingPolicyApprovalRef ApprovalRef() =>
        new(EvaluationRef(), CaseId, 3, new string('b', 64));

    private static SourcingPolicyEvaluationRef EvaluationRef() => new(
        4, BundleId, new string('c', 64), new string('d', 64), new string('e', 64), new string('f', 64),
        new string('1', 64));

    private static SourcingCatalogSnapshot Snapshot() =>
        new(Ref(Guid.NewGuid()), LineId, 1, new SourcingEntityRef(SupplierId, 1), new string('2', 64));

    private static SourcingEntityRef Process() => new(ProcessId, 4);

    private static SourcingContentRef Proposal() => Ref(ProposalId);

    private static SourcingContentRef Request() => Ref(RequestId);

    private static CommercialTerms Terms() => new(15, "EXW", "NET30", 365);

    private static SourcingContentRef Ref(Guid id) => new(id, 1, new string('a', 64));
}
