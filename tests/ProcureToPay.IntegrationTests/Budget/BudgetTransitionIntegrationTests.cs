using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Budget;

/// <summary>
/// SPEC 08 REQ-09 / CA-07 evidence over SQL Server: the closed producer table resolves exactly one
/// workload, COMMIT consumes the RESERVED remainder, CONSUME consumes the COMMITTED remainder and
/// REVERSE reverts only the un-advanced delta of its own source. Every path fails closed.
/// </summary>
public sealed class BudgetTransitionIntegrationTests
{
    private static readonly ApprovalWorkloadIdentity Producer =
        new("internal://procure-to-pay", "purchase-order-domain");

    [Fact]
    public async Task A_commit_consumes_the_reserved_remainder_and_is_idempotent_by_key()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(
            100m, TestContext.Current.CancellationToken);

        var committed = await harness.CommitAsync(
            reservedMovement, 60m, "commit-1", TestContext.Current.CancellationToken);
        var result = committed.Result;
        Assert.False(result.Replayed);
        Assert.Single(result.Movements);
        Assert.Equal("COMMITTED", result.Movements[0].Type);
        Assert.Equal(60m, result.Movements[0].Amount);
        Assert.Equal(reservedMovement, result.Movements[0].ParentMovementId);

        var buckets = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(60m, buckets.Committed);
        Assert.Equal(40m, buckets.Reserved);

        // Replay: the same key with the same preimage returns the recorded movement instead of posting.
        var replay = (await harness.CommitAsync(
            reservedMovement, 60m, "commit-1", TestContext.Current.CancellationToken)).Result;
        Assert.True(replay.Replayed);
        Assert.Equal(result.OperationId, replay.OperationId);
        Assert.Equal(result.Movements[0].MovementId, replay.Movements[0].MovementId);

