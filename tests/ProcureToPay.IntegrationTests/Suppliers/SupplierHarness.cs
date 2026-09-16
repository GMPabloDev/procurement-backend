using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence.Suppliers;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>In-memory agreement storage of the integration suites (REQ-07).</summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public async Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await request.Content.CopyToAsync(buffer, cancellationToken);
        Objects[request.ObjectKey] = buffer.ToArray();
    }

    public Uri GenerateTemporaryDownloadUrl(string objectKey) =>
        new($"https://agreements.test/{Uri.EscapeDataString(objectKey)}");
}

/// <summary>
/// SQL Server harness of the Supplier module (SPEC 09): real migrations, real services and the real
/// Approval Workflow, with only the external key provider and the object storage replaced so the
/// suite exercises the contractual behavior instead of a product double.
/// </summary>
public sealed class SupplierHarness : IAsyncDisposable
{
    public const string BankingKeyBase64 = "F0oXa2lQ0Z4c7pM6sT9uVwYx1B3dE5gH7jK9mN1pQ3s=";

    /// <summary>Trusted in-process workload the Purchase Request flow uses to submit (SPEC 06).</summary>
    public static readonly ApprovalWorkloadIdentity SubmissionWorkload = PurchaseRequestSubmissionService.Workload;

    private MsSqlContainer container = null!;

    public Guid OrganizationId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public Guid DepartmentId { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public Guid BuyerId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public Guid ApproverId { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public Guid ApSpecialistId { get; } = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public Guid AuditorId { get; } = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public Guid RequesterId { get; } = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public Guid RequestedForId { get; } = Guid.Parse("88888888-8888-8888-8888-888888888888");

    public Guid LegalEntityId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");

    public Guid CostCenterId { get; private set; }

    public int CostCenterVersion { get; private set; }

    public int SpendCategoryVersion { get; private set; }

    public string SpendCategoryCode { get; } = "HARDWARE";

    public string SpendCategoryDigest { get; private set; } = string.Empty;

    public InMemoryFileStorage Storage { get; } = new();

    public string ConnectionString { get; private set; } = string.Empty;

    public static async Task<SupplierHarness> StartAsync(CancellationToken cancellationToken)
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("ProcureToPay_test_2026!")
            .Build();
        await container.StartAsync(cancellationToken);
        var harness = new SupplierHarness
        {
            container = container,
            ConnectionString = container.GetConnectionString()
        };
        await harness.SeedAsync(cancellationToken);
        return harness;
    }

    public ProcureToPayDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(ConnectionString);
        if (Environment.GetEnvironmentVariable("SUPPLIER_SQL_TRACE") == "1")
        {
            builder.LogTo(Console.Error.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);
        }

        return new ProcureToPayDbContext(builder.Options);
    }

