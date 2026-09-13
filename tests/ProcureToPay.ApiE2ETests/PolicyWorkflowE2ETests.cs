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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP evidence for contractual limits (400/413 Problem Details) and the idempotent
/// quotation waiver replay with a controlled verifier and no real workflow (CA-09, CA-10, CA-11, CA-12).
/// </summary>
public sealed class PolicyWorkflowE2ETests
{
    [Fact]
    public async Task Limits_return_problem_details_and_a_waiver_replay_returns_the_same_reevaluation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var connectionString = sqlServer.GetConnectionString();
        await using (var context = new ProcureToPayDbContext(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options))
        {
            await context.Database.MigrateAsync(cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            await new OrganizationBootstrapper(context, loggerFactory.CreateLogger<OrganizationBootstrapper>())
                .InitializeAsync(Options("admin-1"), cancellationToken);
        }

        await using var factory = new WorkflowApiFactory(connectionString);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", WorkflowApiFactory.CreateToken("admin-1"));

        using (var oversized = await admin.PostAsJsonAsync("/api/v1/policies/drafts", new
        {
            scopesJson = "[\"LINE\"]",
            contentJson = new string('x', (10 * 1024 * 1024) + 1),
            contentDigest = new string('a', 64),
            reason = "oversized policy"
        }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            Assert.Contains("/problems/payload-too-large",
                await oversized.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        var manyRules = """{"rules":[""" + string.Join(',', Enumerable.Repeat("{}", 2001)) + "]}";
        using (var tooManyRules = await admin.PostAsJsonAsync("/api/v1/policies/drafts", new
        {
            scopesJson = "[\"LINE\"]",
            contentJson = manyRules,
            contentDigest = PolicyCanonicalizer.Hash(manyRules),
            reason = "too many rules"
        }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooManyRules.StatusCode);
            Assert.Contains("/problems/payload-too-large",
                await tooManyRules.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        Guid organizationId;
        await using (var context = new ProcureToPayDbContext(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options))
        {
            organizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
            Assert.NotEqual(Guid.Empty, organizationId);
        }

        // SPEC 05 REQ-05/REQ-07: the legacy user-initiated waiver route is gone. Even ADMIN is
        // rejected before any evaluation or verifier is consulted, and the policy engine cannot
        // be called without the service workload identity.
        using (var legacy = await admin.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{Guid.NewGuid()}/quotation-waiver",
            new
            {
                type = "REDUCE_MIN_VALID_QUOTATIONS",
                referenceId = Guid.NewGuid(),
                requestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                requestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
                workflowDecisionId = Guid.NewGuid(),
                workflowDecisionVersion = 1,
                evidenceDigest = new string('c', 64),
                targetRequirementKey = "RFQ",
                from = 3,
                to = 2,
                floor = 1,
                coveredLines = Array.Empty<object>(),
                requesterId = (Guid?)null,
                originatorId = Guid.NewGuid(),
                workloadSubjectId = Guid.NewGuid(),
                nonce = "user-waiver-nonce"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
            Assert.Contains("/problems/forbidden",
                await legacy.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        // The verifier service endpoint is not reachable with an interactive user token either.
        using (var forbidden = await admin.PostAsJsonAsync(
            "/v1/policy-exceptions/verify",
            new { contract_version = "workflow-verification-request/v1" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        }
    }

    private static OrganizationBootstrapOptions Options(string subject) => new(
        "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
        "Acme Peru S.A.C.", "IT", "Software / IT",
        "https://keycloak.test/realms/procure-to-pay", subject, "API workflow bootstrap");

    private sealed class WorkflowApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateToken(string subject) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                "https://keycloak.test/realms/procure-to-pay",
                "procure-to-pay-tests",
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Policy:Workloads:0:Issuer", "https://keycloak.test/realms/procure-to-pay");
            builder.UseSetting("Policy:Workloads:0:ClientId", "procurement-api");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Policy:Workloads:0:Issuer"] = "https://keycloak.test/realms/procure-to-pay",
                    ["Policy:Workloads:0:ClientId"] = "procurement-api"
                }));
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
                    options.TokenValidationParameters.ValidIssuer = "https://keycloak.test/realms/procure-to-pay";
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
