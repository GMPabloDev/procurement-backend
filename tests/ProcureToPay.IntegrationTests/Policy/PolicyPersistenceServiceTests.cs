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
        Assert.Equal(1, provider.Calls);
        await Assert.ThrowsAsync<DomainConflictException>(() => evaluationService
            .EvaluateEnterprisePurchaseRequestAsync(
                factRequest with { SubjectId = Guid.NewGuid() }, "policy-service-replay", cancellationToken));
        Assert.Equal(1, provider.Calls);

        await service.RetireAsync(
            activation.Id,
            DateTimeOffset.UtcNow.AddMinutes(1),
            actor,
            "Retire policy after persistence test",
            "corr-policy-4",
            cancellationToken);
        Assert.Null(await service.FindActiveAsync(organizationId, DateTimeOffset.UtcNow.AddHours(1), cancellationToken));
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
            var manifestPreimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["line_count"] = manifest.Lines.Count,
                ["lines"] = manifest.Lines.OrderBy(line => line.Id).ThenBy(line => line.Version)
                    .Select(line => new { id = line.Id.ToString("D"), version = line.Version }).ToArray(),
                ["request_id"] = manifest.RequestId.ToString("D"),
                ["request_version"] = manifest.RequestVersion
            };
            manifest = manifest with { Digest = PolicyCanonicalizer.Hash(JsonSerializer.Serialize(manifestPreimage)) };
            using var requestDocument = JsonDocument.Parse(PolicyCanonicalizer.CanonicalizeRequest(
                request, DateTimeOffset.UnixEpoch));
            var factPayload = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in requestDocument.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "evaluated_at_utc", StringComparison.Ordinal))
                {
                    factPayload[string.Equals(property.Name, "subject", StringComparison.Ordinal)
                        ? "subject_ref" : property.Name] = property.Value.Clone();
                }
            }
            var factsPreimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["completeness_manifest"] = new
                {
                    request_id = manifest.RequestId.ToString("D"),
                    request_version = manifest.RequestVersion,
                    lines = manifest.Lines.OrderBy(line => line.Id).ThenBy(line => line.Version)
                        .Select(line => new { id = line.Id.ToString("D"), version = line.Version }).ToArray(),
                    digest = manifest.Digest
                },
                ["facts"] = factPayload,
                ["provenance"] = new Dictionary<string, string>(), 
                ["provider_contract_version"] = ContractVersion,
                ["provider_id"] = ProviderId,
                ["subject_ref"] = new { id = request.Subject.Id.ToString("D"), version = request.Subject.Version },
                ["organization_id"] = request.OrganizationId.ToString("D"),
                ["legal_entity_id"] = request.LegalEntityId.ToString("D"),
                ["base_currency"] = request.BaseCurrency
            };
            var facts = new PolicyFactBundle(
                request, manifest, ProviderId, ContractVersion,
                PolicyCanonicalizer.Hash(JsonSerializer.Serialize(factsPreimage)));
            return Task.FromResult(facts);
        }
    }
}
