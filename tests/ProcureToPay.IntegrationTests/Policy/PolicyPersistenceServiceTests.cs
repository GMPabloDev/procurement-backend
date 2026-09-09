using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        await service.RetireAsync(
            activation.Id,
            DateTimeOffset.UtcNow.AddMinutes(1),
            actor,
            "Retire policy after persistence test",
            "corr-policy-4",
            cancellationToken);
        Assert.Null(await service.FindActiveAsync(organizationId, DateTimeOffset.UtcNow.AddHours(1), cancellationToken));
    }
}
