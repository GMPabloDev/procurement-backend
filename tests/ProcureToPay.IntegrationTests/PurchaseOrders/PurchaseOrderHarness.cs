using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.IntegrationTests.Sourcing;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server harness of the Purchase Orders module (SPEC 11). It composes the real Sourcing suite so
/// every claim consumes a genuine published award through the real <c>award-consumption/v1</c>
/// verifier, and it wires the per-line takeover service into both sides of the fence.
/// </summary>
public sealed class PurchaseOrderHarness : IAsyncDisposable
{
    private PurchaseOrderHarness(SourcingHarness sourcing)
    {
        Sourcing = sourcing;
    }

    public SourcingHarness Sourcing { get; }

    public Guid OrganizationId => Sourcing.OrganizationId;

    public Guid BuyerId => Sourcing.BuyerId;

    public Guid RequesterId => Sourcing.RequesterId;

    public Guid LineId => Sourcing.LineId;

    public Guid OtherLineId => Sourcing.OtherLineId;

    public Guid RequestId => Sourcing.RequestId;

    public Guid SupplierId => Sourcing.SupplierId;

    public static async Task<PurchaseOrderHarness> StartAsync(CancellationToken cancellationToken) =>
        new(await SourcingHarness.StartAsync(cancellationToken));

    public ProcureToPayDbContext CreateContext() => Sourcing.CreateContext();

    public PurchaseRequestLineTakeoverService CreateTakeoverService(ProcureToPayDbContext context) => new(context);

    public SourcingAwardService CreateAwardService(ProcureToPayDbContext context) =>
        new(context, new PurchaseRequestLineTakeoverService(context));

    public PurchaseOrderClaimService CreateClaimService(ProcureToPayDbContext context) =>
        new(context, CreateAwardService(context), new PurchaseRequestLineTakeoverService(context));

    public PurchaseOrderDraftService CreateDraftService(ProcureToPayDbContext context) =>
        new(context, new PurchaseRequestLineTakeoverService(context));

    /// <summary>
    /// Publishes one real award over the seeded request with the complete approved evidence, which is
    /// the only source a claim may consume. The standard scenario awards one line; the two-line
    /// variant is the fixture that makes partial coverage observable (CA-01, CA-02).
    /// </summary>
    public async Task<SourcingAwardView> PublishAwardAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken,
        bool twoLines = false)
    {
        var (created, rfq) = await SourcingScenario.OpenRfqAsync(Sourcing, context, cancellationToken);
        var process = await Sourcing.CreateProcessService(context)
            .GetProcessAsync(OrganizationId, created.ProcessId, cancellationToken);
        await SourcingScenario.EvaluateAsync(Sourcing, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(Sourcing, context, rfq, cancellationToken);
        if (twoLines)
        {
            await Sourcing.CreateSelectionService(context).SelectAsync(
                new SelectLineCommand(
                    OrganizationId, rfq.RfqId, OtherLineId, SupplierId, null, null,
                    $"select-{Guid.NewGuid():N}"),
                BuyerId,
                "corr-select-two",
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        await Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        var proposal = await Sourcing.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                OrganizationId, rfq.RfqId, SupplierId, $"proposal-{Guid.NewGuid():N}"),
            BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken);
        await SourcingScenario.SeedApprovalAsync(Sourcing, context, proposal, cancellationToken);
        return await CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                OrganizationId,
                proposal.ProposalId,
                proposal.Version,
                process.Version,
                null,
                $"award-{Guid.NewGuid():N}",
                "Award the selected supplier"),
            BuyerId,
            "corr-award",
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Builds the exact claim request over the covered lines of one published award.</summary>
    public AwardConsumptionClaimRequest ClaimRequest(
        SourcingAwardView award,
        string claimKey,
        IReadOnlyList<Guid>? lines = null,
        Guid? poId = null)
    {
        var covered = (lines ?? award.AwardedLines)
            .Select(lineId => new PurchaseOrderContentRef(lineId, 1, LineDigest(lineId)))
            .ToArray();
        return new AwardConsumptionClaimRequest(
            OrganizationId,
            poId ?? Guid.NewGuid(),
            new PurchaseOrderContentRef(award.AwardId, award.Version, award.ContentDigest),
            covered,
            claimKey,
            DateTimeOffset.UtcNow,
            PurchaseOrderCodes.DomainWorkloadIssuer,
            PurchaseOrderCodes.DomainWorkloadClientId);
    }

    /// <summary>The attested line digest of one seeded line, as the request version persisted it.</summary>
    public string LineDigest(Guid lineId) => lineId == LineId ? new string('2', 64) : new string('3', 64);

    public async ValueTask DisposeAsync() => await Sourcing.DisposeAsync();
}
