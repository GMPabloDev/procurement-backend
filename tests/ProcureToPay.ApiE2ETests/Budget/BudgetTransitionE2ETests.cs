using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests.Budget;

/// <summary>
/// HTTP evidence of the workload-only transition surface (SPEC 08 REQ-09, CA-07, CA-08): the closed
/// producer registry, the workload identity and the idempotent command contract are exercised over
/// real HTTP and real SQL, never through the service directly. A registered producer posts COMMIT,
/// replays by key and receives 409 on a divergent preimage; a foreign workload, an administrative
/// user without workload identity and an absent or disabled producer fail closed without movements.
/// </summary>
public sealed class BudgetTransitionE2ETests
{
    private const string WorkloadIssuer = "https://keycloak.test/realms/procure-to-pay";
    private const string ProducerClientId = "purchase-order-domain";
    private const string ForeignClientId = "foreign-order-domain";
    private static readonly Guid ActorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid SourceId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid SourceLineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ReservationCaseId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private sealed record TransitionSourceBody(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("digest")] string Digest);

    private sealed record TransitionMovementBody(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("parent_movement_id")] Guid ParentMovementId,
        [property: JsonPropertyName("parent_movement_version")] int ParentMovementVersion,
        [property: JsonPropertyName("target")] object? Target);

    private sealed record TransitionCommandBody(
        [property: JsonPropertyName("command_version")] string ContractVersion,
        [property: JsonPropertyName("movements")] IReadOnlyList<TransitionMovementBody> Movements,
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("operation_key")] string OperationKey,
        [property: JsonPropertyName("organization_id")] Guid OrganizationId,
        [property: JsonPropertyName("reason_code")] string ReasonCode,
        [property: JsonPropertyName("source")] TransitionSourceBody Source);

    [Fact]
    public async Task A_registered_workload_commits_over_http_and_replays_by_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new BudgetTransitionApiFactory(sqlServer.ConnectionString);
        var (organizationId, reservedMovementId) = await sqlServer.SeedReservationAsync(cancellationToken);
        var command = Command(organizationId, reservedMovementId, "COMMIT", "PURCHASE_ORDER", 60m, "commit-http-1");

        // An authenticated workload outside the closed table never reaches the ledger (REQ-09).
        using (var foreign = factory.CreateClient())
        {
            foreign.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", BudgetTransitionApiFactory.CreateWorkloadToken(ForeignClientId));
            using var response = await foreign.PostAsJsonAsync(
                "/api/v1/budgets/movements", command, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(
                "/problems/forbidden",
                await response.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        // An administrative user token has no workload client identity, so it cannot post either.
        using (var administrator = factory.CreateClient())
        {
            administrator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", BudgetTransitionApiFactory.CreateUserToken());
            using var response = await administrator.PostAsJsonAsync(
                "/api/v1/budgets/movements", command, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await using (var untouched = sqlServer.CreateContext())
        {
            Assert.Equal(
                0,
                await untouched.BudgetMovements.CountAsync(
                    movement => movement.Type == (int)BudgetMovementType.Committed, cancellationToken));
        }

        using var producer = factory.CreateClient();
        producer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BudgetTransitionApiFactory.CreateWorkloadToken(ProducerClientId));
        Guid movementId;
        Guid operationId;
        using (var committed = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements", command, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
            var response = await committed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("budget-transition-response/v1", response.GetProperty("contract_version").GetString());
            Assert.False(response.GetProperty("replayed").GetBoolean());
            operationId = response.GetProperty("operation_id").GetGuid();
            var movements = response.GetProperty("movements");
            Assert.Equal(1, movements.GetArrayLength());
            movementId = movements[0].GetProperty("id").GetGuid();
            Assert.Equal("COMMITTED", movements[0].GetProperty("type").GetString());
            Assert.Equal(60m, movements[0].GetProperty("amount").GetDecimal());
            Assert.Equal(reservedMovementId, movements[0].GetProperty("parent_movement_id").GetGuid());
            Assert.Equal(64, movements[0].GetProperty("position_key_digest").GetString()!.Length);
        }

        await using (var verification = sqlServer.CreateContext())
        {
            var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
            Assert.Equal(60m, balance.Committed);
            Assert.Equal(40m, balance.Reserved);
        }

        // The same key and preimage returns the recorded operation instead of posting again (NFR-01).
        using (var replay = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements", command, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var response = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.True(response.GetProperty("replayed").GetBoolean());
            Assert.Equal(operationId, response.GetProperty("operation_id").GetGuid());
            Assert.Equal(movementId, response.GetProperty("movements")[0].GetProperty("id").GetGuid());
        }

        // Reusing the operation key with another preimage is a contract conflict.
        using (var conflict = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements",
            Command(organizationId, reservedMovementId, "COMMIT", "PURCHASE_ORDER", 50m, "commit-http-1"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Contains(
                "/problems/conflict",
                await conflict.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        await using (var verification = sqlServer.CreateContext())
        {
            Assert.Equal(
                1,
                await verification.BudgetMovements.CountAsync(
                    movement => movement.Type == (int)BudgetMovementType.Committed, cancellationToken));
            Assert.Equal(
                1,
                await verification.BudgetOperations.CountAsync(
                    operation => operation.Kind == (int)BudgetOperationKind.Commit, cancellationToken));
            var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
            Assert.Equal(60m, balance.Committed);
            Assert.Equal(40m, balance.Reserved);
        }
    }

    [Fact]
    public async Task An_absent_disabled_or_stale_transition_fails_closed_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new BudgetTransitionApiFactory(sqlServer.ConnectionString);
        var (organizationId, reservedMovementId) = await sqlServer.SeedReservationAsync(cancellationToken);

        using var producer = factory.CreateClient();
        producer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BudgetTransitionApiFactory.CreateWorkloadToken(ProducerClientId));

        // A contract version outside the closed vocabulary is rejected before any producer lookup.
        using (var unrecognized = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements",
            Command(organizationId, reservedMovementId, "COMMIT", "PURCHASE_ORDER", 10m, "commit-http-v0") with
            {
                ContractVersion = "budget-transition-command/v0"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unrecognized.StatusCode);
            Assert.Contains(
                "/problems/validation",
                await unrecognized.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        // No registered producer for REVERSE+INVOICE: the surface fails closed with 503, never with
        // an improvised transition.
        using (var missing = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements",
            Command(organizationId, reservedMovementId, "REVERSE", "INVOICE", 10m, "reverse-http-missing"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
            Assert.Contains(
                "/problems/budget-dependency-unavailable",
                await missing.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        // A registered producer that is disabled in this deployment fails closed with 503.
        using (var disabled = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements",
            Command(organizationId, reservedMovementId, "CONSUME", "INVOICE", 10m, "consume-http-disabled"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
            Assert.Contains(
                "/problems/budget-dependency-unavailable",
                await disabled.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        // A REVERSE from a source other than its parent operation is forbidden and posts nothing.
        using (var forbiddenReverse = await producer.PostAsJsonAsync(
            "/api/v1/budgets/movements",
            Command(organizationId, reservedMovementId, "REVERSE", "PURCHASE_ORDER", 10m, "reverse-http-1"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenReverse.StatusCode);
            Assert.Contains(
                "/problems/forbidden",
                await forbiddenReverse.Content.ReadAsStringAsync(cancellationToken),
                StringComparison.Ordinal);
        }

        await using var verification = sqlServer.CreateContext();
        Assert.Equal(
            0,
            await verification.BudgetMovements.CountAsync(
                movement => movement.Type == (int)BudgetMovementType.Committed ||
                            movement.Type == (int)BudgetMovementType.Consumed ||
                            movement.Type == (int)BudgetMovementType.Reverse,
                cancellationToken));
        var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(100m, balance.Reserved);
        Assert.Equal(0m, balance.Committed);
        Assert.Equal(0m, balance.Consumed);
    }

    private static TransitionCommandBody Command(
        Guid organizationId,
        Guid parentMovementId,
        string operation,
        string sourceType,
        decimal amount,
        string operationKey) => new(
        BudgetCodes.TransitionCommandVersion,
        [new TransitionMovementBody(amount, parentMovementId, BudgetCodes.ParentMovementVersion, null)],
        operation,
        operationKey,
        organizationId,
        "PO_ISSUED",
        new TransitionSourceBody(sourceType, SourceId, 1, new string('a', 64)));

    private sealed class BudgetTransitionApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateWorkloadToken(string clientId) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                WorkloadIssuer,
                "procure-to-pay-tests",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{clientId}"),
                    new Claim("client_id", clientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        /// <summary>A valid user token with an administrative subject but no workload client_id.</summary>
        internal static string CreateUserToken() =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                WorkloadIssuer,
                "procure-to-pay-tests",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "e2e-administrator"),
                    new Claim(ClaimTypes.Role, "ADMIN")
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            var producers = new Dictionary<string, string?>
            {
                ["Budget:MovementProducers:0:Operation"] = "COMMIT",
                ["Budget:MovementProducers:0:ContractVersion"] = "v1",
                ["Budget:MovementProducers:0:SourceType"] = BudgetCodes.PurchaseOrderSourceType,
                ["Budget:MovementProducers:0:ProducerId"] = ProducerClientId,
                ["Budget:MovementProducers:0:Issuer"] = WorkloadIssuer,
                ["Budget:MovementProducers:0:ClientId"] = ProducerClientId,
                // A disabled entry is present in configuration but never authorizes a transition.
                ["Budget:MovementProducers:1:Operation"] = "CONSUME",
                ["Budget:MovementProducers:1:ContractVersion"] = "v1",
                ["Budget:MovementProducers:1:SourceType"] = BudgetCodes.InvoiceSourceType,
                ["Budget:MovementProducers:1:ProducerId"] = "invoice-domain",
                ["Budget:MovementProducers:1:Issuer"] = WorkloadIssuer,
                ["Budget:MovementProducers:1:ClientId"] = "invoice-domain",
                ["Budget:MovementProducers:1:Enabled"] = "false",
                ["Budget:MovementProducers:2:Operation"] = "REVERSE",
                ["Budget:MovementProducers:2:ContractVersion"] = "v1",
                ["Budget:MovementProducers:2:SourceType"] = BudgetCodes.PurchaseOrderSourceType,
                ["Budget:MovementProducers:2:ProducerId"] = ProducerClientId,
                ["Budget:MovementProducers:2:Issuer"] = WorkloadIssuer,
                ["Budget:MovementProducers:2:ClientId"] = ProducerClientId
            };
            foreach (var setting in producers)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests"
                };
                foreach (var producer in producers)
                {
                    settings[producer.Key] = producer.Value;
                }

                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    options.TokenValidationParameters.ValidIssuer = WorkloadIssuer;
                    options.TokenValidationParameters.ValidAudience = "procure-to-pay-tests";
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                });
            });
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
            await using (var context = environment.CreateContext())
            {
                await context.Database.MigrateAsync(cancellationToken);
                using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
                await new OrganizationBootstrapper(
                    context, loggerFactory.CreateLogger<OrganizationBootstrapper>())
                    .InitializeAsync(Options("admin-1"), cancellationToken);
            }

            return environment;
        }

        /// <summary>
        /// Creates a funded position (1000 PEN) and one real RESERVED hold of 100 PEN, so the COMMIT
        /// and REVERSE transitions over HTTP have a ledger to advance.
        /// </summary>
        public async Task<(Guid OrganizationId, Guid ReservedMovementId)> SeedReservationAsync(
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var organizationId = await context.Organizations
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
            var departmentId = await context.Departments
                .Select(record => record.Id)
                .SingleAsync(cancellationToken);
            var catalogs = new ReferenceCatalogPersistenceService(context);
            var costCenter = await catalogs.CreateCostCenterAsync(
                organizationId,
                ActorId,
                "CC-E2E-BUDGET",
                "Budget HTTP transitions",
                departmentId,
                "Cost center for the budget HTTP transition tests",
                "corr-http-cc",
                cancellationToken);
            var spendCategory = await catalogs.CreateSpendCategoryAsync(
                organizationId,
                ActorId,
                "HARDWARE",
                "Hardware",
                "Spend category for the budget HTTP transition tests",
                "corr-http-sc",
                cancellationToken);
            var budgets = new BudgetPersistenceService(context);
            await budgets.SetAllocationAsync(
                organizationId,
                ActorId,
                costCenter.Id,
                2026,
                spendCategory.Code,
                1000m,
                "PEN",
                expectedVersion: null,
                allocationKey: "alloc-http-1",
                reason: "Initial allocation for the budget HTTP transition tests",
                correlationReference: "corr-http-alloc",
                cancellationToken);
            var payload = await budgets.ResolvePositionAsync(
                organizationId,
                costCenter.Id,
                2026,
                spendCategory.Code,
                "PEN",
                cancellationToken);
            var ledger = new BudgetLedgerService(context, budgets);
            var outcome = await ledger.ReserveAsync(
                organizationId,
                "budget-http-request-1",
                "budget-http-reserve-1",
                new BudgetSource(
                    BudgetCodes.PurchaseRequestSourceType, ReservationCaseId, 1, new string('d', 64)),
                BudgetActors.OwnerSystem,
                "BUDGET_CHECK",
                ReservationCaseId,
                [new BudgetDemand(100m, payload, SourceLineId, 1, new string('b', 64), null)],
                "corr-http-reserve",
                cancellationToken: cancellationToken);
            return (organizationId, outcome.ReservedMovements.Single().Id);
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public async ValueTask DisposeAsync() => await container.DisposeAsync();

        private static OrganizationBootstrapOptions Options(string subject) => new(
            "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
            "Acme Peru S.A.C.", "IT", "Software / IT",
            WorkloadIssuer, subject, "API budget transition bootstrap");
    }
}
