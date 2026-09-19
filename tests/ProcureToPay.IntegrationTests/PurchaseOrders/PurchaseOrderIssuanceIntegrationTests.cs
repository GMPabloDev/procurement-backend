using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-03 and REQ-04 (CA-04, CA-05): the adapter presents an order only
/// with the financial authorities of the completed request case, the approval result advances the
/// state machine, and issuing posts exactly one COMMIT of the ordered amount plus the REVERSE of the
/// open remainder before the order becomes ISSUED.
/// </summary>
public sealed class PurchaseOrderIssuanceIntegrationTests
{
    private static readonly string TargetDigest = new('d', 64);
    private const decimal Ordered = 20m;
    private const decimal Reserved = 50m;

    [Fact]
    public async Task Presenting_and_issuing_an_order_commits_exactly_its_amount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        await harness.SeedRequestEvidenceAsync(context, cancellationToken);
        var movement = await harness.SeedReservationAsync(context, cancellationToken);

        var claim = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(claim, harness.BuyerId, cancellationToken);

        // A complete draft: delivery plus the acceptance responsibility of the GOOD line.
        var draft = await harness.CreateDraftService(context).UpdateDraftAsync(
            new UpdatePurchaseOrderDraftCommand(
                harness.OrganizationId,
                claim.PoId,
                1,
                new DeliveryCommitment(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), "Lima HQ"),
                [
                    new PurchaseOrderLineAssignments(
                        harness.LineId,
                        1,
                        [
                            new AcceptanceAssignmentRequest(
                                PurchaseOrderCodes.ResponsibilityGoodsReceipt, harness.RequesterId, 1, null)
                        ])
                ],
                $"draft-{Guid.NewGuid():N}",
                "Complete the draft before presenting it"),
            harness.BuyerId,
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(2, draft.PurchaseOrderVersionNumber);

        var issuance = CreateIssuanceService(harness, context);
        var presented = await issuance.SubmitAsync(
            new SubmitPurchaseOrderCommand(
                harness.OrganizationId,
                claim.PoId,
                2,
                $"submit-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-submit"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(PurchaseOrderState.PendingApproval, presented.State);
        Assert.Null(presented.ApprovalRef);

        // The adapter published exactly one procurement requirement over the ordered line.
        await using var inspection = harness.CreateContext();
        var caseRow = await inspection.ApprovalCases
            .AsNoTracking()
            .SingleAsync(
                record => record.SubjectType == PurchaseOrderCodes.PurchaseOrderSubjectType &&
                          record.SubjectId == claim.PoId,
                cancellationToken);
        Assert.Equal(PurchaseOrderCodes.IssueOperation, caseRow.Operation);
        var requirement = await inspection.ApprovalRequirements
            .AsNoTracking()
            .SingleAsync(record => record.CaseId == caseRow.Id, cancellationToken);
        Assert.Equal((int)SystemRole.ProcurementApprover, requirement.Role);
        Assert.Contains("\"id\":\"" + harness.LineId.ToString("D") + "\"", requirement.TargetsJson, StringComparison.Ordinal);

        // The approval result of the presented case advances the order to APPROVED.
        var consumer = new PurchaseOrderResultConsumer(
            context, NullLogger<PurchaseOrderResultConsumer>.Instance, "approval-result/v3");
        await consumer.DeliverAsync(
            new Application.Abstractions.ApprovalResultDelivery(
                Guid.NewGuid(),
                "approval-result/v3",
                ApprovalPayload(caseRow.Id, claim.PoId, 3),
                "corr-approval"),
            cancellationToken);

        await using (var approved = harness.CreateContext())
        {
            var version = await approved.PurchaseOrderVersions
                .AsNoTracking()
                .OrderByDescending(record => record.Version)
                .FirstAsync(record => record.PoId == claim.PoId, cancellationToken);
            Assert.Equal((int)PurchaseOrderState.Approved, version.State);
            Assert.NotNull(version.ApprovalDigest);
        }

        var approvedVersion = await RequiredOrderVersionAsync(harness, claim.PoId, cancellationToken);
        var issued = await CreateIssuanceService(harness, harness.CreateContext()).IssueAsync(
            new IssuePurchaseOrderCommand(
                harness.OrganizationId,
                claim.PoId,
                approvedVersion,
                $"issue-{Guid.NewGuid():N}",
                "corr-issue"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(PurchaseOrderState.Issued, issued.State);
        Assert.NotNull(issued.IssuedAt);
        Assert.Equal(2, issued.BudgetOperationRefs.Count);
        Assert.Contains(issued.BudgetOperationRefs, reference => reference.Operation == "COMMIT");
        Assert.Contains(issued.BudgetOperationRefs, reference => reference.Operation == "REVERSE");

        await using var verification = harness.CreateContext();
        var committed = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Committed)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        Assert.Equal(Ordered, committed);
        var reversed = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Reverse)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        Assert.Equal(Reserved - Ordered, reversed);
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == movement.PositionId, cancellationToken);
        Assert.Equal(Ordered, balance.Committed);
        Assert.Equal(0m, balance.Reserved);
        var claimRow = await verification.AwardConsumptionClaims
            .AsNoTracking()
            .SingleAsync(record => record.PoId == claim.PoId, cancellationToken);
        Assert.Equal((int)AwardClaimState.Issued, claimRow.State);
        var projection = await verification.PurchaseRequestLineProjections
            .AsNoTracking()
            .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
        Assert.Equal((int)PurchaseRequestLineProjection.Ordered, projection.Projection);
    }

