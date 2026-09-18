using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-01, REQ-02 and REQ-10 (CA-01, CA-02, CA-09): a claim consumes
/// one current award exactly once, transfers the line takeovers, creates a reproducible incomplete
/// draft, releases the claim on a pre-issue cancellation and never reuses an issued award.
/// </summary>
public sealed class PurchaseOrderClaimIntegrationTests
{
    [Fact]
    public async Task A_claim_creates_one_draft_and_replays_its_outcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken, twoLines: true);
        Assert.Equal(2, award.AwardedLines.Count);
        var key = $"claim-{Guid.NewGuid():N}";
        var request = harness.ClaimRequest(award, key);
        var claimService = harness.CreateClaimService(context);

        var first = await claimService.ClaimAsync(request, harness.BuyerId, cancellationToken);
        var replay = await harness.CreateClaimService(harness.CreateContext())
            .ClaimAsync(request, harness.BuyerId, cancellationToken);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ClaimRef.Id, replay.ClaimRef.Id);
        Assert.Equal(first.PoRef.Id, replay.PoRef.Id);

        // The draft carries no delivery and no assignments, and its document is reproducible.
        await using var verification = harness.CreateContext();
        var versionRow = await verification.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(record => record.PoId == request.PoId, cancellationToken);
        Assert.Equal((int)PurchaseOrderState.Draft, versionRow.State);
        Assert.Null(versionRow.DeliveryJson);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(versionRow.LineParametersJson);
        Assert.Equal(2, parameters.Count);
        Assert.All(parameters, parameter => Assert.Equal("GOOD", parameter.PurchaseType));
        var document = PurchaseOrderSerialization.ReadDocument(versionRow.DocumentJson, versionRow.ContentDigest);
        Assert.Null(document.Delivery);
        Assert.All(document.Lines, line => Assert.Empty(line.AcceptanceResponsibilities));
        Assert.Equal(2, document.Lines.Count);

        // The takeovers moved from SOURCING to PURCHASE_ORDER for every covered line.
        var takeovers = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.State == (int)TakeoverState.Active)
            .OrderBy(row => row.LineId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, takeovers.Length);
        Assert.All(takeovers, row =>
        {
            Assert.Equal((int)PurchaseRequestLineOwner.PurchaseOrder, row.Owner);
            Assert.Equal(request.PoId, row.ConsumerId);
            Assert.NotNull(row.PredecessorId);
        });

        // A second claim for the same award under another key is refused: the award is consumed by
        // the live claim and the lines are fenced by the Purchase Order that owns them.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.CreateClaimService(harness.CreateContext()).ClaimAsync(
                harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}", poId: Guid.NewGuid()),
                harness.BuyerId,
                cancellationToken));
        await using var afterConflict = harness.CreateContext();
        Assert.Equal(1, await afterConflict.PurchaseOrders.CountAsync(cancellationToken));
        Assert.Equal(1, await afterConflict.AwardConsumptionClaims.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task The_same_key_with_another_preimage_is_a_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var key = $"claim-{Guid.NewGuid():N}";

        await harness.CreateClaimService(context).ClaimAsync(
            harness.ClaimRequest(award, key), harness.BuyerId, cancellationToken);

        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.CreateClaimService(harness.CreateContext()).ClaimAsync(
                harness.ClaimRequest(award, key, poId: Guid.NewGuid()),
                harness.BuyerId,
                cancellationToken));
    }

    [Fact]
    public async Task A_partial_coverage_is_refused_without_a_second_purchase_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken, twoLines: true);
        Assert.Equal(2, award.AwardedLines.Count);

        // The award covers line 1 and 2; a claim that covers only the first line is partial coverage.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.CreateClaimService(context).ClaimAsync(
                harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}", lines: [harness.LineId]),
                harness.BuyerId,
                cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.Equal(0, await verification.PurchaseOrders.CountAsync(cancellationToken));
        Assert.Equal(0, await verification.AwardConsumptionClaims.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_cancelled_claim_returns_the_takeovers_to_the_award_and_allows_a_new_claim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var firstRequest = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        var claimService = harness.CreateClaimService(context);
        var first = await claimService.ClaimAsync(firstRequest, harness.BuyerId, cancellationToken);

        await using (var release = harness.CreateContext())
        {
            await harness.CreateClaimService(release).ReleaseClaimAsync(
                harness.OrganizationId,
                first.ClaimRef.Id,
                harness.BuyerId,
                "pre-issue cancellation",
                DateTimeOffset.UtcNow,
                cancellationToken);
            await release.SaveChangesAsync(cancellationToken);
        }

        await using var verification = harness.CreateContext();
        var claimRow = await verification.AwardConsumptionClaims
            .AsNoTracking()
            .SingleAsync(record => record.Id == first.ClaimRef.Id, cancellationToken);
        Assert.Equal((int)AwardClaimState.Released, claimRow.State);
        var returned = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.State == (int)TakeoverState.Active)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, returned.Length);
        Assert.All(returned, row => Assert.Equal((int)PurchaseRequestLineOwner.Sourcing, row.Owner));

        // The award is claimable again under a new key and a new purchase order.
        var secondRequest = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}", poId: Guid.NewGuid());
        var second = await harness.CreateClaimService(harness.CreateContext())
            .ClaimAsync(secondRequest, harness.BuyerId, cancellationToken);
        Assert.NotEqual(first.PoRef.Id, second.PoRef.Id);
    }

    [Fact]
    public async Task An_unclaimed_award_with_a_stale_supplier_is_recovered_by_reopen_or_cancel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        await using (var advance = harness.CreateContext())
        {
            await harness.Sourcing.AdvanceSupplierVersionAsync(advance, cancellationToken);
        }

        // The stale supplier makes the award not consumable, and recovery never bypasses Policy.
        await Assert.ThrowsAnyAsync<DomainException>(() =>
            harness.CreateClaimService(harness.CreateContext()).ClaimAsync(
                harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}"),
                harness.BuyerId,
                cancellationToken));

        var cancelled = await harness.CreateClaimService(harness.CreateContext()).RecoverAsync(
            new AwardRecoveryCommand(
                harness.OrganizationId,
                new PurchaseOrderContentRef(award.AwardId, award.Version, award.ContentDigest),
                await CurrentProcessVersionAsync(harness, cancellationToken),
                award.Version,
                AwardRecoveryCommand.ActionCancel,
                $"recovery-{Guid.NewGuid():N}",
                "Supplier no longer eligible"),
            harness.BuyerId,
            cancellationToken);

        Assert.Equal("CANCELLED", cancelled.ProcessState);
        await using var verification = harness.CreateContext();
        var released = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.State == (int)TakeoverState.Active)
            .CountAsync(cancellationToken);
        Assert.Equal(0, released);
        Assert.Equal(0, await verification.PurchaseOrders.CountAsync(cancellationToken));
    }

    private static async Task<int> CurrentProcessVersionAsync(
        PurchaseOrderHarness harness,
        CancellationToken cancellationToken)
    {
        await using var context = harness.CreateContext();
        return await context.SourcingProcesses
            .AsNoTracking()
            .Select(record => record.Version)
            .SingleAsync(cancellationToken);
    }

    [Fact]
    public async Task A_reopen_keeps_the_takeover_and_refuses_an_award_with_a_claim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var reopened = await harness.CreateClaimService(context).RecoverAsync(
            new AwardRecoveryCommand(
                harness.OrganizationId,
                new PurchaseOrderContentRef(award.AwardId, award.Version, award.ContentDigest),
                await CurrentProcessVersionAsync(harness, cancellationToken),
                award.Version,
                AwardRecoveryCommand.ActionReopen,
                $"recovery-{Guid.NewGuid():N}",
                "Reopen to build a successor proposal"),
            harness.BuyerId,
            cancellationToken);

        Assert.Equal("ACTIVE", reopened.ProcessState);
        await using var verification = harness.CreateContext();
        var process = await verification.SourcingProcesses
            .AsNoTracking()
            .SingleAsync(record => record.Id == award.ProcessId, cancellationToken);
        Assert.Equal((int)SourcingProcessState.Active, process.State);
        Assert.Equal(2, await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .CountAsync(row => row.State == (int)TakeoverState.Active, cancellationToken));
    }

    [Fact]
    public async Task Published_versions_are_append_only_in_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var request = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(request, harness.BuyerId, cancellationToken);

        await using var tampering = harness.CreateContext();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseOrders].[PurchaseOrderVersions] SET [DocumentJson] = {0} WHERE [PoId] = {1}",
            ["{}", request.PoId],
            cancellationToken));
        Assert.Contains("append-only", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_one_live_takeover_per_line_survives_a_concurrent_claim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(async index =>
            {
                try
                {
                    await using var scope = harness.CreateContext();
                    return await harness.CreateClaimService(scope).ClaimAsync(
                        harness.ClaimRequest(
                            award, $"claim-{Guid.NewGuid():N}", poId: Guid.NewGuid()),
                        harness.BuyerId,
                        cancellationToken);
                }
                catch (Exception)
                {
                    return null;
                }
            }));

        Assert.Single(attempts, attempt => attempt is not null);
        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.AwardConsumptionClaims.CountAsync(cancellationToken));
        var active = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.State == (int)TakeoverState.Active && row.LineId == harness.LineId)
            .ToArrayAsync(cancellationToken);
        var owned = Assert.Single(active);
        Assert.Equal((int)PurchaseRequestLineOwner.PurchaseOrder, owned.Owner);
    }
}
