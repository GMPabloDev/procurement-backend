using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-09 (CA-05): the recommended set is server-side, a deviation
/// demands its recorded justification, a stale selection version conflicts, one line has one current
/// selection and the partition splits what the contract cannot join.
/// </summary>
public sealed class SourcingSelectionIntegrationTests
{
    [Fact]
    public async Task A_recommended_selection_is_persisted_and_partitioned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        var selections = harness.CreateSelectionService(context);

        var selection = await selections.SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.True(selection.Selection.Recommended);
        Assert.Null(selection.Selection.DeviationJustification);
        Assert.Equal(1, selection.Version);
        Assert.NotNull(selection.EvaluationContentDigest);

        var drafts = await selections.PartitionAsync(harness.OrganizationId, rfq.RfqId, cancellationToken);
        var draft = Assert.Single(drafts);
        Assert.Equal(harness.SupplierId, draft.SupplierRef.Id);
        Assert.Single(draft.Selections);
    }

    [Fact]
    public async Task A_deviation_needs_its_justification_and_a_stale_version_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        var selections = harness.CreateSelectionService(context);

        // The only valid quotation of the line is the selected supplier, so a deviation needs the
        // second supplier to be eligible; without a valid quotation it is refused (REQ-09/REQ-11).
        await Assert.ThrowsAsync<DomainConflictException>(() => selections.SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SecondSupplierId, null, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken));

        var first = await selections.SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken);
        await Assert.ThrowsAsync<DomainConflictException>(() => selections.SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken));

        var revised = await selections.SelectAsync(
            new SelectLineCommand(
                harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, first.Version, null,
                $"select-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-select",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(2, revised.Version);

        // A replacement is an append-only successor: the first decision stays recorded.
        await using var verification = harness.CreateContext();
        var versions = await verification.SourcingSelectionVersions
            .AsNoTracking()
            .Where(row => row.SelectionId == first.SelectionId)
            .OrderBy(row => row.Version)
            .Select(row => row.Version)
            .ToArrayAsync(cancellationToken);
        Assert.Equal([1, 2], versions);
    }

    [Fact]
    public async Task The_selection_must_match_the_supplier_declared_by_the_request_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);

        // The request line now declares its own supplier: a selection of another one is refused.
        await using (var declare = harness.CreateContext())
        {
            await declare.Database.ExecuteSqlRawAsync(
                "UPDATE [PurchaseRequest].[PurchaseRequestLineVersions] SET [SupplierJson] = {0} " +
                "WHERE [LineId] = {1}",
                [
                    $"{{\"entity_type\":\"SUPPLIER\",\"id\":\"{harness.SecondSupplierId:D}\",\"version\":1}}",
                    harness.LineId
                ],
                cancellationToken);
        }

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateSelectionService(context)
            .SelectAsync(
                new SelectLineCommand(
                    harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                    $"select-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-select",
                DateTimeOffset.UtcNow,
                cancellationToken));
    }

    [Fact]
    public async Task A_selection_requires_a_current_evaluation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateSelectionService(context)
            .SelectAsync(
                new SelectLineCommand(
                    harness.OrganizationId, rfq.RfqId, harness.LineId, harness.SupplierId, null, null,
                    $"select-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-select",
                DateTimeOffset.UtcNow,
                cancellationToken));
    }
}
