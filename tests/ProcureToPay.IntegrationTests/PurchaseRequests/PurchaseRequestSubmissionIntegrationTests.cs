using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.PurchaseRequests;

/// <summary>
/// Durable presentation traversal of SPEC 06 REQ-06/REQ-08 (CA-04, CA-05, CA-06): real Policy
/// provider, real <c>policy-approval-adapter/v2</c>, attempts that survive a partial failure and a
/// revision that supersedes the previous case with retained and added lines.
/// </summary>
public sealed class PurchaseRequestSubmissionIntegrationTests
{
    [Fact]
    public async Task Submit_presents_once_and_resumes_after_a_partial_approval_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(lineCount: 2, cancellationToken);

        // Fault injection after Policy was confirmed: the attempt stays durable and recovers.
        harness.FailNextApproval();
        await Assert.ThrowsAsync<ApprovalDependencyUnavailableException>(() =>
            harness.SubmitAsync(requestId, version, "submit-1", cancellationToken));

        await using (var verification = harness.CreateContext())
        {
            var attempt = await verification.PurchaseRequestSubmissionAttempts.SingleAsync(cancellationToken);
            Assert.Equal((int)PurchaseRequestSubmissionStatus.PolicyConfirmed, attempt.Status);
            Assert.Equal(PurchaseRequestSubmissionCodes.ErrorApprovalDependency, attempt.ErrorCode);
            Assert.NotNull(attempt.PolicyEvaluationBundleId);
            Assert.Null(attempt.ApprovalCaseId);
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Draft, request.Status);
            Assert.Equal(0, await verification.ApprovalCases.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.PolicyEvaluationBundles.CountAsync(cancellationToken));
        }

        var outcome = await harness.SubmitAsync(requestId, version, "submit-1", cancellationToken);
        Assert.False(outcome.Replayed);
        Assert.Equal(PurchaseRequestStatus.InApproval, outcome.Status);
        Assert.NotNull(outcome.ApprovalCaseId);
        Assert.NotNull(outcome.PolicyEvaluationBundleId);

        await using (var verification = harness.CreateContext())
        {
            var attempt = await verification.PurchaseRequestSubmissionAttempts.SingleAsync(cancellationToken);
            Assert.Equal((int)PurchaseRequestSubmissionStatus.ApprovalConfirmed, attempt.Status);
            Assert.Equal(outcome.ApprovalCaseId, attempt.ApprovalCaseId);
            Assert.Equal(2, attempt.AttemptCount);
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.InApproval, request.Status);
            var lifecycle = await verification.PurchaseRequestLifecycleEvents
                .Where(record => record.RequestId == requestId &&
                                 (record.Action == PurchaseRequestSubmissionCodes.ActionSubmitted ||
                                  record.Action == PurchaseRequestSubmissionCodes.ActionInApproval ||
                                  record.Action == PurchaseRequestSubmissionCodes.ActionApproved))
                .OrderBy(record => record.OccurredAt)
                .ThenBy(record => record.Id)
                .Select(record => record.Action)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(
                [PurchaseRequestSubmissionCodes.ActionSubmitted, PurchaseRequestSubmissionCodes.ActionInApproval],
                lifecycle);

            // requester=originator is excluded exactly once and never decides (SPEC 06 REQ-07).
            var approvalCase = await verification.ApprovalCases.SingleAsync(cancellationToken);
            Assert.Equal(harness.RequesterId, approvalCase.OriginatorId);
            Assert.Equal(harness.RequesterId, approvalCase.RequesterId);
            var requirements = await verification.ApprovalRequirements
                .Where(record => record.CaseId == approvalCase.Id)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, requirements.Length);
            foreach (var requirement in requirements)
            {
                Assert.Equal([harness.RequesterId], ApprovalJsonPersistence.DeserializeGuids(
                    requirement.ExcludedUserIdsJson));
                Assert.Contains(
                    ApprovalDecisionAction.RequestChanges,
                    ApprovalJsonPersistence.DeserializeActions(requirement.ActionsJson));
            }
        }

        // The persisted attestation and its minimized references are readable for the owner and an
        // AUDITOR (REQ-10): every assertion carries its owner identity and valid status.
        await using (var verification = harness.CreateContext())
        {
            var attestations = await new PurchaseRequestPersistenceService(
                    verification, NullLogger<PurchaseRequestPersistenceService>.Instance)
                .ReadAttestationsAsync(requestId, harness.OrganizationId, cancellationToken);
            var attestation = Assert.Single(attestations);
            Assert.Equal(version, attestation.Version);
            Assert.True(attestation.Assertions.Count >= 6);
            Assert.All(
                attestation.Assertions,
                assertion =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(assertion.OwnerId));
                    Assert.False(string.IsNullOrWhiteSpace(assertion.OwnerContractVersion));
                    Assert.Equal("ACTIVE", assertion.Status);
                });
        }

        // Identical retry replays the confirmed case without writing a second attempt or case.
        var replay = await harness.SubmitAsync(requestId, version, "submit-1", cancellationToken);
        Assert.True(replay.Replayed);
        Assert.Equal(outcome.ApprovalCaseId, replay.ApprovalCaseId);
        await using (var verification = harness.CreateContext())
        {
            Assert.Equal(1, await verification.PurchaseRequestSubmissionAttempts.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
            Assert.Equal(2, await verification.PurchaseRequestLifecycleEvents.CountAsync(
                record => record.Action == PurchaseRequestSubmissionCodes.ActionSubmitted ||
                          record.Action == PurchaseRequestSubmissionCodes.ActionInApproval,
                cancellationToken));
        }

        // Another payload with the same key is a conflict, and the version is presented once.
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.SubmitAsync(requestId, version, "submit-1", cancellationToken, reason: "Another reason"));
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.SubmitAsync(requestId, version, "submit-other", cancellationToken));
    }

    [Fact]
    public async Task Revision_supersedes_with_retained_added_lines_and_verified_materiality()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(lineCount: 2, cancellationToken);
        var first = await harness.SubmitAsync(requestId, version, "submit-rev-1", cancellationToken);
        await harness.ApproveAllAsync(first.ApprovalCaseId!.Value, cancellationToken);

        var revisedVersion = await harness.ReviseAsync(requestId, version, cancellationToken);
        var second = await harness.SubmitAsync(requestId, revisedVersion, "submit-rev-2", cancellationToken);
        Assert.False(second.Replayed);
        Assert.NotEqual(first.ApprovalCaseId, second.ApprovalCaseId);
        Assert.Equal(PurchaseRequestStatus.InApproval, second.Status);

        await using (var verification = harness.CreateContext())
        {
            var previous = await verification.ApprovalCases.SingleAsync(
                record => record.Id == first.ApprovalCaseId, cancellationToken);
            Assert.Equal((int)ApprovalCaseStatus.Superseded, previous.Status);
            var current = await verification.ApprovalCases.SingleAsync(
                record => record.Id == second.ApprovalCaseId, cancellationToken);
            Assert.Equal(revisedVersion, current.SubjectVersion);

            // Exactly one retained line carries forward; the materially changed line and the added
            // line keep their own tasks (REQ-08, DEC-07).
            var carryForwards = await verification.DecisionCarryForwardEntries
                .Where(record => record.NewCaseId == second.ApprovalCaseId)
                .ToArrayAsync(cancellationToken);
            Assert.Single(carryForwards);
            var derived = await verification.ApprovalDecisions.SingleAsync(
                record => record.CaseId == second.ApprovalCaseId, cancellationToken);
            Assert.Equal((int)ApprovalDecisionOrigin.CarryForward, derived.Origin);
            var pendingTasks = await verification.ApprovalTasks
                .Where(record => record.CaseId == second.ApprovalCaseId)
                .CountAsync(cancellationToken);
            Assert.Equal(1, pendingTasks);

            var supersession = await verification.CaseSupersessions.SingleAsync(
                record => record.NewCaseId == second.ApprovalCaseId, cancellationToken);
            var delta = ApprovalJsonPersistence.DeserializeSupersessionDelta(supersession.TargetMappingJson);
            Assert.Equal(3, delta.Count);
            Assert.Equal(2, delta.Count(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Retained));
            Assert.Equal(1, delta.Count(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Added));
            Assert.All(
                delta.Where(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Retained),
                entry => Assert.Equal(entry.Previous!.Id, entry.Replacement!.Id));
        }

        // Results and lifecycle events of both cases are consumed: the superseded case keeps its
        // history, late events never move the current projection and duplicates are inert (REQ-09).
        var dispatch = await harness.DispatchAsync(cancellationToken);
        Assert.True(dispatch.Delivered >= 2);
        await using (var verification = harness.CreateContext())
        {
            var superseded = await verification.PurchaseRequestApprovalResults.CountAsync(
                record => record.ContractVersion == ApprovalEvolutionCodes.LifecycleContractVersion &&
                          record.Result == "SUPERSEDED",
                cancellationToken);
            Assert.Equal(2, superseded);
            var failures = await verification.ApprovalOutboxEvents
                .AsNoTracking()
                .Where(record => record.State != (int)ApprovalOutboxState.Delivered)
                .Select(record => new { record.ContractVersion, record.LastError })
                .ToArrayAsync(cancellationToken);
            Assert.Empty(failures);
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            // The carried-forward line is approved while the changed and added lines stay pending.
            Assert.Equal((int)PurchaseRequestStatus.PartiallyApproved, request.Status);
        }

        var repeatedDispatch = await harness.DispatchAsync(cancellationToken);
        Assert.Equal(0, repeatedDispatch.Delivered);

        // The attempt of the successor version points at the new case and the request is projected
        // without rewriting the previous version's history.
        await using (var verification = harness.CreateContext())
        {
            var attempt = await verification.PurchaseRequestSubmissionAttempts.SingleAsync(
                record => record.RequestVersion == revisedVersion, cancellationToken);
            Assert.Equal(second.ApprovalCaseId, attempt.ApprovalCaseId);
            var submitted = await verification.PurchaseRequestLifecycleEvents.CountAsync(
                record => record.RequestId == requestId &&
                          record.RequestVersion == revisedVersion &&
                          record.Action == PurchaseRequestSubmissionCodes.ActionSubmitted,
                cancellationToken);
            Assert.Equal(1, submitted);
        }
    }

    [Fact]
    public async Task Approval_results_project_line_status_and_ignore_duplicate_deliveries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(lineCount: 2, cancellationToken);
        var submission = await harness.SubmitAsync(requestId, version, "submit-projection", cancellationToken);
        var caseId = submission.ApprovalCaseId!.Value;

        // One approved obligation of two leaves the request partially approved (REQ-09).
        await harness.ApproveRequirementAsync(caseId, 0, cancellationToken);
        var firstDispatch = await harness.DispatchAsync(cancellationToken);
        Assert.True(firstDispatch.Delivered >= 1);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.PartiallyApproved, request.Status);
            Assert.Equal(1, await verification.PurchaseRequestApprovalResults.CountAsync(cancellationToken));
        }

        // The second approval completes every line and projects APPROVED.
        await harness.ApproveRequirementAsync(caseId, 1, cancellationToken);
        var secondDispatch = await harness.DispatchAsync(cancellationToken);
        Assert.True(secondDispatch.Delivered >= 1);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Approved, request.Status);
            Assert.Equal(2, await verification.PurchaseRequestApprovalResults.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.PurchaseRequestLifecycleEvents.CountAsync(
                record => record.Action == PurchaseRequestSubmissionCodes.ActionApproved, cancellationToken));
        }

        // A redelivery of the same durable payload is inert: the inbox is append-only and deduplicated.
        var redelivery = await harness.RedeliverLastResultAsync(cancellationToken);
        Assert.NotNull(redelivery);
        await using (var verification = harness.CreateContext())
        {
            Assert.Equal(2, await verification.PurchaseRequestApprovalResults.CountAsync(cancellationToken));
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Approved, request.Status);
        }
    }

    [Fact]
    public async Task Concurrent_submissions_and_revisions_produce_exactly_one_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
                var (requestId, version) = await harness.CreateAsync(lineCount: 2, cancellationToken);

        // Two presentations of the same version race: both either confirm or replay the same
        // single case, and no second attempt, case or lifecycle event is written (NFR-03).
        var winner = harness.SubmitAsync(requestId, version, "race-1", cancellationToken);
        var loser = harness.SubmitAsync(requestId, version, "race-2", cancellationToken);
        var outcomes = await Task.WhenAll(CaptureAsync(winner), CaptureAsync(loser));
        var succeeded = outcomes.Where(outcome => outcome.Error is null).Select(outcome => outcome.Value!).ToArray();
        Assert.NotEmpty(succeeded);
        Assert.Single(succeeded.Select(outcome => outcome.ApprovalCaseId).Distinct());
        await using (var verification = harness.CreateContext())
        {
            Assert.Equal(1, await verification.PurchaseRequestSubmissionAttempts.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.PurchaseRequestLifecycleEvents.CountAsync(
                record => record.Action == PurchaseRequestSubmissionCodes.ActionSubmitted, cancellationToken));
        }

        // Two revisions of the same expected version race: exactly one successor is appended and the
        // loser fails closed without leaving a partial version or delta.
        var revisionA = harness.ReviseAsync(requestId, version, "race-revision-a", cancellationToken);
        var revisionB = harness.ReviseAsync(requestId, version, "race-revision-b", cancellationToken);
        var revisions = await Task.WhenAll(CaptureAsync(revisionA), CaptureAsync(revisionB));
        Assert.Single(revisions, revision => revision.Error is null);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == requestId, cancellationToken);
            Assert.Equal(2, request.CurrentVersion);
            Assert.Equal(1, await verification.PurchaseRequestVersions.CountAsync(
                record => record.RequestId == requestId && record.Version == 2, cancellationToken));
            Assert.Equal(1, await verification.PurchaseRequestRevisionDeltas.CountAsync(
                record => record.RequestId == requestId, cancellationToken));
        }
    }

    [Fact]
    public async Task Cancellation_closes_an_open_case_and_keeps_a_completed_case_terminal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);

        // An open case cannot survive a confirmed cancellation (REQ-10).
        var (openRequest, openVersion) = await harness.CreateAsync(lineCount: 2, cancellationToken);
        var openSubmission = await harness.SubmitAsync(openRequest, openVersion, "cancel-open-1", cancellationToken);
        var cancellation = await harness.CancelAsync(openRequest, openVersion, "cancel-open-1", cancellationToken);
        Assert.False(cancellation.Replayed);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == openRequest, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Cancelled, request.Status);
            var approvalCase = await verification.ApprovalCases.SingleAsync(
                record => record.Id == openSubmission.ApprovalCaseId, cancellationToken);
            Assert.Equal((int)ApprovalCaseStatus.Cancelled, approvalCase.Status);
            Assert.Equal(0, await verification.ApprovalTasks.CountAsync(
                record => record.CaseId == approvalCase.Id &&
                          record.Status != (int)ApprovalTaskStatus.Cancelled,
                cancellationToken));
        }

        // A case completed by a decision is terminal and does not block cancelling APPROVED.
        var (approvedRequest, approvedVersion) = await harness.CreateAsync(lineCount: 1, cancellationToken);
        var approvedSubmission = await harness.SubmitAsync(
            approvedRequest, approvedVersion, "cancel-approved-1", cancellationToken);
        await harness.ApproveAllAsync(approvedSubmission.ApprovalCaseId!.Value, cancellationToken);
        var approvedCancellation = await harness.CancelAsync(
            approvedRequest, approvedVersion, "cancel-approved-1", cancellationToken);
        Assert.False(approvedCancellation.Replayed);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == approvedRequest, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Cancelled, request.Status);
            var approvalCase = await verification.ApprovalCases.SingleAsync(
                record => record.Id == approvedSubmission.ApprovalCaseId, cancellationToken);
            Assert.Equal((int)ApprovalCaseStatus.Completed, approvalCase.Status);
        }
    }

    [Fact]
    public async Task Cancellation_retries_once_and_fails_closed_when_the_case_keeps_moving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);

        // One concurrent version advance: the second read cancels the case and the request.
        var (retryRequest, retryVersion) = await harness.CreateAsync(lineCount: 1, cancellationToken);
        var retrySubmission = await harness.SubmitAsync(retryRequest, retryVersion, "race-cancel-1", cancellationToken);
        var caseId = retrySubmission.ApprovalCaseId!.Value;
        var bumped = false;
        harness.CancelRaceHook = id =>
        {
            if (!bumped && id == caseId)
            {
                bumped = true;
                harness.AdvanceCaseVersion(id);
            }
        };
        var cancellation = await harness.CancelAsync(retryRequest, retryVersion, "race-cancel-1", cancellationToken);
        Assert.False(cancellation.Replayed);
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == retryRequest, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.Cancelled, request.Status);
            Assert.Equal((int)ApprovalCaseStatus.Cancelled, await harness.CaseStatusAsync(caseId, cancellationToken));
        }

        // The case keeps moving on every read: the cancellation fails closed and leaves no half
        // state (REQ-10, NFR-03).
        harness.CancelRaceHook = null;
        var (openRequest, openVersion) = await harness.CreateAsync(lineCount: 1, cancellationToken);
        var openSubmission = await harness.SubmitAsync(openRequest, openVersion, "race-cancel-2", cancellationToken);
        var openCaseId = openSubmission.ApprovalCaseId!.Value;
        harness.CancelRaceHook = id =>
        {
            if (id == openCaseId)
            {
                harness.AdvanceCaseVersion(id);
            }
        };
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.CancelAsync(openRequest, openVersion, "race-cancel-2", cancellationToken));
        harness.CancelRaceHook = null;
        await using (var verification = harness.CreateContext())
        {
            var request = await verification.PurchaseRequests.SingleAsync(
                record => record.Id == openRequest, cancellationToken);
            Assert.Equal((int)PurchaseRequestStatus.InApproval, request.Status);
            Assert.Equal((int)ApprovalCaseStatus.Open, await harness.CaseStatusAsync(openCaseId, cancellationToken));
            Assert.Equal(0, await verification.PurchaseRequestCommands.CountAsync(
                record => record.RequestId == openRequest &&
                          record.CommandType == PurchaseRequestPersistenceService.CommandCancel,
                cancellationToken));
        }
    }

    private static async Task<(T? Value, Exception? Error)> CaptureAsync<T>(Task<T> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception exception)
        {
            return (default, exception);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private string connectionString = string.Empty;
        private readonly Dictionary<(string, PurchaseRequestReferenceType?), ControlledOwner> owners = [];
        private readonly FaultState fault = new();
        private PolicySetVersion policy = null!;
        private DateTimeOffset activationAt;

        public Guid OrganizationId { get; private set; }
        public Guid LegalEntityId { get; private set; }
        public Guid DepartmentId { get; private set; }
        public Guid RequesterId { get; private set; }
        public Guid ApproverId { get; private set; }
        public Guid RequestedForId { get; private set; }

        public static async Task<Harness> StartAsync(CancellationToken cancellationToken)
        {
            var harness = new Harness
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await harness.container.StartAsync(cancellationToken);
            harness.connectionString = harness.container.GetConnectionString();
            await using var context = harness.CreateContext();
            await context.Database.MigrateAsync(cancellationToken);
            harness.Seed(context);
            await context.SaveChangesAsync(cancellationToken);
            await harness.ActivatePolicyAsync(cancellationToken);
            return harness;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(connectionString)
                .Options);

        public void FailNextApproval() => fault.FailOnce = true;

        /// <summary>Fault injection for the cancellation race (R7): applied to every service set.</summary>
        public Action<Guid>? CancelRaceHook { get; set; }

        public void AdvanceCaseVersion(Guid caseId)
        {
            using var context = CreateContext();
            context.ApprovalCases
                .Where(record => record.Id == caseId)
                .ExecuteUpdate(setters => setters.SetProperty(
                    record => record.Version, record => record.Version + 1));
        }

        public async Task<int> CaseStatusAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalCases
                .Where(record => record.Id == caseId)
                .Select(record => record.Status)
                .SingleAsync(cancellationToken);
        }

        public async Task<(Guid RequestId, int Version)> CreateAsync(int lineCount, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var persistence = new PurchaseRequestPersistenceService(
                context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            var drafts = Enumerable.Range(0, lineCount)
                .Select(index => new PurchaseRequestLineDraft(
                    $"line-{index}", LineContent(index == 0 ? 100m : 300m)))
                .ToArray();
            var creation = await persistence.CreateAsync(
                new PurchaseRequestCreateCommand(
                    OrganizationId,
                    RequesterId,
                    new VersionedEntityRef("LEGAL_ENTITY", LegalEntityId, 1),
                    "Business justification",
                    $"create-{Guid.NewGuid():N}",
                    "Initial request",
                    drafts),
                RequesterId,
                "corr-create",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return (creation.RequestId, creation.Version);
        }

        /// <summary>Retains the first line, changes the second one and adds a third (REQ-02, REQ-08).</summary>
        public Task<int> ReviseAsync(
            Guid requestId,
            int expectedVersion,
            string revisionKey,
            CancellationToken cancellationToken) =>
            ReviseAsync(requestId, expectedVersion, cancellationToken, revisionKey);

        public async Task<int> ReviseAsync(
            Guid requestId,
            int expectedVersion,
            CancellationToken cancellationToken,
            string? revisionKey = null)
        {
            await using var context = CreateContext();
            var persistence = new PurchaseRequestPersistenceService(
                context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            var view = await persistence.ReadAsync(requestId, OrganizationId, cancellationToken)
                ?? throw new InvalidOperationException("The purchase request is not visible.");
            var current = view.Versions.Single(version => version.Version == expectedVersion);
            // The reader orders lines by id: select them by their material amount, not by position.
            var retained = current.Lines.Single(line => line.Content.BaseAmount == 100m);
            var changed = current.Lines.Single(line => line.Content.BaseAmount == 300m);
            var revision = await persistence.ReviseAsync(
                new PurchaseRequestRevisionCommand(
                    requestId,
                    expectedVersion,
                    "Justification v2",
                    $"revision-{(revisionKey ?? Guid.NewGuid().ToString("N"))}",
                    "Scope change",
                    [new PurchaseRequestLineRef(retained.LineId, retained.LineVersion, retained.ContentDigest)],
                    [new PurchaseRequestLineChange(
                        changed.LineId,
                        changed.LineVersion,
                        changed.ContentDigest,
                        LineContent(250m))],
                    [new PurchaseRequestLineDraft("line-new", LineContent(300m))],
                    []),
                OrganizationId,
                RequesterId,
                "corr-revision",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return revision.Version;
        }

        public async Task<PurchaseRequestSubmissionOutcome> SubmitAsync(
            Guid requestId,
            int version,
            string submissionKey,
            CancellationToken cancellationToken,
            string reason = "Present to approval")
        {
            if (DateTimeOffset.UtcNow < activationAt)
            {
                await Task.Delay(
                    activationAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            return await CreateServices().Submission.SubmitAsync(
                new PurchaseRequestSubmissionCommand(
                    requestId,
                    version,
                    submissionKey,
                    reason,
                    OrganizationId,
                    RequesterId,
                    "corr-submit"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        public async Task ApproveAllAsync(Guid caseId, CancellationToken cancellationToken)
        {
            var count = await CountRequirementsAsync(caseId, cancellationToken);
            for (var index = 0; index < count; index++)
            {
                await ApproveRequirementAsync(caseId, index, cancellationToken);
            }
        }

        public async Task ApproveRequirementAsync(Guid caseId, int index, CancellationToken cancellationToken)
        {
            var services = CreateServices();
            Guid taskId;
            int taskVersion;
            await using (var context = CreateContext())
            {
                var requirementId = await context.ApprovalRequirements
                    .Where(record => record.CaseId == caseId)
                    .OrderBy(record => record.SourceRequirementKey)
                    .Select(record => record.Id)
                    .Skip(index)
                    .FirstAsync(cancellationToken);
                var task = await context.ApprovalTasks
                    .SingleAsync(record => record.RequirementId == requirementId, cancellationToken);
                taskId = task.Id;
                taskVersion = task.Version;
            }

            await services.Decisions.DecideAsync(
                new ApprovalDecisionCommand(
                    taskId,
                    ApprovalDecisionAction.Approve,
                    "Approved",
                    $"decision-{taskId:N}",
                    taskVersion,
                    ApproverId,
                    "corr-decision"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        public async Task<PurchaseRequestCancellation> CancelAsync(
            Guid requestId,
            int version,
            string cancelKey,
            CancellationToken cancellationToken) =>
            await CreateServices().Submission.CancelAsync(
                requestId,
                version,
                cancelKey,
                "No longer needed",
                OrganizationId,
                RequesterId,
                "corr-cancel",
                DateTimeOffset.UtcNow,
                cancellationToken);

        public async Task<ApprovalDispatchOutcome> DispatchAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var dispatcher = new ApprovalOutboxDispatcher(
                context,
                new ApprovalResultConsumerRegistry(
                [
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new BudgetReleaseService(
                            context,
                            new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                            new BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        "approval-result/v2"),
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new BudgetReleaseService(
                            context,
                            new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                            new BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        "approval-result/v3"),
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new BudgetReleaseService(
                            context,
                            new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                            new BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.LifecycleContractVersion)
                ]),
                NullLogger<ApprovalOutboxDispatcher>.Instance);
            return await dispatcher.DispatchAsync(
                OrganizationId, "integration-dispatcher", DateTimeOffset.UtcNow.AddSeconds(1), 100, cancellationToken);
        }

        /// <summary>At-least-once redelivery of the last consumed v2 result (REQ-09).</summary>
        public async Task<string?> RedeliverLastResultAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var delivery = await context.ApprovalOutboxEvents
                .AsNoTracking()
                .Where(record => record.ContractVersion == ApprovalOutboxPolicy.ContractVersion &&
                                 record.State != (int)ApprovalOutboxState.Pending)
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.Id)
                .Select(record => new { record.Id, record.PayloadJson, record.CorrelationReference })
                .FirstOrDefaultAsync(cancellationToken);
            if (delivery is null)
            {
                return null;
            }

            await new PurchaseRequestApprovalResultConsumer(
                    context,
                    new BudgetReleaseService(
                        context,
                        new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                        new BudgetPersistenceService(context)),
                    NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                    ApprovalOutboxPolicy.ContractVersion)
                .DeliverAsync(
                    new ApprovalResultDelivery(
                        delivery.Id, ApprovalOutboxPolicy.ContractVersion, delivery.PayloadJson,
                        delivery.CorrelationReference),
                    cancellationToken);
            return delivery.PayloadJson;
        }

        private async Task<int> CountRequirementsAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalRequirements.CountAsync(
                record => record.CaseId == caseId, cancellationToken);
        }

        private Services CreateServices()
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Policy:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Policy:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId,
                    ["Approval:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId
                })
                .Build();
            var context = CreateContext();
            var persistence = new PurchaseRequestPersistenceService(
                context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            var attestation = new PurchaseRequestAttestationService(
                context,
                new PurchaseRequestReferenceOwnerRegistry(owners.Values),
                NoApprovedSupplierCatalog.Instance,
                persistence,
                NullLogger<PurchaseRequestAttestationService>.Instance);
            var provider = new PurchaseRequestPolicyFactProvider(
                context, persistence, NullLogger<PurchaseRequestPolicyFactProvider>.Instance);
            var policyEvaluations = new PolicyEvaluationService(
                new PolicyPersistenceService(context),
                new PolicyWorkloadAllowlist(configuration),
                new PolicyFactProviderRegistry([provider]),
                new PolicyExceptionVerifierRegistry([]),
                new PolicyReferenceCatalogRegistry(
                [
                    new AcceptAllCatalog("DEPARTMENT"),
                    new AcceptAllCatalog("LEGAL_ENTITY"),
                    new AcceptAllCatalog("COST_CENTER"),
                    new SpendCategoryCatalog()
                ]),
                NullLogger<PolicyEvaluationService>.Instance);
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            // SPEC 09 REQ-10: new submissions use the v4 contract, registered beside the historical
            // v2/v3 adapters that stay resolvable for an attempt already persisted.
            var registry = new ApprovalSubmissionAdapterRegistry(
            [
                new FaultInjectingAdapter(new PolicyApprovalAdapter(context), fault),
                new FaultInjectingAdapter(
                    new PolicyApprovalAdapter(
                        context,
                        PolicyApprovalAdapter.ContractVersionV3,
                        new PurchaseRequestBudgetDemandBuilder(context)),
                    fault),
                new FaultInjectingAdapter(
                    new PolicyApprovalAdapter(
                        context,
                        PolicyApprovalAdapter.ContractVersionV4,
                        new PurchaseRequestBudgetDemandBuilder(context)),
                    fault)
            ]);
            var assignmentEngine = new ApprovalAssignmentEngine(
                context,
                new OrganizationEligibilityService(context),
                new ApprovalScopeResolver(context));
            var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
            var submissions = new ApprovalSubmissionService(
                context, registry, ownerWorkloads, allowlist, assignmentEngine,
                loggerFactory.CreateLogger<ApprovalSubmissionService>());
            var supersessions = new ApprovalSupersessionService(
                context, allowlist, registry, ownerWorkloads, assignmentEngine,
                loggerFactory.CreateLogger<ApprovalSupersessionService>());
            var workflow = new ApprovalWorkflowService(
                context, allowlist, assignmentEngine, loggerFactory.CreateLogger<ApprovalWorkflowService>());
            var submissionService = new PurchaseRequestSubmissionService(
                    context,
                    persistence,
                    attestation,
                    policyEvaluations,
                    submissions,
                    supersessions,
                    workflow,
                    new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrecheckService(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.BudgetDemandBuilderRegistry(
                            [new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestBudgetDemandBuilder(context)]),
                        new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        attestation),
                    new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                    NullLogger<PurchaseRequestSubmissionService>.Instance)
            {
                BeforeApprovalCaseCancel = CancelRaceHook
            };
            return new Services(
                submissionService,
                new ApprovalDecisionService(
                    context, assignmentEngine, loggerFactory.CreateLogger<ApprovalDecisionService>()));
        }

        private void Seed(ProcureToPayDbContext context)
        {
            OrganizationId = Guid.NewGuid();
            LegalEntityId = Guid.NewGuid();
            DepartmentId = Guid.NewGuid();
            RequesterId = Guid.NewGuid();
            ApproverId = Guid.NewGuid();
            RequestedForId = Guid.NewGuid();
            context.Organizations.Add(new OrganizationRecord
            {
                Id = OrganizationId,
                Code = "PR-SUBMIT",
                Name = "Purchase Request Submission",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1
            });
            context.LegalEntities.Add(new LegalEntityRecord
            {
                Id = LegalEntityId,
                OrganizationId = OrganizationId,
                Code = "COMPANY-PE",
                Name = "Company Peru",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            context.Departments.Add(new DepartmentRecord
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Code = "IT",
                Name = "Information Technology",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            context.UserProfiles.AddRange(
                new UserProfileRecord
                {
                    Id = RequesterId,
                    OrganizationId = OrganizationId,
                    Issuer = "https://issuer.test",
                    Subject = "requester",
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                },
                new UserProfileRecord
                {
                    Id = ApproverId,
                    OrganizationId = OrganizationId,
                    Issuer = "https://issuer.test",
                    Subject = "approver",
                    DepartmentId = DepartmentId,
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                },
                new UserProfileRecord
                {
                    Id = RequestedForId,
                    OrganizationId = OrganizationId,
                    Issuer = "https://issuer.test",
                    Subject = "requested-for",
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = ApproverId,
                Role = (int)SystemRole.ItReviewer,
                ScopeJson = "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ApproverId,
                Version = 1
            });
            RegisterOwners();
        }

        private void RegisterOwners()
        {
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.Department)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.Department,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.LegalEntity)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.LegalEntity,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.User)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.User,
                "organization-domain",
                "organization-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.CostCenter)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.CostCenter,
                "cost-center-domain",
                "cost-center-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.CostCenterOwnedByDepartment),
                PurchaseRequestReferenceType.CostCenter)] = new ControlledOwner(
                PurchaseRequestAssertionType.CostCenterOwnedByDepartment,
                PurchaseRequestReferenceType.CostCenter,
                "cost-center-domain",
                "cost-center-db/v1");
            owners[(PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                PurchaseRequestReferenceType.SpendCategory)] = new ControlledOwner(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestReferenceType.SpendCategory,
                "spend-category-domain",
                "spend-category-db/v1");
        }

        private async Task ActivatePolicyAsync(CancellationToken cancellationToken)
        {
            policy = new PolicySetVersion(
                Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
            var decisionScope = DecisionScopeDescriptor.Create(
                    OrganizationId, [new DecisionScopeEntry(ScopeDimension.Department, DepartmentId, 1)])
                .ToCanonicalJson();
            policy.AddRule(new PolicyRule(
                "LINE_APPROVAL_LOW",
                PolicyScope.Line,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.LessThanOrEqual, PolicyValue.Money(200, "PEN"))],
                [
                    new PolicyEffect(
                        PolicyEffectType.RequireApproval,
                        "DEPARTMENT_APPROVAL_LOW",
                        approval: new PolicyApprovalDescriptor(
                            SystemRole.ItReviewer,
                            authorityType: null,
                            authorityLevel: null,
                            amountBase: null,
                            baseCurrency: null,
                            decisionScope))
                ]));
            policy.AddRule(new PolicyRule(
                "LINE_APPROVAL_HIGH",
                PolicyScope.Line,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(200, "PEN"))],
                [
                    new PolicyEffect(
                        PolicyEffectType.RequireApproval,
                        "DEPARTMENT_APPROVAL_HIGH",
                        approval: new PolicyApprovalDescriptor(
                            SystemRole.ItReviewer,
                            authorityType: null,
                            authorityLevel: null,
                            amountBase: null,
                            baseCurrency: null,
                            decisionScope))
                ]));
            policy.AddRule(new PolicyRule(
                "LINE_ALLOW",
                PolicyScope.Line,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
                isFallback: true));
            policy.AddRule(new PolicyRule(
                "REQUEST_ALLOW",
                PolicyScope.Request,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_ALLOW")],
                isFallback: true));
            policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
            var persistence = new PolicyPersistenceService(CreateContext());
            var actor = new PolicyActor("USER", Guid.NewGuid());
            var appended = await persistence.AppendVersionAsync(
                policy,
                DateTimeOffset.UtcNow,
                actor,
                "Policy for purchase request submission integration",
                "corr-policy-append",
                cancellationToken);
            activationAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await persistence.ActivateAsync(
                OrganizationId,
                appended.Id,
                activationAt,
                actor,
                "Activate policy for purchase request submission integration",
                "corr-policy-activate",
                cancellationToken);
        }

        private PurchaseRequestLineContent LineContent(decimal amount) =>
            new(
                amount,
                "PEN",
                amount,
                "PEN",
                2026,
                "GOOD",
                new VersionedCodeRef("SPEND_CATEGORY", "HARDWARE", 1, new string('d', 64)),
                new VersionedEntityRef("COST_CENTER", Guid.Parse("99999999-9999-9999-9999-999999999999"), 1),
                new VersionedEntityRef("DEPARTMENT", DepartmentId, 1),
                new VersionedEntityRef("DEPARTMENT", DepartmentId, 1),
                new VersionedEntityRef("USER", RequestedForId, 1),
                null,
                null,
                null,
                false,
                false,
                "NONE",
                "Need for the submission test",
                [],
                null);

        public async ValueTask DisposeAsync()
        {
            await container.DisposeAsync();
        }

        private sealed record Services(
            PurchaseRequestSubmissionService Submission,
            ApprovalDecisionService Decisions);

        /// <summary>Delegates to the real adapter v2 and can fail the first approval call (CA-08).</summary>
        private sealed class FaultState
        {
            public bool FailOnce { get; set; }
        }

        private sealed class FaultInjectingAdapter(
            PolicyApprovalAdapter inner,
            FaultState state) : IApprovalSubmissionAdapter
        {
            public ApprovalAdapterDescriptor Descriptor => inner.Descriptor;

            public Task<ApprovalSubmission> BuildAsync(
                ApprovalSubmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (state.FailOnce)
                {
                    state.FailOnce = false;
                    throw new ApprovalDependencyUnavailableException("Injected approval failure.");
                }

                return inner.BuildAsync(request, cancellationToken);
            }
        }

        private sealed class AcceptAllCatalog(string catalogId) : IPolicyReferenceCatalog
        {
            public string CatalogId => catalogId;

            public string ContractVersion => "test/v1";

            public Task<bool> ExistsAsync(
                PolicyReferenceLookup reference,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        private sealed class SpendCategoryCatalog : IPolicyReferenceCatalog
        {
            public string CatalogId => "SPEND_CATEGORY";

            public string ContractVersion => "test/v1";

            public Task<bool> ExistsAsync(
                PolicyReferenceLookup reference,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        private sealed class ControlledOwner(
            PurchaseRequestAssertionType assertionType,
            PurchaseRequestReferenceType referenceType,
            string ownerId,
            string contractVersion) : IPurchaseRequestReferenceOwner
        {
            public string AssertionType => PurchaseRequestCodes.Code(assertionType);

            public PurchaseRequestReferenceType ReferenceType => referenceType;

            public string OwnerId => ownerId;

            public string ContractVersion => contractVersion;

            public Task<PurchaseRequestVerificationResponse> VerifyAsync(
                PurchaseRequestVerificationRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PurchaseRequestVerificationResponse(
                    true,
                    request.AssertionType,
                    request.OrganizationId,
                    ContractVersion,
                    OwnerId,
                    request.SourceRef,
                    PurchaseRequestAssertionCodes.StatusActive,
                    request.TargetRef,
                    request.VerifiedAt));
        }
    }
}
