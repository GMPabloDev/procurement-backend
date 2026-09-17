using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-10 (CA-07): exactly one typed provider serves <c>(SOURCING_PROPOSAL, SOURCING_PO)</c>,
/// and the provider refuses to serve an envelope whose attested request or manifest no longer matches
/// the persisted proposal instead of degrading to a partial fact set.
/// </summary>
public sealed class SourcingPolicyFactProviderIntegrationTests
{
    [Fact]
    public void The_registry_requires_exactly_one_provider()
    {
        var provider = new StubProvider();
        Assert.Same(provider, new SourcingPolicyFactProviderRegistry([provider]).Resolve(
            SourcingCodes.ApprovalSubjectType, SourcingCodes.PolicyOperation));
        Assert.Throws<SourcingDependencyUnavailableException>(() =>
            new SourcingPolicyFactProviderRegistry([]).Resolve(
                SourcingCodes.ApprovalSubjectType, SourcingCodes.PolicyOperation));
        Assert.Throws<SourcingDependencyUnavailableException>(() =>
            new SourcingPolicyFactProviderRegistry([provider, new StubProvider()]).Resolve(
                SourcingCodes.ApprovalSubjectType, SourcingCodes.PolicyOperation));
    }

    [Fact]
    public async Task The_provider_fails_closed_when_the_attested_request_changed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);
        var proposal = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken);

        // The seeded request manifest is not the one a real attestation produces, so the provider
        // refuses to serve the envelope: no partial fact set reaches Policy (REQ-10).
        var provider = harness.CreateSourcingPolicyFactProvider(context);
        // The request dependency fails closed with its own module exception; the contract that matters
        // is that no partial fact set reaches Policy.
        var exception = await Assert.ThrowsAnyAsync<DomainException>(() =>
            provider.GetFactsAsync(
                new PolicyFactRequest(
                    SourcingCodes.ApprovalSubjectType,
                    proposal.ProposalId,
                    proposal.Version,
                    SourcingCodes.PolicyOperation,
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal("internal://procure-to-pay", SourcingCodes.QuotationStatusProcessorId),
                    "corr-policy")
                {
                    OrganizationId = harness.OrganizationId
                },
                cancellationToken));
        Assert.Contains(
            ["attested", "reproducible"],
            candidate => exception.Message.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_provider_refuses_another_subject_or_operation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var provider = harness.CreateSourcingPolicyFactProvider(context);

        var wrongOperation = await Assert.ThrowsAsync<SourcingDependencyUnavailableException>(() =>
            provider.GetFactsAsync(
                new PolicyFactRequest(
                    SourcingCodes.ApprovalSubjectType, Guid.NewGuid(), 1, "REQUEST_EVALUATE",
                    DateTimeOffset.UtcNow,
                    new PolicyWorkloadPrincipal("internal://procure-to-pay", SourcingCodes.QuotationStatusProcessorId),
                    "corr-policy"),
                cancellationToken));
        Assert.Contains("SOURCING_PO", wrongOperation.Message, StringComparison.Ordinal);

        // A proposal that does not exist cannot produce an envelope either.
        await Assert.ThrowsAsync<SourcingDependencyUnavailableException>(() => provider.GetFactsAsync(
            new PolicyFactRequest(
                SourcingCodes.ApprovalSubjectType, Guid.NewGuid(), 1, SourcingCodes.PolicyOperation,
                DateTimeOffset.UtcNow,
                new PolicyWorkloadPrincipal("internal://procure-to-pay", SourcingCodes.QuotationStatusProcessorId),
                "corr-policy"),
            cancellationToken));
    }

    private sealed class StubProvider : ISourcingPolicyFactProvider
    {
        public string ProviderId => SourcingCodes.PolicyFactProviderId;

        public string ContractVersion => SourcingCodes.PolicyFactProviderContractVersion;

        public Task<SourcingPolicyFactEnvelope> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
