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
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP evidence for the approval ingestion boundary and the workload surfaces of Bloque 1:
/// workload allowlist, canonical limits, idempotent replay, owner-scoped prerequisite signals
/// and owner cancellation with version checks (CA-01, CA-03, CA-06).
/// </summary>
public sealed class ApprovalWorkflowE2ETests
{
    private const string WorkloadIssuer = "https://keycloak.test/realms/procure-to-pay";
    private const string OwnerClientId = "procurement-api";
    private const string ForeignClientId = "budget-api";
    private const string UnlistedClientId = "unlisted-api";
    private static readonly Guid OriginatorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SubjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineOne = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineTwo = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid LineThree = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private sealed record SubmissionBody(
        Guid OrganizationId,
        string SubjectType,
        Guid SubjectId,
        int SubjectVersion,
        string Operation,
        string ContractVersion,
        string SubmissionKey,
        Guid? RequesterId,
        Guid OriginatorId);

    private sealed record SignalBody(
        bool Satisfied,
        string SignalKey,
        int ExpectedVersion,
        string? EvidenceReference,
        string? EvidenceDigest,
        string? CorrelationReference);

    private sealed record CancelBody(string Reason, int ExpectedVersion);

    [Fact]
    public async Task Submission_ingestion_requires_an_allowlisted_workload_and_enforces_canonical_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new ApprovalApiFactory(
            sqlServer.ConnectionString,
            request => SimpleSubmission(request));

