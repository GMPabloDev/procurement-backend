using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-10 (CA-09): at most one active takeover exists per line version
/// whatever its owner — the migration's filtered unique index rejects an overlap even from a direct
/// writer — while a released takeover never blocks a new claim of the same line.
/// </summary>
public sealed class PurchaseRequestLineTakeoverIntegrationTests
{
    [Fact]
    public async Task An_overlapping_active_takeover_of_one_line_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await AcquireAsync(harness, context, PurchaseRequestLineOwner.Sourcing, Guid.NewGuid(), cancellationToken);

        // A second active row of another owner for the same line version violates the fence.
        await Assert.ThrowsAsync<DomainConflictException>(() => AcquireAsync(
            harness, context, PurchaseRequestLineOwner.DirectPurchase, Guid.NewGuid(), cancellationToken));
        context.ChangeTracker.Clear();

        var active = await context.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .CountAsync(record => record.LineId == harness.LineId, cancellationToken);
        Assert.Equal(1, active);

        // The filtered unique index is the backstop: even a direct writer cannot overlap the fence.
        var current = await context.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
        context.PurchaseRequestLineTakeovers.Add(new PurchaseRequestLineTakeoverRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = current.OrganizationId,
            RequestId = current.RequestId,
            RequestVersion = current.RequestVersion,
            RequestContentDigest = current.RequestContentDigest,
            LineId = current.LineId,
            LineVersion = current.LineVersion,
            LineContentDigest = current.LineContentDigest,
            Owner = (int)PurchaseRequestLineOwner.Fulfillment,
            State = (int)TakeoverState.Active,
            Version = 1,
            ConsumerId = Guid.NewGuid(),
            ConsumerVersion = 1,
            ConsumerDigest = new string('3', 64),
            ConsumerType = "FULFILLMENT",
            ActorUserId = harness.BuyerId,
            OccurredAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        context.ChangeTracker.Clear();

        // A released row never blocks the next owner of the same line version.
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseOrders].[PurchaseRequestLineTakeovers] SET [State] = 2 WHERE [LineId] = {0}",
            [harness.LineId],
            cancellationToken);
        await AcquireAsync(harness, context, PurchaseRequestLineOwner.PurchaseOrder, Guid.NewGuid(), cancellationToken);
        var owners = await context.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(record => record.LineId == harness.LineId && record.State == (int)TakeoverState.Active)
            .Select(record => record.Owner)
            .ToArrayAsync(cancellationToken);
        Assert.Equal([(int)PurchaseRequestLineOwner.PurchaseOrder], owners);
    }

    private static async Task AcquireAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        PurchaseRequestLineOwner owner,
        Guid consumerId,
        CancellationToken cancellationToken)
    {
        await new PurchaseRequestLineTakeoverService(context).AcquireAsync(
            harness.OrganizationId,
            harness.RequestId,
            1,
            new string('1', 64),
            [new PurchaseRequestLineTakeoverService.TakeoverLine(
                harness.LineId, 1, harness.LineDigest(harness.LineId))],
            owner,
            consumerId,
            1,
            new string('2', 64),
            PurchaseOrderCodes.TargetPurchaseOrder,
            predecessorConsumerId: null,
            predecessorConsumerVersion: null,
            harness.BuyerId.ToString("D"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
