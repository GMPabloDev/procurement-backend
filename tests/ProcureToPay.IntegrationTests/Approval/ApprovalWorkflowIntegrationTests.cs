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
/// SQL Server persistence guarantees for the approval core: reservation uniqueness,
/// idempotent replay, exactly-one task per requirement and outbox atomicity
/// (REQ-01, REQ-08, REQ-10).
/// </summary>
public sealed class ApprovalWorkflowIntegrationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ApprovalWorkloadIdentity Workload = new("internal://procure-to-pay", "adapter");

    [Fact]
    public void Approval_model_maps_its_schema_and_exactly_one_indexes()
    {
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer("Server=localhost;Database=ProcureToPay_ModelOnly;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var context = new ProcureToPayDbContext(options);

        var caseRecord = context.Model.FindEntityType(typeof(ApprovalCaseRecord))!;
        var task = context.Model.FindEntityType(typeof(ApprovalTaskRecord))!;
        var decision = context.Model.FindEntityType(typeof(ApprovalDecisionRecord))!;
        var decisionTarget = context.Model.FindEntityType(typeof(ApprovalDecisionTargetRecord))!;
        var outbox = context.Model.FindEntityType(typeof(ApprovalOutboxEventRecord))!;
        var reservation = context.Model.FindEntityType(typeof(ApprovalSubmissionReservationRecord))!;
        var signal = context.Model.FindEntityType(typeof(ApprovalPrerequisiteSignalRecord))!;

        Assert.Equal("Approval", caseRecord.GetSchema());
        Assert.Equal("ApprovalCases", caseRecord.GetTableName());
        Assert.Equal("ApprovalOutboxEvents", outbox.GetTableName());
        Assert.True(caseRecord.FindProperty(nameof(ApprovalCaseRecord.RowVersion))!.IsConcurrencyToken);

        // At most one current task per requirement (REQ-10).
        Assert.Contains(task.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(ApprovalTaskRecord.RequirementId)]));

        // Exactly one terminal decision per (requirement, target) (REQ-10).
        Assert.Contains(decisionTarget.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalDecisionTargetRecord.RequirementId),
                nameof(ApprovalDecisionTargetRecord.TargetType),
                nameof(ApprovalDecisionTargetRecord.TargetId),
                nameof(ApprovalDecisionTargetRecord.TargetVersion),
                nameof(ApprovalDecisionTargetRecord.MaterialSnapshotDigest)]));

        // One submission key per workload scope and one case per reservation (REQ-01).
        Assert.Contains(reservation.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalSubmissionReservationRecord.OrganizationId),
                nameof(ApprovalSubmissionReservationRecord.WorkloadIssuer),
                nameof(ApprovalSubmissionReservationRecord.WorkloadClientId),
                nameof(ApprovalSubmissionReservationRecord.SubjectType),
                nameof(ApprovalSubmissionReservationRecord.Operation),
                nameof(ApprovalSubmissionReservationRecord.SubmissionKey)]));
        Assert.Contains(decision.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalDecisionRecord.OrganizationId),
                nameof(ApprovalDecisionRecord.ActorUserId),
                nameof(ApprovalDecisionRecord.DecisionKey)]));
        Assert.Contains(signal.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(ApprovalPrerequisiteSignalRecord.OrganizationId),
                nameof(ApprovalPrerequisiteSignalRecord.PrerequisiteId),
                nameof(ApprovalPrerequisiteSignalRecord.SignalKey)]));
    }

    [Fact]
    public async Task Submission_persists_the_case_graph_and_replays_idempotently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var adapter = new StubAdapter(
            new ApprovalRequirementDefinition(
                "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                OrganizationDecisionScope(), [ApprovalDecisionAction.Approve],
                [StubAdapter.Originator], [StubAdapter.Target("LINE", Guid.Parse("44444444-4444-4444-4444-444444444444"))], []));
        var (service, _) = environment.CreateServices(adapter);

        var command = Command("submission-1");
        var first = await service.SubmitAsync(command, DateTimeOffset.UtcNow, cancellationToken);
        Assert.False(first.Replayed);
        Assert.Equal("BLOCKED", first.Status);

        var second = await service.SubmitAsync(command, DateTimeOffset.UtcNow, cancellationToken);
        Assert.True(second.Replayed);
        Assert.Equal(first.CaseId, second.CaseId);

        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalRequirements.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalTasks.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalSubmissionReservations.CountAsync(cancellationToken));
            var requirement = await verification.ApprovalRequirements.SingleAsync(cancellationToken);
            Assert.Equal(
                ApprovalRequirementStatus.Unassigned,
                (ApprovalRequirementStatus)requirement.Status);
            Assert.Equal(
                ApprovalCaseStatus.Blocked,
                (ApprovalCaseStatus)(await verification.ApprovalCases.SingleAsync(cancellationToken)).Status);
        }

        // The same key with different content is a conflict.
        var changed = command with { SubjectVersion = 2 };
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.SubmitAsync(changed, DateTimeOffset.UtcNow, cancellationToken));
    }

    [Fact]
    public async Task Concurrent_identical_submissions_create_a_single_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var adapter = new StubAdapter(
            new ApprovalRequirementDefinition(
                "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                OrganizationDecisionScope(), [ApprovalDecisionAction.Approve],
                [StubAdapter.Originator], [StubAdapter.Target("LINE", Guid.Parse("55555555-5555-5555-5555-555555555555"))], []));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var (service, _) = environment.CreateServices(adapter);
            return await service.SubmitAsync(Command("submission-concurrent"), DateTimeOffset.UtcNow, cancellationToken);
        }));

        Assert.Single(outcomes.Select(outcome => outcome.CaseId).Distinct());
        await using var verification = environment.CreateContext();
        Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalSubmissionReservations.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Prerequisite_signal_is_idempotent_and_writes_one_outbox_event_per_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var first = StubAdapter.Target("LINE", Guid.Parse("66666666-6666-6666-6666-666666666666"));
        var second = StubAdapter.Target("LINE", Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var adapter = new StubAdapter(
            new ApprovalRequirementDefinition(
                "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                OrganizationDecisionScope(), [ApprovalDecisionAction.Approve], [StubAdapter.Originator],
                [first],
                [new ApprovalDependencyRef(DependencyPredecessorKind.External, "BUDGET_CHECK", [first])]),
            new ExternalPrerequisiteDefinition(
                "BUDGET_CHECK", "adapter", "v1", "BUDGET_CHECK", new string('d', 64),
                "{\"minimum\":1}", [first, second]));
        var (service, workflow) = environment.CreateServices(adapter);
        var outcome = await service.SubmitAsync(Command("submission-signal"), DateTimeOffset.UtcNow, cancellationToken);

        Guid prerequisiteId;
        await using (var context = environment.CreateContext())
        {
            prerequisiteId = (await context.ApprovalPrerequisites.SingleAsync(cancellationToken)).Id;
        }

        var signal = new ApprovalSignalCommand(
            Workload, true, "signal-1", 1, "evidence", new string('e', 64), "correlation");
        var signalled = await workflow.SignalAsync(prerequisiteId, signal, DateTimeOffset.UtcNow, cancellationToken);
        Assert.False(signalled.Replayed);
        Assert.Equal(2, signalled.OutboxEventIds.Count);
        Assert.Equal("BLOCKED", signalled.Status);

        var replayed = await workflow.SignalAsync(prerequisiteId, signal, DateTimeOffset.UtcNow, cancellationToken);
        Assert.True(replayed.Replayed);

        await Assert.ThrowsAsync<DomainConflictException>(() => workflow.SignalAsync(
            prerequisiteId,
            signal with { Satisfied = false },
            DateTimeOffset.UtcNow,
            cancellationToken));

        await using var verification = environment.CreateContext();
        Assert.Equal(1, await verification.ApprovalPrerequisiteSignals.CountAsync(cancellationToken));
        Assert.Equal(2, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        Assert.All(
            await verification.ApprovalOutboxEvents.ToArrayAsync(cancellationToken),
            record => Assert.Equal("SATISFIED", record.Result));
        Assert.Equal(outcome.CaseId, signalled.CaseId);
    }

    private static DecisionScopeDescriptor OrganizationDecisionScope() =>
        DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);

    private static ApprovalSubmissionCommand Command(string submissionKey) => new(
        Workload,
        OrganizationId,
        "PURCHASE_REQUEST",
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        1,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        StubAdapter.Originator,
        "correlation");

    private sealed class StubAdapter(params object[] definitions) : IApprovalSubmissionAdapter
    {
        public static readonly Guid Originator = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public ApprovalAdapterDescriptor Descriptor { get; } = new(
            "adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            var requirements = definitions.OfType<ApprovalRequirementDefinition>().ToArray();
            var prerequisites = definitions.OfType<ExternalPrerequisiteDefinition>().ToArray();
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
                prerequisites));
        }

        public static ApprovalTarget Target(string type, Guid id) => new(type, id, 1, new string('c', 64));
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private string connectionString = null!;

        public static async Task<SqlEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new SqlEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            environment.connectionString = environment.container.GetConnectionString();
            await using var context = environment.CreateContext();
            await context.Database.MigrateAsync(cancellationToken);
            return environment;
        }

        public ProcureToPayDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            return new ProcureToPayDbContext(options);
        }

        public (ApprovalSubmissionService Submission, ApprovalWorkflowService Workflow) CreateServices(
            IApprovalSubmissionAdapter adapter)
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
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
            var registry = new ApprovalSubmissionAdapterRegistry([adapter]);
            // One unit of work per service pair: the routing engine adds assignment and audit
            // rows that the transition service must save in its own transaction.
            var context = CreateContext();
            var assignmentEngine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
            return (
                new ApprovalSubmissionService(
                    context, registry, ownerWorkloads, allowlist, assignmentEngine,
                    loggerFactory.CreateLogger<ApprovalSubmissionService>()),
                // pi-lens-ignore: lsp:CS1729
                new ApprovalWorkflowService(
                    context, allowlist, assignmentEngine,
                    loggerFactory.CreateLogger<ApprovalWorkflowService>()));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
