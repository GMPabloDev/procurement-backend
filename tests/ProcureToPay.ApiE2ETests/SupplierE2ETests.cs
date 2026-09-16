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
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence.Suppliers;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// HTTP surface of the Supplier Master (SPEC 09 REQ-02, REQ-03, REQ-05, REQ-11, REQ-12, CA-02,
/// CA-04, CA-08): the governed lifecycle over the real API, the minimal reads by role, the masked
/// banking projection, the audited reveal and the supplier readiness code.
/// </summary>
public sealed class SupplierE2ETests
{
    private const string Issuer = "https://keycloak.test/realms/procure-to-pay";

    [Fact]
    public async Task The_supplier_lifecycle_is_governed_over_http_with_minimal_reads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SupplierEnvironment.StartAsync(cancellationToken);
        using var buyer = environment.Client("buyer");
        using var approver = environment.Client("approver");
        using var requester = environment.Client("requester");
        using var auditor = environment.Client("auditor");

        // Only PROCUREMENT_BUYER drafts, and only with a complete content.
        using var forbidden = await requester.PostAsJsonAsync(
            "/api/v1/suppliers", environment.ContentRequest("ABC Tech SAC", "20123456789"), cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var created = await buyer.PostAsJsonAsync(
            "/api/v1/suppliers", environment.ContentRequest("ABC Tech SAC", "20123456789"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var supplierId = createdBody.GetProperty("supplierId").GetGuid();
        var candidateVersion = createdBody.GetProperty("version").GetInt32();
        Assert.True(createdBody.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal("DRAFT", createdBody.GetProperty("status").GetString());
        Assert.Contains(
            "legal_name",
            createdBody.GetProperty("sensitiveFields").EnumerateArray().Select(field => field.GetString()));

        // A duplicated fiscal identity is a conflict, never a second root.
        using var duplicated = await buyer.PostAsJsonAsync(
            "/api/v1/suppliers", environment.ContentRequest("ABC Tech S.A.C.", "20123456789"), cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicated.StatusCode);

        // Submitting freezes the candidate and opens the approval case.
        using var submitted = await buyer.PostAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}/versions/{candidateVersion}/submit",
            new { reason = "Activate supplier", submissionKey = "e2e-submit-1" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        // The AP specialist does not decide supplier governance; the approver does, and the editor
        // never approves its own proposal (REQ-03).
        // A task that does not belong to the actor is not visible, so the editor can never decide
        // its own proposal (REQ-03).
        using var wrongApprover = await buyer.PostAsync(
            $"/api/v1/approval/tasks/{await environment.PendingTaskIdAsync(cancellationToken)}/decision",
            JsonContent(new { action = "APPROVE", reason = "Not allowed", decisionKey = "e2e-denied" }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, wrongApprover.StatusCode);

        await environment.ApprovePendingTaskAsync(cancellationToken);

        // The supplier is now readable with the materialized status and the masked banking view.
        using var detail = await buyer.GetAsync($"/api/v1/suppliers/{supplierId}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("ACTIVE", detailBody.GetProperty("status").GetString());
        Assert.Equal("ABC Tech SAC", detailBody.GetProperty("legalName").GetString());

        // Any active user reads only the minimal reference projection.
        using var minimal = await requester.GetAsync("/api/v1/suppliers", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, minimal.StatusCode);
        var minimalBody = await minimal.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var reference = minimalBody.EnumerateArray().Single(entry => entry.GetProperty("id").GetGuid() == supplierId);
        Assert.Equal("ABC Tech SAC", reference.GetProperty("legalName").GetString());
        Assert.True(reference.TryGetProperty("categoriesSupplied", out _));
        Assert.False(reference.TryGetProperty("tax_id", out _));

        // History belongs to Procurement and AUDITOR; a requester is refused.
        using var history = await auditor.GetAsync($"/api/v1/suppliers/{supplierId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.True((await history.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetArrayLength() >= 2);
        using var deniedHistory = await requester.GetAsync($"/api/v1/suppliers/{supplierId}/history", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedHistory.StatusCode);

        // Readiness is healthy with the owner, catalog, adapters, processor and key configured.
        using var health = await environment.CreateClient().GetAsync("/health/supplier", cancellationToken);
        var healthBody = await health.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("SUPPLIER_OK", healthBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_banking_projection_stays_masked_and_the_reveal_is_role_bound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SupplierEnvironment.StartAsync(cancellationToken);
        using var buyer = environment.Client("buyer");
        using var ap = environment.Client("ap");
        using var approver = environment.Client("approver");
        using var requester = environment.Client("requester");

        using var created = await buyer.PostAsJsonAsync(
            "/api/v1/suppliers", environment.ContentRequest("ABC Tech SAC", "20123456789"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var supplierId = createdBody.GetProperty("supplierId").GetGuid();
        await environment.ApprovePendingAsync(
            supplierId, createdBody.GetProperty("version").GetInt32(), cancellationToken);

        // The account is stored encrypted; only the masked projection is published (REQ-05).
        using var saved = await buyer.PostAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}/banking",
            new
            {
                bankingDetailId = (Guid?)null,
                accountHolder = "ABC Tech SAC",
                bankName = "Banco de Prueba",
                bankCountryCode = "PE",
                currency = "PEN",
                accountType = "Checking",
                accountNumber = "00123456789012345678",
                iban = (string?)null,
                swiftBic = "BANKPEPL",
                isDefault = true,
                reason = "E2E banking",
                changeKey = "e2e-banking-1"
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var bankingRef = await saved.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var bankingDetailId = bankingRef.GetProperty("id").GetGuid();
        var bankingVersion = bankingRef.GetProperty("version").GetInt32();

        // Making the account operational is a governed change that needs its own approval.
        using var current = await buyer.GetAsync($"/api/v1/suppliers/{supplierId}", cancellationToken);
        var currentBody = await current.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        using var revised = await buyer.PutAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}",
            environment.ContentRequest(
                "ABC Tech SAC", "20123456789",
                bankingRefs: new object[] { new { id = bankingDetailId, version = bankingVersion } },
                expectedVersion: currentBody.GetProperty("version").GetInt32(),
                changeKey: "e2e-banking-2"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var revisedBody = await revised.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.True(revisedBody.GetProperty("requiresApproval").GetBoolean());
        Assert.Contains(
            "banking_details",
            revisedBody.GetProperty("sensitiveFields").EnumerateArray().Select(field => field.GetString()));
        await environment.ApprovePendingAsync(
            supplierId, revisedBody.GetProperty("version").GetInt32(), cancellationToken);

        using var detail = await buyer.GetAsync($"/api/v1/suppliers/{supplierId}", cancellationToken);
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var banking = detailBody.GetProperty("banking").EnumerateArray().Single();
        Assert.Equal("5678", banking.GetProperty("maskedAccount").GetString());
        Assert.Equal("Banco de Prueba", banking.GetProperty("bankName").GetString());
        Assert.DoesNotContain("00123456789012345678", detailBody.GetRawText(), StringComparison.Ordinal);

        // The ordinary read never returns the account number, not even for AP.
        using var apList = await ap.GetAsync($"/api/v1/suppliers/{supplierId}/banking", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, apList.StatusCode);
        var apListBody = await apList.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.DoesNotContain("00123456789012345678", apListBody.GetRawText(), StringComparison.Ordinal);

        // A requester cannot even read the masked projection.
        using var deniedList = await requester.GetAsync(
            $"/api/v1/suppliers/{supplierId}/banking", cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);

        // The reveal is explicit, audited and only for AP or the assigned approver.
        using var reveal = await ap.PostAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}/banking/{bankingDetailId}/versions/{bankingVersion}/reveal",
            new { purpose = "PAYMENT_EXECUTION" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        var revealed = await reveal.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("00123456789012345678", revealed.GetProperty("accountNumber").GetString());

        using var deniedReveal = await buyer.PostAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}/banking/{bankingDetailId}/versions/{bankingVersion}/reveal",
            new { purpose = "PAYMENT_EXECUTION" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedReveal.StatusCode);

        using var deniedApprover = await approver.PostAsJsonAsync(
            $"/api/v1/suppliers/{supplierId}/banking/{bankingDetailId}/versions/{bankingVersion}/reveal",
            new { purpose = "APPROVAL_VERIFICATION" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApprover.StatusCode);

        // Every reveal left an audit record with the actor, never the value.
        await using var context = SupplierEnvironment.CreateContext(environment.ConnectionString);
        var audits = await context.SupplierAuditRecords
            .AsNoTracking()
            .Where(record => record.Action == SupplierCodes.ActionBankingRevealed)
            .ToArrayAsync(cancellationToken);
        Assert.Single(audits);
        Assert.DoesNotContain("00123456789012345678", audits[0].TargetJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_degrades_when_the_agreement_storage_is_unreachable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await SupplierEnvironment.StartAsync(
            cancellationToken, storageAvailable: false);

        using var health = await environment.CreateClient().GetAsync("/health/supplier", cancellationToken);
        var body = await health.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("SUPPLIER_ATTACHMENT_STORAGE_UNAVAILABLE", body.GetProperty("code").GetString());
        Assert.Equal("DEGRADED", body.GetProperty("status").GetString());
    }

    private static StringContent JsonContent(object value) => new(
        JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private sealed class SupplierEnvironment : IAsyncDisposable
    {
        private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";

        private MsSqlContainer container = null!;
        private TestApiFactory factory = null!;
        private readonly Dictionary<string, Guid> users = new(StringComparer.Ordinal);

        public string ConnectionString { get; private set; } = string.Empty;

        public Guid OrganizationId { get; private set; }

        public Guid DepartmentId { get; private set; }

        public string SpendCategoryDigest { get; private set; } = string.Empty;

        public static Task<SupplierEnvironment> StartAsync(CancellationToken cancellationToken) =>
            StartAsync(cancellationToken, storageAvailable: true);

        public static async Task<SupplierEnvironment> StartAsync(
            CancellationToken cancellationToken,
            bool storageAvailable)
        {
            var environment = new SupplierEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            var connectionString = environment.container.GetConnectionString();
            environment.ConnectionString = connectionString;
            _ = connectionString;
            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync(cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            await new OrganizationBootstrapper(context, loggerFactory.CreateLogger<OrganizationBootstrapper>())
                .InitializeAsync(
                    new OrganizationBootstrapOptions(
                        "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE", "Acme Peru S.A.C.",
                        "IT", "Software / IT", Issuer, "admin-bootstrap", "API E2E bootstrap"),
                    cancellationToken);
            var organization = await context.Organizations.SingleAsync(cancellationToken);
            environment.OrganizationId = organization.Id;
            var department = await context.Departments.SingleAsync(cancellationToken);
            environment.DepartmentId = department.Id;
            environment.Seed(context, organization.Id, department.Id);
            await context.SaveChangesAsync(cancellationToken);
            var catalogs = new ReferenceCatalogPersistenceService(context);
            var category = await catalogs.CreateSpendCategoryAsync(
                organization.Id, environment.users["buyer"], "HARDWARE", "Hardware", "Seed", "corr-seed",
                cancellationToken);
            environment.SpendCategoryDigest = category.Digest;
            environment.factory = new TestApiFactory(connectionString, storageAvailable);
            return environment;
        }

        public HttpClient Client(string user)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
            return client;
        }

        public HttpClient CreateClient() => factory.CreateClient();

        public object ContentRequest(
            string legalName,
            string taxId,
            IReadOnlyList<object>? bankingRefs = null,
            int? expectedVersion = null,
            string? changeKey = null) => new
        {
            legalName,
            tradeName = (string?)null,
            countryCode = "PE",
            taxId,
            addresses = new[]
            {
                new
                {
                    id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    label = "HQ",
                    line1 = "Av. Siempre Viva 742",
                    line2 = (string?)null,
                    city = "Lima",
                    region = (string?)null,
                    postalCode = (string?)null,
                    countryCode = "PE"
                }
            },
            contacts = new[]
            {
                new
                {
                    id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    name = "Ana Perez",
                    email = "ana@abc.test",
                    phone = (string?)null,
                    jobTitle = (string?)null
                }
            },
            paymentTerms = new { code = "NET30", netDays = 30 },
            supportedCurrencies = new[] { "PEN" },
            categoriesSupplied = new[]
            {
                new { code = "HARDWARE", version = 1, digest = SpendCategoryDigest }
            },
            riskStatus = "LOW",
            performanceScore = (decimal?)null,
            performanceSource = (string?)null,
            performanceMeasuredAt = (DateTimeOffset?)null,
            bankingRefs = bankingRefs ?? Array.Empty<object>(),
            requestedStatus = 3,
            expectedVersion,
            reason = "E2E supplier",
            changeKey = changeKey ?? $"e2e-{Guid.NewGuid():N}"
        };

        /// <summary>Creates and approves an activation through the governed service, then the API.</summary>
        public async Task ApprovePendingAsync(Guid supplierId, int candidateVersion, CancellationToken cancellationToken)
        {
            await using var context = CreateContext(ConnectionString);
            var governance = SupplierServices(context, Configuration());
            await governance.SubmitAsync(
                OrganizationId, users["buyer"], supplierId, candidateVersion,
                "E2E activation", $"e2e-{Guid.NewGuid():N}", "corr-e2e", cancellationToken);
            var approval = new SupplierApprovalHarness(context, Configuration());
            await approval.ApproveAndApplyAsync(cancellationToken);
        }

        public Task<Guid> PendingTaskIdAsync(CancellationToken cancellationToken) =>
            PendingTaskIdAsyncCore(cancellationToken);

        public async Task ApprovePendingTaskAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext(ConnectionString);
            await new SupplierApprovalHarness(context, Configuration()).ApproveAndApplyAsync(cancellationToken);
        }

        private async Task<Guid> PendingTaskIdAsyncCore(CancellationToken cancellationToken)
        {
            await using var context = CreateContext(ConnectionString);
            return await context.ApprovalTasks
                .AsNoTracking()
                .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
                .OrderBy(record => record.Id)
                .Select(record => record.Id)
                .FirstAsync(cancellationToken);
        }

        internal static ProcureToPayDbContext CreateContext(string connectionString) => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(connectionString).Options);

        internal static SupplierGovernanceService SupplierServices(
            ProcureToPayDbContext context,
            IConfiguration configuration)
        {
            var persistence = new SupplierPersistenceService(
                context, Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierPersistenceService>.Instance);
            var banking = new SupplierBankingService(
                context, persistence,
                new ConfigurationSupplierBankingKeyProvider(configuration),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierBankingService>.Instance);
            return new SupplierGovernanceService(
                context, persistence, ApprovalSubmissionServices(context, configuration), banking, configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierGovernanceService>.Instance);
        }

        internal static ApprovalSubmissionService ApprovalSubmissionServices(
            ProcureToPayDbContext context,
            IConfiguration configuration)
        {
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var adapters = new ApprovalSubmissionAdapterRegistry(
            [
                new SupplierGovernanceApprovalAdapter(
                    context, configuration, SupplierCodes.ApprovalSubjectType,
                    SupplierCodes.SupplierApprovalOperation, SupplierCodes.ApprovalTargetType),
                new SupplierGovernanceApprovalAdapter(
                    context, configuration, SupplierCodes.CatalogApprovalSubjectType,
                    SupplierCodes.CatalogApprovalOperation, SupplierCodes.CatalogApprovalTargetType)
            ]);
            return new ApprovalSubmissionService(
                context, adapters, new ApprovalOwnerWorkloadRegistry(configuration, allowlist), allowlist,
                new ApprovalAssignmentEngine(
                    context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalSubmissionService>.Instance);
        }

        private static IConfiguration Configuration() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supplier:Banking:KeyBase64"] = TestApiFactory.BankingKeyBase64,
                ["Supplier:Banking:KeyVersion"] = "e2e-v1",
                ["Approval:Workloads:0:Issuer"] = "internal://procure-to-pay",
                ["Approval:Workloads:0:ClientId"] = SupplierCodes.SupplierOwnerId,
                ["Approval:OwnerWorkloads:0:AdapterId"] = SupplierCodes.ActiveSupplierOwnerAdapterId,
                ["Approval:OwnerWorkloads:0:AdapterVersion"] = SupplierCodes.ActiveSupplierOwnerAdapterVersion,
                ["Approval:OwnerWorkloads:0:Issuer"] = "internal://procure-to-pay",
                ["Approval:OwnerWorkloads:0:ClientId"] = SupplierCodes.ActiveSupplierProcessorId
            })
            .Build();

        private void Seed(ProcureToPayDbContext context, Guid organizationId, Guid departmentId)
        {
            foreach (var user in new[] { "buyer", "approver", "ap", "auditor", "requester" })
            {
                users[user] = Guid.NewGuid();
            }

            foreach (var (subject, id) in users)
            {
                context.UserProfiles.Add(new UserProfileRecord
                {
                    Id = id,
                    OrganizationId = organizationId,
                    Issuer = Issuer,
                    Subject = subject,
                    Email = $"{subject}@acme.test",
                    DisplayName = subject,
                    DepartmentId = departmentId,
                    JobTitle = "Employee",
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                });
            }

            context.RoleAssignments.AddRange(
                Assignment(organizationId, users["buyer"], SystemRole.ProcurementBuyer),
                Assignment(organizationId, users["approver"], SystemRole.ProcurementApprover),
                Assignment(organizationId, users["ap"], SystemRole.ApSpecialist),
                Assignment(organizationId, users["auditor"], SystemRole.Auditor),
                Assignment(organizationId, users["requester"], SystemRole.Requester));
            var level = new AuthorityLevelRecord
            {
                Id = Guid.NewGuid(),
                Type = (int)ApprovalAuthorityType.SupplierMaster,
                Code = "SUPPLIER_L1",
                Rank = 1,
                LevelVersion = 1,
                IsActive = true
            };
            context.AuthorityLevels.Add(level);
            context.AuthorityGrants.Add(new AuthorityGrantRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = users["approver"],
                AuthorityLevelId = level.Id,
                MaxAmountBase = null,
                BaseCurrency = "PEN",
                ScopeJson = GlobalScopeJson,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = null,
                Status = (int)GrantStatus.Active,
                GrantedAt = DateTimeOffset.UtcNow.AddDays(-1),
                GrantedBy = users["buyer"],
                Version = 1
            });
        }

        private static RoleAssignmentRecord Assignment(Guid organizationId, Guid userId, SystemRole role) => new()
        {
            Id = Guid.NewGuid(),
            UserProfileId = userId,
            Role = (int)role,
            ScopeJson = GlobalScopeJson,
            AssignedAt = DateTimeOffset.UtcNow.AddDays(-1),
            AssignedBy = userId,
            Status = (int)AssignmentStatus.Active,
            Version = 1
        };

        private static string Token(string subject)
        {
            var handler = new JwtSecurityTokenHandler();
            var token = new JwtSecurityToken(
                Issuer,
                "procure-to-pay-tests",
                [new Claim(JwtRegisteredClaimNames.Sub, subject), new Claim("email", $"{subject}@acme.test")],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddMinutes(30),
                signingCredentials: new SigningCredentials(TestApiFactory.SigningKey, SecurityAlgorithms.HmacSha256));
            return handler.WriteToken(token);
        }

        public async ValueTask DisposeAsync()
        {
            factory?.Dispose();
            await container.DisposeAsync();
        }
    }

    /// <summary>Decides the live supplier task through the real decision service and dispatches it.</summary>
    private sealed class SupplierApprovalHarness(ProcureToPayDbContext context, IConfiguration configuration)
    {
        public async Task ApproveAndApplyAsync(CancellationToken cancellationToken)
        {
            var task = await context.ApprovalTasks
                .AsNoTracking()
                .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
                .OrderBy(record => record.Id)
                .FirstAsync(cancellationToken);
            var assignment = await context.ApprovalAssignments
                .AsNoTracking()
                .SingleAsync(record => record.TaskId == task.Id && record.ReleasedAt == null, cancellationToken);
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var engine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
            await new ApprovalDecisionService(
                    context, engine,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalDecisionService>.Instance)
                .DecideAsync(
                    new ApprovalDecisionCommand(
                        task.Id, ApprovalDecisionAction.Approve, "E2E approval",
                        $"e2e-{Guid.NewGuid():N}", task.Version, assignment.AssigneeUserId, "corr-e2e"),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            var persistence = new SupplierPersistenceService(
                context, Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierPersistenceService>.Instance);
            var banking = new SupplierBankingService(
                context, persistence,
                new ConfigurationSupplierBankingKeyProvider(configuration),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierBankingService>.Instance);
            var governance = new SupplierGovernanceService(
                context, persistence,
                SupplierEnvironment.ApprovalSubmissionServices(context, configuration), banking, configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierGovernanceService>.Instance);
            var storageProvider = new ServiceCollection()
                .AddSingleton<IFileStorage, InMemoryAgreementStorage>()
                .BuildServiceProvider();
            var catalog = new ApprovedSupplierCatalogGovernanceService(
                context, persistence,
                SupplierEnvironment.ApprovalSubmissionServices(context, configuration),
                new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestReferenceOwnerRegistry([]),
                storageProvider, configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovedSupplierCatalogGovernanceService>.Instance);
            var dispatcher = new ApprovalOutboxDispatcher(
                context,
                new ApprovalResultConsumerRegistry(
                [
                    new ApprovalResultRouter(
                        new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestApprovalResultConsumer(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                                context,
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                                    context,
                                    new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                                ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestApprovalResultConsumer>.Instance,
                            ApprovalOutboxPolicy.ContractVersion),
                        new SupplierApprovalResultConsumer(
                            context, governance, catalog,
                            Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierApprovalResultConsumer>.Instance,
                            ApprovalOutboxPolicy.ContractVersion),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalResultRouter>.Instance,
                        ApprovalOutboxPolicy.ContractVersion)
                ]),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalOutboxDispatcher>.Instance);
            var outcome = await dispatcher.DispatchAsync(
                await context.ApprovalCases
                    .AsNoTracking()
                    .Where(record => record.Id == task.CaseId)
                    .Select(record => record.OrganizationId)
                    .SingleAsync(cancellationToken),
                "e2e-supplier-dispatcher",
                DateTimeOffset.UtcNow.AddSeconds(1),
                50,
                cancellationToken);
            if (outcome.Failed > 0 || outcome.DeadLettered > 0)
            {
                throw new InvalidOperationException("The supplier approval result was not delivered.");
            }
        }
    }

    private sealed class InMemoryAgreementStorage(bool available = true) : IFileStorage
    {
        public Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Uri GenerateTemporaryDownloadUrl(string objectKey) => new($"https://agreements.test/{objectKey}");

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(available);
    }

    private sealed class TestApiFactory(string connectionString, bool storageAvailable = true)
        : WebApplicationFactory<Program>
    {
        internal static readonly SymmetricSecurityKey SigningKey =
            new(Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

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
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    // SPEC 09 REQ-12: the owner workload and the processor identity must match.
                    ["Approval:Workloads:0:Issuer"] = "internal://procure-to-pay",
                    ["Approval:Workloads:0:ClientId"] = SupplierCodes.SupplierOwnerId,
                    ["Approval:OwnerWorkloads:0:AdapterId"] = SupplierCodes.ActiveSupplierOwnerAdapterId,
                    ["Approval:OwnerWorkloads:0:AdapterVersion"] = SupplierCodes.ActiveSupplierOwnerAdapterVersion,
                    ["Approval:OwnerWorkloads:0:Issuer"] = "internal://procure-to-pay",
                    ["Approval:OwnerWorkloads:0:ClientId"] = SupplierCodes.ActiveSupplierProcessorId,
                    ["Policy:Workloads:0:Issuer"] = "internal://procure-to-pay",
                    ["Policy:Workloads:0:ClientId"] = "purchase-request-domain",
                    ["Supplier:Banking:KeyBase64"] = BankingKeyBase64,
                    ["Supplier:Banking:KeyVersion"] = "e2e-v1"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(new InMemoryAgreementStorage(storageAvailable));
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

        internal const string BankingKeyBase64 = "F0oXa2lQ0Z4c7pM6sT9uVwYx1B3dE5gH7jK9mN1pQ3s=";
    }
}
