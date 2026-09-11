using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Policy;

/// <summary>
/// Reevaluation history/diff (REQ-13, CA-09), contractual size limits (REQ-17, CA-11),
/// attestation validation (CA-06/CA-12) and minimized tracing (NFR-05).
/// </summary>
public sealed class PolicyReevaluationTests
{
    [Fact]
    public async Task Reevaluation_persists_history_with_a_stable_diff_and_enforces_contract_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        await using var context = new ProcureToPayDbContext(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(sqlServer.GetConnectionString())
                .Options);
        await context.Database.MigrateAsync(cancellationToken);

        var organizationId = Guid.NewGuid();
        context.Organizations.Add(new OrganizationRecord
        {
            Id = organizationId, Code = "REEVAL-TEST", Name = "Reevaluation Test",
            BaseCurrency = "PEN", TimeZoneId = "America/Lima", FiscalYearStartMonth = 1, Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);

        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
        policy.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
        policy.AddRule(new PolicyRule("THRESHOLD", PolicyScope.Request,
            [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(1000, "PEN"))],
            [new PolicyEffect(PolicyEffectType.RequirePo, "PO_REQUIRED")]));
        policy.AddRule(new PolicyRule("REQUEST_DEFAULT", PolicyScope.Request, [],
            [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_DEFAULT")], isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));

        var persistence = new PolicyPersistenceService(context);
        var actor = new PolicyActor("USER", Guid.NewGuid());
        var version = await persistence.AppendVersionAsync(
            policy, DateTimeOffset.UtcNow, actor, "reevaluation policy", "corr-reeval-policy", cancellationToken);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Policy:Workloads:0:Issuer"] = "https://issuer.test",
            ["Policy:Workloads:0:ClientId"] = "procurement-api"
        }).Build();
        var subjectId = Guid.NewGuid();
        var provider = new VersionedFactProvider(organizationId, subjectId);
        var evaluationService = new PolicyEvaluationService(
            persistence,
            new PolicyWorkloadAllowlist(configuration),
            new PolicyFactProviderRegistry([provider, new OversizedFactProvider(organizationId), new TamperedManifestProvider(organizationId), new CodeRefFactProvider(organizationId)]),
            new PolicyExceptionVerifierRegistry([]),
            new PolicyReferenceCatalogRegistry([]),
            NullLogger<PolicyEvaluationService>.Instance);

        await persistence.ActivateAsync(
            organizationId, version.Id, DateTimeOffset.UtcNow.AddSeconds(1), actor,
            "activate reevaluation policy", "corr-reeval-activate", cancellationToken);
        // Before the activation is effective, evaluations must fail closed with 409 and no bundle.
        await Assert.ThrowsAsync<PolicyConfigurationUnavailableException>(() =>
            evaluationService.EvaluateEnterprisePurchaseRequestAsync(
                FactRequest(organizationId, subjectId, 1, "reeval-no-policy"),
                "reeval-no-policy", cancellationToken));
        Assert.Equal(0, await context.PolicyEvaluationBundles
            .CountAsync(item => item.EvaluationKey == "reeval-no-policy", cancellationToken));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);

        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PolicyTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var firstRequest = FactRequest(organizationId, subjectId, 1, "reeval-v1");
        var first = await evaluationService.EvaluateEnterprisePurchaseRequestAsync(
            firstRequest, "reeval-v1", cancellationToken);
        Assert.Equal(PolicyResult.Passed, first.Result);
        Assert.Empty(first.Controls);
        Assert.Empty(first.Diff);

        var secondRequest = FactRequest(organizationId, subjectId, 2, "reeval-v2") with
        {
            PreviousBundleId = first.Id,
            Cause = "MATERIAL_FACT_CHANGE",
            PreviousResultDigest = first.ResultDigest
        };
        var second = await evaluationService.EvaluateEnterprisePurchaseRequestAsync(
            secondRequest, "reeval-v2", cancellationToken);

        Assert.Equal(PolicyResult.RequirementsGenerated, second.Result);
        Assert.Equal(first.Id, second.PreviousBundleId);
        // EvaluationSequence is monotonic per subject/version, so the new version starts at 1.
        Assert.Equal(1, second.EvaluationSequence);
        var added = Assert.Single(second.Diff);
        Assert.Equal(PolicyEvaluationDiff.Added, added.Change);
        Assert.Equal("PO_REQUIRED", added.RequirementKey);
        Assert.Equal(provider.RequestFor(2).Lines[0].Subject.Id, added.SubjectIds.Single());
        var poControl = Assert.Single(second.Controls, control => control.RequirementKey == "PO_REQUIRED");
        Assert.Equal("line-provider", poControl.FactProvenance["GROSS_AMOUNT_BASE"]);
        Assert.Contains("GROSS_AMOUNT_BASE", poControl.OriginFacts);

