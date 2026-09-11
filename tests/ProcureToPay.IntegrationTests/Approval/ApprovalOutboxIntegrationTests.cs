using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Approval;

/// <summary>
/// Operational evidence for the approval outbox (REQ-10, NFR-02, NFR-04, NFR-05, CA-06, CA-08):
/// at-least-once delivery with persistent leases, the contractual retry backoff, dead letters,
/// administrative replay that never edits the payload, restart recovery and minimized telemetry.
/// </summary>
public sealed class ApprovalOutboxIntegrationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CaseId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TargetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Clock = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dispatch_delivers_due_events_once_with_the_persisted_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer();
        var (dispatcher, _) = environment.CreateDispatcher(consumer);
        var eventId = await environment.SeedPendingAsync(Clock, cancellationToken);
        var payload = await environment.PayloadAsync(eventId, cancellationToken);

        var outcome = await dispatcher.DispatchAsync(OrganizationId, "instance-1", Clock, 50, cancellationToken);

        Assert.Equal(1, outcome.Leased);
        Assert.Equal(1, outcome.Delivered);
        var delivery = Assert.Single(consumer.Deliveries);
        Assert.Equal(eventId, delivery.EventId);
        Assert.Equal(ApprovalOutboxPolicy.ContractVersion, delivery.ContractVersion);
        // The dispatcher never rewrites the durable payload.
        Assert.Equal(payload, delivery.PayloadJson);

        await using var verification = environment.CreateContext();
        var record = await verification.ApprovalOutboxEvents.SingleAsync(cancellationToken);
        Assert.Equal((int)ApprovalOutboxState.Delivered, record.State);
        Assert.Equal(0, record.Attempts);
        Assert.NotNull(record.DeliveredAt);
        Assert.Null(record.LockedUntil);
        Assert.Null(record.LockOwner);
    }

    [Fact]
    public async Task Two_instances_never_deliver_the_same_event_and_restart_recovers_abandoned_leases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer();
        var eventId = await environment.SeedPendingAsync(Clock, cancellationToken);

        // Two instances race for the same due event; the lease makes exactly one of them deliver.
        var instances = Enumerable.Range(0, 2)
            .Select(index => environment.CreateDispatcher(consumer).Dispatcher)
            .ToArray();
        await Task.WhenAll(instances.Select(dispatcher =>
            dispatcher.DispatchAsync(OrganizationId, "instance-race", Clock, 50, cancellationToken)));

        Assert.Single(consumer.Deliveries);
        Assert.Equal(eventId, consumer.Deliveries[0].EventId);

        // A second event held by a live lease is invisible to other instances.
        var leasedId = await environment.SeedPendingAsync(Clock, cancellationToken);
        await environment.SetLeaseAsync(leasedId, "crashed-instance", Clock.AddMinutes(1), cancellationToken);
        var (other, _) = environment.CreateDispatcher(consumer);
        var skipped = await other.DispatchAsync(OrganizationId, "instance-2", Clock, 50, cancellationToken);
        Assert.Equal(0, skipped.Leased);

        // After a restart the abandoned lease expires and the event becomes dispatchable again.
        var (restarted, _) = environment.CreateDispatcher(consumer);
        var recovered = await restarted.DispatchAsync(
            OrganizationId, "instance-2", Clock.AddMinutes(5), 50, cancellationToken);
        Assert.Equal(1, recovered.Leased);
        Assert.Equal(1, recovered.Delivered);
        Assert.Contains(consumer.Deliveries, delivery => delivery.EventId == leasedId);
    }

    [Fact]
    public async Task Failures_follow_the_contract_backoff_and_dead_letter_after_ten_attempts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer { Failure = new InvalidOperationException("delivery rejected") };
        var (dispatcher, _) = environment.CreateDispatcher(consumer);
        await environment.SeedPendingAsync(Clock, cancellationToken);

        var first = await dispatcher.DispatchAsync(OrganizationId, "instance-1", Clock, 50, cancellationToken);
        Assert.Equal(1, first.Failed);
        await using (var verification = environment.CreateContext())
        {
            var record = await verification.ApprovalOutboxEvents.SingleAsync(cancellationToken);
            Assert.Equal(1, record.Attempts);
            // 1 s after the first failure (REQ-10).
            Assert.Equal(Clock.AddSeconds(1), record.NextAttemptAt);
            // Only the exception type is persisted, never its message (NFR-05).
            Assert.Equal(nameof(InvalidOperationException), record.LastError);
        }

        // Drive the remaining attempts until the event is parked as a dead letter.
        for (var attempt = 2; attempt <= ApprovalOutboxPolicy.MaxAttempts; attempt++)
        {
            var clock = Clock.AddMinutes(attempt * 11);
            var outcome = await dispatcher.DispatchAsync(OrganizationId, "instance-1", clock, 50, cancellationToken);
            if (attempt < ApprovalOutboxPolicy.MaxAttempts)
            {
                Assert.Equal(1, outcome.Failed);
            }
            else
            {
                Assert.Equal(1, outcome.DeadLettered);
            }
        }

        await using var final = environment.CreateContext();
        var deadLetter = await final.ApprovalOutboxEvents.SingleAsync(cancellationToken);
        Assert.Equal((int)ApprovalOutboxState.DeadLetter, deadLetter.State);
        Assert.Equal(ApprovalOutboxPolicy.MaxAttempts, deadLetter.Attempts);

        // A dead letter is not dispatched again even when it becomes due.
        var idle = await dispatcher.DispatchAsync(
            OrganizationId, "instance-1", Clock.AddDays(1), 50, cancellationToken);
        Assert.Equal(0, idle.Leased);
        Assert.Null(deadLetter.DeliveredAt);
    }

    [Fact]
    public async Task Admin_replay_resets_a_dead_letter_without_editing_its_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer { Failure = new InvalidOperationException("delivery rejected") };
        var (dispatcher, administration) = environment.CreateDispatcher(consumer);
        var eventId = await environment.SeedPendingAsync(Clock, cancellationToken);
        var payload = await environment.PayloadAsync(eventId, cancellationToken);

        for (var attempt = 1; attempt <= ApprovalOutboxPolicy.MaxAttempts; attempt++)
        {
            await dispatcher.DispatchAsync(
                OrganizationId, "instance-1", Clock.AddMinutes(attempt * 11), 50, cancellationToken);
        }

        await using (var verification = environment.CreateContext())
        {
            Assert.Equal(
                (int)ApprovalOutboxState.DeadLetter,
                (await verification.ApprovalOutboxEvents.SingleAsync(cancellationToken)).State);
        }

        var replayed = await administration.ReplayAsync(
            OrganizationId, eventId, ActorId, "correlation-replay", Clock.AddDays(1), cancellationToken);

        Assert.Equal("PENDING", replayed.State);
        Assert.Equal(0, replayed.Attempts);
        await using (var verification = environment.CreateContext())
        {
            var record = await verification.ApprovalOutboxEvents.SingleAsync(cancellationToken);
            // The contractual payload is byte-identical after the administrative replay.
            Assert.Equal(payload, record.PayloadJson);
            Assert.Equal((int)ApprovalOutboxState.Pending, record.State);
            Assert.Equal(0, record.Attempts);
            Assert.Null(record.LastError);
            Assert.Contains(
                await verification.ApprovalAuditEntries.ToArrayAsync(cancellationToken),
                entry => entry.Action == "OUTBOX_REPLAYED" && entry.ActorId == ActorId);
        }

        // With the consumer repaired the replayed event is delivered.
        consumer.Failure = null;
        var delivered = await dispatcher.DispatchAsync(
            OrganizationId, "instance-1", Clock.AddDays(1), 50, cancellationToken);
        Assert.Equal(1, delivered.Delivered);
        // Neither the rejected attempts nor the replay rewrote the durable payload.
        Assert.NotEmpty(consumer.Deliveries);
        Assert.All(consumer.Deliveries, delivery => Assert.Equal(payload, delivery.PayloadJson));

        // A delivered event does not need a replay.
        await Assert.ThrowsAsync<DomainConflictException>(() => administration.ReplayAsync(
            OrganizationId, eventId, ActorId, "correlation-replay-2", Clock.AddDays(1), cancellationToken));
    }

    [Fact]
    public async Task Consumer_contract_deduplicates_at_least_once_deliveries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer();
        var (dispatcher, _) = environment.CreateDispatcher(consumer);
        var eventId = await environment.SeedPendingAsync(Clock, cancellationToken);

        await dispatcher.DispatchAsync(OrganizationId, "instance-1", Clock, 50, cancellationToken);
        // A redelivery of the same event must be ignored by the consumer.
        await consumer.DeliverAsync(new ApprovalResultDelivery(
            eventId, ApprovalOutboxPolicy.ContractVersion, "{}", "replay"), cancellationToken);

        Assert.Equal(1, consumer.AppliedEffects);
        Assert.Equal(2, consumer.Deliveries.Count(delivery => delivery.EventId == eventId));
    }

    [Fact]
    public async Task Backlog_reports_overdue_state_and_telemetry_carries_no_personal_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        var consumer = new RecordingConsumer();
        var (dispatcher, administration) = environment.CreateDispatcher(consumer);

        // One open case makes a reconciliation due, and an old pending event makes the backlog overdue.
        await environment.SeedOpenCaseAsync(cancellationToken);
        await environment.SeedPendingAsync(Clock, cancellationToken);

        var backlog = await administration.GetBacklogAsync(OrganizationId, Clock.AddMinutes(6), cancellationToken);
        Assert.Equal(1, backlog.Pending);
        Assert.Equal(0, backlog.DeadLetter);
        Assert.True(backlog.IsOverdue);
        Assert.True(backlog.ReconciliationOverdue);
        Assert.Equal(Clock, backlog.OldestPendingCreatedAt);

        var measurements = new List<(string Name, string? Outcome)>();
        var activities = new List<Activity>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApprovalTelemetry.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "approval.outcome")
                {
                    outcome = tag.Value?.ToString();
                }

                // No reason, snapshot or actor identity may be emitted (NFR-05).
                Assert.DoesNotContain("reason", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("snapshot", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("actor", tag.Key, StringComparison.OrdinalIgnoreCase);
            }

            measurements.Add((instrument.Name, outcome));
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ApprovalTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(activityListener);
        activityListener.ActivityStopped = activities.Add;

        await dispatcher.DispatchAsync(OrganizationId, "instance-1", Clock.AddMinutes(6), 50, cancellationToken);

        Assert.Contains(measurements, entry =>
            entry.Name == "approval.dispatches" && entry.Outcome == "DELIVERED");
        var dispatch = Assert.Single(activities);
        Assert.Equal("approval.dispatch", dispatch.OperationName);
        Assert.Equal("DELIVERED", dispatch.GetTagItem("approval.outcome"));
        Assert.NotNull(dispatch.GetTagItem("approval.correlation_reference"));
        Assert.Null(dispatch.GetTagItem("approval.reason"));
    }

    [Fact]
    public async Task Additive_migrations_revert_and_reapply_while_preserving_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SqlEnvironment.StartAsync(cancellationToken);
        // Seed durable history: one open case and one pending outbox event.
        await environment.SeedOpenCaseAsync(cancellationToken);
        var eventId = await environment.SeedPendingAsync(Clock, cancellationToken);

        await using var context = environment.CreateContext();
        var migrator = context.GetService<IMigrator>();
        Assert.True(await environment.HasColumnAsync("ApprovalDecisions", "TaskVersion", cancellationToken));

        // Revert the additive Approval migrations down to the foundation of the feature.
        await migrator.MigrateAsync("20260911100346_ApprovalWorkflowFoundation", cancellationToken);

        // The revert removes only what the additive migrations added: durable history survives
        // and no case, assignment, decision, audit or outbox row is destroyed (REQ-10).
        Assert.False(await environment.HasColumnAsync("ApprovalDecisions", "TaskVersion", cancellationToken));
        Assert.Equal(1, await environment.CountCasesAsync(cancellationToken));
        Assert.Equal(1, await environment.CountOutboxAsync(cancellationToken));

        // Recovery corrects forward and the same durable history is still there.
        await migrator.MigrateAsync(targetMigration: null, cancellationToken);
        Assert.True(await environment.HasColumnAsync("ApprovalDecisions", "TaskVersion", cancellationToken));
        Assert.Equal(1, await environment.CountCasesAsync(cancellationToken));
        Assert.Equal(1, await environment.CountOutboxAsync(cancellationToken));
        Assert.Equal(eventId, await environment.OnlyOutboxIdAsync(cancellationToken));
    }

    private sealed class RecordingConsumer : IApprovalResultConsumer
    {
        private readonly HashSet<(Guid EventId, string ContractVersion)> applied = [];

        public string ContractVersion => ApprovalOutboxPolicy.ContractVersion;

        public Exception? Failure { get; set; }

        public List<ApprovalResultDelivery> Deliveries { get; } = [];

        public int AppliedEffects => applied.Count;

        public Task DeliverAsync(ApprovalResultDelivery delivery, CancellationToken cancellationToken = default)
        {
            Deliveries.Add(delivery);
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            // Consumers deduplicate by event_id + contract_version (REQ-10).
            if (applied.Add((delivery.EventId, delivery.ContractVersion)))
            {
                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        }
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

        public async Task<Guid> SeedPendingAsync(DateTimeOffset createdAt, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var eventId = Guid.NewGuid();
            context.ApprovalOutboxEvents.Add(new ApprovalOutboxEventRecord
            {
                Id = eventId,
                CaseId = CaseId,
                OrganizationId = OrganizationId,
                RequirementId = null,
                TargetType = "LINE",
                TargetId = TargetId,
                TargetVersion = 1,
                MaterialSnapshotDigest = new string('c', 64),
                Result = "APPROVED",
                ContractVersion = ApprovalOutboxPolicy.ContractVersion,
                PayloadJson = "{\"contract_version\":\"approval-result/v1\",\"event_id\":\"" + eventId + "\"}",
                State = (int)ApprovalOutboxState.Pending,
                Attempts = 0,
                NextAttemptAt = createdAt,
                CreatedAt = createdAt,
                CorrelationReference = "correlation-outbox",
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
            return eventId;
        }

        public async Task SeedOpenCaseAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            context.ApprovalCases.Add(new ApprovalCaseRecord
            {
                Id = CaseId,
                OrganizationId = OrganizationId,
                SubjectType = "PURCHASE_REQUEST",
                SubjectId = Guid.NewGuid(),
                SubjectVersion = 1,
                Operation = "SUBMIT",
                SourceSnapshotDigest = new string('a', 64),
                WorkloadIssuer = "internal://procure-to-pay",
                WorkloadClientId = "adapter",
                SubmissionKey = "submission-outbox",
                SubmissionFingerprint = new string('b', 64),
                OriginatorId = Guid.NewGuid(),
                Status = (int)ApprovalCaseStatus.Open,
                Version = 1,
                CreatedAt = Clock,
                CorrelationReference = "correlation-outbox"
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task SetLeaseAsync(
            Guid eventId,
            string owner,
            DateTimeOffset lockedUntil,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var record = await context.ApprovalOutboxEvents.SingleAsync(
                candidate => candidate.Id == eventId, cancellationToken);
            record.LockOwner = owner;
            record.LockedUntil = lockedUntil;
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<string> PayloadAsync(Guid eventId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalOutboxEvents.SingleAsync(
                candidate => candidate.Id == eventId, cancellationToken)).PayloadJson;
        }

        public async Task<bool> HasColumnAsync(
            string table,
            string column,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var count = await context.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = 'Approval' AND TABLE_NAME = {table} AND COLUMN_NAME = {column}
                    """)
                .SingleAsync(cancellationToken);
            return count > 0;
        }

        public async Task<int> CountCasesAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalCases.CountAsync(cancellationToken);
        }

        public async Task<int> CountOutboxAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalOutboxEvents.CountAsync(cancellationToken);
        }

        public async Task<Guid> OnlyOutboxIdAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalOutboxEvents.SingleAsync(cancellationToken)).Id;
        }

        public (ApprovalOutboxDispatcher Dispatcher, ApprovalOutboxAdministrationService Administration)
            CreateDispatcher(IApprovalResultConsumer consumer)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var context = CreateContext();
            return (
                new ApprovalOutboxDispatcher(
                    context,
                    new ApprovalResultConsumerRegistry([consumer]),
                    loggerFactory.CreateLogger<ApprovalOutboxDispatcher>()),
                new ApprovalOutboxAdministrationService(
                    context,
                    loggerFactory.CreateLogger<ApprovalOutboxAdministrationService>()));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
