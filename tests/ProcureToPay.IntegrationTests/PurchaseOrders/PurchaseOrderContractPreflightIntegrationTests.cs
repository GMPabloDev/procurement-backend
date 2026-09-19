using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-10/REQ-12 (CA-10): the startup preflight refuses to serve
/// commands when a historical request-wide takeover cannot be represented by its per-line rows — a
/// missing process line set, a missing request version or an incomplete expansion.
/// </summary>
public sealed class PurchaseOrderContractPreflightIntegrationTests
{
    [Fact]
    public async Task An_unexpandable_legacy_takeover_stops_the_host()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var preflight = new PurchaseOrderContractPreflight(context);

        // No legacy takeover at all: the module is safe to enable.
        await preflight.EnsureTakeoverExpansionAsync(cancellationToken);

        // A takeover whose process carries no line set cannot be expanded.
        var orphanProcessId = Guid.NewGuid();
        context.SourcingTakeovers.Add(LegacyTakeover(harness, orphanProcessId, released: false));
        await context.SaveChangesAsync(cancellationToken);
        var missingLines = await Assert.ThrowsAsync<PurchaseOrderDependencyUnavailableException>(
            () => preflight.EnsureTakeoverExpansionAsync(cancellationToken));
        Assert.Contains("TAKEOVER_PROCESS_LINES_MISSING", missingLines.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_incomplete_expansion_of_a_legacy_takeover_stops_the_host()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processId = Guid.NewGuid();
        context.SourcingTakeovers.Add(LegacyTakeover(harness, processId, released: false));
        context.SourcingProcesses.Add(new SourcingProcessRecord
        {
            Id = processId,
            OrganizationId = harness.OrganizationId,
            RequestId = harness.RequestId,
            RequestVersion = 1,
            State = 1,
            Version = 1,
            ActorUserId = harness.BuyerId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        context.SourcingProcessLines.Add(new SourcingProcessLineRecord
        {
            ProcessId = processId,
            LineId = harness.LineId,
            LineVersion = 1,
            LineContentDigest = harness.LineDigest(harness.LineId),
            RequestedQuantity = 2m,
            UnitCode = "EA"
        });
        await context.SaveChangesAsync(cancellationToken);
        var preflight = new PurchaseOrderContractPreflight(context);

        // The line set exists but the per-line row was never written: the fence is incomplete.
        var notExpanded = await Assert.ThrowsAsync<PurchaseOrderDependencyUnavailableException>(
            () => preflight.EnsureTakeoverExpansionAsync(cancellationToken));
        Assert.Contains("TAKEOVER_LINE_NOT_EXPANDED", notExpanded.Message, StringComparison.Ordinal);

        // A complete expansion is accepted.
        context.PurchaseRequestLineTakeovers.Add(new PurchaseRequestLineTakeoverRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = harness.OrganizationId,
            RequestId = harness.RequestId,
            RequestVersion = 1,
            RequestContentDigest = new string('1', 64),
            LineId = harness.LineId,
            LineVersion = 1,
            LineContentDigest = harness.LineDigest(harness.LineId),
            Owner = (int)PurchaseRequestLineOwner.Sourcing,
            State = (int)TakeoverState.Active,
            Version = 1,
            ConsumerId = processId,
            ConsumerVersion = 1,
            ConsumerDigest = new string('2', 64),
            ConsumerType = PurchaseOrderContractPreflight.SourcingConsumerType,
            ActorUserId = harness.BuyerId,
            OccurredAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);
        await preflight.EnsureTakeoverExpansionAsync(cancellationToken);
    }

    private static SourcingTakeoverRecord LegacyTakeover(
        PurchaseOrderHarness harness,
        Guid processId,
        bool released) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = harness.OrganizationId,
            RequestId = harness.RequestId,
            RequestVersion = 1,
            ProcessId = processId,
            ProcessVersion = 1,
            ActorUserId = harness.BuyerId,
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ReleasedAt = released ? DateTimeOffset.UtcNow : null,
            ReleaseReason = released ? "Released" : null
        };
}
