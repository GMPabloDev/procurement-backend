using System.Diagnostics.Metrics;
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
/// SQL Server evidence for decisions (REQ-06, REQ-08, REQ-10, NFR-01, NFR-02, CA-05, CA-06, CA-07):
/// only the still-eligible current assignee decides, the decision is atomic with its targets,
/// audit and outbox, an identical replay returns the original decision, and a concurrent
/// decision cannot produce two terminal results.
/// </summary>
public sealed class ApprovalDecisionIntegrationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DepartmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid FirstApprover = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecondApprover = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OriginatorId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid LineOne = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineTwo = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset AssignedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ApprovalWorkloadIdentity Workload = new("internal://procure-to-pay", "adapter");

    [Fact]
    public async Task Decision_commits_decision_targets_audit_and_outbox_atomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices();
        var submission = await services.Submission.SubmitAsync(
            Command("submission-decision"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        var outcome = await services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-1",
                taskVersion, FirstApprover, "correlation-decision"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.False(outcome.Replayed);
        Assert.Equal("APPROVE", outcome.Action);
        Assert.Equal("APPROVED", outcome.RequirementStatus);
        Assert.Equal("APPROVED", outcome.TaskStatus);
        Assert.Equal("COMPLETED", outcome.CaseStatus);
        // One result event per target of the grouped requirement.
        Assert.Equal(2, outcome.OutboxEventIds.Count);

        await using var verification = environment.CreateContext();
        var decision = await verification.ApprovalDecisions.SingleAsync(cancellationToken);
        Assert.Equal(outcome.DecisionId, decision.Id);
        Assert.Equal(FirstApprover, decision.ActorUserId);
        Assert.Equal("decision-1", decision.DecisionKey);
        Assert.Equal(64, decision.Fingerprint.Length);
        Assert.Equal(64, decision.DecisionDigest.Length);
        Assert.Equal(64, decision.AuthorityEvidenceDigest.Length);
        Assert.NotEmpty(decision.EligibilityEvidenceJson);
        // The stored preimage version is exactly the version the caller had to match (REQ-06).
        // pi-lens-ignore: lsp:CS1061
        Assert.Equal(taskVersion, decision.TaskVersion);

        Assert.Equal(2, await verification.ApprovalDecisionTargets.CountAsync(cancellationToken));
        Assert.Equal(2, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        Assert.All(
            await verification.ApprovalOutboxEvents.ToArrayAsync(cancellationToken),
            record => Assert.Equal("APPROVED", record.Result));
        Assert.Contains(
            await verification.ApprovalAuditEntries.ToArrayAsync(cancellationToken),
            entry => entry.Action == "DECISION_APPROVED");
        // The decided task releases its current assignment so the load is freed.
        var assignment = await verification.ApprovalAssignments.SingleAsync(cancellationToken);
        Assert.NotNull(assignment.ReleasedAt);
        Assert.Equal(
            "Aprobado por negocio",
            (await verification.ApprovalDecisions.SingleAsync(cancellationToken)).Reason);
    }

    [Fact]
    public async Task Decision_replay_returns_the_original_and_a_changed_payload_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices();
        var submission = await services.Submission.SubmitAsync(
            Command("submission-replay"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);
        // pi-lens-ignore: lsp:CS0246
        var command = new ApprovalDecisionCommand(
            taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-replay",
            taskVersion, FirstApprover, "correlation-decision");

        var first = await services.Decision.DecideAsync(command, DateTimeOffset.UtcNow, cancellationToken);
        // The task is already terminal, yet an identical replay still returns the original.
        var replay = await services.Decision.DecideAsync(command, DateTimeOffset.UtcNow, cancellationToken);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.DecisionId, replay.DecisionId);
        // NFR-01: the replay returns the original artifacts, including the events of the decision.
        // The contract fixes identity, not list order, so the comparison is by set.
        Assert.Equal(
            first.OutboxEventIds.OrderBy(id => id).ToArray(),
            replay.OutboxEventIds.OrderBy(id => id).ToArray());
        Assert.Equal(2, replay.OutboxEventIds.Count);
        Assert.Equal("COMPLETED", replay.CaseStatus);

        await Assert.ThrowsAsync<DomainConflictException>(() => services.Decision.DecideAsync(
            command with { Action = ApprovalDecisionAction.Reject, Reason = "Rechazado por negocio" },
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using var verification = environment.CreateContext();
        Assert.Equal(1, await verification.ApprovalDecisions.CountAsync(cancellationToken));
        Assert.Equal(2, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Reserved_role_holders_are_never_candidates_even_with_the_business_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        // The only approver also holds an active ADMIN assignment in the organization (DEC-08).
        await using (var granting = environment.CreateContext())
        {
            granting.RoleAssignments.Add(ReservedRole(FirstApprover));
            await granting.SaveChangesAsync(cancellationToken);
        }

        var submission = await environment.CreateServices().Submission.SubmitAsync(
            Command("submission-reserved-candidate"), DateTimeOffset.UtcNow, cancellationToken);

        // No candidate remains: the reserved role excludes the user from candidacy and assignment.
        Assert.Equal("BLOCKED", submission.Status);
        await using var verification = environment.CreateContext();
        var task = await verification.ApprovalTasks.SingleAsync(cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Unassigned, task.Status);
        Assert.Null(task.CurrentAssigneeUserId);
        Assert.Empty(await verification.ApprovalAssignments.ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task A_reserved_role_granted_after_assignment_blocks_the_decision_and_reconciles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices();
        var submission = await services.Submission.SubmitAsync(
            Command("submission-reserved-grant"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        // The assignee acquires an active ADMIN assignment after being assigned.
        await using (var granting = environment.CreateContext())
        {
            granting.RoleAssignments.Add(ReservedRole(FirstApprover));
            await granting.SaveChangesAsync(cancellationToken);
        }

        // A new decision is forbidden: the assignee stopped being eligible (REQ-05, REQ-06).
        await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-reserved",
                taskVersion, FirstApprover, "correlation-reserved"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        // Reconciliation releases the task because no candidate remains (NFR-03, CA-05).
        var reconciled = await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-reserved-1", "integration-test", "correlation-reserved",
            DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(1, reconciled.Scanned);
        Assert.Equal(1, reconciled.Unassigned);

        await using var verification = environment.CreateContext();
        var task = await verification.ApprovalTasks.SingleAsync(
            record => record.Id == taskId, cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Unassigned, task.Status);
        Assert.Null(task.CurrentAssigneeUserId);
        // The forbidden attempt created no decision and no successful audit.
        Assert.Empty(await verification.ApprovalDecisions.ToArrayAsync(cancellationToken));
        Assert.DoesNotContain(
            await verification.ApprovalAuditEntries.ToArrayAsync(cancellationToken),
            entry => entry.Action == "DECISION_APPROVED");
    }

    private static RoleAssignmentRecord ReservedRole(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserProfileId = userId,
        Role = (int)SystemRole.Admin,
        ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
        Status = (int)AssignmentStatus.Active,
        AssignedAt = AssignedAt,
        AssignedBy = AdminId,
        Version = 1
    };

    [Fact]
    public async Task Only_the_current_assignee_decides_and_losing_eligibility_blocks_the_decision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        await environment.SeedAsync(SecondApprover, cancellationToken);
        var services = environment.CreateServices();
        var submission = await services.Submission.SubmitAsync(
            Command("submission-authority"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        // A different eligible approver holds no authority over this task.
        await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Intento ajeno", "decision-foreign",
                taskVersion, SecondApprover, "correlation-foreign"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        // A stale expected version is a conflict, not a silent overwrite.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Version obsoleta", "decision-stale",
                taskVersion + 5, FirstApprover, "correlation-stale"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        // Confirmed authority change between assignment and decision: the role is revoked.
        await using (var revoking = environment.CreateContext())
        {
            var assignment = await revoking.RoleAssignments.SingleAsync(
                record => record.UserProfileId == FirstApprover, cancellationToken);
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = AssignedAt;
            await revoking.SaveChangesAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<DomainForbiddenException>(() => services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Autoridad revocada", "decision-revoked",
                taskVersion, FirstApprover, "correlation-revoked"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using var verification = environment.CreateContext();
        Assert.Equal(0, await verification.ApprovalDecisions.CountAsync(cancellationToken));
        Assert.Equal(0, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        Assert.Equal(
            (int)ApprovalTaskStatus.Pending,
            (await verification.ApprovalTasks.SingleAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task Concurrent_decisions_produce_a_single_terminal_decision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var submission = await environment.CreateServices().Submission.SubmitAsync(
            Command("submission-race"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(async index =>
            {
                var services = environment.CreateServices();
                try
                {
                    var outcome = await services.Decision.DecideAsync(
                        // pi-lens-ignore: lsp:CS0246
                        new ApprovalDecisionCommand(
                            taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio",
                            $"decision-race-{index}", taskVersion, FirstApprover, $"correlation-race-{index}"),
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    return outcome.DecisionId;
                }
                catch (DomainConflictException)
                {
                    // The loser fails with the contractual 409; any other exception fails the test.
                    return Guid.Empty;
                }
            }));

        Assert.Single(attempts, id => id != Guid.Empty);

        await using var verification = environment.CreateContext();
        // Exactly one terminal decision per (requirement, target): no duplicated evidence.
        Assert.Equal(1, await verification.ApprovalDecisions.CountAsync(cancellationToken));
        Assert.Equal(2, await verification.ApprovalDecisionTargets.CountAsync(cancellationToken));
        Assert.Equal(2, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalAuditEntries
            .CountAsync(entry => entry.Action == "DECISION_APPROVED", cancellationToken));
        Assert.Equal(
            (int)ApprovalTaskStatus.Approved,
            (await verification.ApprovalTasks.SingleAsync(cancellationToken)).Status);
    }

    [Fact]
    public async Task Approval_unlocks_and_routes_the_dependent_requirement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices(withDependentRequirement: true);
        var submission = await services.Submission.SubmitAsync(
            Command("submission-approve-chain"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        var outcome = await services.Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-chain",
                taskVersion, FirstApprover, "correlation-chain"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        // The approval enables its dependent edge, which is routed in the same transaction.
        Assert.Equal("APPROVED", outcome.RequirementStatus);
        Assert.Equal("OPEN", outcome.CaseStatus);

        await using var verification = environment.CreateContext();
        var dependent = await verification.ApprovalRequirements.SingleAsync(
            record => record.SourceRequirementKey == "FINANCE_REQ", cancellationToken);
        Assert.Equal((int)ApprovalRequirementStatus.Pending, dependent.Status);
        var dependentTask = await verification.ApprovalTasks.SingleAsync(
            record => record.RequirementId == dependent.Id, cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Pending, dependentTask.Status);
        Assert.Equal(FirstApprover, dependentTask.CurrentAssigneeUserId);
    }

    [Fact]
    public async Task Rejection_cancels_only_linked_descendants_and_emits_their_targets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices(withDependentRequirement: true);
        var submission = await services.Submission.SubmitAsync(
            Command("submission-reject"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        var outcome = await services.Decision.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Reject, "Rechazado por presupuesto", "decision-reject",
                taskVersion, FirstApprover, "correlation-reject"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal("REJECTED", outcome.TaskStatus);
        Assert.Equal("REJECTED", outcome.RequirementStatus);
        // REQ-07: every remaining requirement is terminal, so the case completes as unsuccessful.
        Assert.Equal("COMPLETED", outcome.CaseStatus);

        await using var verification = environment.CreateContext();
        var dependent = await verification.ApprovalRequirements
            .SingleAsync(
                record => record.SourceRequirementKey == "FINANCE_REQ", cancellationToken);
        var events = await verification.ApprovalOutboxEvents.ToArrayAsync(cancellationToken);
        // The decided requirement publishes its own result per target; the cancelled descendant
        // keeps its own CANCELLED result and its own source (REQ-08, DEC-07).
        Assert.Equal(3, events.Length);
        Assert.Equal(2, events.Count(record => record.Result == "REJECTED"));
        var cancelledEvent = Assert.Single(events, record => record.Result == "CANCELLED");
        Assert.Equal("APPROVAL_REQUIREMENT", cancelledEvent.ResultSourceType);
        Assert.Equal(dependent.Id, cancelledEvent.ResultSourceId);
        Assert.Equal((int)ApprovalRequirementStatus.Cancelled, dependent.Status);
        Assert.Equal(
            (int)ApprovalTaskStatus.Cancelled,
            (await verification.ApprovalTasks.SingleAsync(
                record => record.RequirementId == dependent.Id, cancellationToken)).Status);
    }

    [Fact]
    public async Task Approval_operations_emit_minimized_metrics_without_personal_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        await environment.SeedAsync(FirstApprover, cancellationToken);
        var services = environment.CreateServices();

        var measurements = new List<(string Instrument, string? Operation, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, registered) =>
            {
                if (instrument.Meter.Name == ApprovalTelemetry.SourceName)
                {
                    registered.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "approval.operation")
                {
                    operation = tag.Value?.ToString();
                }

                if (tag.Key == "approval.outcome")
                {
                    outcome = tag.Value?.ToString();
                }

                // Only outcome vocabulary is emitted: never a reason, an actor or a snapshot (NFR-05).
                Assert.DoesNotContain("reason", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("actor", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("user", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("snapshot", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("email", tag.Key, StringComparison.OrdinalIgnoreCase);
            }

            measurements.Add((instrument.Name, operation, outcome));
        });
        listener.Start();

        var submission = await services.Submission.SubmitAsync(
            Command("submission-metrics"), DateTimeOffset.UtcNow, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);

        // A stale expected version is a conflict.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-metrics-stale",
                taskVersion + 3, FirstApprover, "correlation-metrics"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        await services.Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Aprobado por negocio", "decision-metrics",
                taskVersion, FirstApprover, "correlation-metrics"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        // Reusing the decision key with different content is a conflict too.
        await Assert.ThrowsAsync<DomainConflictException>(() => services.Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Reject, "Rechazado por negocio", "decision-metrics",
                taskVersion, FirstApprover, "correlation-metrics"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        await services.Reconciliation.ReconcileAsync(
            OrganizationId, AdminId, "reconcile-metrics-1", "integration-test", "correlation-metrics",
            DateTimeOffset.UtcNow, cancellationToken);

        Assert.Contains(measurements, entry =>
            entry.Instrument == "approval.submissions" && entry.Outcome == "CREATED");
        Assert.Contains(measurements, entry =>
            entry.Instrument == "approval.assignments" && entry.Outcome == "ASSIGNED");
        Assert.Contains(measurements, entry =>
            entry.Instrument == "approval.decisions" && entry.Outcome == "APPROVE_ACCEPTED");
        Assert.Contains(measurements, entry =>
            entry.Instrument == "approval.conflicts" && entry.Operation == "DECIDE");
        Assert.Contains(measurements, entry =>
            entry.Instrument == "approval.reconciliations" && entry.Outcome == "COMPLETED");
    }

    private static ApprovalSubmissionCommand Command(string submissionKey) => new(
        Workload,
        OrganizationId,
        "PURCHASE_REQUEST",
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        1,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        OriginatorId,
        "correlation");

    /// <summary>A department requirement, optionally with a dependent one so rejection has a linked descendant.</summary>
    private sealed class DepartmentAdapter(bool withDependentRequirement = false) : IApprovalSubmissionAdapter
    {
        public ApprovalAdapterDescriptor Descriptor { get; } =
            new("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            var requirements = new List<ApprovalRequirementDefinition>
            {
                Requirement(request.OrganizationId, "DEPARTMENT_REQ", "DEPARTMENT", [LineOne, LineTwo], [])
            };
            if (withDependentRequirement)
            {
                requirements.Add(Requirement(
                    request.OrganizationId,
                    "FINANCE_REQ",
                    "FINANCE",
                    [LineOne],
                    [new ApprovalDependencyRef(
                        DependencyPredecessorKind.Approval, "DEPARTMENT_REQ",
                        [new ApprovalTarget("LINE", LineOne, 1, new string('c', 64))])]));
            }

            return Task.FromResult(new ApprovalSubmission(
                request.SubmissionKey,
                request.OrganizationId,
                request.SubjectType,
                request.SubjectId,
                request.SubjectVersion,
                request.Operation,
                new string('a', 64),
                request.RequesterId,
                request.OriginatorId,
                requirements,
                []));
        }

        private static ApprovalRequirementDefinition Requirement(
            Guid organizationId,
            string sourceKey,
            string stageCode,
            IReadOnlyList<Guid> lineIds,
            IReadOnlyList<ApprovalDependencyRef> dependencies) => new(
            sourceKey,
            stageCode,
            SystemRole.ItReviewer,
            AuthorityRequirement.None,
            DecisionScopeDescriptor.Create(
                organizationId,
                [new DecisionScopeEntry(ScopeDimension.Department, DepartmentId, 1)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
            [OriginatorId],
            lineIds.Select(lineId => new ApprovalTarget("LINE", lineId, 1, new string('c', 64))).ToArray(),
            dependencies);
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
            await using (var context = environment.CreateContext())
            {
                await context.Database.MigrateAsync(cancellationToken);
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

            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public async Task<(Guid TaskId, int TaskVersion)> TaskAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            // A case may hold several tasks; the decision tests target the root requirement.
            var task = await (
                    from candidate in context.ApprovalTasks
                    join requirement in context.ApprovalRequirements
                        on candidate.RequirementId equals requirement.Id
                    where candidate.CaseId == caseId && requirement.SourceRequirementKey == "DEPARTMENT_REQ"
                    select candidate)
                .SingleAsync(cancellationToken);
            return (task.Id, task.Version);
        }

        public async Task SeedAsync(Guid userId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var subject = $"approver-{userId:D}";
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
        public (
            ApprovalSubmissionService Submission,
            ApprovalDecisionService Decision,
            ApprovalReconciliationService Reconciliation) CreateServices(
            bool withDependentRequirement = false)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Approval:Workloads:0:Issuer"] = Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = Workload.ClientId
                })
                .Build();
            var context = CreateContext();
            var assignmentEngine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            return (
                new ApprovalSubmissionService(
                    context,
                    new ApprovalSubmissionAdapterRegistry([new DepartmentAdapter(withDependentRequirement)]),
                    new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
                    allowlist,
                    assignmentEngine,
                    loggerFactory.CreateLogger<ApprovalSubmissionService>()),
                // pi-lens-ignore: lsp:CS0246
                new ApprovalDecisionService(
                    // pi-lens-ignore: lsp:CS0246
                    context, assignmentEngine, loggerFactory.CreateLogger<ApprovalDecisionService>()),
                // pi-lens-ignore: lsp:CS0246
                new ApprovalReconciliationService(
                    context, assignmentEngine, loggerFactory.CreateLogger<ApprovalReconciliationService>()));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