        // A catalog-keyed reference without a registered adapter fails closed (REQ-05/CA-12).
        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            evaluationService.EvaluateEnterprisePurchaseRequestAsync(
                new PolicyFactRequest("PURCHASE_REQUEST", subjectId, 1, "CODE_REF_EVALUATE",
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal("https://issuer.test", "procurement-api"), "reeval-code-ref")
                {
                    OrganizationId = organizationId
                },
                "reeval-code-ref", cancellationToken));

        var replayed = await evaluationService.EvaluateEnterprisePurchaseRequestAsync(
            secondRequest with { RequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, "reeval-v2", cancellationToken);
        Assert.Equal(second.Id, replayed.Id);
        // Provider calls: fail-closed probe, v1, v2; the replay must not call it again.
        Assert.Equal(3, provider.CallCount);

        Assert.Equal(2, await context.PolicyEvaluationBundles
            .CountAsync(item => item.SubjectId == subjectId, cancellationToken));
        var persistedSecond = await persistence.FindLatestEvaluationAsync(
            organizationId, subjectId, 2, cancellationToken);
        Assert.NotNull(persistedSecond);
        Assert.Single(PolicyEvaluationBundleRehydrator.FromJson(persistedSecond!.BundleJson).Diff);

        var oversized = await Assert.ThrowsAsync<PolicyPayloadTooLargeException>(() =>
            evaluationService.EvaluateEnterprisePurchaseRequestAsync(
                new PolicyFactRequest("PURCHASE_REQUEST", subjectId, 3, "RISK_ANSWER_EVALUATE",
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal("https://issuer.test", "procurement-api"), "reeval-large")
                {
                    OrganizationId = organizationId
                },
                "reeval-large", cancellationToken));
        Assert.Contains("256", oversized.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            evaluationService.EvaluateEnterprisePurchaseRequestAsync(
                new PolicyFactRequest("PURCHASE_REQUEST", subjectId, 1, "TAMPERED_MANIFEST",
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal("https://issuer.test", "procurement-api"), "reeval-tampered")
                {
                    OrganizationId = organizationId
                },
                "reeval-tampered", cancellationToken));
        Assert.Equal(0, await context.PolicyEvaluationBundles
            .CountAsync(item => item.EvaluationKey == "reeval-tampered", cancellationToken));

        await Assert.ThrowsAsync<PolicyPayloadTooLargeException>(() => persistence.AppendDraftAsync(
            Draft(organizationId, """{"rules":[""" + string.Join(',', Enumerable.Repeat("{}", 2001)) + "]}"),
            DateTimeOffset.UtcNow, actor, "oversized rules", "corr-large-rules", cancellationToken));
        await Assert.ThrowsAsync<PolicyPayloadTooLargeException>(() => persistence.AppendDraftAsync(
            Draft(organizationId, """{"rules":[{"predicates":[""" + string.Join(',', Enumerable.Repeat("{}", 33)) + "]}]}"),
            DateTimeOffset.UtcNow, actor, "oversized predicates", "corr-large-predicates", cancellationToken));
        await Assert.ThrowsAsync<PolicyPayloadTooLargeException>(() => persistence.AppendDraftAsync(
            Draft(organizationId, """{"rules":[{"effects":[""" + string.Join(',', Enumerable.Repeat("{}", 17)) + "]}]}"),
            DateTimeOffset.UtcNow, actor, "oversized effects", "corr-large-effects", cancellationToken));

        Assert.Contains(captured, activity =>
            activity.DisplayName == "policy.evaluate" &&
            string.Equals(activity.GetTagItem("policy.operation") as string, "REQUEST_EVALUATE", StringComparison.Ordinal) &&
            activity.GetTagItem("policy.result") is not null);
        Assert.DoesNotContain(captured, activity => activity.GetTagItem("policy.snapshot") is not null);
    }

    private static PolicyDraftDocument Draft(Guid organizationId, string content) =>
        new(organizationId, "[\"LINE\"]", content, PolicyCanonicalizer.Hash(content));

    private static PolicyFactRequest FactRequest(
        Guid organizationId, Guid subjectId, int version, string correlation) =>
        new("PURCHASE_REQUEST", subjectId, version, "REQUEST_EVALUATE", DateTimeOffset.UtcNow,
            new PolicyWorkloadPrincipal("https://issuer.test", "procurement-api"), correlation)
        {
            OrganizationId = organizationId
        };

    private sealed class VersionedFactProvider(Guid organizationId, Guid subjectId) : IPolicyFactProvider
    {
        private readonly Dictionary<int, PolicyRequestInput> requests = new()
        {
            [1] = Build(organizationId, subjectId, 1, 300m),
            [2] = Build(organizationId, subjectId, 2, 1500m)
        };

        public int CallCount { get; private set; }
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "REQUEST_EVALUATE";
        public string ProviderId => "versioned-test-provider";
        public string ContractVersion => "v1";

        public PolicyRequestInput RequestFor(int version) => requests[version];

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var input = requests[request.SubjectVersion];
            var manifest = new PolicyCompletenessManifest(
                input.Subject.Id, input.Subject.Version,
                input.Lines.Select(line => line.Subject).ToArray(), string.Empty);
            manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
            var bundle = new PolicyFactBundle(input, manifest, ProviderId, ContractVersion, string.Empty)
            {
                Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GROSS_AMOUNT_BASE"] = "line-provider"
                }
            };
            return Task.FromResult(bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) });
        }

        private static PolicyRequestInput Build(Guid organizationId, Guid subjectId, int version, decimal amount) =>
            new(
                new PolicySubjectReference(subjectId, version),
                organizationId,
                Guid.NewGuid(),
                "PEN",
                [new PolicyLineInput(
                    new PolicySubjectReference(Guid.NewGuid(), 1),
                    new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
                    {
                        ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(amount, "PEN")
                    })]);
    }

    private sealed class CodeRefFactProvider(Guid organizationId) : IPolicyFactProvider
    {
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "CODE_REF_EVALUATE";
        public string ProviderId => "code-ref-test-provider";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default)
        {
            var input = new PolicyRequestInput(
                new PolicySubjectReference(request.SubjectId, request.SubjectVersion),
                organizationId,
                Guid.NewGuid(),
                "PEN",
                [new PolicyLineInput(
                    new PolicySubjectReference(Guid.NewGuid(), 1),
                    new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
                    {
                        ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10, "PEN"),
                        ["SPEND_CATEGORY"] = PolicyValue.VersionedCode("SPEND_CATEGORY", "SOFTWARE", 1, new string('a', 64))
                    })]);
            var manifest = new PolicyCompletenessManifest(
                input.Subject.Id, input.Subject.Version,
                input.Lines.Select(line => line.Subject).ToArray(), string.Empty);
            manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
            var bundle = new PolicyFactBundle(input, manifest, ProviderId, ContractVersion, string.Empty);
            return Task.FromResult(bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) });
        }
    }

    private sealed class TamperedManifestProvider(Guid organizationId) : IPolicyFactProvider
    {
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "TAMPERED_MANIFEST";
        public string ProviderId => "tampered-test-provider";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default)
        {
            var line = new PolicyLineInput(
                new PolicySubjectReference(Guid.NewGuid(), 1),
                new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10, "PEN")
                });
            var input = new PolicyRequestInput(
                new PolicySubjectReference(request.SubjectId, request.SubjectVersion),
                organizationId,
                Guid.NewGuid(),
                "PEN",
                [line]);
            // Attestation omits the only declared line: the engine must reject it before persisting.
            var manifest = new PolicyCompletenessManifest(input.Subject.Id, input.Subject.Version, [], string.Empty);
            manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
            var bundle = new PolicyFactBundle(input, manifest, ProviderId, ContractVersion, string.Empty);
            return Task.FromResult(bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) });
        }
    }

    private sealed class OversizedFactProvider(Guid organizationId) : IPolicyFactProvider
    {
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "RISK_ANSWER_EVALUATE";
        public string ProviderId => "oversized-test-provider";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default)
        {
            var facts = new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10, "PEN")
            };
            for (var index = 0; index < 257; index++)
            {
                facts[$"RISK_ANSWER_{index}"] = PolicyValue.Code("YES");
            }
            var input = new PolicyRequestInput(
                new PolicySubjectReference(request.SubjectId, request.SubjectVersion),
                organizationId,
                Guid.NewGuid(),
                "PEN",
                [new PolicyLineInput(new PolicySubjectReference(Guid.NewGuid(), 1), facts)]);
            var manifest = new PolicyCompletenessManifest(
                input.Subject.Id, input.Subject.Version,
                input.Lines.Select(line => line.Subject).ToArray(), string.Empty);
            manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
            var bundle = new PolicyFactBundle(input, manifest, ProviderId, ContractVersion, string.Empty);
            return Task.FromResult(bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) });
        }
    }
}