    public IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supplier:Banking:KeyBase64"] = BankingKeyBase64,
            ["Supplier:Banking:KeyVersion"] = "test-v1",
            ["Approval:Workloads:0:Issuer"] = SupplierGovernanceService.Workload.Issuer,
            ["Approval:Workloads:0:ClientId"] = SupplierGovernanceService.Workload.ClientId,
            ["Approval:Workloads:1:Issuer"] = ApprovedSupplierCatalogGovernanceService.Workload.Issuer,
            ["Approval:Workloads:1:ClientId"] = ApprovedSupplierCatalogGovernanceService.Workload.ClientId,
            ["Approval:OwnerWorkloads:0:AdapterId"] = SupplierCodes.ActiveSupplierOwnerAdapterId,
            ["Approval:OwnerWorkloads:0:AdapterVersion"] = SupplierCodes.ActiveSupplierOwnerAdapterVersion,
            ["Approval:OwnerWorkloads:0:Issuer"] = SupplierGovernanceService.Workload.Issuer,
            ["Approval:OwnerWorkloads:0:ClientId"] = SupplierCodes.ActiveSupplierProcessorId,
            ["Policy:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
            ["Policy:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId,
            ["Approval:Workloads:2:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
            ["Approval:Workloads:2:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId
        })
        .Build();

    public Services CreateServices(ProcureToPayDbContext context)
    {
        var configuration = Configuration;
        var persistence = new SupplierPersistenceService(
            context, NullLogger<SupplierPersistenceService>.Instance);
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
        var assignmentEngine = new ApprovalAssignmentEngine(
            context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
        var adapters = new ApprovalSubmissionAdapterRegistry(
        [
            new SupplierGovernanceApprovalAdapter(
                context, configuration, SupplierCodes.ApprovalSubjectType,
                SupplierCodes.SupplierApprovalOperation, SupplierCodes.ApprovalTargetType),
            new SupplierGovernanceApprovalAdapter(
                context, configuration, SupplierCodes.CatalogApprovalSubjectType,
                SupplierCodes.CatalogApprovalOperation, SupplierCodes.CatalogApprovalTargetType),
            new PolicyApprovalAdapter(context),
            new PolicyApprovalAdapter(context, PolicyApprovalAdapter.ContractVersionV3),
            new PolicyApprovalAdapter(context, PolicyApprovalAdapter.ContractVersionV4)
        ]);
        var submissions = new ApprovalSubmissionService(
            context, adapters, ownerWorkloads, allowlist, assignmentEngine,
            NullLogger<ApprovalSubmissionService>.Instance);
        var keys = new ConfigurationSupplierBankingKeyProvider(configuration);
        var banking = new SupplierBankingService(
            context, persistence, keys, NullLogger<SupplierBankingService>.Instance);
        var governance = new SupplierGovernanceService(
            context, persistence, submissions, banking, configuration,
            NullLogger<SupplierGovernanceService>.Instance);
        var storageProvider = new ServiceCollection()
            .AddSingleton<IFileStorage>(Storage)
            .BuildServiceProvider();
        var catalog = new ApprovedSupplierCatalogGovernanceService(
            context, persistence, submissions,
            new PurchaseRequestReferenceOwnerRegistry(
            [
                new SpendCategoryReferenceOwner(context),
                new ProductReferenceOwner()
            ]),
            storageProvider, configuration, NullLogger<ApprovedSupplierCatalogGovernanceService>.Instance);
        var workflow = new ApprovalWorkflowService(
            context, allowlist, assignmentEngine, NullLogger<ApprovalWorkflowService>.Instance);
        var processor = new SupplierPrerequisiteProcessor(
            context, workflow, ownerWorkloads, new SupplierProcessorIdentity(),
            NullLogger<SupplierPrerequisiteProcessor>.Instance);
        var prFlow = CreatePurchaseRequestFlow(context, governance, catalog);
        return new Services(
            context,
            persistence,
            governance,
            catalog,
            banking,
            submissions,
            assignmentEngine,
            allowlist,
            processor,
            new ApprovedSupplierFactOwner(context, catalog),
            configuration,
            prFlow);
    }

    /// <summary>
    /// Real Purchase Request → Policy → Approval flow of the cross-module suite: the same provider,
    /// catalogs, adapters and owners production registers, with only the object storage replaced.
    /// </summary>
    private PurchaseRequestFlow CreatePurchaseRequestFlow(
        ProcureToPayDbContext context,
        SupplierGovernanceService governance,
        ApprovedSupplierCatalogGovernanceService catalog)
    {
        var configuration = Configuration;
        var persistence = new PurchaseRequestPersistenceService(
            context, NullLogger<PurchaseRequestPersistenceService>.Instance);
        IReadOnlyList<IPurchaseRequestReferenceOwner> owners =
        [
            new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.LegalEntity),
            new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.User),
            new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.Department),
            new CostCenterReferenceOwner(context, PurchaseRequestAssertionType.ActiveInOrganization),
            new CostCenterReferenceOwner(context, PurchaseRequestAssertionType.CostCenterOwnedByDepartment),
            new SpendCategoryReferenceOwner(context),
            new SupplierReferenceOwner(context)
        ];
        var attestation = new PurchaseRequestAttestationService(
            context,
            new PurchaseRequestReferenceOwnerRegistry(owners),
            new ApprovedSupplierFactOwner(context, catalog),
            persistence,
            NullLogger<PurchaseRequestAttestationService>.Instance);
        var provider = new PurchaseRequestPolicyFactProvider(
            context, persistence, NullLogger<PurchaseRequestPolicyFactProvider>.Instance);
        var policyEvaluations = new PolicyEvaluationService(
            new PolicyPersistenceService(context),
            new PolicyWorkloadAllowlist(configuration),
            new PolicyFactProviderRegistry([provider]),
            new PolicyExceptionVerifierRegistry([]),
            new PolicyReferenceCatalogRegistry(
            [
                new DatabasePolicyReferenceCatalog(context, "DEPARTMENT"),
                new DatabasePolicyReferenceCatalog(context, "LEGAL_ENTITY"),
                new CostCenterPolicyReferenceCatalog(context),
                new SpendCategoryPolicyReferenceCatalog(context),
                new SupplierPolicyReferenceCatalog(context)
            ]),
            NullLogger<PolicyEvaluationService>.Instance);
        return new PurchaseRequestFlow(persistence, attestation, policyEvaluations, context);
    }

