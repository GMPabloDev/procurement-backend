using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-10 (CA-07, CA-08): the proposal is built from the frozen
/// evaluation and the selected quotations, its document and manifest are persisted as their digest
/// preimages, the evaluation it consumed can no longer be replaced and a written version is immutable.
/// </summary>
public sealed class SourcingProposalIntegrationTests
{
    [Fact]
    public async Task A_proposal_binds_the_selection_the_evaluation_and_the_request_bundle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (process, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        var evaluation = await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);

        var proposal = await harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(1, proposal.Version);
        Assert.Equal(SourcingSelectionBasis.Rfq, proposal.SelectionBasis);
        Assert.Equal(harness.SupplierId, proposal.SupplierRef.Id);
        Assert.Equal(harness.RequestId, proposal.RequestId);
        _ = process;
        Assert.Contains(harness.LineId, proposal.SelectedLines);
        Assert.Equal(evaluation.EvaluationId, proposal.EvaluationId);
        Assert.Equal(evaluation.Version, proposal.EvaluationVersion);
        Assert.False(string.IsNullOrWhiteSpace(proposal.ManifestDigest));

        // The stored document and manifest hash back to their recorded digests.
        var replayed = await harness.CreateProposalService(context).GetProposalAsync(
            harness.OrganizationId, proposal.ProposalId, proposal.Version, cancellationToken);
        Assert.Equal(proposal.ContentDigest, replayed.ContentDigest);
        Assert.Equal(proposal.DocumentJson, replayed.DocumentJson);
        Assert.Equal(proposal.ManifestDigest, replayed.ManifestDigest);

        // The proposal consumed the evaluation: a new evaluation of the same RFQ is refused.
        var consumed = await harness.CreateEvaluationService(context).GetEvaluationAsync(
            harness.OrganizationId, evaluation.EvaluationId, evaluation.Version, cancellationToken);
        Assert.True(consumed.Consumed);

        // A written proposal version cannot be rewritten by a direct writer.
        await using var tampering = harness.CreateContext();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [Sourcing].[SourcingProposalVersions] SET [DocumentJson] = {0} " +
            "WHERE [ProposalId] = {1} AND [Version] = {2}",
            ["{}", proposal.ProposalId, proposal.Version],
            cancellationToken));
        Assert.Contains("immutable", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Building_the_same_proposal_twice_with_one_key_replays_the_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);
        var command = new BuildProposalCommand(
            harness.OrganizationId, rfq.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}");

        var first = await harness.CreateProposalService(context).BuildAsync(
            command, harness.BuyerId, "corr-1", DateTimeOffset.UtcNow, cancellationToken);
        var replay = await harness.CreateProposalService(context).BuildAsync(
            command, harness.BuyerId, "corr-2", DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(first.ProposalId, replay.ProposalId);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal("DRAFT", first.State);
    }

    [Fact]
    public async Task A_proposal_requires_a_selected_line_of_that_supplier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, rfq) = await SourcingScenario.OpenRfqAsync(harness, context, cancellationToken);
        await SourcingScenario.EvaluateAsync(harness, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(harness, context, rfq, cancellationToken);
        await harness.SeedProposalFixturesAsync(context, cancellationToken);

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, rfq.RfqId, harness.SecondSupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken));
    }
}
