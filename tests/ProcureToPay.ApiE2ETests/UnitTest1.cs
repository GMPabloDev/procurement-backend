using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
            AssignedBy = Guid.NewGuid(), Version = 1
        });
        context.RoleAssignments.Add(new RoleAssignmentRecord
        {
            Id = Guid.NewGuid(), UserProfileId = auditor.Id, Role = (int)SystemRole.ItReviewer,
            ScopeJson = "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]",
            Status = (int)AssignmentStatus.Active, AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = Guid.NewGuid(), Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);

        await using var factory = new TestApiFactory(connectionString);
        using var pendingClient = factory.CreateClient();
        pendingClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestApiFactory.CreateToken("pending-1"));
        using var pendingState = await pendingClient.GetAsync("/api/v1/me", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, pendingState.StatusCode);
        var pendingProfile = await pendingState.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var pendingProfileIdText = pendingProfile.GetProperty("id").GetString();
        Assert.True(Guid.TryParse(pendingProfileIdText, out var pendingProfileId), pendingProfile.ToString());
        using var pendingBusiness = await pendingClient.GetAsync("/api/v1/organization", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, pendingBusiness.StatusCode);

        using var invalidLifecycleClient = factory.CreateClient();
        invalidLifecycleClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestApiFactory.CreateToken("pending-2"));
        using var pendingTwoState = await invalidLifecycleClient.GetAsync("/api/v1/me", cancellationToken);
        var pendingTwo = await pendingTwoState.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var pendingTwoId = pendingTwo.GetProperty("id").GetGuid();

        using var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestApiFactory.CreateToken("admin-1"));
        using var organization = await adminClient.GetAsync("/api/v1/organization", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, organization.StatusCode);
        var bootstrapAdmin = await context.UserProfiles.SingleAsync(
            item => item.Subject == "admin-1", cancellationToken);
        using var lastAdminDeactivate = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{bootstrapAdmin.Id}/deactivate",
            new { reason = "must retain administrator", expectedVersion = bootstrapAdmin.Version }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, lastAdminDeactivate.StatusCode);
        using var invalidLifecycle = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingTwoId}/activate",
            new { reason = "activate before setup", expectedVersion = 1 }, cancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidLifecycle.StatusCode);
        Assert.Contains("/problems/domain-rule-violation",
            await invalidLifecycle.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        using var validAdminAssignment = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingTwoId}/roles",
            new
            {
                role = "ADMIN",
                scopes = new[] { new { dimension = "ORGANIZATION", reference = (string?)null } },
                reason = "valid global admin scope",
                expectedUserVersion = 1
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, validAdminAssignment.StatusCode);
        using var setup = await adminClient.PatchAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/setup",
            new { departmentId = department.Id, jobTitle = "Requester", reason = "setup", expectedVersion = 1 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using var activate = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/activate",
            new { reason = "activate", expectedVersion = 2 }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using var assign = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/roles",
            new
            {
                role = "AUDITOR",
                scopes = new[] { new { dimension = "DEPARTMENT", reference = "IT" } },
                reason = "assign",
                expectedUserVersion = 3
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using var authorizedBeforeRevoke = await pendingClient.GetAsync("/api/v1/users", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorizedBeforeRevoke.StatusCode);
        using var deactivate = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/deactivate",
            new { reason = "deactivate", expectedVersion = 3 }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        using var revokedBusiness = await pendingClient.GetAsync("/api/v1/organization", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, revokedBusiness.StatusCode);
        using var returnToSetup = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/return-to-setup",
            new { reason = "return without restoring privileges", expectedVersion = 4 }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, returnToSetup.StatusCode);
        using var reactivate = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{pendingProfileId}/activate",
            new { reason = "reactivate", expectedVersion = 5 }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        using var restoredPrivileges = await pendingClient.GetAsync("/api/v1/users", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, restoredPrivileges.StatusCode);
        using var deactivationAuditResponse = await adminClient.GetAsync("/api/v1/audit", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivationAuditResponse.StatusCode);
        var deactivationAudit = await deactivationAuditResponse.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken);
        Assert.Contains(deactivationAudit!, item =>
            item.GetProperty("action").GetString() == "USER_DEACTIVATED" &&
            item.GetProperty("afterJson").GetString()!.Contains("\"before\"", StringComparison.Ordinal));
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
        using var validTimeZone = await adminClient.PutAsJsonAsync("/api/v1/organization",
            new { name = "Acme CET", timeZoneId = "CET", expectedVersion = 1, reason = "timezone" }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, validTimeZone.StatusCode);

        using var invalidAuthority = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "NOT_A_REAL_TYPE", code = "BAD", rank = 1, reason = "invalid" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAuthority.StatusCode);

        using var firstLevel = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "FINANCIAL", code = "FINANCE_L1", rank = 1, reason = "create" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, firstLevel.StatusCode);
        var firstLevelBody = await firstLevel.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(1, firstLevelBody.GetProperty("version").GetInt32());

        using var duplicateRank = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "FINANCIAL", code = "FINANCE_OTHER", rank = 1, reason = "duplicate" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicateRank.StatusCode);

        using var staleLevel = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "FINANCIAL", code = "FINANCE_L1", rank = 2, expectedPreviousVersion = 99, reason = "stale" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, staleLevel.StatusCode);

        using var secondLevel = await adminClient.PostAsJsonAsync("/api/v1/authority-levels",
            new { type = "FINANCIAL", code = "FINANCE_L1", rank = 2, expectedPreviousVersion = 1, reason = "version" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, secondLevel.StatusCode);
        var secondLevelBody = await secondLevel.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(2, secondLevelBody.GetProperty("version").GetInt32());
        using var invalidScope = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{auditor.Id}/roles",
            new
            {
                role = "REQUESTER",
                scopes = new[] { new { dimension = "DEPARTMENT", reference = "DOES_NOT_EXIST" } },
                reason = "invalid reference",
                expectedUserVersion = 1
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);

        using var auditorClient = factory.CreateClient();
        auditorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestApiFactory.CreateToken("auditor-1"));
        using var usersResponse = await auditorClient.GetAsync("/api/v1/users", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        var users = await usersResponse.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken);
        Assert.NotNull(users);
        Assert.NotEmpty(users!);
        Assert.All(users!, user => Assert.Equal(JsonValueKind.Null, user.GetProperty("email").ValueKind));
        using var mutation = await auditorClient.PostAsJsonAsync("/api/v1/departments",
            new { code = "HR", name = "Human Resources", reason = "not allowed" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);
        using var validEligibility = await auditorClient.PostAsJsonAsync("/api/v1/eligibility",
            new
            {
                requiredRole = "IT_REVIEWER",
                requiredScopes = new[] { new { dimension = "DEPARTMENT", reference = "IT" } }
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, validEligibility.StatusCode);
        var eligibility = await validEligibility.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken);
        Assert.NotNull(eligibility);
        Assert.NotEmpty(eligibility!);
        Assert.True(eligibility![0].GetProperty("evidence").GetProperty("roleAssignmentVersion").GetInt32() >= 1);

        using var outOfScopeEligibility = await auditorClient.PostAsJsonAsync("/api/v1/eligibility",
            new
            {
                requiredRole = "IT_REVIEWER",
                requiredScopes = new[] { new { dimension = "DEPARTMENT", reference = "FIN" } }
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScopeEligibility.StatusCode);

        var invalidTokens = new[]
        {
            "not-a-jwt",
            TestApiFactory.CreateToken("invalid-issuer", "https://wrong-issuer"),
            TestApiFactory.CreateToken("invalid-audience", audience: "wrong-audience"),
            TestApiFactory.CreateToken("missing-sub", includeSubject: false),
            TestApiFactory.CreateToken("expired", expiresAt: DateTime.UtcNow.AddMinutes(-10))
        };
        foreach (var (token, index) in invalidTokens.Select((token, index) => (token, index)))
        {
            using var invalidTokenClient = factory.CreateClient();
            invalidTokenClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            using var invalidToken = await invalidTokenClient.GetAsync(
                "/api/v1/organization", cancellationToken);
            Assert.True(invalidToken.StatusCode == HttpStatusCode.Unauthorized,
                $"Invalid token index {index} returned {invalidToken.StatusCode}.");
        }

        var auditorVersion = await context.UserProfiles
            .AsNoTracking()
            .Where(item => item.Id == auditor.Id)
            .Select(item => item.Version)
            .SingleAsync(cancellationToken);
        var authorityLevelId = secondLevelBody.GetProperty("id").GetGuid();
        using var grant = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{auditor.Id}/grants",
            new
            {
                authorityLevelId,
                maxAmountBase = 20_000m,
                baseCurrency = "PEN",
                scopes = new[] { new { dimension = "DEPARTMENT", reference = "IT" } },
                validFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                reason = "grant",
                expectedUserVersion = auditorVersion
            }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using var deactivateAuditor = await adminClient.PostAsJsonAsync(
            $"/api/v1/users/{auditor.Id}/deactivate",
            new { reason = "revoke all auditor privileges", expectedVersion = auditorVersion }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivateAuditor.StatusCode);
        using var revokedAuditorRequest = await auditorClient.GetAsync("/api/v1/users", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, revokedAuditorRequest.StatusCode);
        var revokedGrant = await context.AuthorityGrants
            .AsNoTracking()
            .SingleAsync(item => item.UserProfileId == auditor.Id, cancellationToken);
        Assert.Equal((int)GrantStatus.Revoked, revokedGrant.Status);
    }

    private static OrganizationBootstrapOptions CreateOptions(string subject, string reason) => new(
        "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
        "Acme Peru S.A.C.", "IT", "Software / IT",
        "https://keycloak.test/realms/procure-to-pay", subject, reason);

    private sealed class TestApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateToken(
            string subject,
            string issuer = "https://keycloak.test/realms/procure-to-pay",
            string audience = "procure-to-pay-tests",
            DateTime? expiresAt = null,
            bool includeSubject = true)
        {
            var token = new JwtSecurityToken(
                issuer,
                audience,
                claims: includeSubject ? [new Claim(JwtRegisteredClaimNames.Sub, subject)] : [],
                expires: expiresAt ?? DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

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
                    options.MapInboundClaims = false;
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
