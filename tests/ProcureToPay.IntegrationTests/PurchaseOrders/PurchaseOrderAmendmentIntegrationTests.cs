using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.IntegrationTests.Sourcing;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-05 (CA-06): an issued Purchase Order only changes through an
/// append-only amendment, a reduction releases just the still-open commitment before the successor
/// version exists, a total cancellation consumes the award claim and returns the lines, and a
/// commercial increase requires a current successor award and commits only its delta.
/// </summary>
public sealed class PurchaseOrderAmendmentIntegrationTests
{
    private const decimal Ordered = 20m;
    private const decimal Reduced = 10m;

    [Fact]
    public async Task A_delivery_change_applies_without_procurement_approval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, _) = await IssueOrderAsync(harness, context, award, cancellationToken);

        var service = harness.CreateAmendmentService(context);
        var amendment = await service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                issued.PurchaseOrderVersionNumber,
                $"amend-{Guid.NewGuid():N}",
                null,
                [],
                [],
                new DeliveryCommitment(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60), "Arequipa DC"),
                "Move the delivery of the order",
                harness.BuyerId,
                "corr-amend"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(AmendmentState.Draft, amendment.State);
        Assert.Empty(amendment.LineDeltas);

        var applied = await service.ApplyAsync(
            new ApplyPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                amendment.Version,
                $"apply-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-apply"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(AmendmentState.Applied, applied.State);
        Assert.Equal(amendment.Version + 2, applied.Version);
        await using var verification = harness.CreateContext();
        var current = await CurrentOrderAsync(verification, poId, cancellationToken);
        Assert.Equal(PurchaseOrderState.Issued, current.State);
        Assert.Equal("Arequipa DC", current.Delivery!.DeliveryLocation);
        Assert.Equal(issued.SourceAmount, current.SourceAmount);
        Assert.Equal(amendment.AmendmentId, current.AmendmentRef!.Id);
        Assert.Equal(issued.BudgetOperationRefs.Count, current.BudgetOperationRefs.Count);
        Assert.False(await verification.ApprovalCases
            .AsNoTracking()
            .AnyAsync(
                record => record.SubjectType == PurchaseOrderCodes.AmendmentSubjectType &&
                          record.SubjectId == amendment.AmendmentId,
                cancellationToken));
        var takeovers = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(record => record.OrganizationId == harness.OrganizationId &&
                             record.ConsumerId == poId &&
                             record.State == (int)TakeoverState.Active)
            .CountAsync(cancellationToken);
        Assert.Equal(1, takeovers);
    }

    [Fact]
    public async Task A_reduction_requires_approval_and_releases_only_the_open_commitment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, positionId) = await IssueOrderAsync(harness, context, award, cancellationToken);
        var line = issued.Lines.Single(candidate => candidate.RequestLineRef.Id == harness.LineId);
        var service = harness.CreateAmendmentService(context);
        var amendment = await service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                issued.PurchaseOrderVersionNumber,
                $"amend-{Guid.NewGuid():N}",
                null,
                [AmendmentLineDelta.Reduce(line, WithQuantity(line, 1m))],
                [],
                null,
                "Reduce the ordered quantity",
                harness.BuyerId,
                "corr-amend"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var presented = await service.SubmitAsync(
            new SubmitPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                amendment.Version,
                $"submit-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-submit"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(AmendmentState.PendingApproval, presented.State);

        // REQ-05: an amendment is approved by the same exactly-one Procurement adapter, over the
        // successor line set of the deltas.
        var caseRow = await context.ApprovalCases
            .AsNoTracking()
            .SingleAsync(
                record => record.SubjectType == PurchaseOrderCodes.AmendmentSubjectType &&
                          record.SubjectId == amendment.AmendmentId,
                cancellationToken);
        Assert.Equal(PurchaseOrderCodes.ApplyAmendmentOperation, caseRow.Operation);
        await DeliverResultAsync(
            harness, context, PurchaseOrderCodes.AmendmentSubjectType, caseRow.Id, amendment.AmendmentId,
            presented.Version, cancellationToken);

        var approved = await service.ReadAmendmentAsync(
            harness.OrganizationId, amendment.AmendmentId, presented.Version + 1, cancellationToken);
        Assert.Equal(AmendmentState.Approved, approved.State);
        Assert.NotNull(approved.ApprovalRef);

        var applied = await service.ApplyAsync(
            new ApplyPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                approved.Version,
                $"apply-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-apply"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(AmendmentState.Applied, applied.State);

        await using var verification = harness.CreateContext();
        var current = await CurrentOrderAsync(verification, poId, cancellationToken);
        Assert.Equal(PurchaseOrderState.Issued, current.State);
        Assert.Equal(Reduced, current.SourceAmount);
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(Reduced, balance.Committed);
        Assert.Equal(0m, balance.Reserved);
        Assert.Equal(PurchaseOrderHarness.Reserved - Reduced, balance.Allocated - balance.Committed);
        var reversed = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Reverse)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        // The issue released the remainder (50 - 20); the reduction returned the committed 10 first
        // to RESERVED and then to AVAILABLE.
        Assert.Equal(30m + Reduced + Reduced, reversed);
        var committed = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Committed)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        Assert.Equal(Ordered, committed);

        // A second amendment of the same base version fails closed: the order already moved on.
        await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                issued.PurchaseOrderVersionNumber,
                $"amend-{Guid.NewGuid():N}",
                null,
                [AmendmentLineDelta.Reduce(line, WithQuantity(line, 1m))],
                [],
                null,
                "Reduce the ordered quantity again",
                harness.BuyerId,
                "corr-amend-again"),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    [Fact]
    public async Task Cancelling_every_line_consumes_the_claim_and_returns_the_lines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, positionId) = await IssueOrderAsync(harness, context, award, cancellationToken);
        var line = issued.Lines.Single(candidate => candidate.RequestLineRef.Id == harness.LineId);

        var service = harness.CreateAmendmentService(context);
        var amendment = await service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                issued.PurchaseOrderVersionNumber,
                $"amend-{Guid.NewGuid():N}",
                null,
                [AmendmentLineDelta.Cancel(line)],
                [],
                null,
                "Cancel the whole order",
                harness.BuyerId,
                "corr-amend"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var presented = await service.SubmitAsync(
            new SubmitPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                amendment.Version,
                $"submit-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-submit"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var caseRow = await context.ApprovalCases
            .AsNoTracking()
            .SingleAsync(
                record => record.SubjectType == PurchaseOrderCodes.AmendmentSubjectType &&
                          record.SubjectId == amendment.AmendmentId,
                cancellationToken);
        await DeliverResultAsync(
            harness, context, PurchaseOrderCodes.AmendmentSubjectType, caseRow.Id, amendment.AmendmentId,
            presented.Version, cancellationToken);
        var approved = await service.ReadAmendmentAsync(
            harness.OrganizationId, amendment.AmendmentId, presented.Version + 1, cancellationToken);
        var applied = await service.ApplyAsync(
            new ApplyPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                approved.Version,
                $"apply-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-apply"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(AmendmentState.Applied, applied.State);

        await using var verification = harness.CreateContext();
        var current = await CurrentOrderAsync(verification, poId, cancellationToken);
        Assert.Equal(PurchaseOrderState.Cancelled, current.State);
        var claim = await verification.AwardConsumptionClaims
            .AsNoTracking()
            .SingleAsync(record => record.PoId == poId, cancellationToken);
        Assert.Equal((int)AwardClaimState.ConsumedCancelled, claim.State);
        Assert.Equal(0, await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(record => record.ConsumerId == poId && record.State == (int)TakeoverState.Active)
            .CountAsync(cancellationToken));
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(0m, balance.Committed);
        Assert.Equal(0m, balance.Reserved);
    }

    [Fact]
    public async Task An_increase_requires_a_successor_award_and_commits_only_its_delta()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, positionId) = await IssueOrderAsync(harness, context, award, cancellationToken);
        var line = issued.Lines.Single(candidate => candidate.RequestLineRef.Id == harness.LineId);

        // A commercial increase without a successor award is refused before any row exists.
        var service = harness.CreateAmendmentService(context);
        await Assert.ThrowsAsync<PurchaseOrderUnprocessableException>(() => service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                issued.PurchaseOrderVersionNumber,
                $"amend-{Guid.NewGuid():N}",
                null,
                [AmendmentLineDelta.Commercial(line, WithQuantity(line, 3m))],
                [],
                null,
                "Grow the order without a successor award",
                harness.BuyerId,
                "corr-amend-invalid"),
            DateTimeOffset.UtcNow,
            cancellationToken));

        // Reduce the order, then publish the successor award of the same process and reserve the
        // additional funds the reevaluated request holds: the increase consumes exactly that delta.
        var reduced = await ApplyWithApprovalAsync(
            harness,
            context,
            poId,
            issued.PurchaseOrderVersionNumber,
            [AmendmentLineDelta.Reduce(line, WithQuantity(line, 1m))],
            cancellationToken);
        Assert.Equal(AmendmentState.Applied, reduced.State);

        var successor = await PublishSuccessorAwardAsync(harness, context, award, cancellationToken);
        await SeedAdditionalReservationAsync(harness, context, positionId, Reduced, cancellationToken);

        var currentVersion = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        var increased = await ApplyWithApprovalAsync(
            harness,
            context,
            poId,
            currentVersion,
            [
                AmendmentLineDelta.Commercial(
                    WithQuantity(line, 1m),
                    line with { })
            ],
            cancellationToken,
            successor);
        Assert.Equal(AmendmentState.Applied, increased.State);

        await using var verification = harness.CreateContext();
        var finalOrder = await CurrentOrderAsync(verification, poId, cancellationToken);
        Assert.Equal(PurchaseOrderState.Issued, finalOrder.State);
        Assert.Equal(Ordered, finalOrder.SourceAmount);
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(Ordered, balance.Committed);
        Assert.Equal(0m, balance.Reserved);
        var increaseRefs = finalOrder.BudgetOperationRefs
            .Where(reference => reference.OperationKey.Contains(increased.AmendmentId.ToString("D"), StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(increaseRefs, reference => reference.Operation == "COMMIT");
    }

    /// <summary>Applies one amendment through the full approval path when it carries line deltas.</summary>
    private static async Task<PurchaseOrderAmendmentVersion> ApplyWithApprovalAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        Guid poId,
        int expectedPoVersion,
        IReadOnlyList<AmendmentLineDelta> deltas,
        CancellationToken cancellationToken,
        PurchaseOrderContentRef? successorAwardRef = null)
    {
        var service = harness.CreateAmendmentService(context);
        var amendment = await service.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                poId,
                expectedPoVersion,
                $"amend-{Guid.NewGuid():N}",
                successorAwardRef,
                deltas,
                [],
                null,
                "Amend the order",
                harness.BuyerId,
                "corr-amend"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var presented = await service.SubmitAsync(
            new SubmitPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                amendment.Version,
                $"submit-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-submit"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var caseRow = await context.ApprovalCases
            .AsNoTracking()
            .Where(record => record.SubjectType == PurchaseOrderCodes.AmendmentSubjectType &&
                             record.SubjectId == amendment.AmendmentId)
            .OrderByDescending(record => record.CreatedAt)
            .FirstAsync(cancellationToken);
        await DeliverResultAsync(
            harness, context, PurchaseOrderCodes.AmendmentSubjectType, caseRow.Id, amendment.AmendmentId,
            presented.Version, cancellationToken);
        var approved = await service.ReadAmendmentAsync(
            harness.OrganizationId, amendment.AmendmentId, presented.Version + 1, cancellationToken);
        return await service.ApplyAsync(
            new ApplyPurchaseOrderAmendmentCommand(
                harness.OrganizationId,
                amendment.AmendmentId,
                approved.Version,
                $"apply-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-apply"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private static async Task<(Guid PoId, PurchaseOrderVersion Issued, Guid PositionId)> IssueOrderAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        SourcingAwardView award,
        CancellationToken cancellationToken)
    {
        await harness.SeedRequestEvidenceAsync(context, cancellationToken);
        var (positionId, _) = await harness.SeedReservationAsync(context, cancellationToken);
        var claim = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(claim, harness.BuyerId, cancellationToken);
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
        var presented = await harness.CreateIssuanceService(context).SubmitAsync(
            new SubmitPurchaseOrderCommand(
                harness.OrganizationId,
                claim.PoId,
                draft.PurchaseOrderVersionNumber,
                $"submit-{Guid.NewGuid():N}",
                harness.BuyerId,
                "corr-submit"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var caseRow = await context.ApprovalCases
            .AsNoTracking()
            .SingleAsync(
                record => record.SubjectType == PurchaseOrderCodes.PurchaseOrderSubjectType &&
                          record.SubjectId == claim.PoId,
                cancellationToken);
        await DeliverResultAsync(
            harness, context, PurchaseOrderCodes.PurchaseOrderSubjectType, caseRow.Id, claim.PoId,
            presented.PurchaseOrderVersionNumber, cancellationToken);
        var approvedVersion = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == claim.PoId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        var issued = await harness.CreateIssuanceService(context).IssueAsync(
            new IssuePurchaseOrderCommand(
                harness.OrganizationId,
                claim.PoId,
                approvedVersion,
                $"issue-{Guid.NewGuid():N}",
                "corr-issue"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return (claim.PoId, issued, positionId);
    }

    private static async Task DeliverResultAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        string subjectType,
        Guid caseId,
        Guid subjectId,
        int subjectVersion,
        CancellationToken cancellationToken)
    {
        var payload =
            $"{{\"contract_version\":\"approval-result/v3\",\"event_id\":\"{Guid.NewGuid():D}\"," +
            $"\"case_id\":\"{caseId:D}\",\"organization_id\":\"{harness.OrganizationId:D}\"," +
            $"\"subject_type\":\"{subjectType}\",\"subject_id\":\"{subjectId:D}\"," +
            $"\"subject_version\":{subjectVersion}," +
            $"\"target\":{{\"type\":\"{PurchaseOrderCodes.ApprovalTargetType}\"," +
            $"\"id\":\"{harness.LineId:D}\",\"version\":1," +
            $"\"material_snapshot_digest\":\"{PurchaseOrderHarness.TargetDigest}\"}}," +
            $"\"result_source\":{{\"type\":\"APPROVAL_CASE\",\"id\":\"{caseId:D}\"}}," +
            "\"result\":\"APPROVED\"," +
            $"\"occurred_at\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}\"}}";
        var consumer = new PurchaseOrderResultConsumer(
            context, NullLogger<PurchaseOrderResultConsumer>.Instance, "approval-result/v3");
        await consumer.DeliverAsync(
            new ApprovalResultDelivery(Guid.NewGuid(), "approval-result/v3", payload, "corr-result"),
            cancellationToken);
    }

    /// <summary>
    /// Seeds the additional reservation a reevaluated request holds for a successor award, so the
    /// increase path consumes a real RESERVED movement instead of a fabricated amount (REQ-05).
    /// </summary>
    private static async Task SeedAdditionalReservationAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        Guid positionId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var operationId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var reservedId = Guid.NewGuid();
        var balance = await context.BudgetBalances.SingleAsync(
            record => record.PositionId == positionId, cancellationToken);
        var nextVersion = balance.BalanceVersion + 1;
        context.BudgetOperations.Add(new BudgetOperationRecord
        {
            Id = operationId,
            OrganizationId = harness.OrganizationId,
            Kind = (int)BudgetOperationKind.ApprovalReserve,
            OperationKey = $"seed-successor-reserve-{operationId:N}",
            Fingerprint = new string('8', 64),
            SourceType = "PURCHASE_REQUEST",
            SourceId = harness.RequestId,
            SourceVersion = 1,
            SourceDigest = new string('7', 64),
            ActorJson = "{\"type\":\"SYSTEM\",\"system_id\":\"BUDGET_OWNER\"}",
            ReasonCode = "PURCHASE_REQUEST_APPROVED",
            Result = "RESERVED",
            OccurredAt = now,
            CorrelationReference = "seed-successor-reserve"
        });
        context.BudgetMovements.Add(new BudgetMovementRecord
        {
            Id = requestedId,
            OperationId = operationId,
            OrganizationId = harness.OrganizationId,
            PositionId = positionId,
            AllocationVersionAtPosting = 1,
            Type = (int)BudgetMovementType.Requested,
            Amount = amount,
            Currency = "PEN",
            TargetId = harness.LineId,
            TargetVersion = 1,
            TargetMaterialSnapshotDigest = PurchaseOrderHarness.TargetDigest,
            TargetType = PurchaseOrderCodes.ApprovalTargetType,
            BalanceVersionAfter = nextVersion,
            OccurredAt = now
        });
        context.BudgetMovements.Add(new BudgetMovementRecord
        {
            Id = reservedId,
            OperationId = operationId,
            OrganizationId = harness.OrganizationId,
            PositionId = positionId,
            AllocationVersionAtPosting = 1,
            Type = (int)BudgetMovementType.Reserved,
            Amount = amount,
            Currency = "PEN",
            ParentMovementId = requestedId,
            TargetId = harness.LineId,
            TargetVersion = 1,
            TargetMaterialSnapshotDigest = PurchaseOrderHarness.TargetDigest,
            TargetType = PurchaseOrderCodes.ApprovalTargetType,
            BalanceVersionAfter = nextVersion,
            AllocatedBefore = balance.Allocated,
            ReservedBefore = balance.Reserved,
            AllocatedAfter = balance.Allocated,
            ReservedAfter = balance.Reserved + amount,
            OccurredAt = now
        });
        balance.Reserved += amount;
        balance.BalanceVersion = nextVersion;
        // The identity trigger of the ledger only admits a one-step balance pointer, so the seeded
        // hold advances both the balance and its position pointer (SPEC 08 REQ-02).
        var position = await context.BudgetPositions.SingleAsync(
            record => record.Id == positionId, cancellationToken);
        position.CurrentBalanceVersion = nextVersion;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<PurchaseOrderVersion> CurrentOrderAsync(
        ProcureToPayDbContext context,
        Guid poId,
        CancellationToken cancellationToken)
    {
        var currentVersion = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        var record = await context.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.PoId == poId && candidate.Version == currentVersion,
                cancellationToken);
        return PurchaseOrderSerialization.ReadDocument(record.DocumentJson, record.ContentDigest);
    }

    [Fact]
    public async Task A_reduction_consumes_the_open_commitment_of_the_whole_lineage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, positionId) = await IssueOrderAsync(harness, context, award, cancellationToken);
        var line = issued.Lines.Single(candidate => candidate.RequestLineRef.Id == harness.LineId);

        // 20 -> 10 (reduction), then 10 -> 20 with a successor award (increase): two open commitments.
        await ApplyWithApprovalAsync(
            harness,
            context,
            poId,
            issued.PurchaseOrderVersionNumber,
            [AmendmentLineDelta.Reduce(line, WithQuantity(line, 1m))],
            cancellationToken);
        var successor = await PublishSuccessorAwardAsync(harness, context, award, cancellationToken);
        await SeedAdditionalReservationAsync(harness, context, positionId, Reduced, cancellationToken);
        var afterIncrease = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        var increased = await ApplyWithApprovalAsync(
            harness,
            context,
            poId,
            afterIncrease,
            [
                AmendmentLineDelta.Commercial(WithQuantity(line, 1m), line)
            ],
            cancellationToken,
            successor);
        Assert.Equal(Ordered, (await CurrentOrderAsync(context, poId, cancellationToken)).SourceAmount);

        // REQ-05: reducing below the original commitment must consume the increase commitment too.
        var reduced = await ApplyWithApprovalAsync(
            harness,
            context,
            poId,
            increased.Version == 0 ? afterIncrease : (await context.PurchaseOrders
                .AsNoTracking()
                .Where(record => record.Id == poId)
                .Select(record => record.CurrentVersion)
                .SingleAsync(cancellationToken)),
            [AmendmentLineDelta.Reduce(line, WithQuantity(line, 0.5m))],
            cancellationToken);
        Assert.Equal(AmendmentState.Applied, reduced.State);

        await using var verification = harness.CreateContext();
        var finalOrder = await CurrentOrderAsync(verification, poId, cancellationToken);
        Assert.Equal(5m, finalOrder.SourceAmount);
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(5m, balance.Committed);
        Assert.Equal(0m, balance.Reserved);
    }

    [Fact]
    public async Task The_creation_atomically_records_its_version_pointer_and_replay_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var (poId, issued, _) = await IssueOrderAsync(harness, context, award, cancellationToken);
        var key = $"amend-{Guid.NewGuid():N}";

        var create = new CreatePurchaseOrderAmendmentCommand(
            harness.OrganizationId,
            poId,
            issued.PurchaseOrderVersionNumber,
            key,
            null,
            [],
            [],
            new DeliveryCommitment(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(45), "Cusco DC"),
            "Move the delivery",
            harness.BuyerId,
            "corr-amend");
        var amendment = await harness.CreateAmendmentService(context).CreateAsync(
            create, DateTimeOffset.UtcNow, cancellationToken);

        // REQ-12: the version, its pointer, the idempotency record and the audit commit together.
        await using (var fresh = harness.CreateContext())
        {
            var command = await fresh.PurchaseOrderCommands
                .AsNoTracking()
                .SingleAsync(record => record.CommandKey == key, cancellationToken);
            Assert.Equal(PurchaseOrderAmendmentService.CreateCommandType, command.CommandType);
            Assert.Equal($"{amendment.AmendmentId:D}:{amendment.Version}", command.ResultRef);
            var root = await fresh.PurchaseOrderAmendmentRoots
                .AsNoTracking()
                .SingleAsync(record => record.Id == amendment.AmendmentId, cancellationToken);
            Assert.Equal(amendment.Version, root.CurrentVersion);
            Assert.Equal(
                1,
                await fresh.PurchaseOrderAuditRecords
                    .AsNoTracking()
                    .CountAsync(
                        record => record.Action == PurchaseOrderCodes.ActionAmendmentCreated &&
                                  record.TargetId == amendment.AmendmentId,
                        cancellationToken));
        }

        // The same preimage replays the recorded version; another one under the same key conflicts.
        var replay = await harness.CreateAmendmentService(context).CreateAsync(
            create, DateTimeOffset.UtcNow, cancellationToken);
        Assert.Equal(amendment.Digest, replay.Digest);
        await Assert.ThrowsAsync<DomainConflictException>(() => harness.CreateAmendmentService(context)
            .CreateAsync(
                create with { Reason = "Another preimage" },
                DateTimeOffset.UtcNow,
                cancellationToken));
    }

    /// <summary>Publishes the successor award of the process and returns its reference (REQ-05).</summary>
    private static async Task<PurchaseOrderContentRef> PublishSuccessorAwardAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        SourcingAwardView award,
        CancellationToken cancellationToken)
    {
        var awardRoot = await context.SourcingAwards
            .AsNoTracking()
            .SingleAsync(record => record.Id == award.AwardId, cancellationToken);
        var successorProposal = await harness.Sourcing.CreateProposalService(context).BuildAsync(
            new BuildProposalCommand(
                harness.OrganizationId, awardRoot.RfqId, harness.SupplierId, $"proposal-{Guid.NewGuid():N}"),
            harness.BuyerId,
            "corr-proposal-successor",
            DateTimeOffset.UtcNow,
            cancellationToken);
        await SourcingScenario.SeedApprovalAsync(
            harness.Sourcing, context, successorProposal, cancellationToken);
        var processVersion = await context.SourcingProcesses
            .AsNoTracking()
            .Where(record => record.Id == awardRoot.ProcessId)
            .Select(record => record.Version)
            .SingleAsync(cancellationToken);
        var successor = await harness.CreateAwardService(context).PublishAsync(
            new PublishAwardCommand(
                harness.OrganizationId,
                successorProposal.ProposalId,
                successorProposal.Version,
                processVersion,
                award.Version,
                $"award-{Guid.NewGuid():N}",
                "Increase the awarded amount"),
            harness.BuyerId,
            "corr-award-successor",
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(award.AwardId, successor.AwardId);
        Assert.Equal(award.Version + 1, successor.Version);
        return new PurchaseOrderContentRef(successor.AwardId, successor.Version, successor.ContentDigest);
    }

    private static PurchaseOrderLine WithQuantity(PurchaseOrderLine line, decimal quantity)
    {
        var gross = PurchaseOrderCodes.Decimal12(quantity * line.UnitPrice);
        return new PurchaseOrderLine(
            line.LineId,
            line.AwardLineRef,
            line.RequestLineRef,
            quantity,
            line.UnitCode,
            line.UnitPrice,
            line.SourceCurrency,
            gross,
            line.BaseCurrency,
            gross,
            gross,
            0m,
            0m,
            0m,
            line.FxSnapshotRef,
            line.AcceptanceResponsibilities);
    }
}
