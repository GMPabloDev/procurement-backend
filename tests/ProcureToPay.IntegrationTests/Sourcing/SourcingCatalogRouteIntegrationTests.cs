using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-06 (CA-06): the catalogue route is only allowed with one
/// declared supplier, preferred and active snapshots and no quotation control in the current Policy
/// evaluation; confirming it takes the request over, and an existing prerequisite keeps waiting.
/// </summary>
public sealed class SourcingCatalogRouteIntegrationTests
{
    [Fact]
    public async Task The_catalogue_route_activates_the_process_and_takes_the_request_over()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.SetRequestSupplierAsync(context, harness.SupplierId, 1, cancellationToken);
        var processes = harness.CreateProcessService(context);
        var process = await processes.CreateProcessAsync(
            new CreateSourcingProcessCommand(
                harness.OrganizationId,
                harness.RequestId,
                1,
                [
                    new SourcingLineDraft(harness.LineId, 1, 10m, "EA"),
                    new SourcingLineDraft(harness.OtherLineId, 1, 5m, "EA")
                ],
                $"process-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-process",
            DateTimeOffset.UtcNow,
            cancellationToken);

        var route = await harness.CreateCatalogRouteService(context).ConfirmAsync(
            new ConfirmCatalogRouteCommand(
                harness.OrganizationId, process.ProcessId, process.Version, $"catalog-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-catalog",
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(harness.SupplierId, route.SupplierRef.Id);
        Assert.Equal(2, route.Snapshots.Count);
        Assert.False(string.IsNullOrWhiteSpace(route.SnapshotsDigest));

        // The process is active with a live takeover, and the request can no longer change.
        var active = await processes.GetProcessAsync(harness.OrganizationId, process.ProcessId, cancellationToken);
        Assert.Equal("ACTIVE", active.State);
        Assert.True(active.TakeoverHeld);
        await using var blocked = harness.CreateContext();
        await Assert.ThrowsAsync<DomainConflictException>(() => new PurchaseRequestPersistenceService(
                blocked, Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<PurchaseRequestPersistenceService>.Instance)
            .CancelAsync(
                harness.RequestId, 1, $"cancel-{Guid.NewGuid():N}", "Consumed", harness.OrganizationId,
                harness.RequesterId, "corr-cancel", DateTimeOffset.UtcNow, cancellationToken));

        // The stored route rehashes to its digest and can be read back.
        var stored = await harness.CreateCatalogRouteService(context)
            .GetRouteAsync(harness.OrganizationId, process.ProcessId, cancellationToken);
        Assert.Equal(route.SnapshotsDigest, stored.SnapshotsDigest);
        Assert.Equal(route.Snapshots.Count, stored.Snapshots.Count);
    }

    [Fact]
    public async Task The_route_requires_one_declared_supplier_and_a_preferred_active_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processes = harness.CreateProcessService(context);
        var process = await processes.CreateProcessAsync(
            new CreateSourcingProcessCommand(
                harness.OrganizationId,
                harness.RequestId,
                1,
                [new SourcingLineDraft(harness.LineId, 1, 10m, "EA")],
                $"process-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-process",
            DateTimeOffset.UtcNow,
            cancellationToken);

        // Without a declared supplier the catalogue route is not available.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateCatalogRouteService(context)
            .ConfirmAsync(
                new ConfirmCatalogRouteCommand(
                    harness.OrganizationId, process.ProcessId, process.Version, $"catalog-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-catalog",
                DateTimeOffset.UtcNow,
                cancellationToken));

        await harness.SetRequestSupplierAsync(context, harness.SupplierId, 1, cancellationToken);
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateCatalogRouteService(
                context, preferred: false)
            .ConfirmAsync(
                new ConfirmCatalogRouteCommand(
                    harness.OrganizationId, process.ProcessId, process.Version, $"catalog-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-catalog",
                DateTimeOffset.UtcNow,
                cancellationToken));
        // A snapshot without a catalogue entry is not a usable route either.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateCatalogRouteService(
                context, catalogEntry: false)
            .ConfirmAsync(
                new ConfirmCatalogRouteCommand(
                    harness.OrganizationId, process.ProcessId, process.Version, $"catalog-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-catalog",
                DateTimeOffset.UtcNow,
                cancellationToken));
    }

    [Fact]
    public async Task An_existing_quotation_control_keeps_the_rfq_obligation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.SetRequestSupplierAsync(context, harness.SupplierId, 1, cancellationToken);
        var processes = harness.CreateProcessService(context);
        var process = await processes.CreateProcessAsync(
            new CreateSourcingProcessCommand(
                harness.OrganizationId,
                harness.RequestId,
                1,
                [new SourcingLineDraft(harness.LineId, 1, 10m, "EA")],
                $"process-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-process",
            DateTimeOffset.UtcNow,
            cancellationToken);

        // The current Policy evaluation demands quotations for that line: the catalogue cannot omit it.
        await harness.SeedProposalFixturesAsync(context, cancellationToken);
        await harness.SeedQuotationControlAsync(context, harness.RequestId, harness.LineId, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => harness
            .CreateCatalogRouteService(context)
            .ConfirmAsync(
                new ConfirmCatalogRouteCommand(
                    harness.OrganizationId, process.ProcessId, process.Version, $"catalog-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-catalog",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("quotations", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verification = harness.CreateContext();
        Assert.False(await verification.SourcingCatalogRoutes.AnyAsync(cancellationToken));
        Assert.False(await verification.SourcingTakeovers.AnyAsync(cancellationToken));
    }
}
