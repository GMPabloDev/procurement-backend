using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.IntegrationTests.Policy;

public sealed class PolicyCanonicalizationIntegrationTests
{
    [Fact]
    public async Task Missing_workflow_verifier_uses_default_deny_instead_of_dependency_failure()
    {
        var verifier = new PolicyExceptionVerifierRegistry([]).Resolve();
        var evidence = await verifier.VerifyAsync(
            new QuotationWaiverRequest(
                PolicyExceptionType.ReduceMinValidQuotations, 3, 2, 1,
                new string('a', 64), new string('b', 64), "binding", "nonce",
                new string('c', 64), "PROCUREMENT_APPROVER", "PROCUREMENT",
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Null(evidence);
    }

    [Fact]
    public void Fact_manifest_and_bundle_digests_match_contractual_golden_vectors()
    {
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.Parse("22222222-2222-2222-2222-222222222222"), 2),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "PEN",
            [new PolicyLineInput(
                new PolicySubjectReference(Guid.Parse("44444444-4444-4444-4444-444444444444"), 1),
                new Dictionary<string, PolicyValue>
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(123.45m, "PEN"),
                    ["DATA_RISK"] = PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64))
                })]);
        var manifest = new PolicyCompletenessManifest(
            request.Subject.Id, request.Subject.Version,
            request.Lines.Select(line => line.Subject).ToArray(), string.Empty);
        manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
        var bundle = new PolicyFactBundle(request, manifest, "test-provider", "v1", string.Empty)
        {
            Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        Assert.Equal(
            "a8de08b7f778503675ed8bead65208137104503ae46ee7d0dbdc99e34e8ef019",
            manifest.Digest);
        Assert.Equal(
            "748840e099793748718acc2026d6de5d60a0dba298fde349a1313b152c07994c",
            PolicyEvaluationService.ComputeFactsDigest(bundle));
    }
}
