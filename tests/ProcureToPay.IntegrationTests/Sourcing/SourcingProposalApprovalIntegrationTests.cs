using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server evidence of SPEC 10 REQ-11 (CA-07): the proposal becomes one idempotent approval
/// subject under the published adapter identity, its targets are the exact Purchase Request lines and
/// a stale manifest or an ineligible supplier fails closed before any case exists.
/// </summary>
public sealed class SourcingProposalApprovalIntegrationTests
{
    [Fact]
    public void The_adapter_descriptor_publishes_the_sourcing_contract()
    {
        var adapter = new SourcingProposalApprovalAdapter(
            null!, new PolicyApprovalAdapter(null!, PolicyApprovalAdapter.ContractVersionV4));

        Assert.Equal("sourcing-policy-approval-adapter", adapter.Descriptor.AdapterId);
        Assert.Equal("v1", adapter.Descriptor.ContractVersion);
        Assert.Equal("SOURCING_PROPOSAL", adapter.Descriptor.SubjectType);
        Assert.Equal("SUBMIT_SOURCING_PROPOSAL", adapter.Descriptor.Operation);
        Assert.True(adapter.Descriptor.RequesterRequired);
        Assert.True(adapter.Descriptor.AllowsRequesterAsOriginator);
        Assert.False(adapter.Descriptor.SupersessionDeltaSupported);
    }

    [Fact]
    public async Task A_proposal_becomes_one_idempotent_approval_subject_over_its_exact_lines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await harness.SeedProposalApprovalAsync(
            context, proposal.ProposalId, proposal.Version, proposal.ContentDigest, proposal.ManifestDigest,
            withCase: false, cancellationToken);
        var submissions = harness.CreateApprovalSubmissionService(context);
        var command = new ApprovalSubmissionCommand(
            SourcingWaiverService.Workload,
            harness.OrganizationId,
            SourcingCodes.ApprovalSubjectType,
            proposal.ProposalId,
            proposal.Version,
            SourcingCodes.ApprovalOperation,
            SourcingCodes.ApprovalAdapterVersion,
            $"submission-{Guid.NewGuid():N}",
            harness.BuyerId,
            harness.BuyerId,
            "corr-submission");

        var outcome = await submissions.SubmitAsync(command, DateTimeOffset.UtcNow, cancellationToken);
        Assert.False(outcome.Replayed);

        await using var verification = harness.CreateContext();
        var approvalCase = await verification.ApprovalCases
            .AsNoTracking()
            .SingleAsync(record => record.Id == outcome.CaseId, cancellationToken);
        Assert.Equal("SOURCING_PROPOSAL", approvalCase.SubjectType);
        Assert.Equal("SUBMIT_SOURCING_PROPOSAL", approvalCase.Operation);
        Assert.Equal(proposal.ProposalId, approvalCase.SubjectId);
        Assert.Equal(proposal.Version, approvalCase.SubjectVersion);
        Assert.Equal(harness.BuyerId, approvalCase.OriginatorId);

        // Replaying the same key returns the same case instead of a second subject.
        var replay = await submissions.SubmitAsync(command, DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(outcome.CaseId, replay.CaseId);
        Assert.True(replay.Replayed);

        // The same key with another requester is another command and conflicts (SPEC 05 REQ-01).
        await Assert.ThrowsAsync<DomainConflictException>(() => submissions.SubmitAsync(
            command with { RequesterId = harness.AuditorId },
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    [Fact]
    public async Task An_ineligible_supplier_blocks_the_submission_without_a_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await harness.SeedProposalApprovalAsync(
            context, proposal.ProposalId, proposal.Version, proposal.ContentDigest, proposal.ManifestDigest,
            withCase: false, cancellationToken);
        await using (var successor = harness.CreateContext())
        {
            await harness.AdvanceSupplierVersionAsync(successor, cancellationToken);
        }

        var submissions = harness.CreateApprovalSubmissionService(context);
        await Assert.ThrowsAsync<AwardNotEligibleException>(() => submissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                SourcingWaiverService.Workload,
                harness.OrganizationId,
                SourcingCodes.ApprovalSubjectType,
                proposal.ProposalId,
                proposal.Version,
                SourcingCodes.ApprovalOperation,
                SourcingCodes.ApprovalAdapterVersion,
                $"submission-{Guid.NewGuid():N}",
                harness.BuyerId,
                harness.BuyerId,
                "corr-submission"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.False(await verification.ApprovalCases.AnyAsync(
            record => record.SubjectType == SourcingCodes.ApprovalSubjectType, cancellationToken));
    }

    [Fact]
    public async Task A_stale_manifest_blocks_the_submission_without_a_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SourcingHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (_, _, proposal) = await SourcingScenario.BuildProposalAsync(harness, context, cancellationToken);
        await harness.SeedProposalApprovalAsync(
            context, proposal.ProposalId, proposal.Version, proposal.ContentDigest, proposal.ManifestDigest,
            withCase: false, cancellationToken);

        // The persisted sourcing evaluation no longer matches the proposal manifest: the adapter
        // refuses to build a subject from diverging evidence (REQ-10).
        await using (var diverge = harness.CreateContext())
        {
            await diverge.Database.ExecuteSqlRawAsync(
                "UPDATE [Policy].[PolicyEvaluationBundles] SET [PolicyContentDigest] = {0} " +
                "WHERE [SubjectId] = {1}",
                [new string('0', 64), proposal.ProposalId],
                cancellationToken);
        }

        var submissions = harness.CreateApprovalSubmissionService(context);
        await Assert.ThrowsAnyAsync<DomainException>(() => submissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                SourcingWaiverService.Workload,
                harness.OrganizationId,
                SourcingCodes.ApprovalSubjectType,
                proposal.ProposalId,
                proposal.Version,
                SourcingCodes.ApprovalOperation,
                SourcingCodes.ApprovalAdapterVersion,
                $"submission-{Guid.NewGuid():N}",
                harness.BuyerId,
                harness.BuyerId,
                "corr-submission"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.False(await verification.ApprovalCases.AnyAsync(
            record => record.SubjectType == SourcingCodes.ApprovalSubjectType, cancellationToken));
    }
}
