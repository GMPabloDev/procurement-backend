using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.Infrastructure.Persistence.Suppliers;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Sourcing;

/// <summary>
/// SQL Server harness of the Sourcing module (SPEC 10): real migrations, real persistence services
/// and the real object-storage contract replaced by an in-memory implementation, so the suite proves
/// the contractual behavior of REQ-01..REQ-04 against SQL Server instead of a product double.
/// </summary>
public sealed class SourcingHarness : IAsyncDisposable
{
    private MsSqlContainer container = null!;

    public Guid OrganizationId { get; } = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    public Guid LegalEntityId { get; } = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    public Guid BuyerId { get; } = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    public Guid AuditorId { get; } = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    public Guid RequesterId { get; } = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    public Guid RequestId { get; } = Guid.Parse("ffffffff-6666-6666-6666-666666666666");

    public Guid LineId { get; } = Guid.Parse("11111111-7777-7777-7777-777777777777");

    public Guid OtherLineId { get; } = Guid.Parse("22222222-8888-8888-8888-888888888888");

    public Guid SupplierId { get; } = Guid.Parse("33333333-9999-9999-9999-999999999999");

    public Guid SecondSupplierId { get; } = Guid.Parse("44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public string ConnectionString { get; private set; } = string.Empty;

    public InMemorySourcingStorage Storage { get; } = new();

    public static async Task<SourcingHarness> StartAsync(CancellationToken cancellationToken)
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await container.StartAsync(cancellationToken);
        var harness = new SourcingHarness
        {
            container = container,
            ConnectionString = container.GetConnectionString()
        };
        await harness.SeedAsync(cancellationToken);
        return harness;
    }

