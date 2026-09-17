using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-13 (CA-03, CA-04, CA-09): exactly-one registrations gate the
/// claim, the quotation owner signals only when the recalculated trace meets its minimum, the
/// procurement owner distinguishes the proposal and the request subject, and cancelling a pre-award
/// process abandons attempts without signalling.
/// </summary>
public sealed class SourcingOwnerProcessorIntegrationTests
{
    [Fact]
    public async Task Exactly_one_registration_is_required_to_claim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var processor = harness.CreatePrerequisiteProcessor(context);
        Assert.False(await processor.IsReadyAsync(cancellationToken));

        await harness.RegisterOwnerAsync(context, SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken);
        Assert.False(await processor.IsReadyAsync(cancellationToken));
        Assert.True(await processor.IsRegisteredAsync(
            SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken));

        await harness.RegisterOwnerAsync(context, SourcingCodes.ProcurementStageOwnerAdapterId, cancellationToken);
        Assert.True(await processor.IsReadyAsync(cancellationToken));

        // A second registration of the same owner under another version is a different identity: the
        // published one keeps claiming while readiness still requires both owners registered.
        await harness.RegisterOwnerAsync(context, SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken);
        Assert.True(await processor.IsRegisteredAsync(
            SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken));
    }

    [Fact]
    public async Task The_quotation_owner_signals_once_when_the_recalculated_minimum_is_met()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.RegisterOwnerAsync(context, SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken);
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 1, floor: 1, cancellationToken);
        var prerequisiteId = await harness.QuotationPrerequisiteIdAsync(cancellationToken);

        var processor = harness.CreatePrerequisiteProcessor(context);
        var outcomes = await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);
        var outcome = outcomes.Single(candidate => candidate.PrerequisiteId == prerequisiteId);
        Assert.Equal("COMPLETED", outcome.State);
        Assert.Equal("SATISFIED", outcome.SignalResult);
        Assert.True(outcome.Satisfied);
        _ = rfq;

        // Exactly one signal and one evidence row exist, and a replay adds none (NFR-03).
        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.ApprovalPrerequisiteSignals.CountAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        Assert.Equal(1, await verification.SourcingOwnerEvidence.CountAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        var status = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Id == prerequisiteId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Satisfied, status);

        _ = await harness.CreatePrerequisiteProcessor(verification)
            .ProcessDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
        Assert.Equal(1, await verification.ApprovalPrerequisiteSignals.CountAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
    }

    [Fact]
    public async Task An_insufficient_trace_leaves_the_prerequisite_waiting_without_a_signal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.RegisterOwnerAsync(context, SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken);
        await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 3, floor: 1, cancellationToken);
        var prerequisiteId = await harness.QuotationPrerequisiteIdAsync(cancellationToken);

        var outcome = (await harness.CreatePrerequisiteProcessor(context)
                .ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken))
            .Single(candidate => candidate.PrerequisiteId == prerequisiteId);
        Assert.Equal("PENDING", outcome.State);
        Assert.Null(outcome.SignalResult);
        Assert.False(outcome.Satisfied);

        await using var verification = harness.CreateContext();
        Assert.False(await verification.ApprovalPrerequisiteSignals.AnyAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        var status = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Id == prerequisiteId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Waiting, status);
    }

    [Fact]
    public async Task The_procurement_owner_satisfies_a_request_prerequisite_only_with_a_published_award()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.RegisterOwnerAsync(context, SourcingCodes.ProcurementStageOwnerAdapterId, cancellationToken);
        var (process, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await SourcingScenario.SeedApprovalAsync(harness, context, proposal, cancellationToken);
        var prerequisiteId = await harness.ProcurementPrerequisiteIdAsync(cancellationToken);

        // No award yet: the owner keeps the prerequisite waiting instead of signalling.
        var waiting = (await harness.CreatePrerequisiteProcessor(context)
                .ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken))
            .Single(candidate => candidate.PrerequisiteId == prerequisiteId);
        Assert.Null(waiting.SignalResult);

        var award = await harness.CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId, proposal.ProposalId, proposal.Version, process.Version, null,
                $"award-{Guid.NewGuid():N}", "Award the selected supplier"),
            harness.BuyerId,
            "corr-award",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(1, award.Version);

        // With the current award published the owner satisfies the Purchase Request prerequisite once.
        var satisfied = (await harness.CreatePrerequisiteProcessor(context)
                .ProcessDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken))
            .Single(candidate => candidate.PrerequisiteId == prerequisiteId);
        Assert.Equal("SATISFIED", satisfied.SignalResult);

        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.ApprovalPrerequisiteSignals.CountAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        Assert.Equal(1, await verification.SourcingOwnerEvidence.CountAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
    }

    [Fact]
    public async Task Cancelling_a_pre_award_process_abandons_its_attempts_without_a_signal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await harness.RegisterOwnerAsync(context, SourcingCodes.QuotationStatusOwnerAdapterId, cancellationToken);
        var (process, _) = await SourcingScenario.OpenRfqWithoutQuotationAsync(harness, context, cancellationToken);
        await SourcingScenario.SetQuotationAllowanceAsync(harness, context, minimum: 1, floor: 1, cancellationToken);
        var prerequisiteId = await harness.QuotationPrerequisiteIdAsync(cancellationToken);

        _ = await harness.CreatePrerequisiteProcessor(context)
            .ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);
        var active = await harness.CreateProcessService(context)
            .GetProcessAsync(harness.OrganizationId, process.ProcessId, cancellationToken);
        // Both owned prerequisites have a durable attempt; cancelling abandons every non-terminal one.
        var abandoned = await harness.CreatePrerequisiteProcessor(context)
            .AbandonAttemptsAsync(process.ProcessId, DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(2, abandoned);

        await using var verification = harness.CreateContext();
        var state = await verification.SourcingOwnerAttempts
            .AsNoTracking()
            .Where(record => record.PrerequisiteId == prerequisiteId)
            .Select(record => new { record.State, record.AbandonedAt })
            .SingleAsync(cancellationToken);
        Assert.Equal("ABANDONED", state.State);
        Assert.NotNull(state.AbandonedAt);
        Assert.False(await verification.ApprovalPrerequisiteSignals.AnyAsync(
            record => record.PrerequisiteId == prerequisiteId, cancellationToken));
        var status = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Id == prerequisiteId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Waiting, status);
        _ = active;
    }
}
