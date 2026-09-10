using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.IntegrationTests.Policy;

public sealed class PolicyCanonicalizationIntegrationTests
{
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
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(123.45m),
                    ["DATA_RISK"] = PolicyValue.Code("HIGH")
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
            "5adeb637e9139153e1bd61f86364b48e727d1e0984f39cbfb6ec21d7128ceebd",
            PolicyEvaluationService.ComputeFactsDigest(bundle));
    }
}
