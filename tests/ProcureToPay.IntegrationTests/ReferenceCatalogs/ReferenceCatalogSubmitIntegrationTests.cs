using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using Testcontainers.MsSql;

namespace ProcureToPay.IntegrationTests.ReferenceCatalogs;

/// <summary>
/// SPEC 07 REQ-07 / CA-06 end-to-end evidence without doubles: Purchase Requests attests with the
/// real Cost Center and Spend Category owners, Policy validates the real catalogs, the Approval
/// case freezes a COST_CENTER decision scope and only the covered candidate is assigned.
/// </summary>
public sealed class ReferenceCatalogSubmitIntegrationTests
{
    [Fact]
    public async Task Submit_with_real_catalogs_attests_evaluates_and_assigns_only_the_covered_candidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);

        var (requestId, version) = await harness.CreateAsync(cancellationToken);
        var outcome = await harness.SubmitAsync(requestId, version, "submit-real-1", cancellationToken);

        Assert.False(outcome.Replayed);
        Assert.Equal(PurchaseRequestStatus.InApproval, outcome.Status);
        Assert.NotNull(outcome.ApprovalCaseId);

        await using var context = harness.CreateContext();
        // The persisted attestation keeps the real owner identities and the exact relation.
        var attestation = await context.PurchaseRequestReferenceAttestations.SingleAsync(
            record => record.RequestId == requestId && record.RequestVersion == version, cancellationToken);
        Assert.Contains("cost-center-domain", attestation.AssertionsJson, StringComparison.Ordinal);
        Assert.Contains("spend-category-domain", attestation.AssertionsJson, StringComparison.Ordinal);
        Assert.Contains("COST_CENTER_OWNED_BY_DEPARTMENT", attestation.AssertionsJson, StringComparison.Ordinal);

        // The Approval requirement froze the COST_CENTER decision scope resolved from the catalog.
        var requirement = await context.ApprovalRequirements.SingleAsync(
            record => record.CaseId == outcome.ApprovalCaseId, cancellationToken);
        Assert.Contains("\"dimension\":\"COST_CENTER\"", requirement.DecisionScopeJson, StringComparison.Ordinal);
        Assert.Contains(harness.CostCenterId.ToString("D"), requirement.DecisionScopeJson, StringComparison.Ordinal);

        // Only the approver whose assignment covers that exact cost center was assigned.
        var task = await context.ApprovalTasks.SingleAsync(
            record => record.RequirementId == requirement.Id, cancellationToken);
        Assert.Equal((int)ApprovalTaskStatus.Pending, task.Status);
        var assignment = await context.ApprovalAssignments.SingleAsync(
            record => record.TaskId == task.Id && record.ReleasedAt == null,
            cancellationToken);
        Assert.Equal(harness.CoveredApproverId, assignment.AssigneeUserId);

        // Replay resolves the same case and never duplicates attestation, evaluation or case.
        var replay = await harness.SubmitAsync(requestId, version, "submit-real-1", cancellationToken);
        Assert.True(replay.Replayed);
        Assert.Equal(outcome.ApprovalCaseId, replay.ApprovalCaseId);
        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.PurchaseRequestReferenceAttestations.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Budget_control_reserves_all_or_nothing_and_signals_the_prerequisite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken, withBudgetCheck: true);
        await harness.FundBudgetAsync(1000m, cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        var submitted = await harness.SubmitAsync(requestId, version, "submit-budget-1", cancellationToken);

        await using var context = harness.CreateContext();
        // The precheck confirmed availability before any case existed (SPEC 08 REQ-06).
        var attempt = await context.PurchaseRequestSubmissionAttempts.SingleAsync(
            record => record.RequestId == requestId && record.RequestVersion == version, cancellationToken);
        Assert.Equal((int)PurchaseRequestSubmissionStatus.ApprovalConfirmed, attempt.Status);

        // The owner reserved the full amount and then signalled the prerequisite.
        await harness.ProcessBudgetAsync(cancellationToken);
        await using var verification = harness.CreateContext();
        var prerequisite = await verification.ApprovalPrerequisites.SingleAsync(
            record => record.CaseId == submitted.ApprovalCaseId, cancellationToken);
        Assert.Equal((int)PrerequisiteStatus.Satisfied, prerequisite.Status);
        Assert.Equal(BudgetCodes.BudgetOwnerAdapterId, prerequisite.OwnerAdapterId);
        Assert.Equal(BudgetCodes.BudgetOwnerAdapterVersion, prerequisite.OwnerAdapterVersion);
        Assert.Equal("budget-check-owner", prerequisite.OwnerWorkloadClientId);

        var budgetAttempt = await verification.BudgetPrerequisiteAttempts.SingleAsync(
            record => record.PrerequisiteId == prerequisite.Id, cancellationToken);
        Assert.Equal("COMPLETED", budgetAttempt.State);
        Assert.Equal("SATISFIED", budgetAttempt.SignalResult);
        Assert.NotNull(budgetAttempt.ReserveOperationId);
        Assert.Equal(64, budgetAttempt.EvidenceDigest!.Length);

        var movements = await verification.BudgetMovements
            .Where(movement => movement.PositionId == budgetAttempt.Id)
            .ToArrayAsync(cancellationToken);
        _ = movements;
        var reserved = await verification.BudgetMovements
            .Where(movement => movement.Type == (int)BudgetMovementType.Reserved)
            .ToArrayAsync(cancellationToken);
        Assert.Single(reserved);
        Assert.Equal(100m, reserved[0].Amount);
        var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(1000m, balance.Allocated);
        Assert.Equal(100m, balance.Reserved);
        Assert.Equal(900m, balance.Allocated - balance.Reserved);

        // The parameters carry Fiscal Year and Spend Category per covered target (REQ-05).
        var prerequisiteRecord = await verification.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == prerequisite.Id, cancellationToken);
        Assert.Contains("\"fiscal_year\":2026", prerequisiteRecord.ParametersJson, StringComparison.Ordinal);
        Assert.Contains("\"spend_category_ref\"", prerequisiteRecord.ParametersJson, StringComparison.Ordinal);
        Assert.Contains(harness.CostCenterId.ToString("D"), prerequisiteRecord.ParametersJson, StringComparison.Ordinal);
        Assert.Contains(harness.SpendCategory.Code, prerequisiteRecord.ParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_requirement_releases_the_open_reservation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken, withBudgetCheck: true);
        await harness.FundBudgetAsync(1000m, cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);
        _ = await harness.SubmitAsync(requestId, version, "submit-budget-release", cancellationToken);
        await harness.ProcessBudgetAsync(cancellationToken);

        await using (var before = harness.CreateContext())
        {
            var held = await before.BudgetBalances.SingleAsync(cancellationToken);
            Assert.Equal(100m, held.Reserved);
        }

        await harness.RejectFirstRequirementAsync(cancellationToken);
        await harness.DispatchResultsAsync(cancellationToken);
        // Every recorded result was delivered at least once (the retry backoff is part of the
        // contract and is exercised by the approval suites).
        var delivered = await harness.ReadOutboxStatesAsync(cancellationToken);
        Assert.All(
            delivered,
            entry => Assert.False(
                entry.Result.Length == 0 && entry.State == "PENDING",
                "A recorded result stayed pending without a payload result."));

        await using var verification = harness.CreateContext();
        // The rejected target released its reservation exactly once: the hold is back to available.
        var balance = await verification.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(0m, balance.Reserved);
        Assert.Equal(1000m, balance.Allocated - balance.Reserved - balance.Committed - balance.Consumed);
        var reverses = await verification.BudgetMovements
            .Where(movement => movement.Type == (int)BudgetMovementType.Reverse)
            .ToArrayAsync(cancellationToken);
        Assert.Single(reverses);
        Assert.Equal(100m, reverses[0].Amount);
        var releaseOperation = await verification.BudgetOperations
            .SingleAsync(
                operation => operation.Kind == (int)BudgetOperationKind.Release, cancellationToken);
        Assert.Equal("RELEASED", releaseOperation.Result);

        // A redelivery of the same terminal result never releases twice.
        await harness.DispatchResultsAsync(cancellationToken);
        await using var replay = harness.CreateContext();
        Assert.Equal(
            1,
            await replay.BudgetMovements.CountAsync(
                movement => movement.Type == (int)BudgetMovementType.Reverse, cancellationToken));
        var released = await replay.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(0m, released.Reserved);
    }

    [Fact]
    public async Task A_budget_without_funds_fails_the_owner_prerequisite_without_reserving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken, withBudgetCheck: true);
        // The position exists but only covers half of the line.
        await harness.FundBudgetAsync(40m, cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        await Assert.ThrowsAsync<PurchaseRequestBudgetInsufficientException>(() =>
            harness.SubmitAsync(requestId, version, "submit-budget-2", cancellationToken));

        await using var context = harness.CreateContext();
        Assert.Empty(await context.ApprovalCases.ToArrayAsync(cancellationToken));
        var attempt = await context.PurchaseRequestSubmissionAttempts.SingleAsync(
            record => record.RequestId == requestId && record.RequestVersion == version, cancellationToken);
        Assert.Equal(PurchaseRequestSubmissionCodes.ErrorBudgetInsufficient, attempt.ErrorCode);

        // The audited precheck is preserved for the retry, and it never reserved anything.
        var operations = await context.BudgetOperations
            .Where(operation => operation.Kind == (int)BudgetOperationKind.Precheck)
            .ToArrayAsync(cancellationToken);
        Assert.Single(operations);
        // The position exists but cannot cover the line: the business outcome is insufficient.
        Assert.Equal("INSUFFICIENT", operations[0].Result);
        var balance = await context.BudgetBalances.SingleAsync(cancellationToken);
        Assert.Equal(0m, balance.Reserved);
        Assert.Equal(0m, balance.Committed);
    }

    [Fact]
    public async Task Stale_references_fail_closed_before_any_manifest_or_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        // Renaming the referenced cost center advances its version: the frozen v1 turns stale.
        await harness.Service.UpdateCostCenterAsync(
            harness.OrganizationId,
            harness.ActorId,
            harness.CostCenterId,
            1,
            "Development renamed",
            null,
            null,
            "Rename before submit",
            "corr-rename",
            cancellationToken);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.SubmitAsync(requestId, version, "submit-stale-1", cancellationToken));

        await using var context = harness.CreateContext();
        Assert.Equal(0, await context.PurchaseRequestReferenceAttestations.CountAsync(cancellationToken));
        Assert.Equal(0, await context.PurchaseRequestCompletenessManifests.CountAsync(cancellationToken));
        Assert.Equal(0, await context.ApprovalCases.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Incoherent_owner_answers_and_missing_slots_fail_closed_without_artifacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        // A missing owner slot is a 503 dependency failure, not a partial attestation.
        await Assert.ThrowsAsync<PurchaseRequestDependencyUnavailableException>(() => harness.SubmitAsync(
            requestId,
            version,
            "submit-missing-owner",
            cancellationToken,
            owners => owners
                .Where(owner => !(owner.ReferenceType == PurchaseRequestReferenceType.CostCenter &&
                                  owner.AssertionType == "COST_CENTER_OWNED_BY_DEPARTMENT"))
                .ToArray()));

        // An unknown status with active=false is an invalid contractual answer, not a 422 negative.
        await Assert.ThrowsAsync<PurchaseRequestDependencyUnavailableException>(() => harness.SubmitAsync(
            requestId,
            version,
            "submit-bad-status",
            cancellationToken,
            owners => owners.Select(owner =>
                owner.ReferenceType == PurchaseRequestReferenceType.SpendCategory
                    ? (IPurchaseRequestReferenceOwner)new Harness.ScriptedOwner(
                        owner.AssertionType, owner.ReferenceType, false, "BANANA", owner.OwnerId, owner.ContractVersion)
                    : owner).ToArray()));

        // A wrong contract identity is also a dependency failure.
        await Assert.ThrowsAsync<PurchaseRequestDependencyUnavailableException>(() => harness.SubmitAsync(
            requestId,
            version,
            "submit-wrong-contract",
            cancellationToken,
            owners => owners.Select(owner =>
                owner.ReferenceType == PurchaseRequestReferenceType.SpendCategory
                    ? (IPurchaseRequestReferenceOwner)new Harness.ScriptedOwner(
                        owner.AssertionType, owner.ReferenceType, true, "ACTIVE", owner.OwnerId,
                        contractVersion: "spend-category-db/v1", responseContractVersion: "wrong-db/v9")
                    : owner).ToArray()));

        await using var verification = harness.CreateContext();
        Assert.Equal(0, await verification.PurchaseRequestReferenceAttestations.CountAsync(cancellationToken));
        Assert.Equal(0, await verification.PurchaseRequestCompletenessManifests.CountAsync(cancellationToken));
        Assert.Equal(0, await verification.ApprovalCases.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Policy_failure_after_attestation_is_recoverable_without_duplicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        harness.FailNextPolicyCatalog();
        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            harness.SubmitAsync(requestId, version, "submit-fault-1", cancellationToken));

        await using (var afterFailure = harness.CreateContext())
        {
            // The attestation is frozen, no case exists and the attempt is recoverable.
            Assert.Equal(1, await afterFailure.PurchaseRequestCompletenessManifests.CountAsync(cancellationToken));
            Assert.Equal(0, await afterFailure.ApprovalCases.CountAsync(cancellationToken));
            var attempt = await afterFailure.PurchaseRequestSubmissionAttempts.SingleAsync(cancellationToken);
            Assert.Equal((int)PurchaseRequestSubmissionStatus.DependencyFailed, attempt.Status);
            Assert.Equal(PurchaseRequestSubmissionCodes.ErrorPolicyDependency, attempt.ErrorCode);
        }

        var outcome = await harness.SubmitAsync(requestId, version, "submit-fault-1", cancellationToken);
        Assert.Equal(PurchaseRequestStatus.InApproval, outcome.Status);
        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.PurchaseRequestCompletenessManifests.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.PurchaseRequestSubmissionAttempts.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Concurrent_submissions_with_real_catalogs_produce_one_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await Harness.StartAsync(cancellationToken);
        var (requestId, version) = await harness.CreateAsync(cancellationToken);

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => harness.SubmitAsync(requestId, version, "submit-race", cancellationToken)),
            CaptureAsync(() => harness.SubmitAsync(requestId, version, "submit-race", cancellationToken)));
        var completed = outcomes.Where(outcome => outcome is not null).Select(outcome => outcome!).ToArray();
        Assert.True(completed.Length >= 1, "At least one concurrent submission must complete.");
        Assert.Single(completed.Select(outcome => outcome.ApprovalCaseId).Distinct());

        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.PurchaseRequestReferenceAttestations.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.PurchaseRequestCompletenessManifests.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.PurchaseRequestSubmissionAttempts.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.ApprovalCases.CountAsync(cancellationToken));
    }

    private static async Task<PurchaseRequestSubmissionOutcome?> CaptureAsync(
        Func<Task<PurchaseRequestSubmissionOutcome>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainConflictException)
        {
            return null;
        }
        catch (PurchaseRequestDependencyUnavailableException)
        {
            return null;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private string connectionString = string.Empty;
        private PolicySetVersion policy = null!;
        private DateTimeOffset activationAt;

        public Guid OrganizationId { get; private init; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Guid LegalEntityId { get; private init; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public Guid DepartmentId { get; private init; } = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Guid RequesterId { get; private init; } = Guid.Parse("44444444-4444-4444-4444-444444444444");
        public Guid RequestedForId { get; private init; } = Guid.Parse("55555555-5555-5555-5555-555555555555");
        public Guid CoveredApproverId { get; private init; } = Guid.Parse("66666666-6666-6666-6666-666666666666");
        public Guid OtherApproverId { get; private init; } = Guid.Parse("77777777-7777-7777-7777-777777777777");
        public Guid ActorId { get; private init; } = Guid.Parse("88888888-8888-8888-8888-888888888888");
        public Guid CostCenterId { get; private set; }
        public SpendCategoryReference SpendCategory { get; private set; } = null!;
        public ReferenceCatalogPersistenceService Service { get; private set; } = null!;

        public static Task<Harness> StartAsync(CancellationToken cancellationToken) =>
            StartAsync(cancellationToken, withBudgetCheck: false);

        public static async Task<Harness> StartAsync(
            CancellationToken cancellationToken,
            bool withBudgetCheck)
        {
            var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("ProcureToPay_test_2026!")
                .Build();
            await container.StartAsync(cancellationToken);
            var harness = new Harness
            {
                container = container,
                connectionString = container.GetConnectionString()
            };
            await using (var context = harness.CreateContext())
            {
                await context.Database.MigrateAsync(cancellationToken);
                harness.Seed(context);
                await context.SaveChangesAsync(cancellationToken);
            }

            var serviceContext = harness.CreateContext();
            harness.Service = new ReferenceCatalogPersistenceService(serviceContext);
            var costCenter = await harness.Service.CreateCostCenterAsync(
                harness.OrganizationId,
                harness.ActorId,
                "CC-IT-DEV",
                "Development",
                harness.DepartmentId,
                "Initial cost center",
                "corr-cc",
                cancellationToken);
            harness.CostCenterId = costCenter.Id;
            await harness.Service.CreateCostCenterAsync(
                harness.OrganizationId,
                harness.ActorId,
                "CC-IT-INFRA",
                "Infrastructure",
                harness.DepartmentId,
                "Other cost center",
                "corr-cc-2",
                cancellationToken);
            harness.SpendCategory = await harness.Service.CreateSpendCategoryAsync(
                harness.OrganizationId,
                harness.ActorId,
                "HARDWARE",
                "Hardware",
                "Initial category",
                "corr-sc",
                cancellationToken);
            await harness.ActivatePolicyAsync(cancellationToken, withBudgetCheck);
            return harness;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>()
                .UseSqlServer(connectionString)
                .Options);

        private void Seed(ProcureToPayDbContext context)
        {
            context.Organizations.Add(new OrganizationRecord
            {
                Id = OrganizationId,
                Code = "PR-CATALOGS",
                Name = "Purchase Requests with catalogs",
                BaseCurrency = "PEN",
                TimeZoneId = "America/Lima",
                FiscalYearStartMonth = 1,
                Version = 1
            });
            context.LegalEntities.Add(new LegalEntityRecord
            {
                Id = LegalEntityId,
                OrganizationId = OrganizationId,
                Code = "COMPANY-PE",
                Name = "Company Peru",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            context.Departments.Add(new DepartmentRecord
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Code = "IT",
                Name = "Information Technology",
                Status = (int)EntityStatus.Active,
                Version = 1
            });
            context.UserProfiles.AddRange(
                User(RequesterId, "requester", null),
                User(RequestedForId, "requested-for", null),
                User(CoveredApproverId, "covered-approver", DepartmentId),
                User(OtherApproverId, "other-approver", DepartmentId));
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = CoveredApproverId,
                Role = (int)SystemRole.ItReviewer,
                ScopeJson = "[{\"dimension\":\"COST_CENTER\",\"reference\":\"CC-IT-DEV\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ActorId,
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = OtherApproverId,
                Role = (int)SystemRole.ItReviewer,
                ScopeJson = "[{\"dimension\":\"COST_CENTER\",\"reference\":\"CC-IT-INFRA\"}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = ActorId,
                Version = 1
            });
        }

        private static UserProfileRecord User(Guid id, string subject, Guid? departmentId) => new()
        {
            Id = id,
            OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Issuer = "https://issuer.test",
            Subject = subject,
            DepartmentId = departmentId,
            Status = (int)UserProfileStatus.Active,
            Version = 1
        };

        public async Task<(Guid RequestId, int Version)> CreateAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var persistence = new PurchaseRequestPersistenceService(
                context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            var creation = await persistence.CreateAsync(
                new PurchaseRequestCreateCommand(
                    OrganizationId,
                    RequesterId,
                    new VersionedEntityRef("LEGAL_ENTITY", LegalEntityId, 1),
                    "Business justification",
                    $"create-{Guid.NewGuid():N}",
                    "Initial request",
                    [new PurchaseRequestLineDraft("line-1", LineContent())]),
                RequesterId,
                "corr-create",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return (creation.RequestId, creation.Version);
        }

        /// <summary>Funds the budget position of the test line through the real allocation path.</summary>
        public async Task FundBudgetAsync(decimal amount, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var budgets = new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context);
            await budgets.SetAllocationAsync(
                OrganizationId,
                ActorId,
                CostCenterId,
                2026,
                SpendCategory.Code,
                amount,
                "PEN",
                expectedVersion: null,
                allocationKey: $"alloc-{Guid.NewGuid():N}",
                reason: "Integration test funding",
                correlationReference: "corr-budget-fund",
                cancellationToken);
        }

        /// <summary>
        /// Rejects the first pending requirement with the real decision service and delivers its
        /// outbox results, so the SPEC 08 release path runs exactly as in production.
        /// </summary>
        public async Task RejectFirstRequirementAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var decision = await context.ApprovalTasks
                .AsNoTracking()
                .Where(record => record.Status == (int)ApprovalTaskStatus.Pending)
                .OrderBy(record => record.Id)
                .FirstAsync(cancellationToken);
            var assignment = await context.ApprovalAssignments
                .AsNoTracking()
                .SingleAsync(record => record.TaskId == decision.Id && record.ReleasedAt == null, cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Approval:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId
                })
                .Build();
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var engine = new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context));
            await new ApprovalDecisionService(
                    context, engine, loggerFactory.CreateLogger<ApprovalDecisionService>())
                .DecideAsync(
                    new ApprovalDecisionCommand(
                        decision.Id,
                        ApprovalDecisionAction.Reject,
                        "Rejected by the budget release test",
                        $"reject-{Guid.NewGuid():N}",
                        decision.Version,
                        assignment.AssigneeUserId,
                        "corr-reject"),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
        }

        /// <summary>
        /// Delivers every recorded outbox event to the real Purchase Request consumer, which also
        /// drives the SPEC 08 release of a terminal result.
        /// </summary>
        public Task DispatchResultsAsync(CancellationToken cancellationToken) =>
            DispatchResultsAsync(DateTimeOffset.UtcNow.AddSeconds(1), cancellationToken);

        public async Task DispatchResultsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            await using var context = CreateContext();
            var budgetPersistence = new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context);
            var releases = new BudgetReleaseService(
                context,
                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                    context, budgetPersistence),
                budgetPersistence);
            var dispatcher = new ApprovalOutboxDispatcher(
                context,
                new ApprovalResultConsumerRegistry(
                [
                    new PurchaseRequestApprovalResultConsumer(
                        context, releases, NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        "approval-result/v2"),
                    new PurchaseRequestApprovalResultConsumer(
                        context, releases, NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        "approval-result/v3"),
                    new PurchaseRequestApprovalResultConsumer(
                        context, releases, NullLogger<PurchaseRequestApprovalResultConsumer>.Instance,
                        ApprovalEvolutionCodes.LifecycleContractVersion)
                ]),
                loggerFactory.CreateLogger<ApprovalOutboxDispatcher>());
            await dispatcher.DispatchAsync(
                OrganizationId,
                "integration-dispatcher",
                now,
                100,
                cancellationToken);
        }

        /// <summary>State of every recorded outbox event, for the release assertions.</summary>
        public async Task<IReadOnlyList<(string Contract, string Result, string State, string? Error)>>
            ReadOutboxStatesAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var rows = await context.ApprovalOutboxEvents
                .AsNoTracking()
                .ToArrayAsync(cancellationToken);
            return rows
                .Select(row => (
                    row.ContractVersion,
                    Result: ParseResult(row.PayloadJson),
                    State: ((ApprovalOutboxState)row.State).ToString().ToUpperInvariant(),
                    Error: row.LastError))
                .ToArray();
        }

        private static string ParseResult(string payloadJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("result", out var result)
                ? result.GetString() ?? string.Empty
                : string.Empty;
        }

        /// <summary>Runs the real budget owner processor once, as the durable worker does.</summary>
        public async Task ProcessBudgetAsync(CancellationToken cancellationToken)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Approval:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId,
                    ["Approval:Workloads:1:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:1:ClientId"] = BudgetCodes.BudgetOwnerAdapterId
                })
                .Build();
            await using var context = CreateContext();
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var assignmentEngine = new ApprovalAssignmentEngine(
                context,
                new OrganizationEligibilityService(context),
                new ApprovalScopeResolver(context));
            var processor = new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrerequisiteProcessor(
                context,
                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context),
                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                    context,
                    new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context)),
                new PurchaseRequestBudgetDemandBuilder(context),
                new ApprovalWorkflowService(
                    context, allowlist, assignmentEngine,
                    loggerFactory.CreateLogger<ApprovalWorkflowService>()),
                new ApprovalInstanceIdentity(configuration));
            await processor.ProcessDueAsync(DateTimeOffset.UtcNow, cancellationToken);
        }

        public async Task<PurchaseRequestSubmissionOutcome> SubmitAsync(
            Guid requestId,
            int version,
            string submissionKey,
            CancellationToken cancellationToken,
            Func<IReadOnlyList<IPurchaseRequestReferenceOwner>, IReadOnlyList<IPurchaseRequestReferenceOwner>>? ownerOverride = null)
        {
            if (DateTimeOffset.UtcNow < activationAt)
            {
                await Task.Delay(
                    activationAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            return await CreateServices(ownerOverride).Submission.SubmitAsync(
                new PurchaseRequestSubmissionCommand(
                    requestId,
                    version,
                    submissionKey,
                    "Present to approval",
                    OrganizationId,
                    RequesterId,
                    "corr-submit"),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        private PurchaseRequestLineContent LineContent() =>
            new(
                100m,
                "PEN",
                100m,
                "PEN",
                2026,
                "GOOD",
                new VersionedCodeRef(
                    ReferenceCatalogCodes.SpendCategoryCatalog,
                    SpendCategory.Code,
                    SpendCategory.Version,
                    SpendCategory.Digest),
                new VersionedEntityRef("COST_CENTER", CostCenterId, 1),
                new VersionedEntityRef("DEPARTMENT", DepartmentId, 1),
                new VersionedEntityRef("DEPARTMENT", DepartmentId, 1),
                new VersionedEntityRef("USER", RequestedForId, 1),
                null,
                null,
                null,
                false,
                false,
                "NONE",
                "Need for the catalog submission test",
                [],
                null);

        /// <summary>
        /// Activates the test policy. <paramref name="withBudgetCheck"/> adds the
        /// <c>REQUIRE_BUDGET_CHECK</c> control that SPEC 08 turns into a real reservation.
        /// </summary>
        private async Task ActivatePolicyAsync(CancellationToken cancellationToken, bool withBudgetCheck = false)
        {
            policy = new PolicySetVersion(
                Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line, PolicyScope.Request]);
            var decisionScope = DecisionScopeDescriptor.Create(
                    OrganizationId, [new DecisionScopeEntry(ScopeDimension.CostCenter, CostCenterId, 1)])
                .ToCanonicalJson();
            policy.AddRule(new PolicyRule(
                "LINE_APPROVAL",
                PolicyScope.Line,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.LessThanOrEqual, PolicyValue.Money(200, "PEN"))],
                [
                    new PolicyEffect(
                        PolicyEffectType.RequireApproval,
                        "COST_CENTER_APPROVAL",
                        approval: new PolicyApprovalDescriptor(
                            SystemRole.ItReviewer,
                            authorityType: null,
                            authorityLevel: null,
                            amountBase: null,
                            baseCurrency: null,
                            decisionScope))
                ]));
            if (withBudgetCheck)
            {
                policy.AddRule(new PolicyRule(
                    "LINE_BUDGET",
                    PolicyScope.Line,
                    [],
                    [new PolicyEffect(PolicyEffectType.RequireBudgetCheck, "BUDGET_CHECK")]));
            }

            policy.AddRule(new PolicyRule(
                "LINE_ALLOW",
                PolicyScope.Line,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
                isFallback: true));
            policy.AddRule(new PolicyRule(
                "REQUEST_ALLOW",
                PolicyScope.Request,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_ALLOW")],
                isFallback: true));
            policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
            var persistence = new PolicyPersistenceService(CreateContext());
            var actor = new PolicyActor("USER", Guid.NewGuid());
            var appended = await persistence.AppendVersionAsync(
                policy,
                DateTimeOffset.UtcNow,
                actor,
                "Policy for the reference catalog submission test",
                "corr-policy-append",
                cancellationToken);
            activationAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await persistence.ActivateAsync(
                OrganizationId,
                appended.Id,
                activationAt,
                actor,
                "Activate policy for the reference catalog submission test",
                "corr-policy-activate",
                cancellationToken);
        }

        private Services CreateServices(Func<IReadOnlyList<IPurchaseRequestReferenceOwner>, IReadOnlyList<IPurchaseRequestReferenceOwner>>? ownerOverride = null)
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Policy:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Policy:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId,
                    ["Approval:Workloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:0:ClientId"] = PurchaseRequestSubmissionService.Workload.ClientId,
                    // SPEC 08: the real budget owner is an allowlisted workload and its adapter/version
                    // resolves exactly one owner (REQ-05, REQ-09).
                    ["Approval:Workloads:1:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:Workloads:1:ClientId"] = BudgetCodes.BudgetOwnerAdapterId,
                    ["Approval:OwnerWorkloads:0:AdapterId"] = BudgetCodes.BudgetOwnerAdapterId,
                    ["Approval:OwnerWorkloads:0:AdapterVersion"] = BudgetCodes.BudgetOwnerAdapterVersion,
                    ["Approval:OwnerWorkloads:0:Issuer"] = PurchaseRequestSubmissionService.Workload.Issuer,
                    ["Approval:OwnerWorkloads:0:ClientId"] = BudgetCodes.BudgetOwnerAdapterId
                })
                .Build();
            var context = CreateContext();
            var persistence = new PurchaseRequestPersistenceService(
                context, NullLogger<PurchaseRequestPersistenceService>.Instance);
            // Real owners: no controlled double answers any reference.
            IReadOnlyList<IPurchaseRequestReferenceOwner> owners =
            [
                new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.LegalEntity),
                new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.User),
                new OrganizationReferenceOwner(context, PurchaseRequestReferenceType.Department),
                new CostCenterReferenceOwner(context, PurchaseRequestAssertionType.ActiveInOrganization),
                new CostCenterReferenceOwner(context, PurchaseRequestAssertionType.CostCenterOwnedByDepartment),
                new SpendCategoryReferenceOwner(context)
            ];
            if (ownerOverride is not null)
            {
                owners = ownerOverride(owners);
            }

            var attestation = new PurchaseRequestAttestationService(
                context,
                new PurchaseRequestReferenceOwnerRegistry(owners),
                persistence,
                NullLogger<PurchaseRequestAttestationService>.Instance);
            var provider = new PurchaseRequestPolicyFactProvider(
                context, persistence, NullLogger<PurchaseRequestPolicyFactProvider>.Instance);
            var policyEvaluations = new PolicyEvaluationService(
                new PolicyPersistenceService(context),
                new PolicyWorkloadAllowlist(configuration),
                new PolicyFactProviderRegistry([provider]),
                new PolicyExceptionVerifierRegistry([]),
                // Real Policy catalogs: the same registrations production uses without an HTTP catalog.
                new PolicyReferenceCatalogRegistry(
                [
                    new DatabasePolicyReferenceCatalog(context, "DEPARTMENT"),
                    new DatabasePolicyReferenceCatalog(context, "LEGAL_ENTITY"),
                    new FaultInjectingCatalog(new CostCenterPolicyReferenceCatalog(context), catalogFault),
                    new SpendCategoryPolicyReferenceCatalog(context)
                ]),
                NullLogger<PolicyEvaluationService>.Instance);
            var allowlist = new ApprovalWorkloadAllowlist(configuration);
            var budgetPersistence = new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService(context);
            var budgetLedger = new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService(
                context, budgetPersistence);
            var budgetBuilder = new PurchaseRequestBudgetDemandBuilder(context);
            var registry = new ApprovalSubmissionAdapterRegistry(
            [
                new PolicyApprovalAdapter(context),
                new PolicyApprovalAdapter(
                    context, PolicyApprovalAdapter.ContractVersionV3, budgetBuilder)
            ]);
            var assignmentEngine = new ApprovalAssignmentEngine(
                context,
                new OrganizationEligibilityService(context),
                new ApprovalScopeResolver(context));
            var ownerWorkloads = new ApprovalOwnerWorkloadRegistry(configuration, allowlist);
            var submissions = new ApprovalSubmissionService(
                context, registry, ownerWorkloads, allowlist, assignmentEngine,
                loggerFactory.CreateLogger<ApprovalSubmissionService>());
            var supersessions = new ApprovalSupersessionService(
                context, allowlist, registry, ownerWorkloads, assignmentEngine,
                loggerFactory.CreateLogger<ApprovalSupersessionService>());
            var workflow = new ApprovalWorkflowService(
                context, allowlist, assignmentEngine, loggerFactory.CreateLogger<ApprovalWorkflowService>());
            var submissionService = new PurchaseRequestSubmissionService(
                context,
                persistence,
                attestation,
                policyEvaluations,
                submissions,
                supersessions,
                workflow,
                new ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrecheckService(
                    context,
                    new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.BudgetDemandBuilderRegistry(
                        [budgetBuilder]),
                    budgetLedger,
                    attestation),
                new ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService(
                    context,
                    budgetLedger,
                    budgetPersistence),
                NullLogger<PurchaseRequestSubmissionService>.Instance);
            return new Services(submissionService);
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();

        private sealed record Services(PurchaseRequestSubmissionService Submission);

        private readonly CatalogFaultState catalogFault = new();

        /// <summary>Arms one injected Policy catalog failure for the next evaluation (CA-06).</summary>
        public void FailNextPolicyCatalog() => catalogFault.FailOnce = true;

        private sealed class CatalogFaultState
        {
            public bool FailOnce { get; set; }
        }

        private sealed class FaultInjectingCatalog(
            IPolicyReferenceCatalog inner,
            CatalogFaultState state) : IPolicyReferenceCatalog
        {
            public string CatalogId => inner.CatalogId;

            public string ContractVersion => inner.ContractVersion;

            public Task<bool> ExistsAsync(
                PolicyReferenceLookup reference,
                CancellationToken cancellationToken = default)
            {
                if (state.FailOnce)
                {
                    state.FailOnce = false;
                    throw new PolicyDependencyUnavailableException("Injected policy catalog failure.");
                }

                return inner.ExistsAsync(reference, cancellationToken);
            }
        }

        /// <summary>Owner that answers with a controlled, possibly incoherent, response (REQ-07).</summary>
        internal sealed class ScriptedOwner(
            string assertionType,
            PurchaseRequestReferenceType referenceType,
            bool active,
            string status,
            string ownerId,
            string contractVersion,
            string? responseContractVersion = null) : IPurchaseRequestReferenceOwner
        {
            public string AssertionType => assertionType;

            public PurchaseRequestReferenceType ReferenceType => referenceType;

            public string OwnerId => ownerId;

            public string ContractVersion => contractVersion;

            public Task<PurchaseRequestVerificationResponse> VerifyAsync(
                PurchaseRequestVerificationRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PurchaseRequestVerificationResponse(
                    active,
                    request.AssertionType,
                    request.OrganizationId,
                    responseContractVersion ?? contractVersion,
                    ownerId,
                    request.SourceRef!,
                    status,
                    request.TargetRef,
                    request.VerifiedAt));
        }
    }
}
