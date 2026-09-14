using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP surface of the reference catalogs (SPEC 07 REQ-01..REQ-04, REQ-08, CA-01..CA-03): ADMIN
/// mutations with audit, minimal reads for active users, scoped AUDITOR history and the conflicts
/// that protect live references.
/// </summary>
public sealed class ReferenceCatalogE2ETests
{
    private const string Issuer = "https://keycloak.test/realms/procure-to-pay";
    private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";

    [Fact]
    public async Task Admin_mutations_and_scoped_reads_follow_the_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var admin = environment.Client("admin-user");
        using var requester = environment.Client("requester");
        using var globalAuditor = environment.Client("auditor-global");
        using var costCenterAuditor = environment.Client("auditor-cost-center");
        using var departmentAuditor = environment.Client("auditor-scoped");

        // Any active user reads the minimal current projections needed to build a request.
        using var costCenters = await requester.GetAsync("/api/v1/reference-catalogs/cost-centers", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, costCenters.StatusCode);
        var list = await costCenters.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var seeded = list.EnumerateArray().Single(entry => entry.GetProperty("code").GetString() == "CC-IT-DEV");
        var seededCostCenterId = seeded.GetProperty("id").GetGuid();
        Assert.Equal(environment.DepartmentId, seeded.GetProperty("department_ref").GetProperty("id").GetGuid());
        Assert.Equal(1, seeded.GetProperty("department_ref").GetProperty("version").GetInt32());
        Assert.Equal(1, seeded.GetProperty("version").GetInt32());

        using var categories = await requester.GetAsync("/api/v1/reference-catalogs/spend-categories", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
        var categoryList = await categories.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var seededCategory = categoryList.EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "HARDWARE");
        Assert.Equal("SPEND_CATEGORY", seededCategory.GetProperty("catalog").GetString());
        Assert.Equal(
            ReferenceCatalogCanonicalizer.SpendCategoryDigest(
                "HARDWARE", "Hardware", environment.OrganizationId, 1),
            seededCategory.GetProperty("digest").GetString());

