using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-10 (CA-07): the typed envelope is reproducible, belongs to exactly one proposal and
/// request version, and translates into <c>PolicySourcingInput</c> without adding or dropping a fact.
/// </summary>
public sealed class SourcingPolicyFactsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegalEntityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ProposalId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid BundleId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public void The_envelope_reproduces_its_manifest_and_facts_digests()
    {
        var envelope = Envelope();

        Assert.Equal(envelope.Manifest.ComputeDigest(), envelope.ManifestDigest);
        Assert.Equal(64, envelope.FactsDigest.Length);
        var replay = Envelope();
        Assert.Equal(envelope.ManifestDigest, replay.ManifestDigest);
        Assert.Equal(envelope.FactsDigest, replay.FactsDigest);
    }

    [Fact]
    public void The_manifest_must_belong_to_the_proposal_and_the_request()
    {
        Assert.Throws<DomainConflictException>(() => new SourcingPolicyFactEnvelope(
            SourcingCodes.PolicyFactProviderId, SourcingCodes.PolicyFactProviderContractVersion,
            new PolicySubjectReference(Guid.NewGuid(), 1), Request(), [Line()], Facts(), Provenance(),
            Manifest(), EvaluationRef()));
        Assert.Throws<DomainConflictException>(() => new SourcingPolicyFactEnvelope(
            SourcingCodes.PolicyFactProviderId, SourcingCodes.PolicyFactProviderContractVersion,
            new PolicySubjectReference(ProposalId, 1), Request(version: 2), [Line()], Facts(), Provenance(),
            Manifest(), EvaluationRef()));
    }

    [Fact]
    public void The_envelope_must_cover_exactly_the_manifest_lines()
    {
        Assert.Throws<DomainConflictException>(() => new SourcingPolicyFactEnvelope(
            SourcingCodes.PolicyFactProviderId, SourcingCodes.PolicyFactProviderContractVersion,
            new PolicySubjectReference(ProposalId, 1), Request(), [Line(Guid.NewGuid())], Facts(), Provenance(),
            Manifest(), EvaluationRef()));
    }

    [Fact]
    public void The_envelope_and_its_manifest_declare_one_provider()
    {
        Assert.Throws<DomainConflictException>(() => new SourcingPolicyFactEnvelope(
            "another-provider", SourcingCodes.PolicyFactProviderContractVersion,
            new PolicySubjectReference(ProposalId, 1), Request(), [Line()], Facts(), Provenance(),
            Manifest(), EvaluationRef()));
    }

    [Fact]
    public void The_typed_envelope_translates_into_the_policy_input()
    {
        var envelope = Envelope();
        var current = CurrentEvaluation();
        var input = envelope.ToPolicyInput(current);

        Assert.Equal(envelope.Subject, input.Subject);
        Assert.Equal(envelope.Request, input.Request);
        Assert.Equal(envelope.CoveredLines, input.CoveredLines);
        Assert.Equal(envelope.Facts, input.Facts);
        Assert.Equal(current, input.CurrentRequestEvaluation);
        Assert.Equal(SourcingCodes.PolicyFactProviderId, input.Manifest.ProviderId);
        Assert.Equal(SourcingCodes.PolicyFactProviderContractVersion, input.Manifest.ContractVersion);
        Assert.Equal(envelope.ManifestDigest, input.Manifest.AttestationDigest);
        Assert.Equal(envelope.CoveredLines, input.Manifest.CoveredLines);
    }

    private static SourcingPolicyFactEnvelope Envelope() => new(
        SourcingCodes.PolicyFactProviderId,
        SourcingCodes.PolicyFactProviderContractVersion,
        new PolicySubjectReference(ProposalId, 1),
        Request(),
        [Line()],
        Facts(),
        Provenance(),
        Manifest(),
        EvaluationRef());

    private static PolicyRequestInput Request(int version = 1) => new(
        new PolicySubjectReference(RequestId, version),
        OrganizationId,
        LegalEntityId,
        "PEN",
        [new PolicyLineInput(new PolicySubjectReference(LineId, 1), Facts())]);

    private static PolicySubjectReference Line(Guid? id = null) => new(id ?? LineId, 1);

    private static IReadOnlyDictionary<string, PolicyValue> Facts() =>
        new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
        {
            ["ESTIMATED_AMOUNT"] = PolicyValue.Money(1000m, "PEN")
        };

    private static IReadOnlyDictionary<string, string> Provenance() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ESTIMATED_AMOUNT"] = "PURCHASE_REQUEST_LINE"
        };

    private static SourcingCompletenessManifest Manifest() => new(
        SourcingCodes.PolicyFactProviderId,
        SourcingCodes.PolicyFactProviderContractVersion,
        RequestId,
        1,
        new string('a', 64),
        new string('b', 64),
        BundleId,
        ProposalId,
        1,
        [new SourcingContentRef(LineId, 1, new string('c', 64))],
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        new string('1', 64),
        new string('2', 64));

    private static SourcingPolicyEvaluationRef EvaluationRef() => new(
        1, BundleId, new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64),
        new string('e', 64));

    private static PolicyEvaluationBundle CurrentEvaluation() => new(
        BundleId,
        "request-evaluation",
        new PolicySubjectReference(RequestId, 1),
        DateTimeOffset.Parse("2026-09-16T12:00:00.0000000Z"),
        new string('d', 64),
        new string('b', 64),
        [],
        [],
        PolicyResult.Passed,
        new string('e', 64))
    {
        Operation = "REQUEST_EVALUATE",
        FactsDigest = new string('a', 64),
        ManifestDigest = new string('c', 64)
    };
}