    /// <summary>Decides the first pending task with the real decision service.</summary>
    public async Task<Guid> DecideFirstPendingAsync(
        ApprovalDecisionAction action,
        Guid? assigneeOverride = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        var task = await context.ApprovalTasks
            .AsNoTracking()
            .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
            .OrderBy(record => record.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException("No pending approval task exists.");
        var assignment = await context.ApprovalAssignments
            .AsNoTracking()
            .SingleAsync(
                record => record.TaskId == task.Id && record.ReleasedAt == null, cancellationToken);
        var services = CreateServices(context);
        await new ApprovalDecisionService(
                context, services.AssignmentEngine, NullLogger<ApprovalDecisionService>.Instance)
            .DecideAsync(
                new ApprovalDecisionCommand(
                    task.Id,
                    action,
                    $"{action} by the supplier integration suite",
                    $"decide-{Guid.NewGuid():N}",
                    task.Version,
                    assigneeOverride ?? assignment.AssigneeUserId,
                    "corr-decision"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        // The decision only records the outbox event: the supplier consumer applies it on delivery,
        // exactly as the production dispatcher does (REQ-03).
        var outcome = await DispatchResultsAsync(cancellationToken);
        await AssertDeliveryAsync(outcome, cancellationToken);
        return task.Id;
    }

    /// <summary>Decides the pending task without dispatching, so a test can replay the delivery.</summary>
    public async Task<Guid> DecideFirstPendingAsyncCoreAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        var task = await context.ApprovalTasks
            .AsNoTracking()
            .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
            .OrderBy(record => record.Id)
            .FirstAsync(cancellationToken);
        var assignment = await context.ApprovalAssignments
            .AsNoTracking()
            .SingleAsync(record => record.TaskId == task.Id && record.ReleasedAt == null, cancellationToken);
        var services = CreateServices(context);
        await new ApprovalDecisionService(
                context, services.AssignmentEngine, NullLogger<ApprovalDecisionService>.Instance)
            .DecideAsync(
                new ApprovalDecisionCommand(
                    task.Id, ApprovalDecisionAction.Approve, "Approved by the hardening suite",
                    $"decide-{Guid.NewGuid():N}", task.Version, assignment.AssigneeUserId, "corr-decision"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        return task.Id;
    }

    /// <summary>
    /// Redelivers the newest recorded outbox event by clearing its delivery checkpoint, which is the
    /// at-least-once redelivery a real dispatcher performs after a lost acknowledgement (REQ-03).
    /// </summary>
    public async Task<ApprovalDispatchOutcome> RedispatchLastEventAsync(CancellationToken cancellationToken)
    {
        await using (var reset = CreateContext())
        {
            var eventId = await reset.ApprovalOutboxEvents
                .AsNoTracking()
                .OrderByDescending(record => record.CreatedAt)
                .Select(record => record.Id)
                .FirstAsync(cancellationToken);
            await reset.Database.ExecuteSqlRawAsync(
                "UPDATE [Approval].[ApprovalOutboxEvents] SET [State] = 0, [DeliveredAt] = NULL, " +
                "[NextAttemptAt] = SYSUTCDATETIME(), [LockedUntil] = NULL, [LockOwner] = NULL " +
                "WHERE [Id] = {0}",
                [eventId],
                cancellationToken);
        }

        return await DispatchResultsAsync(cancellationToken);
    }

    /// <summary>Activates the policy that requires an active supplier for every covered line.</summary>
    public async Task ActivatePolicyAsync(ProcureToPayDbContext context, CancellationToken cancellationToken)
    {
        var policy = new PolicySetVersion(Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
        policy.AddRule(new PolicyRule(
            "ACTIVE_SUPPLIER",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.RequireActiveSupplier, "ACTIVE_SUPPLIER")]));
        policy.AddRule(new PolicyRule(
            "LINE_FALLBACK", PolicyScope.Line, [], [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
            isFallback: true));
        policy.AddRule(new PolicyRule(
            "REQUEST_FALLBACK", PolicyScope.Request, [],
            [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_ALLOW")], isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        var persistence = new PolicyPersistenceService(context);
        var actor = new PolicyActor("USER", BuyerId);
        var appended = await persistence.AppendVersionAsync(
            policy, DateTimeOffset.UtcNow, actor, "Supplier hardening policy", "corr-policy-append",
            cancellationToken);
        await persistence.ActivateAsync(
            OrganizationId, appended.Id, DateTimeOffset.UtcNow.AddSeconds(1), actor,
            "Activate supplier hardening policy", "corr-policy-activate", cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), cancellationToken);
    }

    /// <summary>Decides and returns the delivery outcome so a test can assert it explicitly.</summary>
    public async Task<ApprovalDispatchOutcome> DecideAndDispatchAsync(
        ApprovalDecisionAction action,
        CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        var task = await context.ApprovalTasks
            .AsNoTracking()
            .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
            .OrderBy(record => record.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException("No pending approval task exists.");
        var assignment = await context.ApprovalAssignments
            .AsNoTracking()
            .SingleAsync(record => record.TaskId == task.Id && record.ReleasedAt == null, cancellationToken);
        var services = CreateServices(context);
        await new ApprovalDecisionService(
                context, services.AssignmentEngine, NullLogger<ApprovalDecisionService>.Instance)
            .DecideAsync(
                new ApprovalDecisionCommand(
                    task.Id, action, $"{action} by the supplier integration suite",
                    $"decide-{Guid.NewGuid():N}", task.Version, assignment.AssigneeUserId, "corr-decision"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        var outcome = await DispatchResultsAsync(cancellationToken);
        await AssertDeliveryAsync(outcome, cancellationToken);
        return outcome;
    }

    /// <summary>A failed delivery is surfaced as the persisted error instead of a silent no-op.</summary>
    private async Task AssertDeliveryAsync(ApprovalDispatchOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Failed == 0 && outcome.DeadLettered == 0)
        {
            return;
        }

        await using var context = CreateContext();
        var errors = await context.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(record => record.LastError != null)
            .Select(record => record.LastError)
            .ToArrayAsync(cancellationToken);
        throw new DomainConflictException(
            $"The approval result delivery failed ({outcome.Failed} failed, {outcome.DeadLettered} dead-lettered): " +
            string.Join(",", errors));
    }

    /// <summary>Delivers every recorded outbox event to the real supplier consumer (REQ-03).</summary>
    public async Task<ApprovalDispatchOutcome> DispatchResultsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        var services = CreateServices(context);
        var dispatcher = new ApprovalOutboxDispatcher(
            context,
            new ApprovalResultConsumerRegistry(
            [
                // A plain human decision publishes approval-result/v2, so the router of that
                // contract also dispatches supplier subjects to the supplier consumer (REQ-03).
                new ApprovalResultRouter(
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                                context,
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        ApprovalOutboxPolicy.ContractVersion),
                    new SupplierApprovalResultConsumer(
                        context, services.Governance, services.Catalog,
                        NullLogger<SupplierApprovalResultConsumer>.Instance,
                        ApprovalOutboxPolicy.ContractVersion),
                    NullLogger<ApprovalResultRouter>.Instance,
                    ApprovalOutboxPolicy.ContractVersion),
                new ApprovalResultRouter(
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                                context,
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.ResultContractVersionV3),
                    new SupplierApprovalResultConsumer(
                        context, services.Governance, services.Catalog,
                        NullLogger<SupplierApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.ResultContractVersionV3),
                    NullLogger<ApprovalResultRouter>.Instance,
                    ApprovalEvolutionCodes.ResultContractVersionV3),
                new ApprovalResultRouter(
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                                context,
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        "approval-result/v3"),
                    new SupplierApprovalResultConsumer(
                        context, services.Governance, services.Catalog,
                        NullLogger<SupplierApprovalResultConsumer>.Instance,
                        "approval-result/v3"),
                    NullLogger<ApprovalResultRouter>.Instance,
                    "approval-result/v3"),
                new ApprovalResultRouter(
                    new PurchaseRequestApprovalResultConsumer(
                        context,
                        new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                            context,
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                                context,
                                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                            new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                        NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.LifecycleContractVersion),
                    new SupplierApprovalResultConsumer(
                        context, services.Governance, services.Catalog,
                        NullLogger<SupplierApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.LifecycleContractVersion),
                    NullLogger<ApprovalResultRouter>.Instance,
                    ApprovalEvolutionCodes.LifecycleContractVersion)
            ]),
            NullLogger<ApprovalOutboxDispatcher>.Instance);
        return await dispatcher.DispatchAsync(
            OrganizationId, "supplier-integration", DateTimeOffset.UtcNow.AddSeconds(1), 100, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        context.Organizations.Add(new OrganizationRecord
        {
            Id = OrganizationId,
            Code = "SUPPLIER-TEST",
            Name = "Supplier Test",
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
        context.Departments.Add(new DepartmentRecord
        {
            Id = DepartmentId,
            OrganizationId = OrganizationId,
            Code = "PROCUREMENT",
            Name = "Procurement",
            Status = (int)EntityStatus.Active,
            Version = 1
        });
        context.UserProfiles.AddRange(
            Profile(BuyerId, "buyer@test"),
            Profile(ApproverId, "approver@test"),
            Profile(ApSpecialistId, "ap@test"),
            Profile(AuditorId, "auditor@test"),
            Profile(RequesterId, "requester@test"),
            Profile(RequestedForId, "requested-for@test"));
        context.RoleAssignments.AddRange(
            Assignment(BuyerId, SystemRole.ProcurementBuyer),
            Assignment(ApproverId, SystemRole.ProcurementApprover),
            Assignment(ApproverId, SystemRole.ProcurementBuyer),
            Assignment(ApSpecialistId, SystemRole.ApSpecialist),
            Assignment(AuditorId, SystemRole.Auditor));
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
            UserProfileId = ApproverId,
            AuthorityLevelId = level.Id,
            MaxAmountBase = null,
            BaseCurrency = "PEN",
            ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = null,
            Status = (int)GrantStatus.Active,
            GrantedAt = DateTimeOffset.UtcNow.AddDays(-1),
            GrantedBy = BuyerId,
            Version = 1
        });
        await context.SaveChangesAsync(cancellationToken);
        var catalogs = new ReferenceCatalogPersistenceService(context);
        var category = await catalogs.CreateSpendCategoryAsync(
            OrganizationId, BuyerId, SpendCategoryCode, "Hardware", "Seed", "corr-seed", cancellationToken);
        SpendCategoryDigest = category.Digest;
        SpendCategoryVersion = category.Version;
        var costCenter = await catalogs.CreateCostCenterAsync(
            OrganizationId, BuyerId, "CC-SUPPLIER", "Supplier test", DepartmentId, "Seed", "corr-cc",
            cancellationToken);
        CostCenterId = costCenter.Id;
        CostCenterVersion = costCenter.Version;
    }

    private UserProfileRecord Profile(Guid id, string email) => new()
    {
        Id = id,
        OrganizationId = OrganizationId,
        Issuer = "https://issuer.test",
        Subject = id.ToString("D"),
        Email = email,
        DisplayName = email,
        DepartmentId = null,
        JobTitle = null,
        Status = (int)UserProfileStatus.Active,
        Version = 1
    };

    private RoleAssignmentRecord Assignment(Guid userId, SystemRole role) => new()
    {
        Id = Guid.NewGuid(),
        UserProfileId = userId,
        Role = (int)role,
        ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
        AssignedAt = DateTimeOffset.UtcNow.AddDays(-1),
        AssignedBy = BuyerId,
        Status = (int)AssignmentStatus.Active,
        Version = 1
    };
}

/// <summary>Real Purchase Request → Policy flow shared by the cross-module suites (REQ-08, REQ-09).</summary>
public sealed record PurchaseRequestFlow(
    PurchaseRequestPersistenceService Persistence,
    PurchaseRequestAttestationService Attestation,
    PolicyEvaluationService PolicyEvaluations,
    ProcureToPayDbContext Context);

/// <summary>Services bound to one context of the harness (REQ-02, REQ-03, REQ-06, REQ-10).</summary>
public sealed record Services(
    ProcureToPayDbContext Context,
    SupplierPersistenceService Persistence,
    SupplierGovernanceService Governance,
    ApprovedSupplierCatalogGovernanceService Catalog,
    SupplierBankingService Banking,
    ApprovalSubmissionService Submissions,
    ApprovalAssignmentEngine AssignmentEngine,
    ApprovalWorkloadAllowlist Allowlist,
    SupplierPrerequisiteProcessor Processor,
    ApprovedSupplierFactOwner FactOwner,
    IConfiguration Configuration,
    PurchaseRequestFlow PurchaseRequests);