        // Only an organization ADMIN mutates the catalogs.
        using var forbidden = await requester.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "CC-OPS", name = "Operations", departmentId = environment.DepartmentId, reason = "Denied" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // Creation is audited and validation is fail-closed.
        using var created = await admin.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "cc-ops", name = "Operations", departmentId = environment.DepartmentId, reason = "Ops" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var costCenterId = createdBody.GetProperty("id").GetGuid();
        Assert.Equal("CC-OPS", createdBody.GetProperty("code").GetString());
        using var badCode = await admin.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "1-CC", name = "Bad", departmentId = environment.DepartmentId, reason = "Bad" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Equal("/problems/validation", await ProblemTypeAsync(badCode, cancellationToken));
        using var duplicate = await admin.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "cc-ops", name = "Duplicate", departmentId = environment.DepartmentId, reason = "Dup" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("/problems/conflict", await ProblemTypeAsync(duplicate, cancellationToken));
        using var unknownDepartment = await admin.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "CC-XX", name = "Unknown", departmentId = Guid.NewGuid(), reason = "Unknown" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknownDepartment.StatusCode);

        // Renaming advances the version; a stale expected version is a conflict.
        using var renamed = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{costCenterId}",
            new { name = "Operations renamed", expectedVersion = 1, reason = "Rename" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(2, (await renamed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("version").GetInt32());
        using var stale = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{costCenterId}",
            new { name = "Stale", expectedVersion = 1, reason = "Stale" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var missing = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{Guid.NewGuid()}",
            new { name = "Missing", expectedVersion = 1, reason = "Missing" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var badStatus = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{costCenterId}",
            new { status = "RETIRED", expectedVersion = 2, reason = "Bad status" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        // Spend category updates recompute the digest with the new version.
        using var renamedCategory = await admin.PatchAsJsonAsync(
            "/api/v1/reference-catalogs/spend-categories/HARDWARE",
            new { name = "Hardware and peripherals", expectedVersion = 1, reason = "Rename" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamedCategory.StatusCode);
        var renamedCategoryBody = await renamedCategory.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(2, renamedCategoryBody.GetProperty("version").GetInt32());
        Assert.Equal(
            ReferenceCatalogCanonicalizer.SpendCategoryDigest(
                "HARDWARE", "Hardware and peripherals", environment.OrganizationId, 2),
            renamedCategoryBody.GetProperty("digest").GetString());

        // A COST_CENTER scope is accepted only for an active current catalog code (REQ-04/REQ-08).
        using var scopedRole = await admin.PostAsJsonAsync(
            $"/api/v1/users/{environment.RequesterId}/roles",
            new
            {
                role = "IT_REVIEWER",
                reason = "Scoped reviewer",
                scopes = new[] { new { dimension = "COST_CENTER", reference = "CC-IT-DEV" } },
                expectedUserVersion = 1
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, scopedRole.StatusCode);
        using var unknownScope = await admin.PostAsJsonAsync(
            $"/api/v1/users/{environment.RequesterId}/roles",
            new
            {
                role = "LEGAL_REVIEWER",
                reason = "Unknown cost center",
                scopes = new[] { new { dimension = "COST_CENTER", reference = "CC-NOPE" } },
                expectedUserVersion = 1
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknownScope.StatusCode);

        // History readers: organization scope sees everything, COST_CENTER only its entry.
        using var globalHistory = await globalAuditor.GetAsync(
            $"/api/v1/reference-catalogs/cost-centers/{costCenterId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, globalHistory.StatusCode);
        var history = await globalHistory.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal([1, 2], history.EnumerateArray().Select(entry => entry.GetProperty("version").GetInt32()).ToArray());
        Assert.Equal(
            ["ACTIVE", "ACTIVE"],
            history.EnumerateArray().Select(entry => entry.GetProperty("status").GetString()).ToArray());
        using var scopedHistory = await costCenterAuditor.GetAsync(
            $"/api/v1/reference-catalogs/cost-centers/{seededCostCenterId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, scopedHistory.StatusCode);
        // The COST_CENTER assignment covers only its own code, never another cost center.
        using var foreignScopedHistory = await costCenterAuditor.GetAsync(
            $"/api/v1/reference-catalogs/cost-centers/{costCenterId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreignScopedHistory.StatusCode);
        using var departmentScoped = await departmentAuditor.GetAsync(
            $"/api/v1/reference-catalogs/cost-centers/{seededCostCenterId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, departmentScoped.StatusCode);
        using var scopedCategoryHistory = await costCenterAuditor.GetAsync(
            "/api/v1/reference-catalogs/spend-categories/HARDWARE/history", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, scopedCategoryHistory.StatusCode);
        using var globalCategoryHistory = await globalAuditor.GetAsync(
            "/api/v1/reference-catalogs/spend-categories/HARDWARE/history", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, globalCategoryHistory.StatusCode);
    }

    [Fact]
    public async Task Deactivation_is_blocked_while_a_live_reference_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var admin = environment.Client("admin-user");

        // A fresh department with no users: only its cost center keeps it from being deactivated.
        using var createdDepartment = await admin.PostAsJsonAsync(
            "/api/v1/departments",
            new { code = "OPS", name = "Operations", reason = "Seed operations", expectedOrganizationVersion = 1 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createdDepartment.StatusCode);
        var createdDepartmentBody = await createdDepartment.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var freshDepartmentId = createdDepartmentBody.GetProperty("id").GetGuid();
        using var createdCostCenter = await admin.PostAsJsonAsync(
            "/api/v1/reference-catalogs/cost-centers",
            new { code = "CC-OPS", name = "Operations", departmentId = freshDepartmentId, reason = "Seed" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createdCostCenter.StatusCode);
        var freshCostCenterId = (await createdCostCenter.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("id").GetGuid();

        // A current active cost center keeps its department from being deactivated (SPEC 07 REQ-02).
        using var blockedDepartment = await admin.PatchAsJsonAsync(
            $"/api/v1/departments/{freshDepartmentId}",
            new { status = "INACTIVE", expectedVersion = 1, reason = "Retire department" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, blockedDepartment.StatusCode);

        // A live COST_CENTER scope keeps its cost center from being deactivated (REQ-04).
        using var blockedCostCenter = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{environment.CostCenterId}",
            new { status = "INACTIVE", expectedVersion = 1, reason = "Retire cost center" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, blockedCostCenter.StatusCode);

        // Revoking the scoped assignment releases the reference.
        using var roles = await admin.GetAsync(
            $"/api/v1/users/{environment.CostCenterAuditorId}/roles", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        var assignmentId = (await roles.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .EnumerateArray().Single().GetProperty("id").GetGuid();
        using var revoked = await admin.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/users/{environment.CostCenterAuditorId}/roles/{assignmentId}")
            {
                Content = JsonContent.Create(new { reason = "No longer needed", expectedVersion = 1 })
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var deactivated = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{environment.CostCenterId}",
            new { status = "INACTIVE", expectedVersion = 1, reason = "Retire cost center" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        // Deactivating the cost center releases the department it owned.
        using var deactivatedCostCenter = await admin.PatchAsJsonAsync(
            $"/api/v1/reference-catalogs/cost-centers/{freshCostCenterId}",
            new { status = "INACTIVE", expectedVersion = 1, reason = "Retire cost center" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivatedCostCenter.StatusCode);
        using var departmentNow = await admin.PatchAsJsonAsync(
            $"/api/v1/departments/{freshDepartmentId}",
            new { status = "INACTIVE", expectedVersion = 1, reason = "Retire department" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, departmentNow.StatusCode);
    }

    [Fact]
    public async Task Health_distinguishes_missing_ambiguous_and_corrupted_catalogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Owner slots 0/2 and the Policy catalogs 0 must be reported with the exact grammar.
        await using (var environment = await Environment.StartAsync(
                         cancellationToken,
                         services =>
                         {
                             services.RemoveAll<IPurchaseRequestReferenceOwner>();
                             services.RemoveAll<IPurchaseRequestReferenceOwnerRegistry>();
                             services.RemoveAll<IPolicyReferenceCatalog>();
                             services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
                                 new SpendCategoryReferenceOwner(
                                     provider.GetRequiredService<ProcureToPayDbContext>()));
                             services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
                                 new SpendCategoryReferenceOwner(
                                     provider.GetRequiredService<ProcureToPayDbContext>()));
                             // A resolvable owner with the wrong contractual identity must be reported
                             // as unavailable, never accepted as readiness.
                             services.AddScoped<IPurchaseRequestReferenceOwner>(_ => new WrongContractOwner());
                             services.AddScoped<IPurchaseRequestReferenceOwnerRegistry>(provider =>
                                 new PurchaseRequestReferenceOwnerRegistry(
                                     provider.GetServices<IPurchaseRequestReferenceOwner>()));
                         }))
        {
            using var client = environment.Client("requester");
            using var response = await client.GetAsync("/health/purchase-request", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var code = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("code").GetString() ?? string.Empty;
            Assert.Contains(
                "PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:LEGAL_ENTITY", code, StringComparison.Ordinal);
            Assert.Contains(
                "PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:COST_CENTER", code, StringComparison.Ordinal);
            Assert.Contains(
                "PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:SPEND_CATEGORY", code, StringComparison.Ordinal);
            Assert.Contains("POLICY_REFERENCE_CATALOG_UNAVAILABLE:COST_CENTER", code, StringComparison.Ordinal);
            Assert.Contains("POLICY_REFERENCE_CATALOG_UNAVAILABLE:SPEND_CATEGORY", code, StringComparison.Ordinal);
        }

        // A corrupted current pointer or a non-reproducible digest degrades readiness by catalog.
        await using (var environment = await Environment.StartAsync(cancellationToken))
        {
            await using (var corruption = Environment.CreateContext(environment.ConnectionString))
            {
                // The pointer can move forward but the history cannot be rewritten (append-only
                // triggers), so corruption is injected as a forward version with a wrong digest.
                // The Cost Center pointer is corrupted with its protection explicitly disabled,
                // simulating an out-of-band schema-level incident that health must still detect.
                await corruption.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [ReferenceCatalog].[CostCenters] DISABLE TRIGGER " +
                    "[TR_CostCenters_IdentityStable];" +
                    $"UPDATE [ReferenceCatalog].[CostCenters] SET [CurrentVersion] = 99 WHERE [Id] = '{environment.CostCenterId}';" +
                    "ALTER TABLE [ReferenceCatalog].[CostCenters] ENABLE TRIGGER " +
                    "[TR_CostCenters_IdentityStable];",
                    cancellationToken);
                await corruption.Database.ExecuteSqlRawAsync(
                    "INSERT INTO [ReferenceCatalog].[SpendCategoryVersions] " +
                    "([SpendCategoryId],[Version],[OrganizationId],[Code],[Name],[Digest],[Status]," +
                    "[PredecessorVersion],[ActorUserId],[OccurredAt],[Reason]) " +
                    "SELECT [Id],2,[OrganizationId],'HARDWARE','Hardware'," +
                    $"'{new string('a', 64)}',1,1,'{Guid.NewGuid()}',SYSDATETIMEOFFSET(),'Corrupt' " +
                    "FROM [ReferenceCatalog].[SpendCategories] WHERE [Code] = 'HARDWARE';" +
                    "UPDATE [ReferenceCatalog].[SpendCategories] SET [CurrentVersion] = 2 WHERE [Code] = 'HARDWARE';",
                    cancellationToken);
            }

            using var client = environment.Client("requester");
            using var response = await client.GetAsync("/health/purchase-request", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var code = body.GetProperty("code").GetString() ?? string.Empty;
            Assert.Equal("DEGRADED", body.GetProperty("status").GetString());
            Assert.Contains("REFERENCE_CATALOG_CORRUPTED:COST_CENTER", code, StringComparison.Ordinal);
            Assert.Contains("REFERENCE_CATALOG_CORRUPTED:SPEND_CATEGORY", code, StringComparison.Ordinal);
            // Health never leaks catalog data.
            Assert.DoesNotContain("CC-IT-DEV", code, StringComparison.Ordinal);
            Assert.DoesNotContain("HARDWARE", code, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 64), code, StringComparison.Ordinal);

            // A storage failure on the catalog tables is reported separately and still minimal.
            await using (var breaking = Environment.CreateContext(environment.ConnectionString))
            {
                await breaking.Database.ExecuteSqlRawAsync(
                    "EXEC sp_rename 'ReferenceCatalog.CostCenterVersions', 'CostCenterVersionsBroken'",
                    cancellationToken);
            }

            using var broken = await client.GetAsync("/health/purchase-request", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
            var brokenCode = (await broken.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("code").GetString() ?? string.Empty;
            Assert.Contains("REFERENCE_CATALOG_STORAGE_UNAVAILABLE", brokenCode, StringComparison.Ordinal);
        }
    }

    private static async Task<string?> ProblemTypeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private sealed class Environment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private TestApiFactory factory = null!;

        public Guid OrganizationId { get; private set; }
        public Guid DepartmentId { get; private set; }
        public Guid CostCenterId { get; private set; }
        public Guid CostCenterAuditorId { get; private set; }
        public Guid RequesterId { get; private set; }
        public string ConnectionString { get; private set; } = string.Empty;

        public static Task<Environment> StartAsync(CancellationToken cancellationToken) =>
            StartAsync(cancellationToken, null);

        public static async Task<Environment> StartAsync(
            CancellationToken cancellationToken,
            Action<IServiceCollection>? configureServices)
        {
            var environment = new Environment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            var connectionString = environment.container.GetConnectionString();
            environment.ConnectionString = connectionString;
            await using var context = new ProcureToPayDbContext(
                new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options);
            await context.Database.MigrateAsync(cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var bootstrapper = new OrganizationBootstrapper(
                context, loggerFactory.CreateLogger<OrganizationBootstrapper>());
            await bootstrapper.InitializeAsync(
                new OrganizationBootstrapOptions(
                    "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE", "Acme Peru S.A.C.",
                    "IT", "Software / IT", Issuer, "admin-bootstrap", "API E2E bootstrap"),
                cancellationToken);
            var organization = await context.Organizations.SingleAsync(cancellationToken);
            var department = await context.Departments.SingleAsync(cancellationToken);
            environment.OrganizationId = organization.Id;
            environment.DepartmentId = department.Id;
            environment.Seed(context, organization.Id, department.Id);
            await context.SaveChangesAsync(cancellationToken);

            // Catalog fixtures are created through the real service so the seeded scopes match.
            var service = new ReferenceCatalogPersistenceService(CreateContext(connectionString));
            var costCenter = await service.CreateCostCenterAsync(
                organization.Id, environment.CostCenterAuditorId, "CC-IT-DEV", "Development",
                department.Id, "Seed", "corr-seed-cc", cancellationToken);
            environment.CostCenterId = costCenter.Id;
            await service.CreateSpendCategoryAsync(
                organization.Id, environment.CostCenterAuditorId, "HARDWARE", "Hardware",
                "Seed", "corr-seed-sc", cancellationToken);
            environment.factory = new TestApiFactory(connectionString, configureServices);
            return environment;
        }

        internal static ProcureToPayDbContext CreateContext(string connectionString) => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options);

        private void Seed(ProcureToPayDbContext context, Guid organizationId, Guid departmentId)
        {
            var users = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["requester"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                ["auditor-scoped"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
                ["auditor-global"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"),
                ["admin-user"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"),
                ["auditor-cost-center"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005")
            };
            foreach (var (subject, id) in users)
            {
                context.UserProfiles.Add(new UserProfileRecord
                {
                    Id = id,
                    OrganizationId = organizationId,
                    Issuer = Issuer,
                    Subject = subject,
                    DepartmentId = departmentId,
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                });
            }

            CostCenterAuditorId = users["auditor-cost-center"];
            RequesterId = users["requester"];
            context.RoleAssignments.AddRange(
                Assignment(users["auditor-global"], SystemRole.Auditor, GlobalScopeJson),
                Assignment(users["auditor-scoped"], SystemRole.Auditor,
                    "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]"),
                Assignment(users["auditor-cost-center"], SystemRole.Auditor,
                    "[{\"dimension\":\"COST_CENTER\",\"reference\":\"CC-IT-DEV\"}]"),
                Assignment(users["admin-user"], SystemRole.Admin, GlobalScopeJson));
        }

        private static RoleAssignmentRecord Assignment(Guid userId, SystemRole role, string scopeJson) => new()
        {
            Id = Guid.NewGuid(),
            UserProfileId = userId,
            Role = (int)role,
            ScopeJson = scopeJson,
            Status = (int)AssignmentStatus.Active,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = userId,
            Version = 1
        };

        public HttpClient Client(string subject)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestApiFactory.CreateToken(subject));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            factory.Dispose();
            await container.DisposeAsync();
        }
    }

    private sealed class WrongContractOwner : IPurchaseRequestReferenceOwner
    {
        public string AssertionType => "ACTIVE_IN_ORGANIZATION";

        public PurchaseRequestReferenceType ReferenceType => PurchaseRequestReferenceType.CostCenter;

        public string OwnerId => "wrong-domain";

        public string ContractVersion => "wrong/v1";

        public Task<PurchaseRequestVerificationResponse> VerifyAsync(
            PurchaseRequestVerificationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateToken(string subject) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                configureServices?.Invoke(services);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = "procure-to-pay-tests";
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                });
            });
        }
    }
}
