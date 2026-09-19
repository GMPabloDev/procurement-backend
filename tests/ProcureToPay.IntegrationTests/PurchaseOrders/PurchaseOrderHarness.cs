using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.IntegrationTests.Sourcing;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server harness of the Purchase Orders module (SPEC 11). It composes the real Sourcing suite so
/// every claim consumes a genuine published award through the real <c>award-consumption/v1</c>
/// verifier, and it wires the per-line takeover service into both sides of the fence.
/// </summary>
public sealed class PurchaseOrderHarness : IAsyncDisposable
{
    private PurchaseOrderHarness(SourcingHarness sourcing)
    {
        Sourcing = sourcing;
    }

    public SourcingHarness Sourcing { get; }

    public Guid OrganizationId => Sourcing.OrganizationId;

    public Guid BuyerId => Sourcing.BuyerId;

    public Guid RequesterId => Sourcing.RequesterId;

    public Guid LineId => Sourcing.LineId;

    public Guid OtherLineId => Sourcing.OtherLineId;

    public Guid RequestId => Sourcing.RequestId;

    public Guid SupplierId => Sourcing.SupplierId;

    public static async Task<PurchaseOrderHarness> StartAsync(CancellationToken cancellationToken) =>
        new(await SourcingHarness.StartAsync(cancellationToken));

    public ProcureToPayDbContext CreateContext() => Sourcing.CreateContext();

    public PurchaseRequestLineTakeoverService CreateTakeoverService(ProcureToPayDbContext context) => new(context);

    public SourcingAwardService CreateAwardService(ProcureToPayDbContext context) =>
        new(context, new PurchaseRequestLineTakeoverService(context));

    public PurchaseOrderClaimService CreateClaimService(ProcureToPayDbContext context) =>
        new(context, CreateAwardService(context), new PurchaseRequestLineTakeoverService(context));

    public PurchaseOrderDraftService CreateDraftService(ProcureToPayDbContext context) =>
        new(context, new PurchaseRequestLineTakeoverService(context));

