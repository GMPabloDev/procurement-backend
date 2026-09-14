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
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP surface of SPEC 06 REQ-10/REQ-11 (CA-02): visibility and actor scope, minimized AUDITOR
/// projection, exact limits and the expected Problem Details for create, revision and cancellation.
/// </summary>
public sealed class PurchaseRequestE2ETests
{
    private const string Issuer = "https://keycloak.test/realms/procure-to-pay";
    private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";

    [Fact]
    public async Task Visibility_and_actor_scope_are_enforced_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var requester = environment.Client("requester");
        using var other = environment.Client("other");
        using var scopedAuditor = environment.Client("auditor-scoped");
        using var globalAuditor = environment.Client("auditor-global");
        using var admin = environment.Client("admin-user");

        using var created = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("visibility-1", 1), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var creation = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var requestId = creation.GetProperty("requestId").GetGuid();

        // The requester reads the full snapshot; nobody else in the organization does.
        using var own = await requester.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownBody = await own.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("Justification", ownBody.GetProperty("versions")[0].GetProperty("businessJustification").GetString());
        Assert.NotEqual(
            JsonValueKind.Null,
            ownBody.GetProperty("versions")[0].GetProperty("lines")[0].GetProperty("content").ValueKind);

        foreach (var (client, expected) in new (HttpClient, HttpStatusCode)[]
                 {
                     (other, HttpStatusCode.NotFound),
                     (scopedAuditor, HttpStatusCode.NotFound),
                     (admin, HttpStatusCode.NotFound)
                 })
        {
            using var response = await client.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
            Assert.Equal(expected, response.StatusCode);
        }

        // An organizational AUDITOR reads a minimized projection without PII or summaries (REQ-10).
        using var audit = await globalAuditor.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var auditBody = await audit.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var auditedVersion = auditBody.GetProperty("versions")[0];
        Assert.Equal(
            JsonValueKind.Null,
            auditedVersion.GetProperty("lines")[0].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Null, auditedVersion.GetProperty("businessJustification").ValueKind);
        Assert.Equal(JsonValueKind.Null, auditedVersion.GetProperty("reason").ValueKind);

