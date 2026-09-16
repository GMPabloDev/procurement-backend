using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-05 (CA-04): the shortage that justifies a waiver is recomputed
/// from the quotation versions, the reduction never covers zero offers, never reaches the published
/// minimum and never falls below the persisted floor, and no Policy evaluation means no waiver.
/// </summary>
public sealed class SourcingWaiverIntegrationTests
{
    [Fact]
    public async Task A_shortage_is_recalculated_from_the_quotation_versions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 2, floor: 1, cancellationToken);

        // One valid quotation against a minimum of two: the facts can be built, and the request then
        // fails closed because the presented request version has no persisted policy evaluation.
        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Waiver(harness, context)
            .RequestReductionAsync(
                new RequestQuotationWaiverCommand(
                    harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-waiver",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("policy evaluation", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The facts are not persisted when the binding itself is unavailable: no phantom waiver.
        await using var verification = harness.CreateContext();
        Assert.False(await verification.SourcingWaiverFacts.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task A_minimum_already_met_has_nothing_to_reduce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 1, floor: 1, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Waiver(harness, context)
            .RequestReductionAsync(
                new RequestQuotationWaiverCommand(
                    harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-waiver",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("nothing to reduce", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_trace_without_any_quotation_cannot_justify_a_reduction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqWithoutQuotationAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 2, floor: 1, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Waiver(harness, context)
            .RequestReductionAsync(
                new RequestQuotationWaiverCommand(
                    harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-waiver",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("zero valid quotations", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reduction_below_the_floor_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 3, floor: 2, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Waiver(harness, context)
            .RequestReductionAsync(
                new RequestQuotationWaiverCommand(
                    harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-waiver",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("floor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_requirement_without_allowance_is_not_exceptionable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 3, floor: null, cancellationToken);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Waiver(harness, context)
            .RequestReductionAsync(
                new RequestQuotationWaiverCommand(
                    harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
                harness.BuyerId,
                "corr-waiver",
                DateTimeOffset.UtcNow,
                cancellationToken));
        Assert.Contains("NOT_EXCEPTIONABLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_prerequisite_or_request_without_case_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => Waiver(harness, context).RequestReductionAsync(
            new RequestQuotationWaiverCommand(
                harness.OrganizationId, rfq.RfqId, "OTHER_REQUIREMENT", $"waiver-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-waiver",
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using (var detach = harness.CreateContext())
        {
            await detach.Database.ExecuteSqlRawAsync(
                "DELETE FROM [Approval].[ApprovalPrerequisites]",
                cancellationToken);
        }

        await Assert.ThrowsAsync<DomainNotFoundException>(() => Waiver(harness, context).RequestReductionAsync(
            new RequestQuotationWaiverCommand(
                harness.OrganizationId, rfq.RfqId, "QUOTES", $"waiver-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-waiver",
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    private static SourcingWaiverService Waiver(SourcingHarness harness, ProcureToPayDbContext context) =>
        harness.CreateWaiverService(context);
}