        using var foreign = factory.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(UnlistedClientId));

        // A caller that is authenticated but not allowlisted cannot self-certify requirements (REQ-01).
        using (var forbidden = await foreign.PostAsJsonAsync(
            "/api/v1/approval/submissions", Body(sqlServer.OrganizationId, "submission-forbidden"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Contains("/problems/forbidden",
                await forbidden.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        await using (var verification = sqlServer.CreateContext())
        {
            Assert.Equal(0, await verification.ApprovalCases.CountAsync(cancellationToken));
        }

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(OwnerClientId));
        var body = Body(sqlServer.OrganizationId, "submission-1");

        Guid caseId;
        using (var accepted = await owner.PostAsJsonAsync("/api/v1/approval/submissions", body, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var response = await accepted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            caseId = response.GetProperty("caseId").GetGuid();
            Assert.False(response.GetProperty("replayed").GetBoolean());
            Assert.Equal("BLOCKED", response.GetProperty("status").GetString());
        }

        // An identical replay returns the original case without resolving or creating again.
        using (var replay = await owner.PostAsJsonAsync("/api/v1/approval/submissions", body, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var response = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(caseId, response.GetProperty("caseId").GetGuid());
            Assert.True(response.GetProperty("replayed").GetBoolean());
        }

        // Reusing the submission key with different content is a contract conflict.
        using (var conflict = await owner.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            body with { SubjectVersion = 2 },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Contains("/problems/conflict",
                await conflict.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        await using (var verification = sqlServer.CreateContext())
        {
            Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
            Assert.Equal(1, await verification.ApprovalSubmissionReservations.CountAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task Canonical_limits_above_the_contract_return_413_problem_details()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new ApprovalApiFactory(
            sqlServer.ConnectionString,
            request => OversizedSubmission(request));

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(OwnerClientId));

        using (var oversized = await owner.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            Body(sqlServer.OrganizationId, "submission-oversized"),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            Assert.Contains("/problems/payload-too-large",
                await oversized.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        await using var verification = sqlServer.CreateContext();
        Assert.Equal(0, await verification.ApprovalCases.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Prerequisite_signals_are_owner_scoped_idempotent_and_fail_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new ApprovalApiFactory(
            sqlServer.ConnectionString,
            request => GraphSubmission(request));

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(OwnerClientId));
        using var foreign = factory.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(ForeignClientId));

        using (var submitted = await owner.PostAsJsonAsync(
            "/api/v1/approval/submissions", Body(sqlServer.OrganizationId, "submission-graph"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        }

        Guid budgetCheck;
        Guid riskCheck;
        await using (var context = sqlServer.CreateContext())
        {
            budgetCheck = (await context.ApprovalPrerequisites
                .SingleAsync(record => record.Key == "BUDGET_CHECK", cancellationToken)).Id;
            riskCheck = (await context.ApprovalPrerequisites
                .SingleAsync(record => record.Key == "RISK_CHECK", cancellationToken)).Id;
        }

        var budgetSignal = new SignalBody(true, "signal-budget", 1, "evidence://budget", new string('e', 64), "corr-budget");

        // An allowlisted workload that does not own the prerequisite cannot signal it (REQ-03).
        using (var forbidden = await foreign.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{budgetCheck}/signals", budgetSignal, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var satisfied = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{budgetCheck}/signals", budgetSignal, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, satisfied.StatusCode);
            var response = await satisfied.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.False(response.GetProperty("replayed").GetBoolean());
            // One result event per target of the prerequisite (REQ-08).
            Assert.Equal(2, response.GetProperty("outboxEventIds").GetArrayLength());
        }

        using (var replay = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{budgetCheck}/signals", budgetSignal, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var response = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.True(response.GetProperty("replayed").GetBoolean());
            Assert.Equal(0, response.GetProperty("outboxEventIds").GetArrayLength());
        }

        // The same signal key with different content is a conflict.
        using (var conflict = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{budgetCheck}/signals",
            budgetSignal with { Satisfied = false },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        using (var failed = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{riskCheck}/signals",
            budgetSignal with { Satisfied = false, SignalKey = "signal-risk" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            var response = await failed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(1, response.GetProperty("outboxEventIds").GetArrayLength());
            // FAILED is immutable and keeps the dependent requirement blocked (REQ-03, REQ-07).
            Assert.Equal("BLOCKED", response.GetProperty("status").GetString());
        }

        using (var riskReplay = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{riskCheck}/signals",
            budgetSignal with { Satisfied = false, SignalKey = "signal-risk" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, riskReplay.StatusCode);
            var response = await riskReplay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.True(response.GetProperty("replayed").GetBoolean());
        }

        // A terminal prerequisite cannot be re-opened, not even with a fresh signal key.
        using (var terminal = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{riskCheck}/signals",
            budgetSignal with { Satisfied = false, SignalKey = "signal-risk-second" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, terminal.StatusCode);
        }

        await using var verification = sqlServer.CreateContext();
        Assert.Equal(2, await verification.ApprovalPrerequisiteSignals.CountAsync(cancellationToken));
        Assert.Equal(3, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        var prerequisites = await verification.ApprovalPrerequisites
            .OrderBy(record => record.Key)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(
            (int)PrerequisiteStatus.Satisfied,
            prerequisites.Single(record => record.Key == "BUDGET_CHECK").Status);
        Assert.Equal(
            (int)PrerequisiteStatus.Failed,
            prerequisites.Single(record => record.Key == "RISK_CHECK").Status);
    }

    [Fact]
    public async Task Owner_cancellation_is_authorized_version_checked_and_preserves_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlEnvironment.StartAsync(cancellationToken);
        await using var factory = new ApprovalApiFactory(
            sqlServer.ConnectionString,
            request => SimpleSubmission(request));

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(OwnerClientId));
        using var foreign = factory.CreateClient();
        foreign.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApprovalApiFactory.CreateToken(ForeignClientId));

        Guid caseId;
        using (var submitted = await owner.PostAsJsonAsync(
            "/api/v1/approval/submissions", Body(sqlServer.OrganizationId, "submission-cancel"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            caseId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("caseId").GetGuid();
        }

        var cancellation = new CancelBody("Subject withdrawn by the originator.", 1);

        using (var forbidden = await foreign.PostAsJsonAsync(
            $"/api/v1/approval/cases/{caseId}/cancel", cancellation, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var stale = await owner.PostAsJsonAsync(
            $"/api/v1/approval/cases/{caseId}/cancel",
            cancellation with { ExpectedVersion = 7 },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        using (var cancelled = await owner.PostAsJsonAsync(
            $"/api/v1/approval/cases/{caseId}/cancel", cancellation, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var response = await cancelled.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("CANCELLED", response.GetProperty("status").GetString());
            Assert.Equal(1, response.GetProperty("outboxEventIds").GetArrayLength());
        }

        using (var again = await owner.PostAsJsonAsync(
            $"/api/v1/approval/cases/{caseId}/cancel", cancellation, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        await using var verification = sqlServer.CreateContext();
        Assert.Equal(1, await verification.ApprovalOutboxEvents.CountAsync(cancellationToken));
        var caseRecord = await verification.ApprovalCases.SingleAsync(cancellationToken);
        Assert.Equal(
            "CANCELLED",
            (await verification.ApprovalOutboxEvents.SingleAsync(cancellationToken)).Result);
        Assert.Equal((int)ApprovalCaseStatus.Cancelled, caseRecord.Status);
        Assert.Equal("Subject withdrawn by the originator.", caseRecord.CancellationReason);
    }

    private static SubmissionBody Body(Guid organizationId, string submissionKey) => new(
        organizationId,
        "PURCHASE_REQUEST",
        SubjectId,
        1,
        "SUBMIT",
        "v1",
        submissionKey,
        null,
        OriginatorId);

    private static ApprovalSubmission SimpleSubmission(ApprovalSubmissionRequest request) => new(
        request.SubmissionKey,
        request.OrganizationId,
        request.SubjectType,
        request.SubjectId,
        request.SubjectVersion,
        request.Operation,
        new string('a', 64),
        request.RequesterId,
        request.OriginatorId,
        [
            Requirement(
                request.OrganizationId, "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                [Target(LineOne)], [])
        ],
        []);

    /// <summary>One requirement above the 2.000 requirement contract limit (REQ-09).</summary>
    private static ApprovalSubmission OversizedSubmission(ApprovalSubmissionRequest request) => new(
        request.SubmissionKey,
        request.OrganizationId,
        request.SubjectType,
        request.SubjectId,
        request.SubjectVersion,
        request.Operation,
        new string('a', 64),
        request.RequesterId,
        request.OriginatorId,
        Enumerable.Range(0, ApprovalLimits.MaxRequirementsPerCase + 1)
            .Select(index => Requirement(
                request.OrganizationId, $"REQ-{index}", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                [Target(Guid.NewGuid())], []))
            .ToArray(),
        []);

    /// <summary>Three requirements gated by two owner prerequisites: one satisfiable, one failing.</summary>
    private static ApprovalSubmission GraphSubmission(ApprovalSubmissionRequest request) => new(
        request.SubmissionKey,
        request.OrganizationId,
        request.SubjectType,
        request.SubjectId,
        request.SubjectVersion,
        request.Operation,
        new string('a', 64),
        request.RequesterId,
        request.OriginatorId,
        [
            Requirement(
                request.OrganizationId, "DEPARTMENT_REQ", "DEPARTMENT", SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                [Target(LineOne), Target(LineTwo)], []),
            Requirement(
                request.OrganizationId, "FINANCE_REQ", "FINANCE", SystemRole.FinanceApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.Financial, 1, null, null),
                [Target(LineOne)],
                [new ApprovalDependencyRef(DependencyPredecessorKind.External, "BUDGET_CHECK", [Target(LineOne)])]),
            Requirement(
                request.OrganizationId, "LEGAL_REQ", "LEGAL", SystemRole.LegalReviewer,
                AuthorityRequirement.None,
                [Target(LineThree)],
                [new ApprovalDependencyRef(DependencyPredecessorKind.External, "RISK_CHECK", [Target(LineThree)])])
        ],
        [
            Prerequisite("BUDGET_CHECK", [Target(LineOne), Target(LineTwo)]),
            Prerequisite("RISK_CHECK", [Target(LineThree)])
        ]);

    private static ApprovalRequirementDefinition Requirement(
        Guid organizationId,
        string sourceKey,
        string stageCode,
        SystemRole role,
        AuthorityRequirement authority,
        IReadOnlyList<ApprovalTarget> targets,
        IReadOnlyList<ApprovalDependencyRef> dependencies) => new(
        sourceKey,
        stageCode,
        role,
        authority,
        DecisionScopeDescriptor.Create(
            organizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
        [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
        [OriginatorId],
        targets,
        dependencies);

    private static ExternalPrerequisiteDefinition Prerequisite(string key, IReadOnlyList<ApprovalTarget> targets) =>
        new(key, OwnerClientId, "v1", "BUDGET_CHECK", new string('d', 64), "{\"minimum\":1}", targets);

    private static ApprovalTarget Target(Guid id) => new("LINE", id, 1, new string('c', 64));

    private sealed class ControlledApprovalAdapter(Func<ApprovalSubmissionRequest, ApprovalSubmission> build)
        : IApprovalSubmissionAdapter
    {
        public ApprovalAdapterDescriptor Descriptor { get; } =
            new(OwnerClientId, "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(build(request));
    }

    private sealed class ApprovalApiFactory(
        string connectionString,
        Func<ApprovalSubmissionRequest, ApprovalSubmission> build) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateToken(string clientId) =>
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Approval:Workloads:0:Issuer", WorkloadIssuer);
            builder.UseSetting("Approval:Workloads:0:ClientId", OwnerClientId);
            builder.UseSetting("Approval:Workloads:1:Issuer", WorkloadIssuer);
            builder.UseSetting("Approval:Workloads:1:ClientId", ForeignClientId);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Approval:Workloads:0:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:0:ClientId"] = OwnerClientId,
                    ["Approval:Workloads:1:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:1:ClientId"] = ForeignClientId
                }));
            builder.ConfigureTestServices(services =>
            {
                // The adapter is the controlled trust boundary of SPEC 03 (DEC-01).
                services.RemoveAll<IApprovalSubmissionAdapter>();
                services.AddSingleton<IApprovalSubmissionAdapter>(new ControlledApprovalAdapter(build));
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

        public Guid OrganizationId { get; private set; }

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

            await using (var context = environment.CreateContext())
            {
                environment.OrganizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
            }

            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public async ValueTask DisposeAsync() => await container.DisposeAsync();

        private static OrganizationBootstrapOptions Options(string subject) => new(
            "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
            "Acme Peru S.A.C.", "IT", "Software / IT",
            WorkloadIssuer, subject, "API approval workflow bootstrap");
    }
}
