using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Policy;

public sealed class PolicyPersistenceServiceTests
{
    [Fact]
    public async Task Activation_selection_and_evaluation_persistence_are_idempotent_and_audited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(sqlServer.GetConnectionString())
            .Options;
        await using var context = new ProcureToPayDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);

        var organizationId = Guid.NewGuid();
        context.Organizations.Add(new OrganizationRecord
        {
            Id = organizationId,
            Code = "POLICY-TEST",
            Name = "Policy Test",
            BaseCurrency = "PEN",
            TimeZoneId = "America/Lima",
            FiscalYearStartMonth = 1,
            Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);

        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, [PolicyScope.Line]);
        policy.AddRule(new PolicyRule(
            "LINE_DEFAULT",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.Allow, "DIRECT_PURCHASE")],
            isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));

        var service = new PolicyPersistenceService(context);
        var actor = new PolicyActor("USER", Guid.NewGuid());
        var version = await service.AppendVersionAsync(
            policy,
            DateTimeOffset.UtcNow,
            actor,
            "Create policy for persistence test",
            "corr-policy-1",
            cancellationToken);
        var activationAt = DateTimeOffset.UtcNow.AddSeconds(1);
        var activation = await service.ActivateAsync(
            organizationId,
            version.Id,
            activationAt,
            actor,
            "Activate policy for persistence test",
            "corr-policy-2",
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);

        var selected = await service.FindActiveAsync(organizationId, DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(activation.Id, selected?.Id);

        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            organizationId,
            Guid.NewGuid(),
            "PEN",
            [new PolicyLineInput(
                new PolicySubjectReference(Guid.NewGuid(), 1),
                new Dictionary<string, PolicyValue>
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10)
                })]);
        var bundle = PolicyEvaluator.EvaluateRequest(
            policy,
            request,
            "policy-eval-1",
            DateTimeOffset.UtcNow);
        // pi-lens-ignore: CS0246
        var caller = new PolicyEvaluationCaller(
            organizationId,
            "https://issuer.test",
            "procurement-api",
            "REQUEST_EVALUATE",
            bundle.EvaluationKey,
            version.Id,
            "corr-policy-3");

        var first = await service.AppendEvaluationAsync(bundle, caller, cancellationToken);
        var second = await service.AppendEvaluationAsync(bundle, caller, cancellationToken);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.BundleJson, second.BundleJson);
        using var persistedJson = JsonDocument.Parse(first.BundleJson);
        Assert.Equal(bundle.ResultDigest, persistedJson.RootElement.GetProperty("resultDigest").GetString());
        Assert.Equal(first.ResultDigest, persistedJson.RootElement.GetProperty("resultDigest").GetString());
        Assert.Equal(1, persistedJson.RootElement.GetProperty("evaluationSequence").GetInt64());
        await Assert.ThrowsAsync<DomainConflictException>(() => service.AppendEvaluationAsync(
            bundle with { Subject = new PolicySubjectReference(bundle.Subject.Id, 2) }, caller, cancellationToken));
        Assert.Equal(1, await context.PolicyEvaluationBundles.CountAsync(cancellationToken));
        Assert.Equal(3, await context.AdministrativeAuditRecords.CountAsync(cancellationToken));
        var reservation = await service.ReserveEvaluationKeyAsync(
            organizationId,
            "https://issuer.test",
            "procurement-api",
            "REQUEST_EVALUATE",
            "policy-eval-reservation",
            request.Subject.Id,
            request.Subject.Version,
            PolicyPersistenceService.ComputeCommandFingerprint(
                caller,
                request.Subject),
            cancellationToken);
        Assert.NotNull(reservation);
        await using var concurrentContext = new ProcureToPayDbContext(options);
        var concurrentService = new PolicyPersistenceService(concurrentContext);
        var waitingReservation = concurrentService.ReserveEvaluationKeyAsync(
            organizationId,
            "https://issuer.test",
            "procurement-api",
            "REQUEST_EVALUATE",
            "policy-eval-reservation",
            request.Subject.Id,
            request.Subject.Version,
            PolicyPersistenceService.ComputeCommandFingerprint(caller, request.Subject),
            cancellationToken);
        await Task.Delay(150, cancellationToken);
        await service.ReleaseEvaluationKeyAsync(reservation.Id, cancellationToken);
        var secondReservation = await waitingReservation;
        Assert.NotNull(secondReservation);
        await concurrentService.ReleaseEvaluationKeyAsync(secondReservation.Id, cancellationToken);
        Assert.Null(await context.PolicyEvaluationReservations.SingleOrDefaultAsync(
            item => item.Id == reservation.Id, cancellationToken));

        var provider = new CountingFactProvider(request);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Policy:Workloads:0:Issuer"] = "https://issuer.test",
            ["Policy:Workloads:0:ClientId"] = "procurement-api"
        }).Build();
        var evaluationService = new PolicyEvaluationService(
            service,
            new PolicyWorkloadAllowlist(configuration),
            new PolicyFactProviderRegistry([provider]),
            new PolicyExceptionVerifierRegistry([]),
            new PolicyReferenceCatalogRegistry([]),
            NullLogger<PolicyEvaluationService>.Instance);
        var factRequest = new PolicyFactRequest(
            "PURCHASE_REQUEST", request.Subject.Id, request.Subject.Version, "REQUEST_EVALUATE",
            DateTimeOffset.UtcNow, new PolicyWorkloadPrincipal("https://issuer.test", "procurement-api"),
            "corr-service-replay") { OrganizationId = organizationId };
        var serviceFirst = await evaluationService.EvaluateEnterprisePurchaseRequestAsync(
            factRequest, "policy-service-replay", cancellationToken);
        var serviceReplay = await evaluationService.EvaluateEnterprisePurchaseRequestAsync(
            factRequest with { RequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            "policy-service-replay", cancellationToken);
        Assert.Equal(serviceFirst.Id, serviceReplay.Id);
        var latestEvaluation = await service.FindLatestEvaluationAsync(
            organizationId, request.Subject.Id, request.Subject.Version, cancellationToken);
        Assert.Equal(serviceFirst.Id, latestEvaluation?.Id);
        Assert.Equal(1, provider.Calls);
        await Assert.ThrowsAsync<DomainConflictException>(() => evaluationService
            .EvaluateEnterprisePurchaseRequestAsync(
                factRequest with { SubjectId = Guid.NewGuid() }, "policy-service-replay", cancellationToken));
        Assert.Equal(1, provider.Calls);
        var persistedServiceEvaluation = await context.PolicyEvaluationBundles.SingleAsync(
            item => item.EvaluationKey == "policy-service-replay", cancellationToken);
        persistedServiceEvaluation.BundleJson = "{";
        await context.SaveChangesAsync(cancellationToken);
        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() => evaluationService
            .EvaluateEnterprisePurchaseRequestAsync(factRequest, "policy-service-replay", cancellationToken));
        Assert.Equal(1, provider.Calls);

        var quotationPolicy = new PolicySetVersion(Guid.NewGuid(), organizationId, 2, [PolicyScope.Line]);
        quotationPolicy.AddRule(new PolicyRule(
            "QUOTATIONS", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.RequireQuotations, "RFQ", minimumQuotations: 3,
                minimumExceptionQuotations: 1)]));
        quotationPolicy.AddRule(new PolicyRule(
            "QUOTATION_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));
        quotationPolicy.Publish(PolicyCanonicalizer.ComputePolicyDigest(quotationPolicy));
        var quotationVersion = await service.AppendVersionAsync(
            quotationPolicy,
            DateTimeOffset.UtcNow,
            actor,
            "Create quotation policy for waiver test",
            "corr-waiver-policy",
            cancellationToken);
        var quotationBundle = PolicyEvaluator.EvaluateRequest(
            quotationPolicy, request, "quotation-waiver-evaluation", DateTimeOffset.UtcNow);
        await service.AppendEvaluationAsync(quotationBundle, caller with
        {
            PolicySetVersionId = quotationVersion.Id,
            EvaluationKey = quotationBundle.EvaluationKey,
            CorrelationReference = "corr-waiver-evaluation"
        }, cancellationToken);
        var nonce = "waiver-nonce-1";
        var evidenceDigest = new string('c', 64);
        var binding = QuotationWaiverEvaluator.ComputeBinding(
            organizationId, quotationVersion.Id, quotationBundle.Subject.Id,
            quotationVersion.ContentDigest, quotationBundle.ResultDigest, nonce);
        var waiver = new QuotationWaiverRequest(
            PolicyExceptionType.ReduceMinValidQuotations, 3, 2, 1,
            quotationVersion.ContentDigest, quotationBundle.ResultDigest, binding, nonce,
            evidenceDigest, "PROCUREMENT_APPROVER", "PROCUREMENT",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            TargetRequirementKey = "RFQ"
        };
        var waiverService = new PolicyEvaluationService(
            service,
            new PolicyWorkloadAllowlist(configuration),
            new PolicyFactProviderRegistry([provider]),
            new PolicyExceptionVerifierRegistry([new AcceptedWaiverVerifier(evidenceDigest)]),
            new PolicyReferenceCatalogRegistry([]),
            NullLogger<PolicyEvaluationService>.Instance);
        var reevaluated = await waiverService.ApplyQuotationWaiverAsync(
            quotationBundle, waiver, cancellationToken);
        Assert.Equal(2, reevaluated.Controls.Single(control => control.RequirementKey == "RFQ").MinimumQuotations);
        Assert.Single(reevaluated.Diff);
        Assert.Equal(1, await context.Set<PolicyExceptionVerificationRecord>().CountAsync(cancellationToken));
        Assert.Equal(quotationBundle.Id, reevaluated.PreviousBundleId);

        var successor = new PolicySetVersion(Guid.NewGuid(), organizationId, 3, [PolicyScope.Line]);
        successor.AddRule(new PolicyRule(
            "LINE_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "DIRECT_PURCHASE")], isFallback: true));
        var successorContent = PolicyCanonicalizer.CanonicalizePolicy(successor);
        var successorDraft = await service.AppendDraftAsync(
            new PolicyDraftDocument(
                organizationId, "[\"LINE\"]", successorContent,
                PolicyCanonicalizer.Hash(successorContent)),
            DateTimeOffset.UtcNow,
            actor,
            "Create successor policy",
            "corr-policy-successor-draft",
            cancellationToken);
        var successorEffectiveFrom = DateTimeOffset.UtcNow.AddMinutes(1);
        var successorActivation = await service.PublishAndActivateAsync(
            successorDraft.Id,
            successorEffectiveFrom,
            actor,
            "Publish successor policy",
            "corr-policy-successor-publish",
            cancellationToken,
            organizationId);
        var retirement = await context.PolicyRetirements.SingleAsync(
            item => item.PolicyActivationId == activation.Id, cancellationToken);
        Assert.Equal(successorEffectiveFrom, retirement.EffectiveTo);
        Assert.Equal(successorActivation.Version.Id, await context.PolicyActivations
            .Where(item => item.Id == successorActivation.Activation.Id)
            .Select(item => item.PolicySetVersionId)
            .SingleAsync(cancellationToken));

        await service.RetireAsync(
            successorActivation.Activation.Id,
            successorEffectiveFrom.AddMinutes(1),
            actor,
            "Retire policy after persistence test",
            "corr-policy-4",
            cancellationToken,
            organizationId);
        Assert.Null(await service.FindActiveAsync(
            organizationId, successorEffectiveFrom.AddMinutes(2), cancellationToken));
    }

    private sealed class AcceptedWaiverVerifier(string evidenceDigest) : IQuotationWaiverVerifier
    {
        public Task<QuotationWaiverEvidence?> VerifyAsync(
            QuotationWaiverRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotationWaiverEvidence?>(new QuotationWaiverEvidence(
                evidenceDigest,
                request.Binding,
                request.Nonce,
                DateTimeOffset.UtcNow.AddMinutes(5),
                "workflow-decision-1")
            {
                WorkflowDecisionVersion = 1,
                WorkflowDecisionDigest = new string('d', 64),
                AuthorityEvidenceDigest = new string('e', 64),
                SegregationSatisfied = true
            });
    }

    private sealed class CountingFactProvider(PolicyRequestInput request) : IPolicyFactProvider
    {
        public int Calls { get; private set; }
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "REQUEST_EVALUATE";
        public string ProviderId => "test-provider";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest factRequest,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var manifest = new PolicyCompletenessManifest(
                request.Subject.Id, request.Subject.Version,
                request.Lines.Select(line => line.Subject).ToArray(), string.Empty);
            manifest = manifest with { Digest = PolicyEvaluationService.ComputeManifestDigest(manifest) };
            var facts = new PolicyFactBundle(request, manifest, ProviderId, ContractVersion, string.Empty)
            {
                Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            facts = facts with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(facts) };
            return Task.FromResult(facts);
        }
    }
}
