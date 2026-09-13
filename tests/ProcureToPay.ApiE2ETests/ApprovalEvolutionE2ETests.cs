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
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP evidence for the SPEC 04 surfaces (CA-01, CA-04, CA-06, CA-07): delegation authorization
/// and replay, supersession and revocation restricted to the owning workload or ADMIN, and history
/// visibility that never widens outside the actor scope.
/// </summary>
public sealed class ApprovalEvolutionE2ETests
{
    private const string Issuer = "https://keycloak.test/realms/procure-to-pay";
    private const string WorkloadClientId = "procurement-api";
    private const string AdminSubject = "admin-1";
    private const string ApproverSubject = "approver-1";
    private const string DelegateeSubject = "delegatee-1";
    private const string OutsiderSubject = "outsider-1";
    private const string AuditorSubject = "auditor-1";
    private static readonly Guid ApproverId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DelegateeId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid OutsiderId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid SubjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private sealed record DelegationBody(
        Guid DelegatorUserId,
        Guid DelegateeUserId,
        string Role,
        string ScopeJson,
        DateTimeOffset ValidFrom,
        DateTimeOffset ValidTo,
        string Reason,
        string DelegationCommandKey);

    private sealed record TargetBody(string Type, Guid Id, int Version, string MaterialSnapshotDigest);

    private sealed record MappingBody(
        TargetBody Previous,
        TargetBody Replacement,
        string MaterialitySchemaVersion,
        string MaterialityDigest);

    private sealed record SupersessionBody(
        int ExpectedPreviousCaseVersion,
        string SupersessionKey,
        string SubmissionKey,
        string? ContractVersion,
        IReadOnlyList<MappingBody> TargetMapping);

    private sealed record RevocationBody(
        int ExpectedEvidenceVersion,
        string RevocationKey,
        string Reason,
        string? ReasonCode);

    [Fact]
    public async Task Delegation_endpoints_enforce_authorization_and_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        await environment.SeedProfileAsync(ApproverId, ApproverSubject, SystemRole.ItReviewer, cancellationToken);
        await environment.SeedProfileAsync(DelegateeId, DelegateeSubject, SystemRole.ItReviewer, cancellationToken);
        await environment.SeedProfileAsync(OutsiderId, OutsiderSubject, SystemRole.ItReviewer, cancellationToken);
        await environment.SeedProfileAsync(Guid.NewGuid(), AuditorSubject, SystemRole.Auditor, cancellationToken);
        await using var factory = new EvolutionApiFactory(environment.ConnectionString, environment.DepartmentId);

        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", EvolutionApiFactory.CreateWorkloadToken());
        using var approver = Client(factory, EvolutionApiFactory.CreateUserToken(ApproverSubject));
        using var outsider = Client(factory, EvolutionApiFactory.CreateUserToken(OutsiderSubject));
        using var admin = Client(factory, EvolutionApiFactory.CreateUserToken(AdminSubject));
        using var auditor = Client(factory, EvolutionApiFactory.CreateUserToken(AuditorSubject));

