using System.Diagnostics;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>
/// Controlled adapters and fail-closed registries with the future domains absent (CA-12),
/// plus the minimized OpenTelemetry source contract (NFR-05).
/// </summary>
public sealed class PolicyAdapterContractTests
{
    [Fact]
    public void Missing_or_ambiguous_reference_catalogs_fail_closed_including_cost_center()
    {
        var empty = new PolicyReferenceCatalogRegistry([]);
        Assert.Throws<PolicyDependencyUnavailableException>(() => empty.Resolve("COST_CENTER"));
        Assert.Throws<PolicyDependencyUnavailableException>(() => empty.Resolve("SUPPLIER"));

        var department = new StubCatalog("DEPARTMENT");
        var single = new PolicyReferenceCatalogRegistry([department]);
        Assert.Same(department, single.Resolve("DEPARTMENT"));
        // SPEC 01 eligibility is never asked to resolve COST_CENTER until its catalog exists.
        Assert.Throws<PolicyDependencyUnavailableException>(() => single.Resolve("COST_CENTER"));

        var ambiguous = new PolicyReferenceCatalogRegistry([new StubCatalog("DEPARTMENT"), new StubCatalog("DEPARTMENT")]);
        Assert.Throws<PolicyDependencyUnavailableException>(() => ambiguous.Resolve("DEPARTMENT"));
    }

    [Fact]
    public async Task Missing_exception_verifier_defaults_to_deny_and_ambiguity_fails_closed()
    {
        var defaultDeny = new PolicyExceptionVerifierRegistry([]);
        var verifier = defaultDeny.Resolve();
        var binding = WaiverFixtures.Binding(
            WaiverFixtures.OrganizationId, "PURCHASE_REQUEST", Guid.NewGuid(), 1,
            new string('b', 64), new string('a', 64), "RFQ",
            [WaiverFixtures.Target(Guid.NewGuid(), 1, new string('d', 64))]);
        var evidence = await verifier.VerifyAsync(
            WaiverFixtures.Request(binding, new string('c', 64)),
            TestContext.Current.CancellationToken);
        Assert.Null(evidence);

        var ambiguous = new PolicyExceptionVerifierRegistry(
            [new DefaultDenyQuotationWaiverVerifier(), new DefaultDenyQuotationWaiverVerifier()]);
        Assert.Throws<PolicyDependencyUnavailableException>(() => ambiguous.Resolve());
    }

    [Fact]
    public void Missing_or_ambiguous_fact_providers_fail_closed()
    {
        Assert.Throws<PolicyDependencyUnavailableException>(() =>
            new PolicyFactProviderRegistry([]).Resolve("PURCHASE_REQUEST", "REQUEST_EVALUATE"));

        var first = new StubProvider();
        var single = new PolicyFactProviderRegistry([first]);
        Assert.Same(first, single.Resolve("PURCHASE_REQUEST", "REQUEST_EVALUATE"));

        var ambiguous = new PolicyFactProviderRegistry([new StubProvider(), new StubProvider()]);
        Assert.Throws<PolicyDependencyUnavailableException>(() =>
            ambiguous.Resolve("PURCHASE_REQUEST", "REQUEST_EVALUATE"));
    }

    [Fact]
    public void Policy_activity_source_is_registered_for_export()
    {
        Assert.Equal("ProcureToPay.Policy", PolicyTelemetry.SourceName);
        Assert.Equal(PolicyTelemetry.SourceName, PolicyTelemetry.Source.Name);

        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PolicyTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = PolicyTelemetry.Source.StartActivity("policy.evaluate"))
        {
            activity?.SetTag("policy.operation", "PURCHASE_REQUEST");
        }

        Assert.NotNull(captured);
        Assert.Equal("policy.evaluate", captured!.DisplayName);
        Assert.Equal("PURCHASE_REQUEST", captured.GetTagItem("policy.operation"));
    }

    private sealed class StubCatalog(string catalogId) : IPolicyReferenceCatalog
    {
        public string CatalogId { get; } = catalogId;
        public string ContractVersion => "v1";
        public Task<bool> ExistsAsync(PolicyReferenceLookup reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubProvider : IPolicyFactProvider
    {
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "REQUEST_EVALUATE";
        public string ProviderId => "stub";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(PolicyFactRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
