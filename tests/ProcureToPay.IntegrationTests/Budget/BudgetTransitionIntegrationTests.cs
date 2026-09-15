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

    /// <summary>Produces the producer table a test deployment runs with.</summary>
    private interface ITransitionConfiguration
    {
        IConfiguration Build();
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
        private string SpendCategoryCode { get; set; } = string.Empty;
        private readonly Dictionary<string, BudgetTransitionSource> sources = [];

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
                "budget-request-1",
                "budget-reserve-1",
                new BudgetSource(BudgetCodes.PurchaseRequestSourceType, Guid.NewGuid(), 1, new string('d', 64)),
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                Guid.NewGuid(),
                [new BudgetDemand(amount, payload, Guid.NewGuid(), 1, new string('b', 64), null)],
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