        // The same key with another preimage is a contract conflict.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CommitAsync(
            reservedMovement, 50m, "commit-1", TestContext.Current.CancellationToken));
        var afterConflict = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(60m, afterConflict.Committed);
    }

    [Fact]
    public async Task A_consume_uses_the_committed_remainder_and_zero_producers_fail_closed()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(
            100m, TestContext.Current.CancellationToken);
        var commit = await harness.CommitAsync(
            reservedMovement, 100m, "commit-1", TestContext.Current.CancellationToken);

        var consume = await harness.ConsumeAsync(
            commit.Result.Movements[0].MovementId, 40m, "consume-1", TestContext.Current.CancellationToken);
        Assert.Equal("CONSUMED", consume.Movements[0].Type);
        var buckets = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(40m, buckets.Consumed);
        Assert.Equal(60m, buckets.Committed);

        // A deployment without registrations refuses the operation before any movement exists.
        await Assert.ThrowsAsync<BudgetDependencyUnavailableException>(() => harness.CommitAsync(
            reservedMovement,
            10m,
            "commit-2",
            Harness.NoProducerConfiguration.Instance,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_producers_for_the_same_triple_are_ambiguous_and_rejected()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (_, reservedMovement) = await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);

        var ambiguous = new Harness.ProducerConfiguration(
            ("COMMIT", "PURCHASE_ORDER", "internal://procure-to-pay", "purchase-order-domain"),
            ("COMMIT", "PURCHASE_ORDER", "internal://procure-to-pay", "other-order-domain"));
        await Assert.ThrowsAsync<BudgetDependencyUnavailableException>(() => harness.CommitAsync(
            reservedMovement, 10m, "commit-ambiguous", ambiguous, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_foreign_workload_never_posts_a_transition()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => harness.CommitAsync(
            reservedMovement,
            10m,
            "commit-foreign",
            Harness.DefaultConfiguration,
            TestContext.Current.CancellationToken,
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "foreign-domain")));
        var buckets = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(100m, buckets.Reserved);
        Assert.Equal(0m, buckets.Committed);
    }

    [Fact]
    public async Task A_reverse_reverts_only_the_delta_its_own_source_advanced()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);
        var commit = await harness.CommitAsync(
            reservedMovement, 100m, "commit-1", TestContext.Current.CancellationToken);

        var reverse = await harness.ReverseAsync(
            commit.Result.Movements[0].MovementId,
            40m,
            "reverse-1",
            commit.Source,
            TestContext.Current.CancellationToken);
        Assert.Equal("REVERSE", reverse.Movements[0].Type);
        var buckets = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(60m, buckets.Committed);
        Assert.Equal(40m, buckets.Reserved);

        // Reverting more than the remainder of the parent is a contract conflict (409), never a
        // negative bucket.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.ReverseAsync(
            commit.Result.Movements[0].MovementId,
            80m,
            "reverse-2",
            commit.Source,
            TestContext.Current.CancellationToken));
        var afterExcess = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(60m, afterExcess.Committed);
        Assert.Equal(40m, afterExcess.Reserved);
    }

    [Fact]
    public async Task A_reverse_from_another_source_is_forbidden()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (_, reservedMovement) = await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);
        var commit = await harness.CommitAsync(
            reservedMovement, 100m, "commit-1", TestContext.Current.CancellationToken);

        var foreignSource = new BudgetTransitionSource(
            BudgetCodes.InvoiceSourceType, Guid.NewGuid(), 1, new string('e', 64));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => harness.ReverseAsync(
            commit.Result.Movements[0].MovementId,
            10m,
            "reverse-foreign",
            foreignSource,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_redelivered_reservation_reuses_its_operations_without_double_booking()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);

        // A crash after the reservation was confirmed but before the signal is replayed by the same
        // keys: the ledger resolves the recorded operations and posts nothing twice (REQ-07, NFR-03).
        await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);
        var repeated = await harness.ReserveOutcomeAsync(100m, TestContext.Current.CancellationToken);
        Assert.Equal(BudgetCheckResult.Available, repeated.Result);
        Assert.NotNull(repeated.ReserveOperationId);

        await using var context = harness.CreateContext();
        var reserves = await context.BudgetOperations
            .Where(operation => operation.Kind == (int)BudgetOperationKind.ApprovalReserve)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, reserves.Length);
        Assert.Equal(2, await context.BudgetMovements.CountAsync(TestContext.Current.CancellationToken));
        var balance = await context.BudgetBalances.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100m, balance.Reserved);
        Assert.Equal(900m, balance.Allocated - balance.Reserved);
    }

    [Fact]
    public async Task Rebuilding_a_balance_matches_the_posted_movements_exactly()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(100m, TestContext.Current.CancellationToken);
        var commit = await harness.CommitAsync(
            reservedMovement, 60m, "commit-1", TestContext.Current.CancellationToken);
        await harness.ReverseAsync(
            commit.Result.Movements[0].MovementId,
            20m,
            "reverse-1",
            commit.Source,
            TestContext.Current.CancellationToken);

        // The append-only history reconstructs exactly the projected buckets (NFR-01, REQ-02).
        await using var context = harness.CreateContext();
        var budgets = new BudgetPersistenceService(context);
        var rebuilt = await budgets.RebuildAsync(
            harness.OrganizationId, position, TestContext.Current.CancellationToken);
        var projected = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.NotNull(rebuilt);
        Assert.Equal(projected.Allocated, rebuilt!.Allocated);
        Assert.Equal(projected.Reserved, rebuilt.Reserved);
        Assert.Equal(projected.Committed, rebuilt.Committed);
        Assert.Equal(projected.Consumed, rebuilt.Consumed);
        Assert.Equal(1000m, rebuilt.Allocated);
        Assert.Equal(60m, rebuilt.Reserved);
        Assert.Equal(40m, rebuilt.Committed);
    }

    /// <summary>Produces the producer table a test deployment runs with.</summary>
    private interface ITransitionConfiguration
    {
        IConfiguration Build();
    }

    [Fact]
    public async Task A_superseded_hold_transfers_to_the_replacement_all_or_nothing()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(
            100m, TestContext.Current.CancellationToken);

        // The replacement needs the predecessor's 100 plus 50 of its own: only the transfer, which
        // counts the previous hold as available, can reserve it.
        var replacementCaseId = Guid.NewGuid();
        var replacement = await harness.TransferAsync(
            [reservedMovement], 150m, replacementCaseId, TestContext.Current.CancellationToken);
        Assert.Equal(BudgetCheckResult.Available, replacement.Result);
        var afterTransfer = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(150m, afterTransfer.Reserved);

        // Replay resolves the same operations and touches no bucket.
        var replay = await harness.TransferAsync(
            [reservedMovement], 150m, replacementCaseId, TestContext.Current.CancellationToken);
        Assert.Equal(replacement.ReserveOperationId, replay.ReserveOperationId);
        Assert.Equal(replacement.ReverseOperationId, replay.ReverseOperationId);
        Assert.Equal(150m, (await harness.BucketsAsync(position, TestContext.Current.CancellationToken)).Reserved);
    }

    [Fact]
    public async Task A_transfer_that_cannot_cover_the_whole_set_reverses_nothing()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var (position, reservedMovement) = await harness.ReserveAsync(
            100m, TestContext.Current.CancellationToken);

        // 100 of predecessor hold + 900 of free allocation is short of 1100: the whole set must fail
        // and the predecessor keeps its hold.
        var failed = await harness.TransferAsync(
            [reservedMovement], 1100m, Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(BudgetCheckResult.Insufficient, failed.Result);
        Assert.Null(failed.ReserveOperationId);
        Assert.Empty(failed.ReversedMovements);
        Assert.Empty(failed.ReservedMovements);

        var buckets = await harness.BucketsAsync(position, TestContext.Current.CancellationToken);
        Assert.Equal(100m, buckets.Reserved);
        Assert.Equal(900m, buckets.Available);
    }

    [Fact]
    public async Task A_stale_fence_cannot_confirm_a_movement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (_, parent) = await harness.ReserveAsync(100m, cancellationToken);
        var attemptId = await harness.CreateAttemptAsync(
            fencingToken: 5,
            leaseOwner: "live-worker",
            leaseUntil: DateTimeOffset.UtcNow.AddSeconds(30),
            cancellationToken);

        // A worker whose lease was reclaimed holds an obsolete fencing token: the posting
        // transaction rejects it without confirming the movement (REQ-07, CA-06).
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.FencedCommitAsync(
            parent, 50m, attemptId, fencingToken: 4, "live-worker", cancellationToken));
        await using (var afterStale = harness.CreateContext())
        {
            Assert.Equal(
                0,
                await afterStale.BudgetMovements.CountAsync(
                    movement => movement.Type == (int)BudgetMovementType.Committed, cancellationToken));
        }

        // The exact current fence confirms the same movement.
        await harness.FencedCommitAsync(
            parent, 50m, attemptId, fencingToken: 5, "live-worker", cancellationToken);
        await using var verification = harness.CreateContext();
        Assert.Equal(
            1,
            await verification.BudgetMovements.CountAsync(
                movement => movement.Type == (int)BudgetMovementType.Committed, cancellationToken));
    }

    [Fact]
    public async Task Two_concurrent_allocation_revisions_leave_one_current_version()
    {
        await using var harness = await Harness.StartAsync(TestContext.Current.CancellationToken);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Two administrators revise from version 1 at the same instant (REQ-01, CA-01).
        var first = Task.Run(
            async () =>
            {
                try
                {
                    await harness.ReviseAllocationAsync(1200m, "alloc-race-a", cancellationToken);
                    return true;
                }
                catch (DomainConflictException)
                {
                    return false;
                }
            },
            cancellationToken);
        var second = Task.Run(
            async () =>
            {
                try
                {
                    await harness.ReviseAllocationAsync(1300m, "alloc-race-b", cancellationToken);
                    return true;
                }
                catch (DomainConflictException)
                {
                    return false;
                }
            },
            cancellationToken);
        Assert.Equal(1, (await Task.WhenAll(first, second)).Count(accepted => accepted));

        await using var verification = harness.CreateContext();
        var versions = await verification.BudgetAllocationVersions
            .OrderBy(version => version.Version)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, versions.Length);
        Assert.Equal(1000m, versions[0].AllocatedAmount);
        var position = await verification.BudgetPositions.SingleAsync(cancellationToken);
        Assert.Equal(2, position.CurrentAllocationVersion);
        var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(versions[1].AllocatedAmount, balance.Allocated);
    }

    [Fact]
    public async Task An_allocation_reduction_below_held_funds_and_invalid_references_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (position, _) = await harness.ReserveAsync(100m, cancellationToken);

        // Reducing below RESERVED+COMMITTED+CONSUMED never changes the projection (REQ-01, CA-01).
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            harness.ReviseAllocationAsync(50m, "alloc-reduce", cancellationToken));
        var unchanged = await harness.BucketsAsync(position, cancellationToken);
        Assert.Equal(1000m, unchanged.Allocated);
        Assert.Equal(100m, unchanged.Reserved);

        // An out-of-range Fiscal Year and a currency other than the organization base are rejected
        // before any history row is appended.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.ReviseAllocationRawAsync(1800, "PEN", 900m, "alloc-year", cancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.ReviseAllocationRawAsync(2026, "USD", 900m, "alloc-currency", cancellationToken));

        // An inexistent Spend Category, a deactivated one and a Cost Center that is not an active
        // position of the organization are rejected too (REQ-01).
        var inactiveCode = await harness.CreateInactiveSpendCategoryAsync("LEGACY", cancellationToken);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.ReviseAllocationWithSpendCategoryAsync("GHOST", 900m, "alloc-ghost", cancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.ReviseAllocationWithSpendCategoryAsync(inactiveCode, 900m, "alloc-inactive", cancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.ReviseAllocationForCostCenterAsync(Guid.NewGuid(), 900m, "alloc-foreign", cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.Single(await verification.BudgetAllocationVersions.ToArrayAsync(cancellationToken));
        var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(1000m, balance.Allocated);
    }

    [Fact]
    public async Task A_multi_position_batch_with_one_failing_movement_confirms_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (firstPosition, firstParent) = await harness.ReserveAsync(100m, cancellationToken);
        var (_, secondSpendCategory) = await harness.CreateSecondPositionAsync(50m, cancellationToken);
        var (secondPosition, secondParent) = await harness.ReserveAtAsync(
            secondSpendCategory,
            40m,
            "budget-request-second",
            "budget-reserve-second",
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            cancellationToken);

        // A batch that fits confirms both positions in one operation (REQ-02, CA-02).
        var confirmed = await harness.CommitBatchAsync(
            [(firstParent, 60m), (secondParent, 30m)], "commit-batch-ok", cancellationToken);
        Assert.Equal(2, confirmed.Movements.Count);

        // The next batch has a valid first movement and an impossible second one: the whole batch
        // must roll back and leave the confirmed state intact.
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CommitBatchAsync(
            [(firstParent, 30m), (secondParent, 20m)], "commit-batch-1", cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.Equal(
            1,
            await verification.BudgetOperations.CountAsync(
                operation => operation.Kind == (int)BudgetOperationKind.Commit, cancellationToken));
        Assert.Equal(
            2,
            await verification.BudgetMovements.CountAsync(
                movement => movement.Type == (int)BudgetMovementType.Committed, cancellationToken));
        var firstBuckets = await harness.BucketsAsync(firstPosition, cancellationToken);
        Assert.Equal(40m, firstBuckets.Reserved);
        Assert.Equal(60m, firstBuckets.Committed);
        var secondBuckets = await harness.BucketsAsync(secondPosition, cancellationToken);
        Assert.Equal(10m, secondBuckets.Reserved);
        Assert.Equal(30m, secondBuckets.Committed);
    }

    [Fact]
    public async Task A_precheck_groups_targets_per_position_and_never_changes_buckets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var firstPosition = await harness.PositionIdAsync(
            "HARDWARE", cancellationToken);
        var (_, secondSpendCategory) = await harness.CreateSecondPositionAsync(200m, cancellationToken);
        var secondPosition = await harness.PositionIdAsync(secondSpendCategory, cancellationToken);

        // Two lines of the first position and one of the second are persisted per target and
        // evaluated as one snapshot (REQ-04, CA-03).
        var outcome = await harness.PrecheckAsync(
            [
                (Guid.NewGuid(), 300m, "HARDWARE"),
                (Guid.NewGuid(), 200m, "HARDWARE"),
                (Guid.NewGuid(), 150m, secondSpendCategory)
            ],
            "precheck-grouped",
            cancellationToken);
        Assert.Equal("AVAILABLE", outcome.Result);

        await using (var verification = harness.CreateContext())
        {
            var operation = await verification.BudgetOperations
                .SingleAsync(record => record.Id == outcome.OperationId, cancellationToken);
            Assert.Equal((int)BudgetOperationKind.Precheck, operation.Kind);
            var requested = await verification.BudgetMovements
                .Where(movement => movement.OperationId == outcome.OperationId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(3, requested.Length);
            Assert.All(
                requested,
                movement => Assert.Equal((int)BudgetMovementType.Requested, movement.Type));
            Assert.Equal(2, requested.Count(movement => movement.PositionId == firstPosition));
            Assert.Equal(1, requested.Count(movement => movement.PositionId == secondPosition));
        }

        var untouchedFirst = await harness.BucketsAsync(firstPosition, cancellationToken);
        Assert.Equal(1000m, untouchedFirst.Allocated);
        Assert.Equal(1000m, untouchedFirst.Available);
        var untouchedSecond = await harness.BucketsAsync(secondPosition, cancellationToken);
        Assert.Equal(200m, untouchedSecond.Allocated);

        // An aggregate that one position cannot cover stays non-binding and reserves nothing.
        var insufficient = await harness.PrecheckAsync(
            [(Guid.NewGuid(), 1200m, "HARDWARE")],
            "precheck-insufficient",
            cancellationToken);
        Assert.NotEqual("AVAILABLE", insufficient.Result);
        await using var afterInsufficient = harness.CreateContext();
        var stillReserved = await harness.BucketsAsync(firstPosition, cancellationToken);
        Assert.Equal(0m, stillReserved.Reserved);
        Assert.Equal(
            1,
            await afterInsufficient.BudgetMovements.CountAsync(
                movement => movement.OperationId == insufficient.OperationId, cancellationToken));

        // Two lines that are individually affordable but whose sum exceeds the position must fail:
        // the evaluation aggregates by position instead of checking line by line (REQ-04, CA-03).
        var splitInsufficient = await harness.PrecheckAsync(
            [(Guid.NewGuid(), 600m, "HARDWARE"), (Guid.NewGuid(), 600m, "HARDWARE")],
            "precheck-split-insufficient",
            cancellationToken);
        Assert.NotEqual("AVAILABLE", splitInsufficient.Result);
        await using var afterSplit = harness.CreateContext();
        Assert.Equal(
            2,
            await afterSplit.BudgetMovements.CountAsync(
                movement => movement.OperationId == splitInsufficient.OperationId, cancellationToken));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string ProducerIssuer = "internal://procure-to-pay";
        public const string ProducerClientId = "purchase-order-domain";

        private MsSqlContainer container = null!;
        private string connectionString = string.Empty;

        public Guid OrganizationId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Guid DepartmentId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Guid ActorId { get; } = Guid.Parse("88888888-8888-8888-8888-888888888888");
        private Guid CostCenterId { get; set; }
        private const string ReservationRequestKey = "budget-request-1";
        private const string ReservationReserveKey = "budget-reserve-1";
        private static readonly BudgetSource ReservationSource = new(
            BudgetCodes.PurchaseRequestSourceType, Guid.Parse("44444444-4444-4444-4444-444444444444"), 1, new string('d', 64));
        private static readonly Guid ReservationCaseId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        private static readonly Guid ReservationSourceLineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        private string SpendCategoryCode { get; set; } = string.Empty;
        private readonly Dictionary<string, BudgetTransitionSource> sources = [];
        private readonly Dictionary<string, BudgetSource> transferSources = [];

        public static async Task<Harness> StartAsync(CancellationToken cancellationToken)
        {
            var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("ProcureToPay_test_2026!")
                .Build();
            await container.StartAsync(cancellationToken);
            var harness = new Harness
            {
                container = container,
                connectionString = container.GetConnectionString()
            };
            await using (var context = harness.CreateContext())
            {
                await context.Database.MigrateAsync(cancellationToken);
                harness.Seed(context);
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = harness.CreateContext())
            {
                var catalogs = new ReferenceCatalogPersistenceService(context);
                var costCenter = await catalogs.CreateCostCenterAsync(
                    harness.OrganizationId,
                    harness.ActorId,
                    "CC-IT-DEV",
                    "Development",
                    harness.DepartmentId,
                    "Cost center for the budget transition tests",
                    "corr-transition-cc",
                    cancellationToken);
                harness.CostCenterId = costCenter.Id;
                var spendCategory = await catalogs.CreateSpendCategoryAsync(
                    harness.OrganizationId,
                    harness.ActorId,
                    "HARDWARE",
                    "Hardware",
                    "Category for the budget transition tests",
                    "corr-transition-sc",
                    cancellationToken);
                harness.SpendCategoryCode = spendCategory.Code;
                var budgets = new BudgetPersistenceService(context);
                await budgets.SetAllocationAsync(
                    harness.OrganizationId,
                    harness.ActorId,
                    harness.CostCenterId,
                    2026,
                    harness.SpendCategoryCode,
                    1000m,
                    "PEN",
                    expectedVersion: null,
                    allocationKey: "alloc-transition",
                    reason: "Initial allocation for the transition tests",
                    correlationReference: "corr-transition-alloc",
                    cancellationToken);
            }

            return harness;
        }

        /// <summary>Reserves one demand through the real ledger and returns its RESERVED movement.</summary>
        public async Task<(Guid PositionId, Guid ReservedMovementId)> ReserveAsync(
            decimal amount,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, budgets);
            var payload = await budgets.ResolvePositionAsync(
                OrganizationId,
                CostCenterId,
                2026,
                SpendCategoryCode,
                "PEN",
                cancellationToken);
            var outcome = await ledger.ReserveAsync(
                OrganizationId,
                ReservationRequestKey,
                ReservationReserveKey,
                ReservationSource,
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                ReservationCaseId,
                [new BudgetDemand(amount, payload, ReservationSourceLineId, 1, new string('b', 64), null)],
                "corr-transition-reserve",
                cancellationToken: cancellationToken);
            var position = await context.BudgetPositions
                .AsNoTracking()
                .Where(record => record.OrganizationId == OrganizationId &&
                                 record.CostCenterId == CostCenterId &&
                                 record.FiscalYear == 2026 &&
                                 record.SpendCategoryCode == SpendCategoryCode)
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
            return (position, outcome.ReservedMovements.Single().Id);
        }

        /// <summary>Inserts one durable attempt row with a known lease, for the fence negatives (REQ-07).</summary>
        public async Task<Guid> CreateAttemptAsync(
            int fencingToken,
            string leaseOwner,
            DateTimeOffset leaseUntil,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var attempt = new BudgetPrerequisiteAttemptRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = OrganizationId,
                CaseId = Guid.NewGuid(),
                PrerequisiteId = Guid.NewGuid(),
                PrerequisiteKey = "BUDGET_CHECK",
                RequestId = Guid.NewGuid(),
                RequestVersion = 1,
                ParametersJson = "{}",
                ParametersDigest = new string('a', 64),
                SourceControlDigest = new string('b', 64),
                RequestKey = "budget:test:request",
                ReserveKey = "budget:test:reserve",
                SignalKey = "budget:test:signal",
                CompensateKey = "budget:test:compensate",
                State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending),
                DueAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow,
                Attempts = 1,
                FencingToken = fencingToken,
                LeaseOwner = leaseOwner,
                LeaseUntil = leaseUntil
            };
            context.BudgetPrerequisiteAttempts.Add(attempt);
            await context.SaveChangesAsync(cancellationToken);
            return attempt.Id;
        }

        /// <summary>Applies one fenced COMMIT as a processor effect would (REQ-07).</summary>
        public async Task FencedCommitAsync(
            Guid parentMovementId,
            decimal amount,
            Guid attemptId,
            int fencingToken,
            string leaseOwner,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, budgets);
            var payload = await budgets.ResolvePositionAsync(
                OrganizationId, CostCenterId, 2026, SpendCategoryCode, "PEN", cancellationToken);
            var spec = new BudgetOperationSpec(
                BudgetOperationKind.Commit,
                OrganizationId,
                $"fence-op-{Guid.NewGuid():N}",
                new string('f', 64),
                new BudgetSource(BudgetCodes.PurchaseOrderSourceType, Guid.NewGuid(), 1, new string('d', 64)),
                BudgetActor.ForWorkload(ProducerIssuer, ProducerClientId),
                "PO_ISSUED",
                "COMMITTED",
                [new BudgetOperationMovement(
                    payload.Position,
                    new BudgetMovementRequest(BudgetMovementType.Committed, amount, parentMovementId, null))],
                LeaseFence: new BudgetLeaseFence(attemptId, fencingToken, leaseOwner));
            await ledger.ApplyOperationAsync(
                spec,
                new Dictionary<BudgetPositionKey, BudgetPositionPayload> { [payload.Position] = payload },
                "corr-fence",
                cancellationToken);
        }

        /// <summary>Second funded position (another spend category) for multi-position batches (REQ-02).</summary>
        public async Task<(Guid CostCenterId, string SpendCategoryCode)> CreateSecondPositionAsync(
            decimal allocation,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var catalogs = new ReferenceCatalogPersistenceService(context);
            var spendCategory = await catalogs.CreateSpendCategoryAsync(
                OrganizationId,
                ActorId,
                "SERVICES",
                "Services",
                "Second position for the ledger tests",
                "corr-transition-sc-second",
                cancellationToken);
            var budgets = new BudgetPersistenceService(context);
            await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                CostCenterId,
                2026,
                spendCategory.Code,
                allocation,
                "PEN",
                expectedVersion: null,
                allocationKey: "alloc-transition-second",
                reason: "Second allocation for the ledger tests",
                correlationReference: "corr-transition-alloc-second",
                cancellationToken);
            return (CostCenterId, spendCategory.Code);
        }

        public async Task<Guid> PositionIdAsync(string spendCategoryCode, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.BudgetPositions
                .AsNoTracking()
                .Where(record => record.OrganizationId == OrganizationId &&
                                 record.CostCenterId == CostCenterId &&
                                 record.FiscalYear == 2026 &&
                                 record.SpendCategoryCode == spendCategoryCode)
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
        }

        /// <summary>One administrative allocation revision from the current version (REQ-01).</summary>
        public async Task<BudgetPositionView> ReviseAllocationAsync(
            decimal amount,
            string allocationKey,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            return await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                CostCenterId,
                2026,
                SpendCategoryCode,
                amount,
                "PEN",
                expectedVersion: 1,
                allocationKey,
                reason: "Concurrent allocation revision",
                correlationReference: "corr-alloc-race",
                cancellationToken);
        }

        /// <summary>Creates and deactivates a Spend Category for the invalid-reference negatives (REQ-01).</summary>
        public async Task<string> CreateInactiveSpendCategoryAsync(string code, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var catalogs = new ReferenceCatalogPersistenceService(context);
            var created = await catalogs.CreateSpendCategoryAsync(
                OrganizationId,
                ActorId,
                code,
                "Legacy",
                "Inactive reference for the ledger tests",
                "corr-transition-inactive",
                cancellationToken);
            await catalogs.UpdateSpendCategoryAsync(
                OrganizationId,
                ActorId,
                code,
                created.Version,
                "Legacy",
                EntityStatus.Inactive,
                "Deactivated for the ledger tests",
                "corr-transition-inactive-update",
                cancellationToken);
            return code;
        }

        /// <summary>One allocation revision naming an explicit Spend Category (REQ-01 negatives).</summary>
        public async Task<BudgetPositionView> ReviseAllocationWithSpendCategoryAsync(
            string spendCategoryCode,
            decimal amount,
            string allocationKey,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            return await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                CostCenterId,
                2026,
                spendCategoryCode,
                amount,
                "PEN",
                expectedVersion: 1,
                allocationKey,
                reason: "Invalid reference revision",
                correlationReference: "corr-alloc-invalid",
                cancellationToken);
        }

        /// <summary>One allocation revision naming an explicit Cost Center (REQ-01 negatives).</summary>
        public async Task<BudgetPositionView> ReviseAllocationForCostCenterAsync(
            Guid costCenterId,
            decimal amount,
            string allocationKey,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            return await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                costCenterId,
                2026,
                SpendCategoryCode,
                amount,
                "PEN",
                expectedVersion: 1,
                allocationKey,
                reason: "Foreign cost center revision",
                correlationReference: "corr-alloc-foreign",
                cancellationToken);
        }

        /// <summary>One administrative allocation revision with explicit year/currency (REQ-01).</summary>
        public async Task<BudgetPositionView> ReviseAllocationRawAsync(
            int fiscalYear,
            string currency,
            decimal amount,
            string allocationKey,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            return await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                CostCenterId,
                fiscalYear,
                SpendCategoryCode,
                amount,
                currency,
                expectedVersion: 1,
                allocationKey,
                reason: "Raw allocation revision",
                correlationReference: "corr-alloc-raw",
                cancellationToken);
        }

        /// <summary>Reserves one demand in the named position through the real ledger.</summary>
        public async Task<(Guid PositionId, Guid ReservedMovementId)> ReserveAtAsync(
            string spendCategoryCode,
            decimal amount,
            string requestKey,
            string reserveKey,
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, budgets);
            var payload = await budgets.ResolvePositionAsync(
                OrganizationId, CostCenterId, 2026, spendCategoryCode, "PEN", cancellationToken);
            var outcome = await ledger.ReserveAsync(
                OrganizationId,
                requestKey,
                reserveKey,
                new BudgetSource(BudgetCodes.PurchaseRequestSourceType, sourceId, 1, new string('d', 64)),
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                sourceId,
                [new BudgetDemand(amount, payload, Guid.NewGuid(), 1, new string('b', 64), null)],
                "corr-transition-reserve",
                cancellationToken: cancellationToken);
            var position = await context.BudgetPositions
                .AsNoTracking()
                .Where(record => record.OrganizationId == OrganizationId &&
                                 record.CostCenterId == CostCenterId &&
                                 record.FiscalYear == 2026 &&
                                 record.SpendCategoryCode == spendCategoryCode)
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
            return (position, outcome.ReservedMovements.Single().Id);
        }

        /// <summary>Applies one COMMIT batch of several movements or none (REQ-02, CA-02).</summary>
        public async Task<BudgetTransitionResult> CommitBatchAsync(
            IReadOnlyList<(Guid ParentMovementId, decimal Amount)> movements,
            string key,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var service = new BudgetTransitionService(
                context,
                new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                new BudgetMovementProducerRegistry(DefaultConfiguration.Build()),
                new BudgetPersistenceService(context));
            var source = SourceFor("COMMIT", key, BudgetCodes.PurchaseOrderSourceType);
            return await service.ApplyAsync(
                OrganizationId,
                Producer,
                "COMMIT",
                key,
                "PO_ISSUED",
                source,
                movements
                    .Select(movement => new BudgetTransitionCommandMovement(
                        movement.Amount, movement.ParentMovementId, 1, null))
                    .ToArray(),
                "corr-transition",
                cancellationToken);
        }

        /// <summary>Runs one non-binding precheck over demands of several positions (REQ-04, CA-03).</summary>
        public async Task<BudgetPrecheckOutcome> PrecheckAsync(
            IReadOnlyList<(Guid LineId, decimal Amount, string SpendCategoryCode)> demands,
            string key,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, budgets);
            var built = new List<BudgetDemand>();
            foreach (var demand in demands)
            {
                var payload = await budgets.ResolvePositionAsync(
                    OrganizationId, CostCenterId, 2026, demand.SpendCategoryCode, "PEN", cancellationToken);
                built.Add(new BudgetDemand(
                    demand.Amount, payload, demand.LineId, 1, new string('b', 64), null));
            }

            return await ledger.PrecheckAsync(
                OrganizationId,
                key,
                new string('c', 64),
                new BudgetSource(BudgetCodes.PurchaseRequestSourceType, Guid.NewGuid(), 1, new string('f', 64)),
                built,
                "corr-precheck",
                cancellationToken);
        }

        public Task<(BudgetTransitionResult Result, BudgetTransitionSource Source)> CommitAsync(
            Guid parentMovementId,
            decimal amount,
            string key,
            CancellationToken cancellationToken) =>
            CommitAsync(parentMovementId, amount, key, DefaultConfiguration, cancellationToken, Producer);

        public async Task<(BudgetTransitionResult Result, BudgetTransitionSource Source)> CommitAsync(
            Guid parentMovementId,
            decimal amount,
            string key,
            ITransitionConfiguration configuration,
            CancellationToken cancellationToken,
            ApprovalWorkloadIdentity? workload = null)
        {
            var source = SourceFor("COMMIT", key, BudgetCodes.PurchaseOrderSourceType);
            var result = await ApplyAsync(
                "COMMIT", source, parentMovementId, amount, key, configuration, cancellationToken, workload);
            return (result, source);
        }

        public Task<BudgetTransitionResult> ConsumeAsync(
            Guid parentMovementId,
            decimal amount,
            string key,
            CancellationToken cancellationToken) =>
            ApplyAsync(
                "CONSUME",
                SourceFor("CONSUME", key, BudgetCodes.InvoiceSourceType),
                parentMovementId,
                amount,
                key,
                DefaultConfiguration,
                cancellationToken,
                Producer);

        /// <summary>
        /// The source of one operation key, cached so a redelivery of the same command reuses the
        /// exact source it declared the first time (a different source is another preimage).
        /// </summary>
        /// <summary>The exact source of one transfer command, reused by its redelivery.</summary>
        private BudgetSource TransferSourceFor(string key)
        {
            if (!transferSources.TryGetValue(key, out var source))
            {
                source = new BudgetSource(
                    BudgetCodes.PurchaseRequestSourceType, Guid.NewGuid(), 1, new string('c', 64));
                transferSources[key] = source;
            }

            return source;
        }

        private BudgetTransitionSource SourceFor(string operation, string key, string sourceType)
        {
            if (!sources.TryGetValue(operation + ":" + key, out var source))
            {
                source = new BudgetTransitionSource(sourceType, Guid.NewGuid(), 1, new string('d', 64));
                sources[operation + ":" + key] = source;
            }

            return source;
        }

        public Task<BudgetTransitionResult> ReverseAsync(
            Guid parentMovementId,
            decimal amount,
            string key,
            BudgetTransitionSource source,
            CancellationToken cancellationToken) =>
            ApplyAsync(
                "REVERSE",
                source,
                parentMovementId,
                amount,
                key,
                DefaultConfiguration,
                cancellationToken,
                Producer);

        private async Task<BudgetTransitionResult> ApplyAsync(
            string operation,
            BudgetTransitionSource source,
            Guid parentMovementId,
            decimal amount,
            string key,
            ITransitionConfiguration configuration,
            CancellationToken cancellationToken,
            ApprovalWorkloadIdentity? workload)
        {
            await using var context = CreateContext();
            var service = new BudgetTransitionService(
                context,
                new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                new BudgetMovementProducerRegistry(configuration.Build()),
                new BudgetPersistenceService(context));
            return await service.ApplyAsync(
                OrganizationId,
                workload ?? Producer,
                operation,
                key,
                "PO_ISSUED",
                source,
                [new BudgetTransitionCommandMovement(amount, parentMovementId, 1, null)],
                "corr-transition",
                cancellationToken);
        }

        /// <summary>
        /// Redelivery of the reservation command with the exact preimage the first attempt declared:
        /// this is what a worker resuming after a crash resolves (REQ-07, NFR-03).
        /// </summary>
        public async Task<BudgetReserveOutcome> ReserveOutcomeAsync(
            decimal amount,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, budgets);
            var payload = await budgets.ResolvePositionAsync(
                OrganizationId, CostCenterId, 2026, SpendCategoryCode, "PEN", cancellationToken);
            return await ledger.ReserveAsync(
                OrganizationId,
                ReservationRequestKey,
                ReservationReserveKey,
                ReservationSource,
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                ReservationCaseId,
                [new BudgetDemand(amount, payload, ReservationSourceLineId, 1, new string('b', 64), null)],
                "corr-transition-reserve",
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Runs one serialized transfer of the predecessor reservations into a replacement case
        /// (REQ-08) with the deterministic keys of the replacement attempt.
        /// </summary>
        public async Task<BudgetTransferReserveOutcome> TransferAsync(
            IReadOnlyList<Guid> predecessorMovements,
            decimal amount,
            Guid replacementCaseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var positions = new BudgetPersistenceService(context);
            var ledger = new BudgetLedgerService(context, positions);
            var payload = await positions.ResolvePositionAsync(
                OrganizationId, CostCenterId, 2026, SpendCategoryCode, "PEN", cancellationToken);
            return await ledger.TransferReserveAsync(
                OrganizationId,
                $"budget:{replacementCaseId:D}:request",
                $"budget:{replacementCaseId:D}:reserve",
                TransferSourceFor(replacementCaseId.ToString("D")),
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                replacementCaseId,
                [new BudgetDemand(amount, payload, Guid.NewGuid(), 1, new string('b', 64), null)],
                [new BudgetPredecessorReservation(Guid.NewGuid(), predecessorMovements)],
                "corr-transfer",
                cancellationToken);
        }

        public async Task<BudgetBuckets> BucketsAsync(Guid positionId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var position = await context.BudgetPositions
                .AsNoTracking()
                .Where(record => record.Id == positionId)
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
            var balance = await context.BudgetBalances
                .AsNoTracking()
                .SingleAsync(record => record.PositionId == position, cancellationToken);
            return BudgetBuckets.Create(
                balance.Allocated, balance.Reserved, balance.Committed, balance.Consumed);
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(connectionString)
                .Options);

        private void Seed(ProcureToPayDbContext context)
        {
            context.Organizations.Add(new OrganizationRecord
            {
                Id = OrganizationId,
                Code = "PR-BUDGET",
                Name = "Budget transitions",
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
                Name = "Information Technology",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();

        /// <summary>Configuration with the closed producer table of REQ-09.</summary>
        public sealed record ProducerConfiguration(
            params (string Operation, string SourceType, string Issuer, string ClientId)[] Entries)
            : ITransitionConfiguration
        {
            public IConfiguration Build()
            {
                var values = new Dictionary<string, string?>();
                for (var index = 0; index < Entries.Length; index++)
                {
                    var (operation, sourceType, issuer, clientId) = Entries[index];
                    values[$"Budget:MovementProducers:{index}:Operation"] = operation;
                    values[$"Budget:MovementProducers:{index}:ContractVersion"] = "v1";
                    values[$"Budget:MovementProducers:{index}:SourceType"] = sourceType;
                    values[$"Budget:MovementProducers:{index}:ProducerId"] = $"producer-{index}";
                    values[$"Budget:MovementProducers:{index}:Issuer"] = issuer;
                    values[$"Budget:MovementProducers:{index}:ClientId"] = clientId;
                }

                return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            }
        }

        /// <summary>A deployment without any registration must fail closed.</summary>
        public sealed record NoProducerConfiguration : ITransitionConfiguration
        {
            public static NoProducerConfiguration Instance { get; } = new();

            public IConfiguration Build() => new ConfigurationBuilder().Build();
        }

        public static ProducerConfiguration DefaultConfiguration { get; } = new(
            ("COMMIT", "PURCHASE_ORDER", ProducerIssuer, ProducerClientId),
            ("CONSUME", "INVOICE", ProducerIssuer, ProducerClientId),
            ("REVERSE", "PURCHASE_ORDER", ProducerIssuer, ProducerClientId),
            ("REVERSE", "INVOICE", ProducerIssuer, ProducerClientId));
    }
}
