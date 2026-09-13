using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.IntegrationTests.Policy;

public sealed class PolicyCanonicalizationIntegrationTests
{
    [Fact]
    public async Task Missing_workflow_verifier_uses_default_deny_instead_of_dependency_failure()
    {
        var verifier = new PolicyExceptionVerifierRegistry([]).Resolve();
        var binding = new PolicyExceptionBindingRequest(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "PURCHASE_REQUEST",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            new string('b', 64),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            new string('a', 64),
            new string('9', 64),
            "RFQ",
            [new PolicyExceptionTarget("PURCHASE_REQUEST_LINE", Guid.Parse("55555555-5555-5555-5555-555555555555"), 1, new string('d', 64))],
            3,
            2,
            1,
            Guid.Parse("eeeeeeee-1111-1111-1111-111111111111"),
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            DateTimeOffset.Parse("2026-09-13T11:59:00.0000000Z"),
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
            "nonce-1");
        var evidence = await verifier.VerifyAsync(
            new QuotationWaiverRequest(
                PolicyExceptionType.ReduceMinValidQuotations,
                binding,
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                1,
                new string('c', 64),
                "corr-1"),
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