        // Only the requester may mutate the request.
        using var foreignRevision = await other.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/revisions",
            environment.RevisionBody(creation.GetProperty("version").GetInt32(), "revision-foreign"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRevision.StatusCode);
        using var foreignCancel = await other.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/cancellation",
            new { expectedVersion = creation.GetProperty("version").GetInt32(), cancelKey = "cancel-foreign", reason = "Foreign" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, foreignCancel.StatusCode);
    }

    [Fact]
    public async Task Limits_and_problem_details_are_fail_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var requester = environment.Client("requester");

        // 500 lines is the contractual maximum; 501 is a 413 before anything is persisted.
        using var maximum = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("limits-max", 500), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, maximum.StatusCode);
        using var tooMany = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("limits-over", 501), cancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        Assert.Equal(
            "/problems/payload-too-large",
            await Environment.ProblemTypeAsync(tooMany, cancellationToken));

        // 257 risk answers exceed the per-line limit and never reach persistence.
        using var tooManyAnswers = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("limits-answers", 1, riskAnswers: 257), cancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooManyAnswers.StatusCode);
        using var maximumAnswers = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("limits-answers-ok", 1, riskAnswers: 256), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, maximumAnswers.StatusCode);

        // Invalid keys and summaries are payload validation (400), not a dependency failure.
        using var longKey = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody(new string('k', 129), 1), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, longKey.StatusCode);
        Assert.Equal("/problems/validation", await Environment.ProblemTypeAsync(longKey, cancellationToken));
        using var emptySummary = await requester.PostAsJsonAsync(
            "/v1/purchase-requests", environment.CreateBody("limits-summary", 1, needSummary: string.Empty), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptySummary.StatusCode);

        // A terminal request keeps its history: revision is a conflict, cancellation replays exactly once.
        var created = await maximum.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var requestId = created.GetProperty("requestId").GetGuid();
        var version = created.GetProperty("version").GetInt32();
        using var cancel = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/cancellation",
            new { expectedVersion = version, cancelKey = "cancel-1", reason = "No longer needed" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var cancelReplay = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/cancellation",
            new { expectedVersion = version, cancelKey = "cancel-1", reason = "No longer needed" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancelReplay.StatusCode);
        var replay = await cancelReplay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        using var cancelConflict = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/cancellation",
            new { expectedVersion = version, cancelKey = "cancel-2", reason = "Other reason" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, cancelConflict.StatusCode);
        using var reviseTerminal = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/revisions",
            environment.RevisionBody(version, "revision-terminal"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, reviseTerminal.StatusCode);
    }

    [Fact]
    public async Task Idempotent_keys_replay_and_conflict_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var requester = environment.Client("requester");

        var body = environment.CreateBody("idempotency-1", 1);
        using var created = await requester.PostAsJsonAsync("/v1/purchase-requests", body, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var creation = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.False(creation.GetProperty("replayed").GetBoolean());

        // The same key and payload replay the stored identity; another payload is a conflict.
        using var replay = await requester.PostAsJsonAsync("/v1/purchase-requests", body, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.True(replayBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(creation.GetProperty("requestId").GetGuid(), replayBody.GetProperty("requestId").GetGuid());

        using var conflict = await requester.PostAsJsonAsync(
            "/v1/purchase-requests",
            environment.CreateBody("idempotency-1", 1, needSummary: "Another summary"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("/problems/conflict", await Environment.ProblemTypeAsync(conflict, cancellationToken));

        var requestId = creation.GetProperty("requestId").GetGuid();
        var version = creation.GetProperty("version").GetInt32();
        var (revision, retained) = await environment.RetainedRevisionBodyAsync(
            requester, requestId, version, "revision-idempotent", cancellationToken);
        using var revised = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/revisions", revision, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var revisedBody = await revised.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(2, revisedBody.GetProperty("version").GetInt32());
        using var revisionReplay = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/revisions", revision, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, revisionReplay.StatusCode);
        Assert.True((await revisionReplay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("replayed").GetBoolean());
        using var revisionConflict = await requester.PostAsJsonAsync(
            $"/v1/purchase-requests/{requestId}/revisions",
            environment.RetainedRevisionBody(version, "revision-idempotent", retained, "Changed"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, revisionConflict.StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_missing_purchase_request_registrations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var client = environment.Client("requester");

        // Health is anonymous and code-only: it names the missing owner slots and workload without
        // exposing ids, bindings or content (SPEC 06 NFR-04, T-08).
        using var response = await client.GetAsync("/health/purchase-request", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("DEGRADED", body.GetProperty("status").GetString());
        var code = body.GetProperty("code").GetString();
        Assert.Contains("PURCHASE_REQUEST_OWNER_UNAVAILABLE:COST_CENTER", code, StringComparison.Ordinal);
        Assert.Contains("PURCHASE_REQUEST_OWNER_UNAVAILABLE:SPEND_CATEGORY", code, StringComparison.Ordinal);
        Assert.Contains("PURCHASE_REQUEST_WORKLOAD_UNAVAILABLE", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME", code, StringComparison.Ordinal);
    }

    private sealed class Environment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private TestApiFactory factory = null!;
        private Guid legalEntityId;
        private Guid departmentId;

        public static async Task<Environment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new Environment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            var connectionString = environment.container.GetConnectionString();
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
            environment.legalEntityId = (await context.LegalEntities.SingleAsync(cancellationToken)).Id;
            environment.departmentId = department.Id;
            environment.Seed(context, organization.Id, department.Id);
            await context.SaveChangesAsync(cancellationToken);
            environment.factory = new TestApiFactory(connectionString);
            return environment;
        }

        private void Seed(ProcureToPayDbContext context, Guid organizationId, Guid departmentId)
        {
            var users = new[]
            {
                ("requester", (int)EntityStatus.Active),
                ("other", (int)EntityStatus.Active),
                ("auditor-scoped", (int)EntityStatus.Active),
                ("auditor-global", (int)EntityStatus.Active),
                ("admin-user", (int)EntityStatus.Active)
            };
            foreach (var (subject, status) in users)
            {
                context.UserProfiles.Add(new UserProfileRecord
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    Issuer = Issuer,
                    Subject = subject,
                    Email = $"{subject}@acme.test",
                    DisplayName = subject,
                    DepartmentId = departmentId,
                    Status = status == (int)EntityStatus.Active ? (int)UserProfileStatus.Active : status,
                    Version = 1
                });
            }

            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var profile in context.UserProfiles.Local.Where(record => record.Issuer == Issuer))
            {
                ids[profile.Subject] = profile.Id;
            }

            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = ids["auditor-global"],
                Role = (int)SystemRole.Auditor,
                ScopeJson = GlobalScopeJson,
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ids["admin-user"],
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = ids["auditor-scoped"],
                Role = (int)SystemRole.Auditor,
                ScopeJson = "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ids["admin-user"],
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = ids["admin-user"],
                Role = (int)SystemRole.Admin,
                ScopeJson = GlobalScopeJson,
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ids["admin-user"],
                Version = 1
            });
        }

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

        public object CreateBody(
            string revisionKey,
            int lineCount,
            int riskAnswers = 0,
            string needSummary = "Need for the API E2E test") => new
        {
            legalEntityRef = new { entityType = "LEGAL_ENTITY", id = legalEntityId, version = 1 },
            businessJustification = "Justification",
            reason = "Initial request",
            revisionKey,
            lines = Enumerable.Range(0, lineCount)
                .Select(index => new
                {
                    clientLineKey = $"line-{index}",
                    content = LineContent(needSummary, riskAnswers)
                })
                .ToArray()
        };

        public object RevisionBody(int expectedVersion, string revisionKey) => new
        {
            expectedRequestVersion = expectedVersion,
            businessJustification = "Justification v2",
            reason = "Scope change",
            revisionKey,
            retained = Array.Empty<object>(),
            changed = Array.Empty<object>(),
            added = Array.Empty<object>(),
            removed = Array.Empty<object>()
        };

        /// <summary>A valid revision that reuses the exact current line reference (REQ-02).</summary>
        public async Task<(object Body, object Retained)> RetainedRevisionBodyAsync(
            HttpClient client,
            Guid requestId,
            int version,
            string revisionKey,
            CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var versionBody = body.GetProperty("versions").EnumerateArray()
                .Single(candidate => candidate.GetProperty("version").GetInt32() == version);
            var line = versionBody.GetProperty("lines")[0];
            var retained = new
            {
                id = line.GetProperty("lineId").GetGuid(),
                version = line.GetProperty("lineVersion").GetInt32(),
                contentDigest = line.GetProperty("contentDigest").GetString()
            };
            return (RetainedRevisionBody(version, revisionKey, retained, "Justification v2"), (object)retained);
        }

        public object RetainedRevisionBody(
            int expectedVersion,
            string revisionKey,
            object retained,
            string justification) => new
        {
            expectedRequestVersion = expectedVersion,
            businessJustification = justification,
            reason = "Scope change",
            revisionKey,
            retained = new[] { retained },
            changed = Array.Empty<object>(),
            added = Array.Empty<object>(),
            removed = Array.Empty<object>()
        };

        private object LineContent(string needSummary, int riskAnswers = 0) => new
        {
            estimatedGrossAmount = 100m,
            transactionCurrency = "PEN",
            baseAmount = 100m,
            baseCurrency = "PEN",
            fiscalYear = 2026,
            purchaseType = "GOOD",
            spendCategoryRef = new { catalog = "SPEND_CATEGORY", code = "HARDWARE", version = 1, digest = new string('d', 64) },
            costCenterRef = new { entityType = "COST_CENTER", id = Guid.NewGuid(), version = 1 },
            costCenterDepartmentRef = new { entityType = "DEPARTMENT", id = departmentId, version = 1 },
            beneficiaryDepartmentRef = new { entityType = "DEPARTMENT", id = departmentId, version = 1 },
            requestedForUserRef = new { entityType = "USER", id = Guid.NewGuid(), version = 1 },
            supplierRef = (object?)null,
            preferredProductRef = (object?)null,
            requiredProductRef = (object?)null,
            contractRequired = false,
            nonStandardTerms = false,
            agreementStatus = "NONE",
            needSummary,
            riskAnswers = Enumerable.Range(0, riskAnswers)
                .Select(index => new
                {
                    questionCode = $"Q{index}",
                    schemaVersion = 1,
                    value = "true",
                    valueKind = "BOOLEAN"
                })
                .ToArray(),
            fxAttestationRef = (object?)null
        };

        public static async Task<string?> ProblemTypeAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("type", out var type) ? type.GetString() : null;
        }

        public async ValueTask DisposeAsync()
        {
            factory.Dispose();
            await container.DisposeAsync();
        }
    }

    private sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
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