        var caseId = await SubmitAsync(environment, workload, "submission-evolution-delegation");
        var scopeJson = DecisionScopeDescriptor.Create(
            environment.OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.Department, environment.DepartmentId, 1)])
            .ToCanonicalJson();

        var body = new DelegationBody(
            ApproverId, DelegateeId, "ItReviewer", scopeJson,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2),
            "Vacation coverage", "delegation-e2e-1");

        using (var response = await approver.PostAsJsonAsync("/api/v1/approval/delegations", body, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<DelegationOutcome>(cancellationToken);
            Assert.NotNull(created);
            Assert.False(created!.Replayed);
            Assert.Equal("ACTIVE", created.Status);
        }

        using (var replay = await approver.PostAsJsonAsync("/api/v1/approval/delegations", body, cancellationToken))
        {
            var created = await replay.Content.ReadFromJsonAsync<DelegationOutcome>(cancellationToken);
            Assert.True(created!.Replayed);
        }

        // An unrelated active user cannot delegate on behalf of somebody else (CA-01).
        using (var forbidden = await outsider.PostAsJsonAsync(
                   "/api/v1/approval/delegations",
                   body with { DelegationCommandKey = "delegation-e2e-outsider" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // ADMIN may act as operational relief with a reason (CA-01).
        using (var administered = await admin.PostAsJsonAsync(
                   "/api/v1/approval/delegations",
                   body with
                   {
                       DelegationCommandKey = "delegation-e2e-admin",
                       Reason = "Operational unavailability",
                       ValidFrom = DateTimeOffset.UtcNow.AddHours(3),
                       ValidTo = DateTimeOffset.UtcNow.AddHours(4)
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, administered.StatusCode);
            var created = await administered.Content.ReadFromJsonAsync<DelegationOutcome>(cancellationToken);
            Assert.Equal("SCHEDULED", created!.Status);
        }

        using (var mine = await approver.GetAsync("/api/v1/approval/delegations", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            var delegations = await mine.Content.ReadFromJsonAsync<List<DelegationView>>(cancellationToken);
            Assert.Equal(2, delegations!.Count);
        }

        using (var ownScope = await outsider.GetAsync("/api/v1/approval/delegations", cancellationToken))
        {
            var delegations = await ownScope.Content.ReadFromJsonAsync<List<DelegationView>>(cancellationToken);
            Assert.Empty(delegations!);
        }

        using (var forbidden = await outsider.GetAsync(
                   "/api/v1/approval/delegations/organization", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var organizational = await auditor.GetAsync(
                   "/api/v1/approval/delegations/organization", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, organizational.StatusCode);
            var delegations = await organizational.Content.ReadFromJsonAsync<List<DelegationView>>(cancellationToken);
            Assert.Equal(2, delegations!.Count);
        }

        using (var history = await workload.GetAsync(
                   $"/api/v1/approval/cases/{caseId}/history", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            var chain = await history.Content.ReadFromJsonAsync<CaseChainView>(cancellationToken);
            Assert.Equal(caseId, chain!.CaseId);
        }

        using (var outsideScope = await outsider.GetAsync(
                   $"/api/v1/approval/cases/{caseId}/history", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, outsideScope.StatusCode);
        }
    }

    [Fact]
    public async Task Supersession_and_revocation_are_restricted_and_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await EvolutionEnvironment.StartAsync(cancellationToken);
        await environment.SeedProfileAsync(ApproverId, ApproverSubject, SystemRole.ItReviewer, cancellationToken);
        await using var factory = new EvolutionApiFactory(environment.ConnectionString, environment.DepartmentId);

        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", EvolutionApiFactory.CreateWorkloadToken());
        using var approver = Client(factory, EvolutionApiFactory.CreateUserToken(ApproverSubject));
        using var admin = Client(factory, EvolutionApiFactory.CreateUserToken(AdminSubject));

        var caseId = await SubmitAsync(environment, workload, "submission-evolution-supersede");
        var (taskId, taskVersion) = await environment.TaskAsync(caseId, cancellationToken);
        using (var decision = await approver.PostAsJsonAsync(
                   $"/api/v1/approval/tasks/{taskId}/decisions",
                   new { action = "Approve", reason = "Approved", decisionKey = "decision-e2e", expectedTaskVersion = taskVersion },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }

        var previousVersion = await environment.CaseVersionAsync(caseId, cancellationToken);
        var mapping = new[]
        {
            new MappingBody(
                new TargetBody("LINE", LineId, 1, new string('c', 64)),
                new TargetBody("LINE", LineId, 1, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('f', 64))
        };
        var supersession = new SupersessionBody(
            previousVersion, "supersession-e2e-1", "submission-evolution-supersede-2", "v1", mapping);

        // A user token cannot supersede: only the owning workload can (CA-04, CA-07).
        var secondCaseId = await SubmitAsync(environment, workload, "submission-evolution-supersede-3");
        var secondVersion = await environment.CaseVersionAsync(secondCaseId, cancellationToken);
        using (var forbidden = await approver.PostAsJsonAsync(
                   $"/api/v1/approval/cases/{secondCaseId}/supersessions",
                   supersession with { ExpectedPreviousCaseVersion = secondVersion, SupersessionKey = "supersession-e2e-forbidden" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        Guid newCaseId;
        using (var response = await workload.PostAsJsonAsync(
                   $"/api/v1/approval/cases/{caseId}/supersessions", supersession, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var outcome = await response.Content.ReadFromJsonAsync<SupersessionOutcome>(cancellationToken);
            Assert.NotNull(outcome);
            Assert.False(outcome!.Replayed);
            Assert.Equal(1, outcome.CarryForwardCount);
            newCaseId = outcome.CaseId;
        }

        using (var replay = await workload.PostAsJsonAsync(
                   $"/api/v1/approval/cases/{caseId}/supersessions", supersession, cancellationToken))
        {
            var outcome = await replay.Content.ReadFromJsonAsync<SupersessionOutcome>(cancellationToken);
            Assert.True(outcome!.Replayed);
        }

        // Revocation: the owning workload invalidates its binding, ADMIN only as containment.
        var (evidenceId, evidenceVersion) = await environment.EvidenceAsync(caseId, cancellationToken);
        var revocation = new RevocationBody(
            evidenceVersion, "revocation-e2e-1", "Subject binding invalidated", "OWNER_INVALIDATION");
        using (var response = await workload.PostAsJsonAsync(
                   $"/api/v1/approval/evidence/{evidenceId}/revocations", revocation, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var outcome = await response.Content.ReadFromJsonAsync<RevocationOutcome>(cancellationToken);
            Assert.Equal("REVOKED", outcome!.EvidenceStatus);
        }

        using (var replay = await workload.PostAsJsonAsync(
                   $"/api/v1/approval/evidence/{evidenceId}/revocations", revocation, cancellationToken))
        {
            var outcome = await replay.Content.ReadFromJsonAsync<RevocationOutcome>(cancellationToken);
            Assert.True(outcome!.Replayed);
        }

        using (var forbidden = await approver.PostAsJsonAsync(
                   $"/api/v1/approval/evidence/{evidenceId}/revocations",
                   revocation with { RevocationKey = "revocation-e2e-outside" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var (secondTask, secondTaskVersion) = await environment.TaskAsync(secondCaseId, cancellationToken);
        using (var secondDecision = await approver.PostAsJsonAsync(
                   $"/api/v1/approval/tasks/{secondTask}/decisions",
                   new
                   {
                       action = "Approve",
                       reason = "Second approval",
                       decisionKey = "decision-e2e-2",
                       expectedTaskVersion = secondTaskVersion
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, secondDecision.StatusCode);
        }

        var (secondEvidenceId, secondEvidenceVersion) = await environment.EvidenceAsync(secondCaseId, cancellationToken);
        using (var containment = await admin.PostAsJsonAsync(
                   $"/api/v1/approval/evidence/{secondEvidenceId}/revocations",
                   new RevocationBody(
                       secondEvidenceVersion, "revocation-e2e-admin", "Incident containment",
                       "INCIDENT_CONTAINMENT"),
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, containment.StatusCode);
            var outcome = await containment.Content.ReadFromJsonAsync<RevocationOutcome>(cancellationToken);
            Assert.Equal("REVOKED", outcome!.EvidenceStatus);
        }

        using (var history = await workload.GetAsync(
                   $"/api/v1/approval/cases/{newCaseId}/history", cancellationToken))
        {
            var chain = await history.Content.ReadFromJsonAsync<CaseChainView>(cancellationToken);
            Assert.Single(chain!.CarryForwards!);
            Assert.Equal(caseId, chain.PreviousCaseId);
        }

        using (var previousHistory = await workload.GetAsync(
                   $"/api/v1/approval/cases/{caseId}/history", cancellationToken))
        {
            var chain = await previousHistory.Content.ReadFromJsonAsync<CaseChainView>(cancellationToken);
            Assert.Single(chain!.Revocations!);
            Assert.Equal(newCaseId, chain.NextCaseId);
        }
    }

    private static HttpClient Client(EvolutionApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> SubmitAsync(
        EvolutionEnvironment environment,
        HttpClient workload,
        string submissionKey)
    {
        using var response = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = environment.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = "SUBMIT",
                contractVersion = "v1",
                submissionKey,
                requesterId = (Guid?)null,
                originatorId = SubjectId
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await response.Content.ReadFromJsonAsync<SubmissionOutcome>();
        return submitted!.CaseId;
    }

    private sealed record DelegationOutcome(Guid DelegationId, string Status, int Version, bool Replayed);

    private sealed record DelegationView(Guid DelegationId, Guid DelegatorUserId, Guid DelegateeUserId, string Role);

    private sealed record SupersessionOutcome(
        Guid CaseId, Guid PreviousCaseId, string Status, int Version, bool Replayed, int CarryForwardCount);

    private sealed record RevocationOutcome(Guid RevocationId, Guid EvidenceId, string EvidenceStatus, int EvidenceVersion, bool Replayed);

    private sealed record SubmissionOutcome(Guid CaseId, string Status, int Version, bool Replayed);

    private sealed record CaseChainView(
        Guid CaseId,
        Guid? PreviousCaseId,
        Guid? NextCaseId,
        string Status,
        int Version,
        List<JsonElement>? CarryForwards = null,
        List<JsonElement>? Revocations = null);

    private sealed class EvolutionAdapter : IApprovalSubmissionAdapter
    {
        private readonly Guid departmentId;

        public EvolutionAdapter(Guid departmentId)
        {
            this.departmentId = departmentId;
        }

        public ApprovalAdapterDescriptor Descriptor { get; } = new(
            "adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            var requirement = new ApprovalRequirementDefinition(
                "DEPARTMENT_REQ",
                "DEPARTMENT",
                SystemRole.ItReviewer,
                AuthorityRequirement.None,
                DecisionScopeDescriptor.Create(
                    request.OrganizationId, [new DecisionScopeEntry(ScopeDimension.Department, departmentId, 1)]),
                [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
                [request.OriginatorId],
                [new ApprovalTarget("LINE", LineId, 1, new string('c', 64))],
                []);
            return Task.FromResult(new ApprovalSubmission(
                request.SubmissionKey,
                request.OrganizationId,
                request.SubjectType,
                request.SubjectId,
                request.SubjectVersion,
                request.Operation,
                new string('a', 64),
                request.RequesterId,
                request.OriginatorId,
                [requirement],
                []));
        }
    }

    private sealed class EvolutionApiFactory(string connectionString, Guid departmentId)
        : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateUserToken(string subject) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        internal static string CreateWorkloadToken() =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{WorkloadClientId}"),
                    new Claim("client_id", WorkloadClientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Approval:Workloads:0:Issuer", Issuer);
            builder.UseSetting("Approval:Workloads:0:ClientId", WorkloadClientId);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Approval:Workloads:0:Issuer"] = Issuer,
                    ["Approval:Workloads:0:ClientId"] = WorkloadClientId
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IApprovalSubmissionAdapter>();
                services.AddSingleton<IApprovalSubmissionAdapter>(new EvolutionAdapter(departmentId));
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

    private sealed class EvolutionEnvironment : IAsyncDisposable
    {
        internal static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private MsSqlContainer container = null!;
        private readonly DateTimeOffset assignedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public string ConnectionString { get; private set; } = string.Empty;

        public Guid OrganizationId { get; private set; }

        public Guid DepartmentId { get; private set; }

        public static async Task<EvolutionEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new EvolutionEnvironment
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
                    .InitializeAsync(
                        new OrganizationBootstrapOptions(
                            "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
                            "Acme Peru S.A.C.", "IT", "Software / IT",
                            Issuer, AdminSubject, "API evolution bootstrap"),
                        cancellationToken);
                environment.OrganizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
                environment.DepartmentId = (await context.Departments
                    .SingleAsync(record => record.Code == "IT", cancellationToken)).Id;
            }

            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public async Task SeedProfileAsync(
            Guid userId,
            string subject,
            SystemRole role,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = userId,
                OrganizationId = OrganizationId,
                Issuer = Issuer,
                Subject = subject,
                Email = $"{subject}@acme.test",
                DisplayName = subject,
                DepartmentId = DepartmentId,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = userId,
                Role = (int)role,
                ScopeJson = role == SystemRole.ItReviewer
                    ? "[{\"dimension\":\"DEPARTMENT\",\"reference\":\"IT\"}]"
                    : "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = assignedAt,
                AssignedBy = userId,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<(Guid TaskId, int TaskVersion)> TaskAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var task = await context.ApprovalTasks.SingleAsync(
                record => record.CaseId == caseId, cancellationToken);
            return (task.Id, task.Version);
        }

        public async Task<int> CaseVersionAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalCases.SingleAsync(
                record => record.Id == caseId, cancellationToken)).Version;
        }

        public async Task<(Guid EvidenceId, int Version)> EvidenceAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var decision = await context.ApprovalDecisions
                .Where(record => record.CaseId == caseId)
                .OrderByDescending(record => record.DecidedAt)
                .FirstAsync(cancellationToken);
            var evidence = await context.DecisionAuthorityEvidences.SingleAsync(
                record => record.RootHumanDecisionId == decision.RootHumanDecisionId, cancellationToken);
            return (evidence.Id, evidence.Version);
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
