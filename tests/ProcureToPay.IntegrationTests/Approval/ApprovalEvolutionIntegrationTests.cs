using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
/// SQL Server guarantees of SPEC 04: delegation lifecycle and its reconciliation runs, supersession
/// atomicity with strict carry-forward, and irreversible evidence revocation (CA-01..CA-06).
/// </summary>
public sealed class ApprovalEvolutionIntegrationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DepartmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OriginatorId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DelegatorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DelegateeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid LineOne = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineTwo = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset AssignedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApprovalWorkloadIdentity Workload = new("internal://procure-to-pay", "adapter");

    [Fact]
    public void Approval_model_maps_the_spec04_schema_and_indexes()
    {
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer("Server=localhost;Database=ProcureToPay_ModelOnly;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var context = new ProcureToPayDbContext(options);

        var delegation = context.Model.FindEntityType(typeof(ApprovalDelegationRecord))!;
        var job = context.Model.FindEntityType(typeof(ApprovalDelegationTransitionJobRecord))!;
        var supersession = context.Model.FindEntityType(typeof(CaseSupersessionRecord))!;
        var evidence = context.Model.FindEntityType(typeof(DecisionAuthorityEvidenceRecord))!;
        var carryForward = context.Model.FindEntityType(typeof(DecisionCarryForwardEntryRecord))!;
        var revocation = context.Model.FindEntityType(typeof(DecisionEvidenceRevocationRecord))!;
        var run = context.Model.FindEntityType(typeof(ApprovalReconciliationRunRecord))!;
        var decision = context.Model.FindEntityType(typeof(ApprovalDecisionRecord))!;

        Assert.Equal("Approval", delegation.GetSchema());
        Assert.Equal("ApprovalDelegations", delegation.GetTableName());
        Assert.Equal("ApprovalDelegationTransitionJobs", job.GetTableName());
        Assert.Equal("CaseSupersessions", supersession.GetTableName());
        Assert.Equal("DecisionAuthorityEvidences", evidence.GetTableName());
        Assert.Equal("DecisionCarryForwardRecords", carryForward.GetTableName());
        Assert.Equal("DecisionEvidenceRevocations", revocation.GetTableName());

        Assert.Contains(delegation.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalDelegationRecord.OrganizationId),
                nameof(ApprovalDelegationRecord.ActorType),
                nameof(ApprovalDelegationRecord.ActorUserId),
                nameof(ApprovalDelegationRecord.DelegationCommandKey)]));
        Assert.Contains(job.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalDelegationTransitionJobRecord.DelegationId),
                nameof(ApprovalDelegationTransitionJobRecord.Transition),
                nameof(ApprovalDelegationTransitionJobRecord.ScheduledAt)]));
        Assert.Contains(supersession.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(CaseSupersessionRecord.OrganizationId),
                nameof(CaseSupersessionRecord.WorkloadIssuer),
                nameof(CaseSupersessionRecord.WorkloadClientId),
                nameof(CaseSupersessionRecord.SupersessionKey)]));
        Assert.Contains(evidence.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(DecisionAuthorityEvidenceRecord.RootHumanDecisionId)]));
        Assert.Contains(carryForward.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(DecisionCarryForwardEntryRecord.NewDecisionId)]));
        Assert.Contains(revocation.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(DecisionEvidenceRevocationRecord.OrganizationId),
                nameof(DecisionEvidenceRevocationRecord.EvidenceId),
                nameof(DecisionEvidenceRevocationRecord.RevocationKey)]));
        Assert.Contains(run.GetIndexes(), index => index.IsUnique && index.GetFilter() is not null &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalReconciliationRunRecord.OrganizationId),
                nameof(ApprovalReconciliationRunRecord.DelegationId),
                nameof(ApprovalReconciliationRunRecord.DelegationVersion),
                nameof(ApprovalReconciliationRunRecord.DelegationTransition),
                nameof(ApprovalReconciliationRunRecord.DelegationScheduledAt)]));
        Assert.Contains(decision.GetIndexes(), index => index.IsUnique && index.GetFilter() is not null &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalDecisionRecord.OrganizationId),
                nameof(ApprovalDecisionRecord.ActorUserId),
                nameof(ApprovalDecisionRecord.DecisionKey)]));
    }

    [Fact]
    public async Task Delegation_lifecycle_routes_the_effective_set_and_reconciles_with_runs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        var adapter = new EvolutionAdapter();
        var submission = await Fresh(environment, adapter).Submission.SubmitAsync(
            Command("submission-delegation", 1), Now, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);
        Assert.Equal(DelegatorId, await environment.AssigneeAsync(taskId, cancellationToken));

        // A delegator without a covering role assignment cannot delegate (REQ-01, CA-01).
        await Assert.ThrowsAsync<DomainForbiddenException>(() => Fresh(environment, adapter).Delegations.CreateAsync(
            DelegationCommand(OriginatorId, DelegateeId, Now.AddMinutes(-30), Now.AddHours(1), "delegation-unauthorized"),
            Now,
            cancellationToken));

        var create = DelegationCommand(DelegatorId, DelegateeId, Now.AddMinutes(-30), Now.AddHours(1), "delegation-1");
        var created = await Fresh(environment, adapter).Delegations.CreateAsync(create, Now, cancellationToken);
        Assert.False(created.Replayed);
        Assert.Equal("ACTIVE", created.Status);

        // Idempotent replay and reuse of the command key with different content (CA-01).
        var replayed = await Fresh(environment, adapter).Delegations.CreateAsync(create, Now, cancellationToken);
        Assert.True(replayed.Replayed);
        Assert.Equal(created.DelegationId, replayed.DelegationId);
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Delegations.CreateAsync(
            create with { DelegateeUserId = DelegatorId },
            Now,
            cancellationToken));

        // Overlap with the same delegator, role and scope is rejected (REQ-01).
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Delegations.CreateAsync(
            DelegationCommand(DelegatorId, DelegatorId, Now, Now.AddHours(2), "delegation-overlap"),
            Now,
            cancellationToken));

        await using (var verification = environment.CreateContext())
        {
            var jobs = await verification.ApprovalDelegationTransitionJobs
                .Where(record => record.DelegationId == created.DelegationId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, jobs.Length);
            Assert.Contains(jobs, record => record.Transition == "ACTIVATE" && record.Status == "COMPLETED");
            Assert.Contains(jobs, record => record.Transition == "EXPIRE" && record.Status == "PENDING");
            var runs = await verification.ApprovalReconciliationRuns
                .Where(record => record.DelegationId == created.DelegationId)
                .ToArrayAsync(cancellationToken);
            var run = Assert.Single(runs);
            Assert.Equal("DELEGATION_CHANGE", run.Trigger);
            Assert.Equal("ACTIVATE", run.DelegationTransition);
        }

        // The delegation run only reconciles the covered requirement and moves it to the delegatee.
        await environment.ProcessDelegationRunsAsync(created.DelegationId, cancellationToken);
        Assert.Equal(DelegateeId, await environment.AssigneeAsync(taskId, cancellationToken));

        // A completed run never reopens and a second claim confirms nothing new (REQ-03, CA-03).
        await environment.ProcessDelegationRunsAsync(created.DelegationId, cancellationToken);
        Assert.Equal(DelegateeId, await environment.AssigneeAsync(taskId, cancellationToken));
        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(1, await verification.ApprovalReconciliationRuns.CountAsync(
                record => record.DelegationId == created.DelegationId && record.Trigger == "DELEGATION_CHANGE",
                cancellationToken));
        }

        // A delegated decision fixes the real delegation id and version in its authority evidence.
        var (reassignedTaskId, reassignedVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);
        var decision = await Fresh(environment, adapter).Decision.DecideAsync(
            new ApprovalDecisionCommand(
                reassignedTaskId, ApprovalDecisionAction.Approve, "Approved by the delegatee", "decision-delegated",
                reassignedVersion, DelegateeId, "correlation"),
            Now.AddMinutes(5),
            cancellationToken);
        Assert.False(decision.Replayed);
        await using (var verification = environment.CreateContext())
        {
            var assignment = await verification.ApprovalAssignments
                .Where(record => record.TaskId == taskId && record.DelegationId != null)
                .OrderByDescending(record => record.AssignedAt)
                .FirstAsync(cancellationToken);
            Assert.Equal(created.DelegationId, assignment.DelegationId);
            Assert.Equal(created.Version, assignment.DelegationVersion);
            var decisionRecord = await verification.ApprovalDecisions
                .SingleAsync(record => record.Id == decision.DecisionId, cancellationToken);
            Assert.NotEqual(
                ApprovalFingerprints.AuthorityEvidenceDigest(decisionRecord.EligibilityEvidenceJson),
                decisionRecord.AuthorityEvidenceDigest);
            var evidence = await verification.DecisionAuthorityEvidences
                .SingleAsync(record => record.RootHumanDecisionId == decision.DecisionId, cancellationToken);
            Assert.Equal(decisionRecord.AuthorityEvidenceDigest, evidence.Digest);
        }

        // Revocation cancels pending jobs and creates one DELEGATION_CHANGE run (REQ-03).
        var revokeCommand = new ApprovalDelegationRevokeCommand(
            ApprovalDelegationActorType.Delegator,
            DelegatorId,
            OrganizationId,
            created.DelegationId,
            created.Version,
            "Back from vacation",
            "delegation-revoke-1",
            "correlation");
        var revoked = await Fresh(environment, adapter).Delegations.RevokeAsync(
            revokeCommand, Now.AddMinutes(10), cancellationToken);
        Assert.Equal("REVOKED", revoked.Status);

        // The revoke command replays idempotently and another payload is a conflict; the CREATE
        // command keeps its own independent replay (REQ-01, CA-01).
        var revokeReplay = await Fresh(environment, adapter).Delegations.RevokeAsync(
            revokeCommand, Now.AddMinutes(11), cancellationToken);
        Assert.True(revokeReplay.Replayed);
        Assert.Equal(created.DelegationId, revokeReplay.DelegationId);
        Assert.Equal("REVOKED", revokeReplay.Status);
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Delegations.RevokeAsync(
            revokeCommand with { Reason = "Another reason" },
            Now.AddMinutes(11),
            cancellationToken));
        var createReplay = await Fresh(environment, adapter).Delegations.CreateAsync(
            create, Now.AddMinutes(12), cancellationToken);
        Assert.True(createReplay.Replayed);
        Assert.Equal(created.DelegationId, createReplay.DelegationId);
        await using (var verification = environment.CreateContext())
        {
            var pending = await verification.ApprovalDelegationTransitionJobs
                .CountAsync(
                    record => record.DelegationId == created.DelegationId && record.Status == "PENDING",
                    cancellationToken);
            Assert.Equal(0, pending);
            var runs = await verification.ApprovalReconciliationRuns
                .Where(record => record.DelegationId == created.DelegationId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, runs.Length);
            Assert.Contains(runs, record => record.DelegationTransition == "REVOKE");
        }
    }

    [Fact]
    public async Task Supersession_is_atomic_and_carries_forward_only_strict_equality()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        var adapter = new EvolutionAdapter();
        var first = await Fresh(environment, adapter).Submission.SubmitAsync(
            Command("submission-supersede-1", 1), Now, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(first.CaseId, cancellationToken);
        await Fresh(environment, adapter).Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Approved once", "decision-supersede-1", taskVersion,
                DelegatorId, "correlation"),
            Now.AddMinutes(1),
            cancellationToken);
        var previousVersion = await environment.CaseVersionAsync(first.CaseId, cancellationToken);

        var mapping = new[]
        {
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        var outcome = await Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, first.CaseId, previousVersion, "supersession-1", "submission-supersede-2", "v1", mapping,
                "correlation"),
            Now.AddMinutes(2),
            cancellationToken);
        Assert.False(outcome.Replayed);
        Assert.Equal(1, outcome.CarryForwardCount);
        Assert.NotEqual(first.CaseId, outcome.CaseId);

        // Replay returns the same case; the key with another submission is a conflict (CA-04).
        var replay = await Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, first.CaseId, previousVersion, "supersession-1", "submission-supersede-2", "v1", mapping,
                "correlation"),
            Now.AddMinutes(3),
            cancellationToken);
        Assert.True(replay.Replayed);
        Assert.Equal(outcome.CaseId, replay.CaseId);

        await using (var verification = environment.CreateContext())
        {
            var previous = await verification.ApprovalCases.SingleAsync(
                record => record.Id == first.CaseId, cancellationToken);
            Assert.Equal((int)ApprovalCaseStatus.Superseded, previous.Status);
            Assert.All(
                await verification.ApprovalRequirements
                    .Where(record => record.CaseId == first.CaseId)
                    .ToArrayAsync(cancellationToken),
                record => Assert.Equal((int)ApprovalRequirementStatus.Approved, record.Status));
            var newCase = await verification.ApprovalCases.SingleAsync(
                record => record.Id == outcome.CaseId, cancellationToken);
            Assert.Equal((int)ApprovalCaseStatus.Completed, newCase.Status);
            var carryForward = await verification.ApprovalDecisions.SingleAsync(
                record => record.CaseId == outcome.CaseId, cancellationToken);
            Assert.Equal((int)ApprovalDecisionOrigin.CarryForward, carryForward.Origin);
            Assert.Equal("SYSTEM", carryForward.ActorType);
            Assert.Null(carryForward.ActorUserId);
            Assert.Null(carryForward.TaskId);
            Assert.Null(carryForward.DecisionKey);
            Assert.Equal(0, await verification.ApprovalTasks.CountAsync(
                record => record.CaseId == outcome.CaseId, cancellationToken));
            Assert.Equal(1, await verification.DecisionCarryForwardEntries.CountAsync(
                record => record.NewCaseId == outcome.CaseId, cancellationToken));
            var lifecycle = await verification.ApprovalOutboxEvents.CountAsync(
                record => record.ContractVersion == ApprovalEvolutionCodes.LifecycleContractVersion &&
                          record.CaseId == outcome.CaseId,
                cancellationToken);
            Assert.Equal(1, lifecycle);
            var derived = await verification.ApprovalOutboxEvents.CountAsync(
                record => record.ContractVersion == ApprovalEvolutionCodes.ResultContractVersionV3 &&
                          record.CaseId == outcome.CaseId,
                cancellationToken);
            Assert.Equal(1, derived);
        }

        // A materially different replacement is a new fact: no carry-forward, a new task instead.
        adapter.MaterialDigest = new string('c', 64);
        var second = await Fresh(environment, adapter).Submission.SubmitAsync(
            Command("submission-supersede-3", 1), Now.AddMinutes(4), cancellationToken);
        var (secondTask, secondVersion) = await environment.TaskAsync(second.CaseId, cancellationToken);
        await Fresh(environment, adapter).Decision.DecideAsync(
            new ApprovalDecisionCommand(
                secondTask, ApprovalDecisionAction.Approve, "Approved before change", "decision-supersede-2",
                secondVersion, DelegatorId, "correlation"),
            Now.AddMinutes(5),
            cancellationToken);
        adapter.MaterialDigest = new string('d', 64);
        var changedMapping = new[]
        {
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineOne, 1, new string('d', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        var secondVersionNow = await environment.CaseVersionAsync(second.CaseId, cancellationToken);
        var changed = await Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, second.CaseId, secondVersionNow, "supersession-2", "submission-supersede-4", "v1",
                changedMapping, "correlation"),
            Now.AddMinutes(6),
            cancellationToken);
        Assert.Equal(0, changed.CarryForwardCount);
        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(1, await verification.ApprovalTasks.CountAsync(
                record => record.CaseId == changed.CaseId, cancellationToken));
            Assert.Equal(0, await verification.DecisionCarryForwardEntries.CountAsync(
                record => record.NewCaseId == changed.CaseId, cancellationToken));
        }

        // A mapping that does not cover every previous target fails closed (CA-04).
        var changedVersion = await environment.CaseVersionAsync(changed.CaseId, cancellationToken);
        var incompleteMapping = new[]
        {
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", Guid.NewGuid(), 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineOne, 1, new string('d', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, changed.CaseId, changedVersion, "supersession-3", "submission-supersede-5", "v1",
                incompleteMapping, "correlation"),
            Now.AddMinutes(7),
            cancellationToken));

        // A grouped requirement cannot be split into two requirements of the new version (REQ-04).
        adapter.MaterialDigest = new string('c', 64);
        adapter.Layout = EvolutionLayout.Grouped;
        var grouped = await Fresh(environment, adapter).Submission.SubmitAsync(
            Command("submission-supersede-grouped", 1), Now.AddMinutes(20), cancellationToken);
        var groupedVersion = await environment.CaseVersionAsync(grouped.CaseId, cancellationToken);
        adapter.Layout = EvolutionLayout.Split;
        var splitMapping = new[]
        {
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64)),
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", LineTwo, 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineTwo, 1, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, grouped.CaseId, groupedVersion, "supersession-split", "submission-supersede-split", "v1",
                splitMapping, "correlation"),
            Now.AddMinutes(21),
            cancellationToken));
    }

    [Fact]
    public async Task Scheduled_delegation_transitions_activate_and_expire_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        var adapter = new EvolutionAdapter();

        var create = DelegationCommand(
            DelegatorId, DelegateeId, Now.AddHours(1), Now.AddHours(2), "delegation-scheduled");
        var created = await Fresh(environment, adapter).Delegations.CreateAsync(create, Now, cancellationToken);
        Assert.False(created.Replayed);
        Assert.Equal("SCHEDULED", created.Status);

        await using (var verification = environment.CreateContext())
        {
            var jobs = await verification.ApprovalDelegationTransitionJobs
                .Where(record => record.DelegationId == created.DelegationId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, jobs.Length);
            Assert.All(jobs, record => Assert.Equal("PENDING", record.Status));
            Assert.Empty(await verification.ApprovalReconciliationRuns
                .Where(record => record.DelegationId == created.DelegationId)
                .ToArrayAsync(cancellationToken));
        }

        // Nothing is due before valid_from.
        Assert.Equal(0, await Fresh(environment, adapter).Transitions.ProcessDueAsync(
            "worker-a", Now, cancellationToken: cancellationToken));

        // Two workers race the same due activation: exactly one confirms it (REQ-03, CA-03).
        var confirmations = await Task.WhenAll(
            Task.Run(
                () => Fresh(environment, adapter).Transitions.ProcessDueAsync(
                    "worker-a", Now.AddHours(1), cancellationToken: cancellationToken),
                cancellationToken),
            Task.Run(
                () => Fresh(environment, adapter).Transitions.ProcessDueAsync(
                    "worker-b", Now.AddHours(1), cancellationToken: cancellationToken),
                cancellationToken));
        Assert.Equal(1, confirmations.Sum());

        await using (var verification = environment.CreateContext())
        {
            var delegation = await verification.ApprovalDelegations.SingleAsync(
                record => record.Id == created.DelegationId, cancellationToken);
            Assert.Equal("ACTIVE", delegation.Status);
            var activate = await verification.ApprovalDelegationTransitionJobs.SingleAsync(
                record => record.DelegationId == created.DelegationId && record.Transition == "ACTIVATE",
                cancellationToken);
            Assert.Equal("COMPLETED", activate.Status);
            var run = await verification.ApprovalReconciliationRuns.SingleAsync(
                record => record.DelegationId == created.DelegationId, cancellationToken);
            Assert.Equal("DELEGATION_CHANGE", run.Trigger);
            Assert.Equal("ACTIVATE", run.DelegationTransition);
            // The create audit is the immutable trigger; the transition audit is the run root.
            Assert.Equal(delegation.RootAuditId, run.TriggerAuditId);
            Assert.NotEqual(delegation.RootAuditId, run.RootAuditId);
            Assert.Equal(Now.AddHours(1), run.RequestedAt);
            var transitionAudit = await verification.ApprovalAuditEntries.SingleAsync(
                record => record.Id == run.RootAuditId, cancellationToken);
            Assert.Equal("SYSTEM", transitionAudit.ActorType);
            Assert.Equal("DELEGATION", transitionAudit.CausedByAuditStream);
            Assert.Equal(delegation.Id, transitionAudit.CausedByAuditId);
            Assert.Equal("DELEGATION_ACTIVATED", transitionAudit.Action);
        }

        // A completed run never reopens and the second sweep confirms nothing new.
        Assert.Equal(0, await Fresh(environment, adapter).Transitions.ProcessDueAsync(
            "worker-c", Now.AddHours(1), cancellationToken: cancellationToken));

        // The scheduled expiry confirms exactly one SYSTEM run caused by the delegation.
        Assert.Equal(1, await Fresh(environment, adapter).Transitions.ProcessDueAsync(
            "worker-d", Now.AddHours(2), cancellationToken: cancellationToken));
        await using (var verification = environment.CreateContext())
        {
            var delegation = await verification.ApprovalDelegations.SingleAsync(
                record => record.Id == created.DelegationId, cancellationToken);
            Assert.Equal("EXPIRED", delegation.Status);
            var expiryRun = await verification.ApprovalReconciliationRuns.SingleAsync(
                record => record.DelegationId == created.DelegationId && record.Trigger == "DELEGATION_EXPIRY",
                cancellationToken);
            Assert.Null(expiryRun.TriggerAuditId);
            Assert.Equal(Now.AddHours(2), expiryRun.RequestedAt);
            var rootAudit = await verification.ApprovalAuditEntries.SingleAsync(
                record => record.Id == expiryRun.RootAuditId, cancellationToken);
            Assert.Equal("SYSTEM", rootAudit.ActorType);
            Assert.Equal("DELEGATION", rootAudit.CausedByAuditStream);
            Assert.Equal(delegation.Id, rootAudit.CausedByAuditId);
            var expire = await verification.ApprovalDelegationTransitionJobs.SingleAsync(
                record => record.DelegationId == created.DelegationId && record.Transition == "EXPIRE",
                cancellationToken);
            Assert.Equal("COMPLETED", expire.Status);
        }

        Assert.Equal(0, await Fresh(environment, adapter).Transitions.ProcessDueAsync(
            "worker-e", Now.AddHours(2), cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task Evidence_revocation_is_irreversible_idempotent_and_blocks_carry_forward()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        var adapter = new EvolutionAdapter();
        var submission = await Fresh(environment, adapter).Submission.SubmitAsync(
            Command("submission-revocation", 1), Now, cancellationToken);
        var (taskId, taskVersion) = await environment.TaskAsync(submission.CaseId, cancellationToken);
        await Fresh(environment, adapter).Decision.DecideAsync(
            new ApprovalDecisionCommand(
                taskId, ApprovalDecisionAction.Approve, "Approved for revocation", "decision-revocation",
                taskVersion, DelegatorId, "correlation"),
            Now.AddMinutes(1),
            cancellationToken);

        Guid evidenceId;
        Guid decisionId;
        await using (var verification = environment.CreateContext())
        {
            var evidence = await verification.DecisionAuthorityEvidences.SingleAsync(cancellationToken);
            evidenceId = evidence.Id;
            decisionId = evidence.RootHumanDecisionId;
            Assert.Equal("VALID", evidence.Status);
            Assert.Equal(1, evidence.Version);
        }

        // ADMIN can only revoke as incident containment (REQ-06).
        await Assert.ThrowsAsync<DomainForbiddenException>(() => Fresh(environment, adapter).Revocations.RevokeAsync(
            new ApprovalEvidenceRevocationCommand(
                EvidenceRevocationActorType.Admin, AdminId, null, OrganizationId, evidenceId, 1,
                "revocation-admin-1", EvidenceRevocationReasonCode.OwnerInvalidation, "Containment",
                "correlation"),
            Now.AddMinutes(2),
            cancellationToken));

        var command = new ApprovalEvidenceRevocationCommand(
            EvidenceRevocationActorType.Workload, Guid.Empty, Workload, Guid.Empty, evidenceId, 1,
            "revocation-1", EvidenceRevocationReasonCode.OwnerInvalidation, "Subject binding invalidated",
            "correlation");
        var revocation = await Fresh(environment, adapter).Revocations.RevokeAsync(command, Now.AddMinutes(2), cancellationToken);
        Assert.False(revocation.Replayed);
        Assert.Equal("REVOKED", revocation.EvidenceStatus);
        Assert.Equal(1, revocation.EvidenceVersion);

        var replay = await Fresh(environment, adapter).Revocations.RevokeAsync(command, Now.AddMinutes(3), cancellationToken);
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.EvidenceVersion);
        Assert.Equal(revocation.RevocationId, replay.RevocationId);
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Revocations.RevokeAsync(
            command with { Reason = "Different reason" },
            Now.AddMinutes(3),
            cancellationToken));
        await Assert.ThrowsAsync<DomainConflictException>(() => Fresh(environment, adapter).Revocations.RevokeAsync(
            command with { RevocationKey = "revocation-2" },
            Now.AddMinutes(3),
            cancellationToken));

        await using (var verification = environment.CreateContext())
        {
            var evidence = await verification.DecisionAuthorityEvidences.SingleAsync(cancellationToken);
            Assert.Equal("REVOKED", evidence.Status);
            Assert.Equal(2, evidence.Version);
            var decision = await verification.ApprovalDecisions.SingleAsync(cancellationToken);
            Assert.Equal(decisionId, decision.Id);
            Assert.Equal(ApprovalDecisionOrigin.Human, (ApprovalDecisionOrigin)decision.Origin);
            var revocationRecord = await verification.DecisionEvidenceRevocations.SingleAsync(cancellationToken);
            Assert.Equal(1, revocationRecord.EvidenceVersion);
            var revokedEvent = await verification.ApprovalOutboxEvents.SingleAsync(
                record => record.ContractVersion == ApprovalEvolutionCodes.EvidenceRevokedContractVersion,
                cancellationToken);
            Assert.Contains("\"evidence_version\":1", revokedEvent.PayloadJson);
            Assert.Equal(2, evidence.Version);
        }

        // Revoked evidence never supports a derived decision: the new version needs a human task.
        var previousVersion = await environment.CaseVersionAsync(submission.CaseId, cancellationToken);
        var mapping = new[]
        {
            ApprovalSupersessionMapping.Create(
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                new ApprovalTarget("LINE", LineOne, 1, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        var superseded = await Fresh(environment, adapter).Supersessions.SupersedeAsync(
            new ApprovalSupersessionCommand(
                Workload, submission.CaseId, previousVersion, "supersession-revoked", "submission-revoked-2", "v1",
                mapping, "correlation"),
            Now.AddMinutes(4),
            cancellationToken);
        Assert.Equal(0, superseded.CarryForwardCount);
        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(0, await verification.DecisionCarryForwardEntries.CountAsync(
                record => record.NewCaseId == superseded.CaseId, cancellationToken));
            Assert.Equal(1, await verification.ApprovalTasks.CountAsync(
                record => record.CaseId == superseded.CaseId, cancellationToken));
        }
    }

    [Fact]
    public async Task Migration_backfills_authority_evidence_for_existing_human_decisions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await container.StartAsync(cancellationToken);
        var connectionString = container.GetConnectionString();
        var decisionId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await using (var context = new ProcureToPayDbContext(
                         new DbContextOptionsBuilder<ProcureToPayDbContext>()
                             .UseSqlServer(connectionString)
                             .Options))
        {
            // Migrate to the SPEC 03 head, insert one human decision of the previous schema and
            // then apply the SPEC 04 migration exactly like a production upgrade (CA-06).
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260913064411_ApprovalRequirementActions", cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [Approval].[ApprovalDecisions]
                    ([Id], [CaseId], [OrganizationId], [RequirementId], [TaskId], [Action], [Origin], [ActorUserId],
                     [Reason], [DecidedAt], [DecisionKey], [Fingerprint], [DecisionDigest], [AuthorityEvidenceDigest],
                     [EligibilityEvidenceJson], [RequirementVersion], [TaskVersion], [RequirementStatusAfter],
                     [TaskStatusAfter], [CaseStatusAfter], [CaseVersionAfter], [CorrelationReference], [Version])
                VALUES ({0}, {1}, {2}, {3}, NULL, 1, 1, {4}, N'Legacy approval', SYSDATETIMEOFFSET(),
                        N'legacy-decision', {5}, {6}, {7}, {8}, 1, 1, 4, 3, 3, 2, N'correlation', 1)
                """,
                decisionId,
                caseId,
                organizationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "{}");
            await migrator.MigrateAsync(cancellationToken: cancellationToken);

            var decision = await context.ApprovalDecisions.AsNoTracking().SingleAsync(
                record => record.Id == decisionId, cancellationToken);
            Assert.Equal("HUMAN", decision.ActorType);
            Assert.Equal(decisionId, decision.RootHumanDecisionId);
            Assert.Equal(1, decision.EvidenceVersion);
            var evidence = await context.DecisionAuthorityEvidences.AsNoTracking().SingleAsync(
                record => record.RootHumanDecisionId == decisionId, cancellationToken);
            Assert.Equal(decision.EvidenceId, evidence.Id);
            Assert.Equal(decision.AuthorityEvidenceDigest, evidence.Digest);
            Assert.Equal(decision.EligibilityEvidenceJson, evidence.EvidenceJson);
            Assert.Equal("VALID", evidence.Status);
            Assert.Equal(1, evidence.Version);
        }

        await container.DisposeAsync();
    }

    private static ApprovalSubmissionCommand Command(string submissionKey, int subjectVersion) => new(
        Workload,
        OrganizationId,
        "PURCHASE_REQUEST",
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        subjectVersion,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        OriginatorId,
        "correlation");

    private static ApprovalDelegationCreateCommand DelegationCommand(
        Guid delegatorId,
        Guid delegateeId,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        string key) => new(
        ApprovalDelegationActorType.Delegator,
        delegatorId,
        OrganizationId,
        delegatorId,
        delegateeId,
        SystemRole.ItReviewer,
        DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Department, DepartmentId, 1)]),
        validFrom,
        validTo,
        "Vacation coverage",
        key,
        "correlation");

    private enum EvolutionLayout
    {
        Single,
        Grouped,
        Split
    }

    private sealed class EvolutionAdapter : IApprovalSubmissionAdapter
    {
        public string MaterialDigest { get; set; } = new('c', 64);

        public EvolutionLayout Layout { get; set; } = EvolutionLayout.Single;

        public ApprovalAdapterDescriptor Descriptor { get; } = new(
            "adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ApprovalRequirementDefinition[] requirements = Layout switch
            {
                EvolutionLayout.Grouped =>
                [
                    Requirement(request, "DEPARTMENT_REQ", [LineOne, LineTwo])
                ],
                EvolutionLayout.Split =>
                [
                    Requirement(request, "DEPARTMENT_REQ_L1", [LineOne]),
                    Requirement(request, "DEPARTMENT_REQ_L2", [LineTwo])
                ],
                _ =>
                [
                    Requirement(request, "DEPARTMENT_REQ", [LineOne])
                ]
            };
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

        private ApprovalRequirementDefinition Requirement(
            ApprovalSubmissionRequest request,
            string sourceKey,
            IReadOnlyList<Guid> lineIds) => new(
            sourceKey,
            "DEPARTMENT",
            SystemRole.ItReviewer,
            AuthorityRequirement.None,
            DecisionScopeDescriptor.Create(
                request.OrganizationId, [new DecisionScopeEntry(ScopeDimension.Department, DepartmentId, 1)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
            [OriginatorId],
            lineIds.Select(lineId => new ApprovalTarget("LINE", lineId, 1, MaterialDigest)).ToArray(),
            []);
    }

    /// <summary>Deterministic UTC clock for the reconciliation and transition runs.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A fresh unit of work per operation, exactly like a scoped request in production.</summary>
    private static Services Fresh(EvolutionEnvironment environment, EvolutionAdapter adapter) =>
        environment.CreateServices(adapter);

    private sealed record Services(
        ApprovalSubmissionService Submission,
        ApprovalDecisionService Decision,
        ApprovalReconciliationService Reconciliation,
        ApprovalDelegationService Delegations,
        ApprovalDelegationTransitionProcessor Transitions,
        ApprovalSupersessionService Supersessions,
        ApprovalEvidenceRevocationService Revocations);

    private sealed class EvolutionEnvironment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private string connectionString = null!;
        private readonly TimeProvider timeProvider = new FixedTimeProvider(Now);

        public static async Task<EvolutionEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new EvolutionEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            environment.connectionString = environment.container.GetConnectionString();
            await using var context = environment.CreateContext();
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
            foreach (var userId in new[] { DelegatorId, DelegateeId, OriginatorId })
            {
                await environment.SeedAsync(userId, cancellationToken);
            }

            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(connectionString)
                .Options);

        public async Task<(Guid TaskId, int TaskVersion)> TaskAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var task = await (
                    from candidate in context.ApprovalTasks
                    join requirement in context.ApprovalRequirements
                        on candidate.RequirementId equals requirement.Id
                    where candidate.CaseId == caseId && requirement.SourceRequirementKey == "DEPARTMENT_REQ"
                    select candidate)
                .SingleAsync(cancellationToken);
            return (task.Id, task.Version);
        }

        public async Task<Guid> AssigneeAsync(Guid taskId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var task = await context.ApprovalTasks.SingleAsync(
                record => record.Id == taskId, cancellationToken);
            return task.CurrentAssigneeUserId
                ?? throw new InvalidOperationException("The task has no current assignee.");
        }

        public async Task<int> CaseVersionAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalCases.SingleAsync(
                record => record.Id == caseId, cancellationToken)).Version;
        }

        /// <summary>Processes the pending runs of one delegation with the durable service (REQ-03).</summary>
        public async Task ProcessDelegationRunsAsync(Guid delegationId, CancellationToken cancellationToken)
        {
            var services = CreateServices(new EvolutionAdapter());
            await using var context = CreateContext();
            var runIds = await context.ApprovalReconciliationRuns
                .Where(record => record.DelegationId == delegationId &&
                                 record.Status != ApprovalReconciliationCodes.StatusCompleted)
                .OrderBy(record => record.Id)
                .Select(record => record.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var runId in runIds)
            {
                await services.Reconciliation.ProcessAsync(
                    runId, "integration-test", $"corr-{Guid.NewGuid():N}", cancellationToken);
            }
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
            if (userId != OriginatorId)
            {
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
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        public Services CreateServices(IApprovalSubmissionAdapter adapter)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Approval:Workloads:0:Issuer"] = Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = Workload.ClientId,
                    ["Approval:OwnerWorkloads:0:AdapterId"] = "adapter",
                    ["Approval:OwnerWorkloads:0:AdapterVersion"] = "v1",
                    ["Approval:OwnerWorkloads:0:Issuer"] = Workload.Issuer,
                    ["Approval:OwnerWorkloads:0:ClientId"] = Workload.ClientId
                })
                .Build();
            var context = CreateContext();
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var registry = new ApprovalSubmissionAdapterRegistry([adapter]);
            var scopeResolver = new ApprovalScopeResolver(context);
            var assignmentEngine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), scopeResolver);
            var reconciliation = new ApprovalReconciliationService(
                context, assignmentEngine, loggerFactory.CreateLogger<ApprovalReconciliationService>(),
                timeProvider);
            var delegationService = new ApprovalDelegationService(
                context, scopeResolver, loggerFactory.CreateLogger<ApprovalDelegationService>());
            return new Services(
                new ApprovalSubmissionService(
                    context, registry, new ApprovalOwnerWorkloadRegistry(configuration, allowlist), allowlist,
                    assignmentEngine, loggerFactory.CreateLogger<ApprovalSubmissionService>()),
                new ApprovalDecisionService(
                    context, assignmentEngine, loggerFactory.CreateLogger<ApprovalDecisionService>()),
                reconciliation,
                delegationService,
                new ApprovalDelegationTransitionProcessor(
                    context, delegationService,
                    loggerFactory.CreateLogger<ApprovalDelegationTransitionProcessor>()),
                new ApprovalSupersessionService(
                    context, allowlist, registry, new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
                    assignmentEngine, loggerFactory.CreateLogger<ApprovalSupersessionService>()),
                new ApprovalEvidenceRevocationService(
                    context, loggerFactory.CreateLogger<ApprovalEvidenceRevocationService>()));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
