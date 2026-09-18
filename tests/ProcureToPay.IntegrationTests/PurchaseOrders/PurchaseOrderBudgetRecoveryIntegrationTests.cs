using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-04/REQ-12 (CA-05, CA-10): the documented recovery of an
/// interrupted issue. A crash between the confirmed <c>COMMIT</c> and its durable checkpoint is
/// recovered by re-running the same keys, without a second movement, without a second order version
/// and without editing a single row by hand.
/// </summary>
public sealed class PurchaseOrderBudgetRecoveryIntegrationTests
{
    [Fact]
    public async Task An_interrupted_commit_checkpoint_is_recovered_with_the_same_ids()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        await harness.SeedRequestEvidenceAsync(context, cancellationToken);
        await harness.SeedReservationAsync(context, cancellationToken);
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
        var payload =
            $"{{\"contract_version\":\"approval-result/v3\",\"event_id\":\"{Guid.NewGuid():D}\"," +
            $"\"case_id\":\"{caseRow.Id:D}\",\"organization_id\":\"{harness.OrganizationId:D}\"," +
            $"\"subject_type\":\"{PurchaseOrderCodes.PurchaseOrderSubjectType}\",\"subject_id\":\"{claim.PoId:D}\"," +
            $"\"subject_version\":{presented.PurchaseOrderVersionNumber}," +
            $"\"target\":{{\"type\":\"{PurchaseOrderCodes.ApprovalTargetType}\",\"id\":\"{harness.LineId:D}\"," +
            $"\"version\":1,\"material_snapshot_digest\":\"{PurchaseOrderHarness.TargetDigest}\"}}," +
            $"\"result_source\":{{\"type\":\"APPROVAL_CASE\",\"id\":\"{caseRow.Id:D}\"}}," +
            "\"result\":\"APPROVED\"," +
            $"\"occurred_at\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}\"}}";
        await new PurchaseOrderResultConsumer(
            context, NullLogger<PurchaseOrderResultConsumer>.Instance, "approval-result/v3").DeliverAsync(
            new Application.Abstractions.ApprovalResultDelivery(
                Guid.NewGuid(), "approval-result/v3", payload, "corr-result"),
            cancellationToken);
        var approvedVersion = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == claim.PoId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        var issueKey = $"issue-{Guid.NewGuid():N}";
        var issued = await harness.CreateIssuanceService(context).IssueAsync(
            new IssuePurchaseOrderCommand(
                harness.OrganizationId, claim.PoId, approvedVersion, issueKey, "corr-issue"),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(PurchaseOrderState.Issued, issued.State);

        var committed = await context.BudgetMovements
            .AsNoTracking()
            .CountAsync(record => record.Type == (int)ProcureToPay.Domain.Modules.Budget.BudgetMovementType.Committed,
                cancellationToken);
        var attempt = await context.PurchaseOrderBudgetAttempts
            .AsNoTracking()
            .SingleAsync(record => record.PoId == claim.PoId, cancellationToken);
        Assert.Equal((int)PurchaseOrderBudgetAttemptState.Completed, attempt.State);
        Assert.NotNull(attempt.MovementRefsJson);

        // The documented crash: the COMMIT was confirmed but its durable checkpoint was lost.
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseOrders].[PurchaseOrderBudgetAttempts] SET [State] = 2, [MovementRefsJson] = NULL, " +
            "[CompletedAt] = NULL WHERE [Id] = {0}",
            [attempt.Id],
            cancellationToken);
        var approvedRecord = await context.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.PoId == claim.PoId && record.Version == approvedVersion,
                cancellationToken);
        var approved = PurchaseOrderSerialization.ReadDocument(
            approvedRecord.DocumentJson, approvedRecord.ContentDigest);

        // REQ-04: the recovery reuses the same keys and resolves to the same recorded effect.
        var recovered = await harness.CreateBudgetProducer(context).CommitAsync(
            harness.OrganizationId,
            claim.PoId,
            approvedVersion,
            approvedRecord.ContentDigest,
            approved,
            issueKey,
            "corr-recover",
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.True(recovered.Replayed);
        Assert.Equal(committed, await context.BudgetMovements
            .AsNoTracking()
            .CountAsync(record => record.Type == (int)ProcureToPay.Domain.Modules.Budget.BudgetMovementType.Committed,
                cancellationToken));
        var recoveredAttempt = await context.PurchaseOrderBudgetAttempts
            .AsNoTracking()
            .SingleAsync(record => record.Id == attempt.Id, cancellationToken);
        Assert.Equal((int)PurchaseOrderBudgetAttemptState.Completed, recoveredAttempt.State);
        Assert.Equal(attempt.OperationId, recoveredAttempt.OperationId);
        Assert.Equal(attempt.MovementRefsJson, recoveredAttempt.MovementRefsJson);
        var current = await context.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == claim.PoId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        Assert.Equal(issued.PurchaseOrderVersionNumber, current);
        _ = award;
    }
}