    [Fact]
    public async Task An_order_without_the_financial_authority_of_the_request_is_not_presented()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        await harness.SeedRequestEvidenceAsync(context, cancellationToken, approved: false);
        var claim = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(claim, harness.BuyerId, cancellationToken);
        await harness.CreateDraftService(context).UpdateDraftAsync(
            new UpdatePurchaseOrderDraftCommand(
                harness.OrganizationId,
                claim.PoId,
                1,
                new DeliveryCommitment(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), "Lima HQ"),
                [
                    new PurchaseOrderLineAssignments(
                        harness.LineId,
                        1,
                        [
                            new AcceptanceAssignmentRequest(
                                PurchaseOrderCodes.ResponsibilityGoodsReceipt, harness.RequesterId, 1, null)
                        ])
                ],
                $"draft-{Guid.NewGuid():N}",
                "Complete the draft before presenting it"),
            harness.BuyerId,
            DateTimeOffset.UtcNow,
            cancellationToken);

        await Assert.ThrowsAnyAsync<DomainException>(() =>
            CreateIssuanceService(harness, harness.CreateContext()).SubmitAsync(
                new SubmitPurchaseOrderCommand(
                    harness.OrganizationId,
                    claim.PoId,
                    2,
                    $"submit-{Guid.NewGuid():N}",
                    harness.BuyerId,
                    "corr-submit"),
                DateTimeOffset.UtcNow,
                cancellationToken));

        await using var verification = harness.CreateContext();
        var version = await verification.PurchaseOrderVersions
            .AsNoTracking()
            .OrderByDescending(record => record.Version)
            .FirstAsync(record => record.PoId == claim.PoId, cancellationToken);
        Assert.Equal((int)PurchaseOrderState.Draft, version.State);
    }

    private static async Task<int> RequiredOrderVersionAsync(
        PurchaseOrderHarness harness,
        Guid poId,
        CancellationToken cancellationToken)
    {
        await using var context = harness.CreateContext();
        return await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
    }

    private static string ApprovalPayload(Guid caseId, Guid poId, int poVersion) =>
        $"{{\"contract_version\":\"approval-result/v3\",\"event_id\":\"{Guid.NewGuid():D}\"," +
        $"\"case_id\":\"{caseId:D}\",\"organization_id\":\"{ProcureToPayHarnessId}\"," +
        "\"subject_type\":\"PURCHASE_ORDER\"," +
        $"\"subject_id\":\"{poId:D}\",\"subject_version\":{poVersion}," +
        $"\"target\":{{\"type\":\"PURCHASE_REQUEST_LINE\",\"id\":\"{ProcureToPayLineId}\",\"version\":1," +
        $"\"material_snapshot_digest\":\"{TargetDigest}\"}}," +
        $"\"result_source\":{{\"type\":\"APPROVAL_CASE\",\"id\":\"{caseId:D}\"}}," +
        "\"result\":\"APPROVED\"," +
        $"\"occurred_at\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}\"}}";

    private static string ProcureToPayHarnessId => "aaaaaaaa-1111-1111-1111-111111111111";

    private static string ProcureToPayLineId => "11111111-7777-7777-7777-777777777777";

    private PurchaseOrderIssuanceService CreateIssuanceService(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context)
    {
        var configuration = Configuration();
        var allowlist = new ApprovalWorkloadAllowlist(configuration);
        var adapter = new PurchaseOrderApprovalAdapter(
            context, new PurchaseRequestOrderingEvidenceService(context));
        var submissions = new ApprovalSubmissionService(
            context,
            new ApprovalSubmissionAdapterRegistry([adapter]),
            new ApprovalOwnerWorkloadRegistry(configuration, allowlist),
            allowlist,
            new ApprovalAssignmentEngine(
                context, new OrganizationEligibilityService(context), new ApprovalScopeResolver(context)),
            NullLogger<ApprovalSubmissionService>.Instance);
        var producer = new PurchaseOrderBudgetProducer(
            context,
            new BudgetTransitionService(
                context,
                new BudgetLedgerService(context, new BudgetPersistenceService(context)),
                new BudgetMovementProducerRegistry(configuration),
                new BudgetPersistenceService(context)),
            new ApprovalWorkloadIdentity(
                PurchaseOrderCodes.DomainWorkloadIssuer, PurchaseOrderCodes.DomainWorkloadClientId));
        return new PurchaseOrderIssuanceService(
            context,
            producer,
            new PurchaseRequestLineTakeoverService(context),
            submissions,
            new PurchaseRequestOrderingEvidenceService(context));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Approval:Workloads:0:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Approval:Workloads:0:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId,
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
            // The producer also releases reservations the Purchase Request created, so it is
            // registered for that source too (SPEC 08 REQ-09).
            ["Budget:MovementProducers:2:Operation"] = "REVERSE",
            ["Budget:MovementProducers:2:ContractVersion"] = "v1",
            ["Budget:MovementProducers:2:SourceType"] = "PURCHASE_REQUEST",
            ["Budget:MovementProducers:2:ProducerId"] = PurchaseOrderCodes.DomainProducerId,
            ["Budget:MovementProducers:2:Issuer"] = PurchaseOrderCodes.DomainWorkloadIssuer,
            ["Budget:MovementProducers:2:ClientId"] = PurchaseOrderCodes.DomainWorkloadClientId
        })
        .Build();

}