    public ProcureToPayDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    public IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:S3:TemporaryUrlLifetimeMinutes"] = "15",
            ["Sourcing:Waiver:ValidityDays"] = "7",
            // The sourcing domain is the only workload allowed to open a quotation waiver (REQ-05).
            ["Approval:Workloads:0:Issuer"] = SourcingWaiverService.Workload.Issuer,
            ["Approval:Workloads:0:ClientId"] = SourcingWaiverService.Workload.ClientId
        })
        .Build();

    public SourcingProcessService CreateProcessService(ProcureToPayDbContext context) => new(context);

    public SourcingQuotationService CreateQuotationService(ProcureToPayDbContext context) =>
        new(context, new ServiceCollection().AddSingleton<IFileStorage>(Storage).BuildServiceProvider(),
            Configuration);

    public SourcingEvaluationService CreateEvaluationService(ProcureToPayDbContext context) => new(context);

    public SourcingSelectionService CreateSelectionService(ProcureToPayDbContext context) => new(context);

    public SourcingProposalService CreateProposalService(ProcureToPayDbContext context) => new(context);

    /// <summary>
    /// Seeds the persisted facts the proposal builder consumes from other modules: the attested
    /// completeness manifest of the request version and the newest Policy evaluation bundle it binds.
    /// </summary>
    public async Task SeedProposalFixturesAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var policyVersionId = Guid.NewGuid();
        context.PolicySetVersions.Add(new ProcureToPay.Infrastructure.Persistence.Policy.PolicySetVersionRecord
        {
            Id = policyVersionId,
            OrganizationId = OrganizationId,
            Sequence = 1,
            ScopesJson = "[\"LINE\"]",
            ContentJson = "{}",
            ContentDigest = Digest('1')
        });
        context.PurchaseRequestCompletenessManifests.Add(
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestCompletenessManifestRecord
            {
                RequestId = RequestId,
                RequestVersion = 1,
                OrganizationId = OrganizationId,
                RequestContentDigest = Digest('2'),
                ReferenceAttestationDigest = Digest('3'),
                PolicyManifestDigest = Digest('4'),
                DomainAttestationDigest = Digest('5'),
                LinesJson = "[]",
                ContractVersion = "purchase-request-completeness-manifest/v2"
            });
        context.PolicyEvaluationBundles.Add(
            new ProcureToPay.Infrastructure.Persistence.Policy.PolicyEvaluationBundleRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = OrganizationId,
                EvaluationKey = "sourcing-proposal-seed",
                WorkloadIssuer = "internal://procure-to-pay",
                WorkloadClientId = "purchase-request-domain",
                Operation = "REQUEST_EVALUATE",
                EvaluationSequence = 1,
                SubjectId = RequestId,
                SubjectVersion = 1,
                PolicySetVersionId = policyVersionId,
                EvaluatedAt = now,
                PolicyContentDigest = Digest('6'),
                InputDigest = Digest('7'),
                Result = "ALLOW",
                ResultDigest = Digest('8'),
                BundleJson = "{}",
                IdempotencyFingerprint = Digest('9'),
                CorrelationReference = "seed-proposal"
            });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Real SPEC 05 submission service over the in-memory configuration: the waiver suite proves the
    /// recalculation and the fail-closed behaviour, not a fake submitter.
    /// </summary>
    public SourcingWaiverService CreateWaiverService(ProcureToPayDbContext context) =>
        new(context, WaiverSubmissionService(context), Configuration);

    private PolicyExceptionSubmissionService WaiverSubmissionService(ProcureToPayDbContext context)
    {
        var configuration = Configuration;
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
        var assignmentEngine = new ApprovalAssignmentEngine(
            context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
        var submissions = new ApprovalSubmissionService(
            context, new ApprovalSubmissionAdapterRegistry([]), ownerWorkloads, allowlist, assignmentEngine,
            NullLogger<ApprovalSubmissionService>.Instance);
        return new PolicyExceptionSubmissionService(
            context, submissions, allowlist, NullLogger<PolicyExceptionSubmissionService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
    }

    /// <summary>
    /// Seeds the exact persisted facts the contract consumes: one current Purchase Request version
    /// with two lines, the current approval case of that version with its satisfied predecessor and
    /// the two PROCUREMENT-stage prerequisites, and two active suppliers.
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        context.Organizations.Add(new OrganizationRecord
        {
            Id = OrganizationId,
            Code = "SOURCING-TEST",
            Name = "Sourcing Test",
            BaseCurrency = "PEN",
            TimeZoneId = "America/Lima",
            FiscalYearStartMonth = 1,
            Version = 1
        });
        context.LegalEntities.Add(new LegalEntityRecord
        {
            Id = LegalEntityId,
            OrganizationId = OrganizationId,
            Code = "LE-1",
            Name = "Legal Entity",
            Status = (int)EntityStatus.Active,
            Version = 1
        });
        foreach (var (id, email) in new[]
                 {
                     (BuyerId, "buyer@test"), (AuditorId, "auditor@test"), (RequesterId, "requester@test")
                 })
        {
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = id,
                OrganizationId = OrganizationId,
                Issuer = "https://issuer.test",
                Subject = id.ToString("D"),
                Email = email,
                DisplayName = email,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
        }

        context.RoleAssignments.AddRange(
            new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = BuyerId,
                Role = (int)SystemRole.ProcurementBuyer,
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                AssignedAt = now,
                AssignedBy = BuyerId,
                Status = (int)AssignmentStatus.Active,
                Version = 1
            },
            new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = AuditorId,
                Role = (int)SystemRole.Auditor,
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                AssignedAt = now,
                AssignedBy = BuyerId,
                Status = (int)AssignmentStatus.Active,
                Version = 1
            });
        context.PurchaseRequests.Add(new PurchaseRequestRecord
        {
            Id = RequestId,
            OrganizationId = OrganizationId,
            RequesterId = RequesterId,
            LegalEntityId = LegalEntityId,
            LegalEntityVersion = 1,
            CurrentVersion = 1,
            Status = (int)PurchaseRequestStatus.InApproval,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.PurchaseRequestVersions.Add(new PurchaseRequestVersionRecord
        {
            RequestId = RequestId,
            Version = 1,
            OrganizationId = OrganizationId,
            RequesterId = RequesterId,
            LegalEntityId = LegalEntityId,
            LegalEntityVersion = 1,
            BusinessJustification = "Sourcing integration seed",
            ContentDigest = Digest('1'),
            ActorUserId = RequesterId,
            CreatedAt = now
        });
        foreach (var (lineId, digest) in new[] { (LineId, Digest('2')), (OtherLineId, Digest('3')) })
        {
            context.PurchaseRequestLineVersions.Add(new PurchaseRequestLineVersionRecord
            {
                LineId = lineId,
                LineVersion = 1,
                RequestId = RequestId,
                OrganizationId = OrganizationId,
                EstimatedGrossAmount = 1_000m,
                TransactionCurrency = "PEN",
                BaseAmount = 1_000m,
                BaseCurrency = "PEN",
                FiscalYear = now.Year,
                PurchaseType = "GOODS",
                SpendCategoryJson = "{\"catalog\":\"SPEND_CATEGORY\",\"code\":\"HARDWARE\",\"digest\":\"" +
                                    Digest('4') + "\",\"version\":1}",
                CostCenterJson = "{\"catalog\":\"COST_CENTER\",\"code\":\"CC-1\",\"digest\":\"" + Digest('5') +
                                 "\",\"version\":1}",
                CostCenterDepartmentJson = "{\"entity_type\":\"DEPARTMENT\",\"id\":\"" +
                                           Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb") +
                                           "\",\"version\":1}",
                BeneficiaryDepartmentJson = "{\"entity_type\":\"DEPARTMENT\",\"id\":\"" +
                                            Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb") +
                                            "\",\"version\":1}",
                RequestedForUserJson = "{\"entity_type\":\"USER\",\"id\":\"" + RequesterId + "\",\"version\":1}",
                AgreementStatus = "NONE",
                NeedSummary = "Sourcing integration seed",
                RiskAnswersJson = "[]",
                ContentDigest = digest,
                ActorUserId = RequesterId,
                CreatedAt = now
            });
            context.PurchaseRequestVersionLines.Add(new PurchaseRequestVersionLineRecord
            {
                RequestId = RequestId,
                RequestVersion = 1,
                LineId = lineId,
                LineVersion = 1,
                ContentDigest = digest
            });
        }

        var caseId = Guid.NewGuid();
        context.ApprovalCases.Add(new ApprovalCaseRecord
        {
            Id = caseId,
            OrganizationId = OrganizationId,
            SubjectType = "PURCHASE_REQUEST",
            SubjectId = RequestId,
            SubjectVersion = 1,
            Operation = "SUBMIT_PURCHASE_REQUEST",
            SourceSnapshotDigest = Digest('6'),
            WorkloadIssuer = "internal://procure-to-pay",
            WorkloadClientId = "purchase-request-domain",
            SubmissionKey = "seed-submission",
            SubmissionFingerprint = Digest('7'),
            OriginatorId = RequesterId,
            RequesterId = RequesterId,
            Status = (int)ApprovalCaseStatus.Open,
            Version = 1,
            CreatedAt = now,
            CorrelationReference = "seed-case"
        });
        context.ApprovalPrerequisites.AddRange(
            Prerequisite(caseId, "BUDGET", "budget-check-owner", PrerequisiteStatus.Satisfied, now),
            Prerequisite(caseId, "QUOTES", "quotation-status-owner", PrerequisiteStatus.Waiting, now),
            Prerequisite(caseId, "PROC", "procurement-stage-owner", PrerequisiteStatus.Waiting, now));
        foreach (var (supplierId, digest) in new[] { (SupplierId, Digest('8')), (SecondSupplierId, Digest('e')) })
        {
            var identityId = Guid.NewGuid();
            context.SupplierFiscalIdentities.Add(new SupplierFiscalIdentityRecord
            {
                Id = identityId,
                OrganizationId = OrganizationId,
                SupplierId = supplierId,
                CountryCode = "PE",
                TaxIdKey = $"RUC{supplierId:N}",
                TaxId = $"RUC{supplierId:N}",
                IdentityKeyDigest = digest
            });
            context.Suppliers.Add(new SupplierRecord
            {
                Id = supplierId,
                OrganizationId = OrganizationId,
                FiscalIdentityId = identityId,
                OperationalVersion = 1
            });
            context.SupplierVersions.Add(new SupplierVersionRecord
            {
                SupplierId = supplierId,
                Version = 1,
                OrganizationId = OrganizationId,
                LegalName = $"Supplier {supplierId:N}",
                CountryCode = "PE",
                TaxId = $"RUC{supplierId:N}",
                AddressesJson = "[]",
                ContactsJson = "[]",
                PaymentTermsJson = "{\"code\":\"NET30\",\"net_days\":30}",
                SupportedCurrenciesJson = "[\"PEN\"]",
                CategoriesSuppliedJson = "[]",
                PerformanceJson = "null",
                BankingRefsJson = "[]",
                Status = (int)SupplierOperationalStatus.Active,
                RiskStatus = (int)SupplierRiskStatus.Low,
                ContentDigest = Digest('9'),
                ActorUserId = BuyerId,
                OccurredAt = now,
                Reason = "Seed"
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private ApprovalPrerequisiteRecord Prerequisite(
        Guid caseId,
        string key,
        string owner,
        PrerequisiteStatus status,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CaseId = caseId,
        OrganizationId = OrganizationId,
        Key = key,
        OwnerAdapterId = owner,
        OwnerAdapterVersion = "v1",
        OwnerWorkloadIssuer = "internal://procure-to-pay",
        OwnerWorkloadClientId = $"{owner}-worker",
        SourceControlType = key,
        SourceControlDigest = Digest('a'),
        ParametersJson = "{\"minimum_allowed_quotations\":null,\"minimum_quotations\":2}",
        TargetsJson = "[{\"id\":\"" + LineId + "\",\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":1," +
                      "\"material_snapshot_digest\":\"" + Digest('b') + "\"}]",
        Status = (int)status,
        Version = 1,
        ResolvedAt = status == PrerequisiteStatus.Satisfied ? now : null
    };

    private static string Digest(char value) => new(value, 64);
}

/// <summary>In-memory agreement storage of the sourcing suites (REQ-03).</summary>
public sealed class InMemorySourcingStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public bool Available { get; set; } = true;

    public async Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        Objects[request.ObjectKey] = buffer.ToArray();
    }

    public Uri GenerateTemporaryDownloadUrl(string objectKey) =>
        new($"https://quotes.test/{Uri.EscapeDataString(objectKey)}");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Available);
}
