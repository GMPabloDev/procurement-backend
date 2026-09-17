using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-12 and REQ-14 (CA-08): publishing is a compare-and-swap that
/// leaves exactly one current award per line, only an approved proposal can be awarded, an ineligible
/// supplier never is, the award key replays its version and the consumption verifier answers the exact
/// contract or fails closed.
/// </summary>
public sealed class SourcingAwardIntegrationTests
{
    [Fact]
    public async Task Publishing_an_award_needs_the_completed_approval_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, rfq, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.CreateAwardService(context).PublishAsync(
                new PublishAwardCommand(
                    harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null,
                    $"award-{Guid.NewGuid():N}", "Award the selected supplier"),
                harness.BuyerId,
                "corr-award",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("approval case", exception.Message, StringComparison.OrdinalIgnoreCase);
        _ = rfq;
    }

    [Fact]
    public async Task A_published_award_is_unique_per_line_and_replays_its_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, proposal, cancellationToken);
        var key = $"award-{Guid.NewGuid():N}";
        var command = new PublishAwardCommand(
            harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null, key,
            "Award the selected supplier");

        var first = await harness.CreateAwardService(context).PublishAsync(
            command, harness.BuyerId, "corr-award", DateTimeOffset.UtcNow, cancellationToken);
        var replay = await harness.CreateAwardService(context).PublishAsync(
            command, harness.BuyerId, "corr-award-2", DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(first.AwardId, replay.AwardId);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(1, first.Version);
        Assert.Equal(harness.LineId, Assert.Single(first.AwardedLines));
        Assert.Equal(process.Version + 1, first.ProcessVersion);

        // The process left ACTIVE exactly once and owns the line in the uniqueness index.
        await using var verification = harness.CreateContext();
        var state = await verification.SourcingProcesses
            .AsNoTracking()
            .Where(record => record.Id == process.ProcessId)
            .Select(record => new { record.State, record.Version })
            .SingleAsync(cancellationToken);
        Assert.Equal((int)SourcingProcessState.Awarded, state.State);
        Assert.Equal(process.Version + 1, state.Version);
        var current = await verification.SourcingCurrentAwardLines
            .AsNoTracking()
            .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
        Assert.Equal(first.AwardId, current.AwardId);
        Assert.Equal(first.Version, current.AwardVersion);

        // Publishing again under a different key cannot publish a second award for the same process.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateAwardService(context)
            .PublishAsync(
                command with { AwardKey = $"award-{Guid.NewGuid():N}" },
                harness.BuyerId,
                "corr-award-3",
                DateTimeOffset.UtcNow,
                cancellationToken));

        // A published version cannot be rewritten, not even by a direct writer.
        await using var tampering = harness.CreateContext();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [Sourcing].[SourcingAwardVersions] SET [DocumentJson] = {0} WHERE [AwardId] = {1}",
            ["{}", first.AwardId],
            cancellationToken));
        Assert.Contains("immutable", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ineligible_supplier_never_receives_an_award()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, proposal, cancellationToken);
        // A successor version became current: the awarded version is stale, which is exactly the
        // case REQ-11 must refuse.
        await using (var successor = harness.CreateContext())
        {
            await harness.AdvanceSupplierVersionAsync(successor, cancellationToken);
        }

        await Assert.ThrowsAsync<AwardNotEligibleException>(() => harness.CreateAwardService(context)
            .PublishAsync(
                new PublishAwardCommand(
                    harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null,
                    $"award-{Guid.NewGuid():N}", "Award the selected supplier"),
                harness.BuyerId,
                "corr-award",
                DateTimeOffset.UtcNow,
                cancellationToken));
    }

    [Fact]
    public async Task Consumption_answers_the_exact_contract_and_fails_closed_on_stale_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, rfq, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, proposal, cancellationToken);
        var award = await harness.CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null,
                $"award-{Guid.NewGuid():N}", "Award the selected supplier"),
            harness.BuyerId,
            "corr-award",
            DateTimeOffset.UtcNow,
            cancellationToken);

        var request = new AwardConsumptionRequest(
            harness.OrganizationId,
            award.AwardId,
            award.Version,
            award.ContentDigest,
            harness.RequestId,
            1,
            [new SourcedRef(harness.LineId, 1)],
            DateTimeOffset.UtcNow,
            "internal://procure-to-pay",
            "purchase-order-domain");
        var verifier = harness.CreateAwardService(context);
        var response = await verifier.VerifyAsync(request, cancellationToken);

        Assert.Equal(award.ContentDigest, response.AwardRef.ContentDigest);
        Assert.Equal(harness.RequestId, response.RequestRef.Id);
        Assert.Equal(harness.SupplierId, response.SupplierRef.Id);
        Assert.Equal(harness.LineId, Assert.Single(response.AwardLines).LineRef.Id);
        Assert.Equal(response.AwardLines[0].SourceGrossTotal, response.SourceAmount);
        Assert.Equal("PEN", response.SourceCurrency);
        Assert.Empty(response.CatalogSnapshots);
        Assert.Empty(response.FxSnapshots);
        Assert.Equal(SourcingCodes.AwardConsumptionContract, verifier.ContractVersion);
        _ = rfq;

        // A stale digest, another line set or an ineligible supplier never return a partial answer.
        await Assert.ThrowsAsync<DomainConflictException>(() => verifier.VerifyAsync(
            request with { AwardContentDigest = new string('0', 64) }, cancellationToken));
        await Assert.ThrowsAsync<DomainConflictException>(() => verifier.VerifyAsync(
            request with { CoveredLines = [new SourcedRef(harness.OtherLineId, 1)] }, cancellationToken));
        await using (var successor = harness.CreateContext())
        {
            await harness.AdvanceSupplierVersionAsync(successor, cancellationToken);
        }

        await Assert.ThrowsAsync<AwardNotEligibleException>(() => verifier.VerifyAsync(request, cancellationToken));
    }

    [Fact]
    public async Task A_successor_proposal_supersedes_the_previous_award_of_its_supplier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, rfq, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, proposal, cancellationToken);
        var service = harness.CreateAwardService(context);
        var first = await service.PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null,
                $"award-{Guid.NewGuid():N}", "Award the selected supplier"),
            harness.BuyerId, "corr-award", DateTimeOffset.UtcNow, cancellationToken);

        // REQ-12: a correction builds a successor proposal instead of rewriting the published version.
        var successor = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId, "corr-proposal-successor", DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(2, successor.Version);
        await SourcingScenario.SeedApprovalAsync(harness, context, successor, cancellationToken);

        var awardedProcessVersion = await ProcessVersionAsync(harness, cancellationToken);
        var second = await harness.CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, successor.ProposalId, successor.Version, awardedProcessVersion, 1,
                $"award-{Guid.NewGuid():N}", "Correct the awarded supplier"),
            harness.BuyerId, "corr-award-successor", DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(first.AwardId, second.AwardId);
        Assert.Equal(2, second.Version);
        Assert.True((await service.GetAwardAsync(
            harness.OrganizationId, first.AwardId, 1, cancellationToken)).Superseded);
        Assert.Equal(2, (await service.GetCurrentAwardAsync(
            harness.OrganizationId, rfq.RfqId, harness.SupplierId, cancellationToken)).Version);
        await using (var verification = harness.CreateContext())
        {
            var current = await verification.SourcingCurrentAwardLines
                .AsNoTracking()
                .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
            Assert.Equal(second.AwardId, current.AwardId);
            Assert.Equal(2, current.AwardVersion);
        }

        // Only the successor is consumable: the superseded version fails closed, never partially.
        await Assert.ThrowsAsync<DomainConflictException>(() => service.VerifyAsync(
            Request(harness, first), cancellationToken));
        var response = await service.VerifyAsync(Request(harness, second), cancellationToken);
        Assert.Equal(second.ContentDigest, response.AwardRef.ContentDigest);
        Assert.Equal(harness.SupplierId, response.SupplierRef.Id);
    }

    [Fact]
    public async Task Changing_the_supplier_keeps_one_award_lineage_and_its_predecessor_chain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (created, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.RegisterSupplierQuotationAsync(
            harness, context, rfq, harness.SecondSupplierId, "12", "10", cancellationToken);
        var process = await harness.CreateProcessService(context)
            .GetProcessAsync(harness.OrganizationId, created.ProcessId, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);
        var firstProposal = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId, "corr-proposal", DateTimeOffset.UtcNow, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, firstProposal, cancellationToken);
        var service = harness.CreateAwardService(context);
        var first = await service.PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, firstProposal.ProposalId, firstProposal.Version, process.Version, null,
                $"award-{Guid.NewGuid():N}", "Award the recommended supplier"),
            harness.BuyerId, "corr-award", DateTimeOffset.UtcNow, cancellationToken);

        await harness.CreateSelectionService(context).SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SecondSupplierId,
                1, "Better delivery window for the same requirement", $"select-{Guid.NewGuid():N}"),
            harness.BuyerId, "corr-select-change", DateTimeOffset.UtcNow, cancellationToken);
        var secondProposal = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SecondSupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId, "corr-proposal-change", DateTimeOffset.UtcNow, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, secondProposal, cancellationToken);

        // A supplier change is a successor version of the same award, never a new lineage: the
        // caller must provide the current award version and the CAS rejects a stale one (REQ-12).
        var staleProcessVersion = await ProcessVersionAsync(harness, cancellationToken);
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateAwardService(context)
            .PublishAsync(
                new PublishAwardCommand(
                    harness.OrganizationId, secondProposal.ProposalId, secondProposal.Version,
                    staleProcessVersion, null, $"award-{Guid.NewGuid():N}",
                    "Stale correction"),
                harness.BuyerId, "corr-award-stale", DateTimeOffset.UtcNow, cancellationToken));

        var changed = await harness.CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, secondProposal.ProposalId, secondProposal.Version,
                await ProcessVersionAsync(harness, cancellationToken), 1, $"award-{Guid.NewGuid():N}",
                "Change the awarded supplier"),
            harness.BuyerId, "corr-award-change", DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(first.AwardId, changed.AwardId);
        Assert.Equal(2, changed.Version);
        Assert.Equal(harness.SecondSupplierId, changed.SupplierRef.Id);
        var previous = await service.GetAwardAsync(
            harness.OrganizationId, first.AwardId, 1, cancellationToken);
        Assert.True(previous.Superseded);
        Assert.Equal(2, (await service.GetCurrentAwardAsync(
            harness.OrganizationId, rfq.RfqId, harness.SecondSupplierId, cancellationToken)).Version);
        await using (var verification = harness.CreateContext())
        {
            var current = await verification.SourcingCurrentAwardLines
                .AsNoTracking()
                .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
            Assert.Equal(first.AwardId, current.AwardId);
            Assert.Equal(2, current.AwardVersion);
        }

        await Assert.ThrowsAsync<DomainConflictException>(() => service.VerifyAsync(
            Request(harness, previous), cancellationToken));
        var response = await service.VerifyAsync(Request(harness, changed), cancellationToken);
        Assert.Equal(harness.SecondSupplierId, response.SupplierRef.Id);
    }

    private static AwardConsumptionRequest Request(SourcingHarness harness, SourcingAwardView award) => new(
        harness.OrganizationId,
        award.AwardId,
        award.Version,
        award.ContentDigest,
        harness.RequestId,
        1,
        [new SourcedRef(harness.LineId, 1)],
        DateTimeOffset.UtcNow,
        "internal://procure-to-pay",
        "purchase-order-domain");

    private static async Task<int> ProcessVersionAsync(
        SourcingHarness harness,
        CancellationToken cancellationToken)
    {
        await using var context = harness.CreateContext();
        return await context.SourcingProcesses
            .AsNoTracking()
            .Where(record => record.OrganizationId == harness.OrganizationId)
            .Select(record => record.Version)
            .SingleAsync(cancellationToken);
    }
}
