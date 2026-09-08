using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

public sealed class UnitTest1
{
    [Fact]
    public async Task Protected_endpoint_requires_a_bearer_token_and_returns_problem_details()
    {
        await using var factory = new TestApiFactory("Server=localhost;Database=unused");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.GetAsync(
            "/api/v1/organization",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("/problems/authentication-required", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_jwt_jit_authorization_and_auditor_scope_are_enforced_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await sqlServer.StartAsync(cancellationToken);
        var connectionString = sqlServer.GetConnectionString();
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new ProcureToPayDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var bootstrapper = new OrganizationBootstrapper(
            context, loggerFactory.CreateLogger<OrganizationBootstrapper>());
        await bootstrapper.InitializeAsync(CreateOptions("admin-1", "API E2E bootstrap"), cancellationToken);

        var department = await context.Departments.SingleAsync(cancellationToken);
        var organizationRecord = await context.Organizations.SingleAsync(cancellationToken);
        context.Departments.Add(new DepartmentRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationRecord.Id, Code = "FIN",
            Name = "Finance", Status = (int)EntityStatus.Active, Version = 1
        });
        var auditor = new UserProfileRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationRecord.Id,
            Issuer = "https://keycloak.test/realms/procure-to-pay", Subject = "auditor-1",
            Email = "auditor@acme.test", DisplayName = "Scoped Auditor", DepartmentId = department.Id,
            JobTitle = "Audit", Status = (int)UserProfileStatus.Active, Version = 1
        };
        context.UserProfiles.Add(auditor);
        context.RoleAssignments.Add(new RoleAssignmentRecord
        {
            Id = Guid.NewGuid(), UserProfileId = auditor.Id, Role = (int)SystemRole.Auditor,
            ScopeJson = "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]",
            Status = (int)AssignmentStatus.Active, AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = Guid.Parse("00000000-0000-0000-0000-000000000001"), Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);

        await using var factory = new TestApiFactory(connectionString);
        using var pendingClient = factory.CreateClient();
        pendingClient.DefaultRequestHeaders.Add("X-Test-Subject", "pending-1");
        using var pendingState = await pendingClient.GetAsync("/api/v1/me", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, pendingState.StatusCode);
        using var pendingBusiness = await pendingClient.GetAsync("/api/v1/organization", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, pendingBusiness.StatusCode);

        using var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Test-Subject", "admin-1");
        using var organization = await adminClient.GetAsync("/api/v1/organization", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, organization.StatusCode);
        using var health = await adminClient.GetAsync("/health/bootstrap", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var malformed = await adminClient.PostAsync("/api/v1/departments",
            new StringContent("{", System.Text.Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("/problems/validation",
            await malformed.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);

        using var missingRoleRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/users/{Guid.NewGuid()}/roles/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { reason = "missing", expectedVersion = 1 })
        };
        using var missingRole = await adminClient.SendAsync(missingRoleRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingRole.StatusCode);

        using var staleOrganization = await adminClient.PutAsJsonAsync("/api/v1/organization",
            new { name = "Stale", timeZoneId = "America/Lima", expectedVersion = 999, reason = "stale" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, staleOrganization.StatusCode);

        using var invalidAuthority = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "NOT_A_REAL_TYPE", code = "BAD", rank = 1, reason = "invalid" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAuthority.StatusCode);

        using var auditorClient = factory.CreateClient();
        auditorClient.DefaultRequestHeaders.Add("X-Test-Subject", "auditor-1");
        using var usersResponse = await auditorClient.GetAsync("/api/v1/users", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        var users = await usersResponse.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken);
        Assert.NotNull(users);
        Assert.NotEmpty(users!);
        Assert.All(users!, user => Assert.Equal(JsonValueKind.Null, user.GetProperty("email").ValueKind));
        using var mutation = await auditorClient.PostAsJsonAsync("/api/v1/departments",
            new { code = "HR", name = "Human Resources", reason = "not allowed" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
        using var outOfScopeEligibility = await auditorClient.PostAsJsonAsync("/api/v1/eligibility",
            new
            {
                requiredRole = "IT_REVIEWER",
                requiredScopes = new[] { new { dimension = "DEPARTMENT", reference = "FIN" } }
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScopeEligibility.StatusCode);
    }

    private static OrganizationBootstrapOptions CreateOptions(string subject, string reason) => new(
        "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
        "Acme Peru S.A.C.", "IT", "Software / IT",
        "https://keycloak.test/realms/procure-to-pay", subject, reason);

    private sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
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
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Subject"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var claims = new[]
            {
                new Claim("iss", "https://keycloak.test/realms/procure-to-pay"),
                new Claim("sub", subject),
                new Claim(ClaimTypes.Email, $"{subject}@acme.test"),
                new Claim(ClaimTypes.Name, subject)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            Response.ContentType = "application/problem+json";
            await Response.WriteAsJsonAsync(new
            {
                type = "/problems/authentication-required",
                title = "Authentication required",
                status = (int)HttpStatusCode.Unauthorized,
                traceId = Context.TraceIdentifier
            });
        }
    }
}