    /// <summary>
    /// Publishes one real award over the seeded request with the complete approved evidence, which is
    /// the only source a claim may consume. The standard scenario awards one line; the two-line
    /// variant is the fixture that makes partial coverage observable (CA-01, CA-02).
    /// </summary>
    public async Task<SourcingAwardView> PublishAwardAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken,
        bool twoLines = false)
    {
        var (created, rfq) = await SourcingScenario.OpenRfqAsync(Sourcing, context, cancellationToken);
        var process = await Sourcing.CreateProcessService(context)
            .GetProcessAsync(OrganizationId, created.ProcessId, cancellationToken);
        await SourcingScenario.EvaluateAsync(Sourcing, context, rfq, cancellationToken);
        await SourcingScenario.SelectAsync(Sourcing, context, rfq, cancellationToken);
        if (twoLines)
        {
            await Sourcing.CreateSelectionService(context).SelectAsync(
                new SelectLineCommand(
                    OrganizationId, rfq.RfqId, OtherLineId, SupplierId, null, null,
                    $"select-{Guid.NewGuid():N}"),
                BuyerId,
                "corr-select-two",
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        await Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        var proposal = await Sourcing.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                OrganizationId, rfq.RfqId, SupplierId, $"proposal-{Guid.NewGuid():N}"),
            BuyerId,
            "corr-proposal",
            DateTimeOffset.UtcNow,
            cancellationToken);
        await SourcingScenario.SeedApprovalAsync(Sourcing, context, proposal, cancellationToken);
        return await CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                OrganizationId,
                proposal.ProposalId,
                proposal.Version,
                process.Version,
                null,
                $"award-{Guid.NewGuid():N}",
                "Award the selected supplier"),
            BuyerId,
            "corr-award",
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Builds the exact claim request over the covered lines of one published award.</summary>
    public AwardConsumptionClaimRequest ClaimRequest(
        SourcingAwardView award,
        string claimKey,
        IReadOnlyList<Guid>? lines = null,
        Guid? poId = null)
    {
        var covered = (lines ?? award.AwardedLines)
            .Select(lineId => new PurchaseOrderContentRef(lineId, 1, LineDigest(lineId)))
            .ToArray();
        return new AwardConsumptionClaimRequest(
            OrganizationId,
            poId ?? Guid.NewGuid(),
            new PurchaseOrderContentRef(award.AwardId, award.Version, award.ContentDigest),
            covered,
            claimKey,
            DateTimeOffset.UtcNow,
            PurchaseOrderCodes.DomainWorkloadIssuer,
            PurchaseOrderCodes.DomainWorkloadClientId);
    }

    /// <summary>The attested line digest of one seeded line, as the request version persisted it.</summary>
    public string LineDigest(Guid lineId) => lineId == LineId ? new string('2', 64) : new string('3', 64);

    public async ValueTask DisposeAsync() => await Sourcing.DisposeAsync();

    /// <summary>Attested material digest of the seeded ordered line (REQ-03, REQ-04).</summary>
    public static readonly string TargetDigest = new('d', 64);

    /// <summary>Seeded reservation amount of the ordered target (REQ-04, REQ-05).</summary>
    public const decimal Reserved = 50m;

    /// <summary>Workloads and productive producers the real Budget/Approval boundaries require.</summary>
    public static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Approval:Workloads:0:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Approval:Workloads:0:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId,
            // SPEC 11 REQ-08: the supporting document owner signals the Approval Workflow.
            ["Approval:Workloads:1:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Approval:Workloads:1:ClientId"] = "supporting-document-owner",
            ["Budget:MovementProducers:0:Operation"] = "COMMIT",
            ["Budget:MovementProducers:0:ContractVersion"] = "v1",
            ["Budget:MovementProducers:0:SourceType"] = BudgetCodes.PurchaseOrderSourceType,
            ["Budget:MovementProducers:0:ProducerId"] = PurchaseOrderCodes.DomainProducerId,
            ["Budget:MovementProducers:0:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Budget:MovementProducers:0:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId,
            ["Budget:MovementProducers:1:Operation"] = "REVERSE",
            ["Budget:MovementProducers:1:ContractVersion"] = "v1",
            ["Budget:MovementProducers:1:SourceType"] = BudgetCodes.PurchaseOrderSourceType,
            ["Budget:MovementProducers:1:ProducerId"] = PurchaseOrderCodes.DomainProducerId,
            ["Budget:MovementProducers:1:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Budget:MovementProducers:1:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId,
            ["Budget:MovementProducers:2:Operation"] = "REVERSE",
            ["Budget:MovementProducers:2:ContractVersion"] = "v1",
            ["Budget:MovementProducers:2:SourceType"] = "PURCHASE_REQUEST",
            ["Budget:MovementProducers:2:ProducerId"] = PurchaseOrderCodes.DomainProducerId,
            ["Budget:MovementProducers:2:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Budget:MovementProducers:2:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId
        })
        .Build();

    /// <summary>Submission service with the real order and amendment adapters of the module.</summary>
    public ApprovalSubmissionService CreateSubmissionService(ProcureToPayDbContext context)
    {
        var configuration = Configuration();
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        return new ApprovalSubmissionService(
            context,
            new ApprovalSubmissionAdapterRegistry([
                new PurchaseOrderApprovalAdapter(context, new PurchaseRequestOrderingEvidenceService(context)),
                new PurchaseOrderAmendmentApprovalAdapter(context, new PurchaseRequestOrderingEvidenceService(context))
            ]),
            new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
            allowlist,
            new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
            NullLogger<ApprovalSubmissionService>.Instance);
    }

    /// <summary>The productive COMMIT/REVERSE producer over the real budget ledger transition service.</summary>
    public PurchaseOrderBudgetProducer CreateBudgetProducer(ProcureToPayDbContext context) =>
        new(
            context,
            new BudgetTransitionService(
                context,
                new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                new BudgetMovementProducerRegistry(Configuration()),
                new BudgetPersistenceService(context)),
            new ApprovalWorkloadIdentity(
                PurchaseOrderCodes.DomainWorkloadIssuer, PurchaseOrderCodes.DomainWorkloadClientId));

    public PurchaseOrderIssuanceService CreateIssuanceService(
        ProcureToPayDbContext context,
        PurchaseOrderBudgetProducer? producer = null) =>
        new(
            context,
            producer ?? CreateBudgetProducer(context),
            new PurchaseRequestLineTakeoverService(context),
            CreateSubmissionService(context),
            new PurchaseRequestOrderingEvidenceService(context));

    public PurchaseOrderAmendmentService CreateAmendmentService(ProcureToPayDbContext context) =>
        new(
            context,
            CreateBudgetProducer(context),
            new PurchaseRequestLineTakeoverService(context),
            CreateSubmissionService(context));

/// <summary>
    /// Seeds the completed request case with its financial authority, the submission attempt and the
    /// reservation of the ordered target, which are the three upstream proofs REQ-03 and REQ-04 read.
    /// </summary>
    public async Task SeedRequestEvidenceAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken,
        bool approved = true)
    {
        var now = DateTimeOffset.UtcNow;
        var caseId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        context.ApprovalCases.Add(new ApprovalCaseRecord
        {
            Id = caseId,
            OrganizationId = OrganizationId,
            SubjectType = "PURCHASE_REQUEST",
            SubjectId = RequestId,
            SubjectVersion = 1,
            Operation = "SUBMIT_PURCHASE_REQUEST",
            SourceSnapshotDigest = new string('e', 64),
            WorkloadIssuer = "internal://procure-to-pay",
            WorkloadClientId = "purchase-request-domain",
            SubmissionKey = $"seed-{caseId:N}",
            SubmissionFingerprint = new string('f', 64),
            OriginatorId = RequesterId,
            RequesterId = RequesterId,
            Status = approved ? (int)ApprovalCaseStatus.Completed : (int)ApprovalCaseStatus.Open,
            Version = 2,
            CreatedAt = now,
            CorrelationReference = "seed-request-case"
        });
        context.ApprovalRequirements.Add(new ApprovalRequirementRecord
        {
            Id = requirementId,
            CaseId = caseId,
            OrganizationId = OrganizationId,
            SourceRequirementKey = "FINANCE",
            WorkflowRequirementKey = "FINANCE",
            StageCode = "PRE_PROCUREMENT",
            Role = (int)SystemRole.FinanceApprover,
            // The canonical persisted shape, so the approval hydrator reads the same requirement the
            // real submission would have written (SPEC 03).
            AuthorityJson = ApprovalJsonPersistence.SerializeAuthority(
                AuthorityRequirement.Required(ApprovalAuthorityType.Financial, 1, 1_000m, "PEN")),
            DecisionScopeJson = "{\"scope\":\"ORGANIZATION\"}",
            ExcludedUserIdsJson = "[]",
            ActionsJson = "[\"APPROVE\",\"REJECT\"]",
            TargetsJson = $"[{{\"type\":\"{PurchaseOrderCodes.ApprovalTargetType}\",\"id\":\"{LineId:D}\"," +
                          $"\"version\":1,\"materialSnapshotDigest\":\"{TargetDigest}\"}}]",
            DependenciesJson = "[]",
            Status = approved
                ? (int)ApprovalRequirementStatus.Approved
                : (int)ApprovalRequirementStatus.Rejected,
            Version = 1
        });
        context.ApprovalDecisions.Add(new ApprovalDecisionRecord
        {
            Id = decisionId,
            CaseId = caseId,
            OrganizationId = OrganizationId,
            RequirementId = requirementId,
            Action = approved
                ? (int)ApprovalDecisionAction.Approve
                : (int)ApprovalDecisionAction.Reject,
            Origin = (int)ApprovalDecisionOrigin.Human,
            ActorType = "USER",
            ActorUserId = Sourcing.AuditorId,
            Reason = "Approved",
            DecidedAt = now,
            DecisionKey = "seed-decision",
            Fingerprint = new string('a', 64),
            DecisionDigest = new string('b', 64),
            AuthorityEvidenceDigest = new string('c', 64),
            EligibilityEvidenceJson = "{}",
            EvidenceId = Guid.NewGuid(),
            EvidenceVersion = 1
        });
        context.ApprovalDecisionTargets.Add(new ApprovalDecisionTargetRecord
        {
            Id = Guid.NewGuid(),
            DecisionId = decisionId,
            CaseId = caseId,
            OrganizationId = OrganizationId,
            RequirementId = requirementId,
            TargetType = "PURCHASE_REQUEST_LINE",
            TargetId = LineId,
            TargetVersion = 1,
            MaterialSnapshotDigest = TargetDigest
        });
        var bundleId = await context.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(bundle => bundle.OrganizationId == OrganizationId &&
                             bundle.SubjectId == RequestId &&
                             bundle.Operation == "REQUEST_EVALUATE")
            .OrderByDescending(bundle => bundle.EvaluationSequence)
            .Select(bundle => bundle.Id)
            .FirstAsync(cancellationToken);
        context.PurchaseRequestSubmissionAttempts.Add(new PurchaseRequestSubmissionAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            RequesterId = RequesterId,
            RequestId = RequestId,
            RequestVersion = 1,
            SubmissionKey = $"seed-submission-{Guid.NewGuid():N}",
            Fingerprint = new string('d', 64),
            Status = 3,
            PolicySetVersionId = Sourcing.PolicySetVersionId,
            PolicyEvaluationBundleId = bundleId,
            ApprovalCaseId = caseId,
            ApprovalContractVersion = "approval-result/v3",
            CreatedAt = now,
            UpdatedAt = now,
            AttemptCount = 1
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<(Guid PositionId, Guid ReservedMovementId)> SeedReservationAsync(
        ProcureToPayDbContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var costCenterId = Guid.Parse("66666666-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var departmentId = Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var spendCategoryId = Guid.NewGuid();
        context.Departments.Add(new DepartmentRecord
        {
            Id = departmentId,
            OrganizationId = OrganizationId,
            Code = "DEPT-1",
            Name = "Department",
            Status = (int)EntityStatus.Active,
            Version = 1
        });
        context.CostCenters.Add(new CostCenterRecord
        {
            Id = costCenterId,
            OrganizationId = OrganizationId,
            Code = "CC-1",
            CurrentVersion = 1
        });
        context.CostCenterVersions.Add(new CostCenterVersionRecord
        {
            CostCenterId = costCenterId,
            Version = 1,
            OrganizationId = OrganizationId,
            Name = "Cost Center",
            DepartmentId = departmentId,
            Status = (int)EntityStatus.Active,
            ActorUserId = BuyerId,
            OccurredAt = now,
            Reason = "Seed"
        });
        context.SpendCategories.Add(new SpendCategoryRecord
        {
            Id = spendCategoryId,
            OrganizationId = OrganizationId,
            Code = "HARDWARE",
            CurrentVersion = 1
        });
        context.SpendCategoryVersions.Add(new SpendCategoryVersionRecord
        {
            SpendCategoryId = spendCategoryId,
            Version = 1,
            OrganizationId = OrganizationId,
            Code = "HARDWARE",
            Name = "Hardware",
            Digest = new string('4', 64),
            Status = (int)EntityStatus.Active,
            ActorUserId = BuyerId,
            OccurredAt = now,
            Reason = "Seed"
        });
        var positionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var reservedId = Guid.NewGuid();
        context.BudgetPositions.Add(new BudgetPositionRecord
        {
            Id = positionId,
            OrganizationId = OrganizationId,
            CostCenterId = costCenterId,
            FiscalYear = now.Year,
            SpendCategoryCode = "HARDWARE",
            PositionKeyDigest = BudgetFingerprints.PositionKeyDigest(
                OrganizationId, costCenterId, now.Year, "HARDWARE"),
            CurrentAllocationVersion = 1,
            CurrentBalanceVersion = 1
        });
        context.BudgetAllocationVersions.Add(new BudgetAllocationVersionRecord
        {
            PositionId = positionId,
            Version = 1,
            OrganizationId = OrganizationId,
            CostCenterId = costCenterId,
            CostCenterVersion = 1,
            SpendCategoryCode = "HARDWARE",
            SpendCategoryVersion = 1,
            SpendCategoryDigest = new string('4', 64),
            AllocatedAmount = Reserved,
            Currency = "PEN",
            ActorUserId = BuyerId,
            OccurredAt = now,
            AllocationKey = "seed-allocation",
            Fingerprint = new string('5', 64),
            Reason = "Seed"
        });
        context.BudgetBalances.Add(new BudgetBalanceRecord
        {
            PositionId = positionId,
            BalanceVersion = 1,
            AllocationVersion = 1,
            Allocated = Reserved,
            Reserved = Reserved,
            Committed = 0m,
            Consumed = 0m
        });
        context.BudgetOperations.Add(new BudgetOperationRecord
        {
            Id = operationId,
            OrganizationId = OrganizationId,
            Kind = (int)BudgetOperationKind.ApprovalReserve,
            OperationKey = $"seed-reserve-{operationId:N}",
            Fingerprint = new string('6', 64),
            SourceType = "PURCHASE_REQUEST",
            SourceId = RequestId,
            SourceVersion = 1,
            SourceDigest = new string('7', 64),
            ActorJson = "{\"type\":\"SYSTEM\",\"system_id\":\"BUDGET_OWNER\"}",
            ReasonCode = "PURCHASE_REQUEST_APPROVED",
            Result = "RESERVED",
            OccurredAt = now,
            CorrelationReference = "seed-reserve"
        });
        context.BudgetMovements.Add(new BudgetMovementRecord
        {
            Id = requestedId,
            OperationId = operationId,
            OrganizationId = OrganizationId,
            PositionId = positionId,
            AllocationVersionAtPosting = 1,
            Type = (int)BudgetMovementType.Requested,
            Amount = Reserved,
            Currency = "PEN",
            TargetId = LineId,
            TargetVersion = 1,
            TargetMaterialSnapshotDigest = TargetDigest,
            TargetType = "PURCHASE_REQUEST_LINE",
            BalanceVersionAfter = 1,
            OccurredAt = now
        });
        context.BudgetMovements.Add(new BudgetMovementRecord
        {
            Id = reservedId,
            OperationId = operationId,
            OrganizationId = OrganizationId,
            PositionId = positionId,
            AllocationVersionAtPosting = 1,
            Type = (int)BudgetMovementType.Reserved,
            Amount = Reserved,
            Currency = "PEN",
            ParentMovementId = requestedId,
            TargetId = LineId,
            TargetVersion = 1,
            TargetMaterialSnapshotDigest = TargetDigest,
            TargetType = "PURCHASE_REQUEST_LINE",
            BalanceVersionAfter = 1,
            AllocatedBefore = Reserved,
            ReservedBefore = 0m,
            AllocatedAfter = Reserved,
            ReservedAfter = Reserved,
            OccurredAt = now
        });
        await context.SaveChangesAsync(cancellationToken);
        return (positionId, reservedId);
    }
}
