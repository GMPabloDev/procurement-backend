using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-10 (CA-09): the aggregate of the per-line ordering projection
/// reports ORDERED only when every attested line is ordered, DIRECT_PURCHASE_AUTHORIZED only when
/// every line follows that route, and PARTIALLY_ORDERED for a subset or a mixed set.
/// </summary>
public sealed class PurchaseRequestOrderingIntegrationTests
{
    [Fact]
    public async Task A_complete_ordered_route_reports_ordered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await ProjectAsync(
            harness,
            context,
            PurchaseRequestLineProjection.Ordered,
            harness.LineId,
            harness.OtherLineId);

        var view = await new PurchaseRequestOrderingQuery(context).SummarizeAsync(
            harness.OrganizationId, harness.RequestId, 1, cancellationToken);

        Assert.Equal(2, view.LineCount);
        Assert.Equal(2, view.OrderedLines);
        Assert.Equal(0, view.DirectPurchaseLines);
        Assert.Equal(PurchaseRequestOrderingQuery.Ordered, view.Summary);
    }

    [Fact]
    public async Task A_mixed_route_reports_partially_ordered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await ProjectAsync(harness, context, PurchaseRequestLineProjection.Ordered, harness.LineId);
        await ProjectAsync(
            harness,
            context,
            PurchaseRequestLineProjection.DirectPurchaseAuthorized,
            harness.OtherLineId);

        var view = await new PurchaseRequestOrderingQuery(context).SummarizeAsync(
            harness.OrganizationId, harness.RequestId, 1, cancellationToken);

        Assert.Equal(1, view.OrderedLines);
        Assert.Equal(1, view.DirectPurchaseLines);
        Assert.Equal(PurchaseRequestOrderingQuery.PartiallyOrdered, view.Summary);
    }

    [Fact]
    public async Task A_complete_direct_purchase_route_reports_authorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await ProjectAsync(
            harness,
            context,
            PurchaseRequestLineProjection.DirectPurchaseAuthorized,
            harness.LineId,
            harness.OtherLineId);

        var view = await new PurchaseRequestOrderingQuery(context).SummarizeAsync(
            harness.OrganizationId, harness.RequestId, 1, cancellationToken);

        Assert.Equal(PurchaseRequestOrderingQuery.DirectPurchaseAuthorized, view.Summary);
        Assert.Equal(0, view.OrderedLines);
    }

    [Fact]
    public async Task A_subset_reports_partially_ordered_and_an_empty_projection_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);

        var empty = await new PurchaseRequestOrderingQuery(context).SummarizeAsync(
            harness.OrganizationId, harness.RequestId, 1, cancellationToken);
        Assert.Null(empty.Summary);

        await ProjectAsync(harness, context, PurchaseRequestLineProjection.Ordered, harness.LineId);
        var subset = await new PurchaseRequestOrderingQuery(context).SummarizeAsync(
            harness.OrganizationId, harness.RequestId, 1, cancellationToken);

        Assert.Equal(1, subset.OrderedLines);
        Assert.Equal(PurchaseRequestOrderingQuery.PartiallyOrdered, subset.Summary);
    }

    private static async Task ProjectAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        PurchaseRequestLineProjection projection,
        params Guid[] lines)
    {
        foreach (var lineId in lines)
        {
            context.PurchaseRequestLineProjections.Add(new ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseRequestLineProjectionRecord
            {
                RequestId = harness.RequestId,
                RequestVersion = 1,
                LineId = lineId,
                LineVersion = 1,
                OrganizationId = harness.OrganizationId,
                Projection = (int)projection,
                ConsumerId = Guid.NewGuid(),
                ConsumerVersion = 1,
                ConsumerDigest = new string('a', 64),
                ConsumerType = projection == PurchaseRequestLineProjection.Ordered
                    ? PurchaseOrderCodes.TargetPurchaseOrder
                    : PurchaseOrderCodes.TargetDirectPurchase,
                ConsumerState = 1,
                OccurredAt = DateTimeOffset.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }
}
