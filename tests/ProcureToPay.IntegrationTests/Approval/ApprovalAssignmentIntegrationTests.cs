using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Approval;

/// <summary>
/// SQL Server evidence for deterministic routing and reconciliation (REQ-04, REQ-05, NFR-01,
/// NFR-03, CA-04, CA-05): lowest-load selection across cases, no fallback without a candidate,
/// one current assignment per task enforced by the schema, and reassignment after a revoke.
/// </summary>
public sealed class ApprovalAssignmentIntegrationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DepartmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    // Canonical ascending order of the approver ids: aaaa… < bbbb… < cccc…
    private static readonly Guid FirstApprover = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecondApprover = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ThirdApprover = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OriginatorId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTimeOffset AssignedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ApprovalWorkloadIdentity Workload = new("internal://procure-to-pay", "adapter");

    [Fact]
    public async Task Assignment_routes_to_the_lowest_load_and_breaks_ties_by_canonical_uuid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        await environment.SeedApproverAsync(SecondApprover, "approver-b", cancellationToken);
        await environment.SeedApproverAsync(ThirdApprover, "approver-c", cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));

        // All loads are 0, so the canonical ascending UUID decides: aaaa… wins.
        var first = await services.Submission.SubmitAsync(
            Command("submission-load-1"), DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal("OPEN", first.Status);

        // Loads are now aaaa…=1, bbbb…=0, cccc…=0: the lowest load wins and the tie between
        // bbbb… and cccc… is broken by the canonical UUID.
        var second = await services.Submission.SubmitAsync(
            Command("submission-load-2"), DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal("OPEN", second.Status);

        await using var verification = environment.CreateContext();
        var firstAssignee = await AssigneeOfAsync(verification, first.CaseId, cancellationToken);
        var secondAssignee = await AssigneeOfAsync(verification, second.CaseId, cancellationToken);
        Assert.Equal(FirstApprover, firstAssignee);
        Assert.Equal(SecondApprover, secondAssignee);

        // Exactly one current assignment per task, and the observed load is recorded.
        var assignments = await verification.ApprovalAssignments
            .Where(record => record.CaseId == second.CaseId)
            .ToArrayAsync(cancellationToken);
        var current = Assert.Single(assignments);
        Assert.Equal(SecondApprover, current.AssigneeUserId);
        Assert.Equal(0, current.Load);
        Assert.Null(current.ReleasedAt);
        Assert.Equal(ApprovalAssignmentCause.Initial.ToString(), current.Cause);
        Assert.NotEmpty(current.EligibilityEvidenceJson);
    }

    [Fact]
    public async Task Assignment_has_no_fallback_when_nobody_is_eligible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));

        var outcome = await services.Submission.SubmitAsync(
            Command("submission-no-candidate"), DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal("BLOCKED", outcome.Status);
        await using var verification = environment.CreateContext();
        var task = await verification.ApprovalTasks.SingleAsync(cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Unassigned, task.Status);
        Assert.Null(task.CurrentAssigneeUserId);
        Assert.Equal(
            (int)ApprovalRequirementStatus.Unassigned,
            (await verification.ApprovalRequirements.SingleAsync(cancellationToken)).Status);
        Assert.Empty(await verification.ApprovalAssignments.ToArrayAsync(cancellationToken));
        // The absence of a candidate is durable state (UNASSIGNED/BLOCKED) and never fabricates an
        // assignment or a result event (REQ-04, REQ-08).
    }

    [Fact]
    public async Task Exactly_one_current_assignment_per_task_is_enforced_by_the_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));
        var outcome = await services.Submission.SubmitAsync(
            Command("submission-current"), DateTimeOffset.UtcNow, cancellationToken);

        Guid taskId;
        await using (var context = environment.CreateContext())
        {
            taskId = (await context.ApprovalTasks.SingleAsync(cancellationToken)).Id;
            context.ApprovalAssignments.Add(CurrentAssignment(outcome.CaseId, taskId, FirstApprover));
            await context.SaveChangesAsync(cancellationToken);
        }

        // A second concurrent writer cannot leave two current assignments for the same task.
        await using (var racing = environment.CreateContext())
        {
            racing.ApprovalAssignments.Add(CurrentAssignment(outcome.CaseId, taskId, SecondApprover));
            await Assert.ThrowsAsync<DbUpdateException>(() => racing.SaveChangesAsync(cancellationToken));
        }

        // Releasing the current assignment keeps the history append-only and frees the slot.
        await using (var releasing = environment.CreateContext())
        {
            var current = await releasing.ApprovalAssignments.SingleAsync(cancellationToken);
            current.ReleasedAt = AssignedAt.AddDays(1);
            await releasing.SaveChangesAsync(cancellationToken);
        }

        await using (var successor = environment.CreateContext())
        {
            successor.ApprovalAssignments.Add(CurrentAssignment(outcome.CaseId, taskId, SecondApprover));
            await successor.SaveChangesAsync(cancellationToken);
        }

        await using var verification = environment.CreateContext();
        var history = await verification.ApprovalAssignments
            .OrderBy(record => record.AssignedAt)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, history.Length);
        Assert.Single(history, record => record.ReleasedAt is null);
        Assert.Equal(SecondApprover, history.Single(record => record.ReleasedAt is null).AssigneeUserId);
    }

    [Fact]
    public async Task Reconciliation_reassigns_after_a_revoke_and_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        await environment.SeedApproverAsync(SecondApprover, "approver-b", cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));

        var outcome = await services.Submission.SubmitAsync(
            Command("submission-revoke"), DateTimeOffset.UtcNow, cancellationToken);
        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(FirstApprover, await AssigneeOfAsync(verification, outcome.CaseId, cancellationToken));
        }

        // Confirmed authority change: the current assignee loses the role.
        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(
                record => record.UserProfileId == FirstApprover, cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            assignment.RevokedBy = AdminId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var reconciled = await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-revoke-1", "integration-test", "correlation-reconcile",
            DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(1, reconciled.Scanned);
        Assert.Equal(1, reconciled.Reassigned);
        Assert.Equal(0, reconciled.Unassigned);

        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(SecondApprover, await AssigneeOfAsync(verification, outcome.CaseId, cancellationToken));
            var history = await verification.ApprovalAssignments
                .Where(record => record.CaseId == outcome.CaseId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, history.Length);
            var current = Assert.Single(history, record => record.ReleasedAt is null);
            Assert.Equal(SecondApprover, current.AssigneeUserId);
            Assert.Equal(ApprovalAssignmentCause.Reassigned.ToString(), current.Cause);
            Assert.Equal(
                FirstApprover,
                history.Single(record => record.ReleasedAt is not null).AssigneeUserId);
        }

        // Idempotent: a second pass finds nothing to change and adds no history.
        var repeated = await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-revoke-2", "integration-test", "correlation-reconcile-2",
            DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(1, repeated.Scanned);
        Assert.Equal(0, repeated.Reassigned);
        Assert.Equal(0, repeated.Unassigned);
        Assert.Equal(1, repeated.Unchanged);

        // A request key identifies one run: repeating it never enqueues a second pass (REQ-04).
        var replayed = await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-revoke-1", "integration-test", "correlation-reconcile",
            DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(reconciled.RunId, replayed.RunId);
        Assert.Equal(0, replayed.Scanned);

        await using var final = environment.CreateContext();
        Assert.Equal(
            2,
            await final.ApprovalAssignments.CountAsync(
                record => record.CaseId == outcome.CaseId, cancellationToken));
        Assert.False(
            await services.Reconciliation.IsDueAsync(OrganizationId, DateTimeOffset.UtcNow, cancellationToken));
    }

    [Fact]
    public async Task Reconciliation_releases_a_task_when_no_candidate_remains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));

        var outcome = await services.Submission.SubmitAsync(
            Command("submission-release"), DateTimeOffset.UtcNow, cancellationToken);
        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            assignment.RevokedBy = AdminId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var reconciled = await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-release-1", "integration-test", "correlation-release",
            DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(1, reconciled.Unassigned);
        await using var verification = environment.CreateContext();
        var task = await verification.ApprovalTasks.SingleAsync(
            record => record.CaseId == outcome.CaseId, cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Unassigned, task.Status);
        Assert.Null(task.CurrentAssigneeUserId);
        Assert.Equal(
            (int)ApprovalCaseStatus.Blocked,
            (await verification.ApprovalCases.SingleAsync(
                record => record.Id == outcome.CaseId, cancellationToken)).Status);
        // Returning the task to UNASSIGNED releases the current assignment but keeps the
        // append-only history, so no task is left holding a freed slot (REQ-04, NFR-02).
        var history = await verification.ApprovalAssignments
            .Where(record => record.CaseId == outcome.CaseId)
            .ToArrayAsync(cancellationToken);
        var released = Assert.Single(history);
        Assert.Equal(FirstApprover, released.AssigneeUserId);
        Assert.NotNull(released.ReleasedAt);
        Assert.DoesNotContain(history, record => record.ReleasedAt is null);
        // The release is an automatic effect: SYSTEM/APPROVAL_WORKFLOW, linked to the run root and
        // with its own effect key (REQ-08, CA-04).
        var audit = await verification.ApprovalAuditEntries
            .Where(record => record.CaseId == outcome.CaseId)
            .ToArrayAsync(cancellationToken);
        var effect = Assert.Single(audit, entry => entry.Action == "TASK_RELEASED");
        Assert.Equal("SYSTEM", effect.ActorType);
        Assert.Equal(ApprovalSystemActors.ApprovalWorkflow, effect.ActorSystemId);
        Assert.Equal("APPROVAL", effect.CausedByAuditStream);
        Assert.NotNull(effect.CausedByAuditId);
        Assert.NotNull(effect.AutomaticEffectKey);
    }

    [Fact]
    public async Task Scope_resolution_fails_closed_for_a_stale_catalog_reference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        // The department is at version 1 but the requirement freezes version 2.
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 2));

        await Assert.ThrowsAsync<DomainValidationException>(() => services.Submission.SubmitAsync(
            Command("submission-stale-scope"), DateTimeOffset.UtcNow, cancellationToken));

        await using var verification = environment.CreateContext();
        Assert.Equal(0, await verification.ApprovalCases.CountAsync(cancellationToken));
        Assert.Equal(0, await verification.ApprovalAssignments.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Reconciliation_lease_is_fenced_and_reclaimed_reusing_run_root_and_cursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));
        var submission = await services.Submission.SubmitAsync(
            Command("submission-lease"), DateTimeOffset.UtcNow, cancellationToken);

        // The approver loses the role, so the run has real work to do.
        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            assignment.RevokedBy = AdminId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var runId = await services.Reconciliation.RequestAdminAsync(
            OrganizationId, AdminId, "reconcile-lease-1", "correlation-lease", requestedAt, cancellationToken);
        Guid rootAuditId;
        await using (var seeding = environment.CreateContext())
        {
            var run = await seeding.ApprovalReconciliationRuns.SingleAsync(
                record => record.Id == runId, cancellationToken);
            rootAuditId = run.RootAuditId;
            // Another instance holds a live lease: this holder must not claim nor change anything.
            run.Status = ApprovalReconciliationCodes.StatusRunning;
            run.LeaseOwner = "other-instance";
            run.LockedUntil = requestedAt.AddMinutes(1);
            run.FencingToken = 7;
            await seeding.SaveChangesAsync(cancellationToken);
        }

        var blocked = await services.Reconciliation.ProcessAsync(
            runId, "this-instance", "correlation-lease", cancellationToken);
        Assert.False(blocked.Completed);
        Assert.Equal(0, blocked.Scanned);
        await using (var verification = environment.CreateContext())
        {
            var run = await verification.ApprovalReconciliationRuns
                .AsNoTracking()
                .SingleAsync(record => record.Id == runId, cancellationToken);
            Assert.Equal("other-instance", run.LeaseOwner);
            Assert.Equal(7, run.FencingToken);
            Assert.Null(run.CompletedAt);
        }

        // The lease expires without completing: another instance reclaims the same run, root and cursor.
        await using (var expiring = environment.CreateContext())
        {
            var run = await expiring.ApprovalReconciliationRuns.SingleAsync(
                record => record.Id == runId, cancellationToken);
            run.LockedUntil = requestedAt;
            await expiring.SaveChangesAsync(cancellationToken);
        }

        var reclaimed = await services.Reconciliation.ProcessAsync(
            runId, "this-instance", "correlation-lease", cancellationToken);
        Assert.True(reclaimed.Completed);
        Assert.Equal(1, reclaimed.Scanned);
        Assert.Equal(1, reclaimed.Unassigned);
        await using (var verification = environment.CreateContext())
        {
            var run = await verification.ApprovalReconciliationRuns
                .AsNoTracking()
                .SingleAsync(record => record.Id == runId, cancellationToken);
            Assert.Equal(ApprovalReconciliationCodes.StatusCompleted, run.Status);
            Assert.Equal(rootAuditId, run.RootAuditId);
            Assert.Equal(8, run.FencingToken);
            Assert.NotNull(run.CompletedAt);
            Assert.Equal(submission.CaseId, run.CursorCaseId);
        }

        // A stale holder cannot advance or reopen the completed run.
        var stale = await services.Reconciliation.ProcessAsync(
            runId, "other-instance", "correlation-lease", cancellationToken);
        Assert.False(stale.Completed);
        Assert.Equal(0, stale.Scanned);
    }

    [Fact]
    public async Task Reconciliation_holder_stops_when_the_lease_expires_mid_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        var claimedAt = DateTimeOffset.UtcNow;
        // The claim consumes the first value; everything after sees the lease 31 s later, i.e.
        // expired. A frozen claim clock would keep the holder believing the lease is live.
        var clock = new ScriptedTimeProvider(
            [claimedAt], claimedAt.AddSeconds(LeaseSeconds + 1));
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1), clock);
        var submission = await services.Submission.SubmitAsync(
            Command("submission-lease-expiry"), claimedAt, cancellationToken);

        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            assignment.RevokedBy = AdminId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var runId = await services.Reconciliation.RequestAdminAsync(
            OrganizationId, AdminId, "reconcile-lease-expiry", "correlation-lease-expiry", claimedAt, cancellationToken);

        var outcome = await services.Reconciliation.ProcessAsync(
            runId, "this-instance", "correlation-lease-expiry", cancellationToken);

        // The holder must not confirm effects nor complete with an expired lease (REQ-10).
        Assert.False(outcome.Completed);
        Assert.Equal(0, outcome.Scanned);
        await using (var verification = environment.CreateContext())
        {
            var run = await verification.ApprovalReconciliationRuns
                .AsNoTracking()
                .SingleAsync(record => record.Id == runId, cancellationToken);
            Assert.Equal(ApprovalReconciliationCodes.StatusRunning, run.Status);
            Assert.Null(run.CompletedAt);
            Assert.Equal(1, run.FencingToken);
            Assert.Equal(claimedAt.AddSeconds(LeaseSeconds), run.LockedUntil);
        }
    }

    private const int LeaseSeconds = 30;

    [Fact]
    public async Task Reconciliation_rolls_back_the_case_when_the_lease_expires_while_processing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);
        var claimedAt = DateTimeOffset.UtcNow;
        // Claim, remaining check and holder check see a live lease; the fenced check immediately
        // before persisting the case sees it already expired.
        var clock = new ScriptedTimeProvider(
            [claimedAt, claimedAt, claimedAt], claimedAt.AddSeconds(LeaseSeconds + 1));
        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1), clock);
        var submission = await services.Submission.SubmitAsync(
            Command("submission-lease-midcase"), claimedAt, cancellationToken);

        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            assignment.RevokedBy = AdminId;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        var runId = await services.Reconciliation.RequestAdminAsync(
            OrganizationId, AdminId, "reconcile-lease-midcase", "correlation-lease-midcase", claimedAt, cancellationToken);

        var outcome = await services.Reconciliation.ProcessAsync(
            runId, "this-instance", "correlation-lease-midcase", cancellationToken);

        Assert.False(outcome.Completed);
        await using (var verification = environment.CreateContext())
        {
            var run = await verification.ApprovalReconciliationRuns
                .AsNoTracking()
                .SingleAsync(record => record.Id == runId, cancellationToken);
            Assert.Equal(ApprovalReconciliationCodes.StatusRunning, run.Status);
            Assert.Null(run.CompletedAt);
            // Effects and the cursor were discarded: the task keeps its now-invalid assignee and
            // a reclaimer processes the case again from the previous cursor (REQ-10).
            Assert.Null(run.CursorCaseId);
            var task = await verification.ApprovalTasks.SingleAsync(cancellationToken);
            Assert.Equal(FirstApprover, task.CurrentAssigneeUserId);
            Assert.DoesNotContain(
                await verification.ApprovalAuditEntries.ToArrayAsync(cancellationToken),
                entry => entry.Action == "TASK_RELEASED");
        }
    }

    [Fact]
    public async Task Organization_change_run_keeps_the_triggering_audit_utc_as_requested_at()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedOrganizationAsync(cancellationToken);
        await environment.SeedApproverAsync(FirstApprover, "approver-a", cancellationToken);

        // The organizational change happened in the past; the worker observes it only now.
        var auditOccurredAt = DateTimeOffset.UtcNow.AddMinutes(-90);
        Guid auditId;
        await using (var context = environment.CreateContext())
        {
            var profile = await context.UserProfiles.SingleAsync(cancellationToken);
            var audit = new AdministrativeAuditRecord
            {
                Id = Guid.NewGuid(),
                ActorType = "USER",
                ActorUserId = AdminId,
                OccurredAt = auditOccurredAt,
                Action = "PROFILE_UPDATED",
                TargetType = "UserProfile",
                TargetId = profile.Id,
                ScopeJson = "[]",
                Reason = "Test profile change",
                CorrelationReference = "correlation-org-change"
            };
            context.AdministrativeAuditRecords.Add(audit);
            await context.SaveChangesAsync(cancellationToken);
            auditId = audit.Id;
        }

        var services = environment.CreateServices(new DepartmentAdapter(DepartmentId, 1));
        var runId = await services.Reconciliation.RequestOrganizationChangeAsync(
            OrganizationId, auditId, "correlation-org-change", auditOccurredAt, cancellationToken);

        await using var verification = environment.CreateContext();
        var run = await verification.ApprovalReconciliationRuns.SingleAsync(
            record => record.Id == runId, cancellationToken);
        // requested_at is the UTC of the triggering organizational audit, never the sweep
        // instant: the 60 s budget starts at the change, not at the observation (REQ-10).
        Assert.Equal(auditOccurredAt, run.RequestedAt);
        Assert.Equal(auditId, run.TriggerAuditId);
        Assert.Null(run.StartedAt);
    }

    private static async Task<Guid?> AssigneeOfAsync(
        ProcureToPayDbContext context,
        Guid caseId,
        CancellationToken cancellationToken) =>
        (await context.ApprovalTasks.SingleAsync(
            record => record.CaseId == caseId, cancellationToken)).CurrentAssigneeUserId;

    private static ApprovalSubmissionCommand Command(string submissionKey) => new(
        Workload,
        OrganizationId,
        "PURCHASE_REQUEST",
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        1,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        OriginatorId,
        "correlation");

    private static ApprovalAssignmentRecord CurrentAssignment(Guid caseId, Guid taskId, Guid assigneeUserId) => new()
    {
        Id = Guid.NewGuid(),
        CaseId = caseId,
        OrganizationId = OrganizationId,
        TaskId = taskId,
        AssigneeUserId = assigneeUserId,
        AssignedAt = AssignedAt,
        Load = 0,
        Cause = ApprovalAssignmentCause.Initial.ToString(),
        EligibilityEvidenceJson = "{\"seeded\":true}"
    };

    private sealed class DepartmentAdapter(Guid departmentId, int referenceVersion) : IApprovalSubmissionAdapter
    {
        public ApprovalAdapterDescriptor Descriptor { get; } =
            new("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApprovalSubmission(
                request.SubmissionKey,
                request.OrganizationId,
                request.SubjectType,
                request.SubjectId,
                request.SubjectVersion,
                request.Operation,
                new string('a', 64),
                request.RequesterId,
                request.OriginatorId,
                [
                    new ApprovalRequirementDefinition(
                        "DEPARTMENT_REQ",
                        "DEPARTMENT",
                        SystemRole.ItReviewer,
                        AuthorityRequirement.None,
                        DecisionScopeDescriptor.Create(
                            request.OrganizationId,
                            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, referenceVersion)]),
                        [
                            ApprovalDecisionAction.Approve,
                            ApprovalDecisionAction.Reject,
                            ApprovalDecisionAction.RequestChanges
                        ],
                        [OriginatorId],
                        [new ApprovalTarget("LINE", Guid.Parse("55555555-5555-5555-5555-555555555555"), 1, new string('c', 64))],
                        [])
                ],
                []));
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;

        public string ConnectionString { get; private set; } = string.Empty;

        public static async Task<SqlEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new SqlEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            environment.ConnectionString = environment.container.GetConnectionString();
            await using var context = environment.CreateContext();
            await context.Database.MigrateAsync(cancellationToken);
            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public async Task SeedOrganizationAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            context.Organizations.Add(new OrganizationRecord
            {
                Id = OrganizationId,
                Code = "ACME",
                Name = "Acme Corporation",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1
            });
            context.Departments.Add(new DepartmentRecord
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Code = "IT",
                Name = "Software / IT",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task SeedApproverAsync(Guid userId, string subject, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = userId,
                OrganizationId = OrganizationId,
                Issuer = "https://issuer.test/realms/procure-to-pay",
                Subject = subject,
                Email = $"{subject}@acme.test",
                DisplayName = subject,
                DepartmentId = DepartmentId,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = userId,
                Role = (int)SystemRole.ItReviewer,
                ScopeJson = "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = AssignedAt,
                AssignedBy = AdminId,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        // pi-lens-ignore: lsp:CS0246
        public (ApprovalSubmissionService Submission, ApprovalReconciliationService Reconciliation) CreateServices(
            IApprovalSubmissionAdapter adapter,
            TimeProvider? timeProvider = null)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Approval:Workloads:0:Issuer"] = Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = Workload.ClientId
                })
                .Build();
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
            var context = CreateContext();
            var assignmentEngine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
            return (
                new ApprovalSubmissionService(
                    context,
                    new ApprovalSubmissionAdapterRegistry([adapter]),
                    ownerWorkloads,
                    allowlist,
                    assignmentEngine,
                    loggerFactory.CreateLogger<ApprovalSubmissionService>()),
                // pi-lens-ignore: lsp:CS0246
                new ApprovalReconciliationService(
                    context,
                    assignmentEngine,
                    // pi-lens-ignore: lsp:CS0246
                    loggerFactory.CreateLogger<ApprovalReconciliationService>(),
                    timeProvider));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }

    /// <summary>Returns scripted instants one by one and a fallback once exhausted (test clock).</summary>
    private sealed class ScriptedTimeProvider(IEnumerable<DateTimeOffset> values, DateTimeOffset fallback) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> values = new(values);

        public override DateTimeOffset GetUtcNow() => values.Count > 0 ? values.Dequeue() : fallback;
    }
}
