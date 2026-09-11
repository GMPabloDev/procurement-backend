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
        Guid policyVersionId;
        Guid subjectId;
        Guid originatorId;
        PolicyEvaluationBundle bundle;
        var nonce = "e2e-waiver-nonce";
        var evidenceDigest = new string('c', 64);
        await using (var context = new ProcureToPayDbContext(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options))
        {
            var organization = await context.Organizations.SingleAsync(cancellationToken);
            organizationId = organization.Id;
            originatorId = (await context.UserProfiles.SingleAsync(
                item => item.Subject == "admin-1", cancellationToken)).Id;

            var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, [PolicyScope.Line]);
            policy.AddRule(new PolicyRule("QUOTATIONS", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireQuotations, "RFQ",
                    minimumQuotations: 3, minimumExceptionQuotations: 1)]));
            policy.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
            policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
            policyVersionId = policy.Id;
            context.PolicySetVersions.Add(new PolicySetVersionRecord
            {
                Id = policy.Id, OrganizationId = organizationId, Sequence = 1,
                Status = (int)PolicySetStatus.Published, ScopesJson = "[\"LINE\"]",
                ContentJson = PolicyCanonicalizer.CanonicalizePolicy(policy),
                ContentDigest = policy.ContentDigest!, CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);

            subjectId = Guid.NewGuid();
            var request = new PolicyRequestInput(
                new PolicySubjectReference(subjectId, 1), organizationId, Guid.NewGuid(), "PEN",
                [new PolicyLineInput(
                    new PolicySubjectReference(Guid.NewGuid(), 1),
                    new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
                    {
                        ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10, "PEN")
                    })]);
            using var scope = factory.Services.CreateScope();
            bundle = await scope.ServiceProvider.GetRequiredService<PolicyEvaluationService>()
                .EvaluatePurchaseRequestAsync(
                    policy, request,
                    new PolicyWorkloadIdentity("https://keycloak.test/realms/procure-to-pay", "procurement-api"),
                    "e2e-waiver-base", DateTimeOffset.UtcNow, "corr-e2e-waiver", cancellationToken);
        }

        var binding = QuotationWaiverEvaluator.ComputeBinding(
            organizationId, policyVersionId, subjectId, bundle.PolicyContentDigest, bundle.ResultDigest, nonce);
        var waiver = new
        {
            type = "REDUCE_MIN_VALID_QUOTATIONS",
            from = 3,
            to = 2,
            floor = 1,
            policyDigest = bundle.PolicyContentDigest,
            evaluationDigest = bundle.ResultDigest,
            binding,
            nonce,
            evidenceDigest,
            approverRole = "PROCUREMENT_APPROVER",
            authorityType = "PROCUREMENT",
            approverId = Guid.NewGuid(),
            workloadSubjectId = Guid.NewGuid(),
            originatorId,
            targetRequirementKey = "RFQ"
        };

        Guid reevaluationId;
        using (var first = await admin.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{bundle.Id}/quotation-waiver", waiver, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var body = await first.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            reevaluationId = body.GetProperty("id").GetGuid();
            Assert.Equal(2, body.GetProperty("controls")[0].GetProperty("minimumQuotations").GetInt32());
            Assert.Equal("REMOVED", body.GetProperty("diff")[0].GetProperty("change").GetString());
        }

        using (var replay = await admin.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{bundle.Id}/quotation-waiver", waiver, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var body = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(reevaluationId, body.GetProperty("id").GetGuid());
        }

        await using (var context = new ProcureToPayDbContext(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options))
        {
            Assert.Equal(1, await context.Set<PolicyExceptionVerificationRecord>().CountAsync(cancellationToken));
            Assert.Equal(2, await context.PolicyEvaluationBundles.CountAsync(cancellationToken));
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
                services.RemoveAll<IQuotationWaiverVerifier>();
                services.AddScoped<IQuotationWaiverVerifier, ControlledWaiverVerifier>();
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

    private sealed class ControlledWaiverVerifier : IQuotationWaiverVerifier
    {
        public string VerifierId => "APPROVAL_WORKFLOW";
        public string ContractVersion => "policy-exception-verifier/v1";

        public Task<QuotationWaiverEvidence?> VerifyAsync(
            QuotationWaiverRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotationWaiverEvidence?>(new QuotationWaiverEvidence(
                request.EvidenceDigest, request.Binding, request.Nonce,
                DateTimeOffset.UtcNow.AddMinutes(10), "workflow-decision-e2e")
            {
                WorkflowDecisionVersion = 1,
                WorkflowDecisionDigest = new string('d', 64),
                AuthorityEvidenceDigest = new string('e', 64),
                EligibilityEvidenceDigest = new string('f', 64),
                CoveredLineIds = request.TargetLineIds,
                SegregationSatisfied = true,
                ApproverId = request.ApproverId,
                ApproverRole = "PROCUREMENT_APPROVER",
                AuthorityType = "PROCUREMENT",
                Scope = "LINE",
                ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                VerifierId = "APPROVAL_WORKFLOW",
                VerifierContractVersion = "policy-exception-verifier/v1"
            });
    }
}
