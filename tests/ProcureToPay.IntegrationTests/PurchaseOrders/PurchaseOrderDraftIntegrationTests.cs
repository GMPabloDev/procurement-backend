using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-02 and REQ-06 (CA-02, CA-03): the Buyer completes an incomplete
/// draft through an append-only successor, an inactive or foreign acceptance owner is refused, and a
/// published version whose bytes no longer match its digest is detected instead of trusted.
/// </summary>
public sealed class PurchaseOrderDraftIntegrationTests
{
    [Fact]
    public async Task A_draft_successor_records_delivery_and_acceptance_responsibilities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var request = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(request, harness.BuyerId, cancellationToken);

        var successor = await harness.CreateDraftService(harness.CreateContext()).UpdateDraftAsync(
            new UpdatePurchaseOrderDraftCommand(
                harness.OrganizationId,
                request.PoId,
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

        Assert.Equal(2, successor.PurchaseOrderVersionNumber);
        Assert.Equal(1, successor.PredecessorVersion);
        Assert.NotNull(successor.Delivery);
        Assert.Equal("Lima HQ", successor.Delivery!.DeliveryLocation);
        var line = Assert.Single(successor.Lines);
        var responsibility = Assert.Single(line.AcceptanceResponsibilities);
        Assert.Equal(PurchaseOrderCodes.ResponsibilityGoodsReceipt, responsibility.Kind);
        Assert.Equal(harness.RequesterId, responsibility.Principal.Id);
        Assert.Equal(AcceptanceResponsibilityBuilder.ReasonRequestedFor, responsibility.Reason);

        // Both versions survive: the first is still the incomplete draft the claim created.
        await using var verification = harness.CreateContext();
        var versions = await verification.PurchaseOrderVersions
            .AsNoTracking()
            .Where(record => record.PoId == request.PoId)
            .OrderBy(record => record.Version)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, versions.Length);
        Assert.Equal((int)PurchaseOrderState.Draft, versions[0].State);
        Assert.Null(versions[0].DeliveryJson);
        Assert.Equal(2, await verification.PurchaseOrders
            .Where(order => order.Id == request.PoId)
            .Select(order => order.CurrentVersion)
            .SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task An_inactive_or_foreign_acceptance_owner_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var request = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(request, harness.BuyerId, cancellationToken);

        await using (var deactivate = harness.CreateContext())
        {
            var user = await deactivate.UserProfiles.SingleAsync(
                record => record.Id == harness.Sourcing.AuditorId, cancellationToken);
            user.Status = (int)UserProfileStatus.Inactive;
            user.Version += 1;
            await deactivate.SaveChangesAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<PurchaseOrderUnprocessableException>(() =>
            harness.CreateDraftService(harness.CreateContext()).UpdateDraftAsync(
                Draft(harness, request.PoId, harness.Sourcing.AuditorId, 2, harness.LineId),
                harness.BuyerId,
                DateTimeOffset.UtcNow,
                cancellationToken));

        // A user of another organization is not a candidate either.
        await Assert.ThrowsAsync<PurchaseOrderUnprocessableException>(() =>
            harness.CreateDraftService(harness.CreateContext()).UpdateDraftAsync(
                Draft(harness, request.PoId, Guid.NewGuid(), 1, harness.LineId),
                harness.BuyerId,
                DateTimeOffset.UtcNow,
                cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.Equal(1, await verification.PurchaseOrderVersions
            .CountAsync(record => record.PoId == request.PoId, cancellationToken));
    }

    [Fact]
    public async Task Published_bytes_cannot_be_rewritten_after_the_fact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var award = await harness.PublishAwardAsync(context, cancellationToken);
        var request = harness.ClaimRequest(award, $"claim-{Guid.NewGuid():N}");
        await harness.CreateClaimService(context).ClaimAsync(request, harness.BuyerId, cancellationToken);

        // NFR-01: a direct writer cannot replace the bytes of a published version, so a reader never
        // has to trust a document that no longer matches its digest.
        await using var tampering = harness.CreateContext();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => tampering.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseOrders].[PurchaseOrderVersions] SET [DocumentJson] = {0} WHERE [PoId] = {1}",
            ["{}", request.PoId],
            cancellationToken));
        Assert.Contains("append-only", refused.Message, StringComparison.OrdinalIgnoreCase);

        await using var verification = harness.CreateContext();
        var stored = await verification.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(record => record.PoId == request.PoId, cancellationToken);
        var document = PurchaseOrderSerialization.ReadDocument(stored.DocumentJson, stored.ContentDigest);
        Assert.Equal(stored.ContentDigest, document.Digest);
    }

    private static UpdatePurchaseOrderDraftCommand Draft(
        PurchaseOrderHarness harness,
        Guid poId,
        Guid userId,
        int userVersion,
        Guid lineId) =>
        new(
            harness.OrganizationId,
            poId,
            1,
            new DeliveryCommitment(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), "Lima HQ"),
            [
                new PurchaseOrderLineAssignments(
                    lineId,
                    1,
                    [
                        new AcceptanceAssignmentRequest(
                            PurchaseOrderCodes.ResponsibilityGoodsReceipt, userId, userVersion, "Delegated")
                    ])
            ],
            $"draft-{Guid.NewGuid():N}",
            "Complete the draft before presenting it");
}
