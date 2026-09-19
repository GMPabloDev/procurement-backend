using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Budget;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

namespace ProcureToPay.IntegrationTests.PurchaseOrders;

/// <summary>
/// SQL Server evidence of SPEC 11 REQ-07 (CA-07): a Direct Purchase is authorized only when the
/// current policy evaluation routes every covered line to <c>ALLOW_DIRECT_PURCHASE</c>, it keeps the
/// reservation <c>RESERVED</c> without posting a <c>COMMIT</c>, and cancelling it releases the hold
/// through the <c>DIRECT_PURCHASE_CANCELLED</c> trigger and returns the line takeovers.
/// </summary>
public sealed class DirectPurchaseIntegrationTests
{
    [Fact]
    public async Task Authorizing_a_direct_purchase_keeps_the_reservation_and_takes_the_lines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (positionId, _) = await SeedAsync(harness, context, cancellationToken);

        var authorization = await CreateService(harness, context).AuthorizeAsync(
            Command(harness, $"dp-{Guid.NewGuid():N}"),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(DirectPurchaseState.Authorized, authorization.State);
        Assert.Equal(1, authorization.Version);
        Assert.Equal(1_000m, authorization.MaximumSourceAmount);
        Assert.Equal(1_000m, authorization.MaximumBaseAmount);
        await using var verification = harness.CreateContext();
        Assert.True(await verification.DirectPurchaseAuthorizations
            .AsNoTracking()
            .AnyAsync(record => record.Id == authorization.AuthorizationId, cancellationToken));
        var takeover = await verification.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .SingleAsync(
                record => record.LineId == harness.LineId && record.State == (int)TakeoverState.Active,
                cancellationToken);
        Assert.Equal((int)PurchaseRequestLineOwner.DirectPurchase, takeover.Owner);
        Assert.Equal(PurchaseOrderCodes.TakeoverConsumerDirectPurchase, takeover.ConsumerType);
        Assert.Equal(authorization.AuthorizationId, takeover.ConsumerId);
        var projection = await verification.PurchaseRequestLineProjections
            .AsNoTracking()
            .SingleAsync(record => record.LineId == harness.LineId, cancellationToken);
        Assert.Equal((int)PurchaseRequestLineProjection.DirectPurchaseAuthorized, projection.Projection);

        // REQ-07/DEC-05: a Direct Purchase never posts a COMMIT; the reservation stays held.
        var committed = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Committed)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        Assert.Equal(0m, committed);
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(PurchaseOrderHarness.Reserved, balance.Reserved);
        Assert.Equal(0m, balance.Committed);
    }

    [Fact]
    public async Task A_replay_returns_the_authorization_and_a_different_preimage_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await SeedAsync(harness, context, cancellationToken);
        var service = CreateService(harness, context);
        var key = $"dp-{Guid.NewGuid():N}";

        var first = await service.AuthorizeAsync(Command(harness, key), DateTimeOffset.UtcNow, cancellationToken);
        var replay = await service.AuthorizeAsync(Command(harness, key), DateTimeOffset.UtcNow, cancellationToken);

        Assert.Equal(first.AuthorizationId, replay.AuthorizationId);
        Assert.Equal(first.Digest, replay.Digest);
        await using var verification = harness.CreateContext();
        Assert.Equal(
            1,
            await verification.DirectPurchaseAuthorizations.AsNoTracking().CountAsync(cancellationToken));

        // REQ-07: another preimage under the same key is a conflict, never a second authorization.
        await Assert.ThrowsAsync<DomainConflictException>(() => service.AuthorizeAsync(
            Command(harness, key, [harness.LineId, harness.OtherLineId]),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    [Fact]
    public async Task A_route_without_the_direct_purchase_effect_does_not_authorize()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        await SeedAsync(harness, context, cancellationToken, allowDirectPurchase: false);

        await Assert.ThrowsAsync<PurchaseOrderUnprocessableException>(() => CreateService(harness, context)
            .AuthorizeAsync(Command(harness, $"dp-{Guid.NewGuid():N}"), DateTimeOffset.UtcNow, cancellationToken));

        await using var verification = harness.CreateContext();
        Assert.Equal(
            0,
            await verification.DirectPurchaseAuthorizations.AsNoTracking().CountAsync(cancellationToken));
        Assert.Equal(
            0,
            await verification.PurchaseRequestLineTakeovers
                .AsNoTracking()
                .CountAsync(record => record.State == (int)TakeoverState.Active, cancellationToken));
    }

    [Fact]
    public async Task Cancelling_a_direct_purchase_releases_the_hold_and_the_takeover()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await PurchaseOrderHarness.StartAsync(cancellationToken);
        await using var context = harness.CreateContext();
        var (positionId, _) = await SeedAsync(harness, context, cancellationToken);
        var service = CreateService(harness, context);
        var authorization = await service.AuthorizeAsync(
            Command(harness, $"dp-{Guid.NewGuid():N}"), DateTimeOffset.UtcNow, cancellationToken);

        var cancelled = await service.CancelAsync(
            new CancelDirectPurchaseAuthorizationCommand(
                harness.OrganizationId,
                authorization.AuthorizationId,
                authorization.Version,
                $"dp-cancel-{Guid.NewGuid():N}",
                "The requester no longer needs the purchase",
                harness.RequesterId),
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.Equal(DirectPurchaseState.Cancelled, cancelled.State);
        Assert.Equal(2, cancelled.Version);
        await using var verification = harness.CreateContext();
        var balance = await verification.BudgetBalances
            .AsNoTracking()
            .SingleAsync(record => record.PositionId == positionId, cancellationToken);
        Assert.Equal(0m, balance.Reserved);
        Assert.Equal(0m, balance.Committed);
        var released = await verification.BudgetMovements
            .AsNoTracking()
            .Where(record => record.Type == (int)BudgetMovementType.Reverse)
            .SumAsync(record => (decimal?)record.Amount, cancellationToken);
        Assert.Equal(PurchaseOrderHarness.Reserved, released);
        var release = await verification.BudgetOperations
            .AsNoTracking()
            .SingleAsync(record => record.ReasonCode == "DIRECT_PURCHASE_CANCELLED", cancellationToken);
        Assert.Equal("RELEASED", release.Result);
        Assert.Equal(
            0,
            await verification.PurchaseRequestLineTakeovers
                .AsNoTracking()
                .CountAsync(record => record.State == (int)TakeoverState.Active, cancellationToken));

        // The cancellation replay is idempotent by command key.
        var replay = await service.CancelAsync(
            new CancelDirectPurchaseAuthorizationCommand(
                harness.OrganizationId,
                authorization.AuthorizationId,
                cancelled.Version,
                $"dp-cancel-{Guid.NewGuid():N}",
                "The requester no longer needs the purchase",
                harness.RequesterId),
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.Equal(DirectPurchaseState.Cancelled, replay.State);
    }

    private static CreateDirectPurchaseAuthorizationCommand Command(
        PurchaseOrderHarness harness,
        string key,
        IReadOnlyList<Guid>? lines = null) =>
        new(
            harness.OrganizationId,
            harness.RequestId,
            1,
            key,
            harness.RequesterId,
            (lines ?? [harness.LineId])
                .Select(lineId => new OrderingEvidenceTargetRef(lineId, 1))
                .ToArray(),
            [
                new DirectPurchaseAssignmentRequest(
                    harness.LineId,
                    1,
                    PurchaseOrderCodes.ResponsibilityGoodsReceipt,
                    harness.RequesterId,
                    1,
                    null)
            ]);

    private static DirectPurchaseService CreateService(PurchaseOrderHarness harness, ProcureToPayDbContext context)
    {
        var positions = new BudgetPersistenceService(context);
        return new DirectPurchaseService(
            context,
            new PurchaseRequestOrderingEvidenceService(context),
            new PurchaseRequestLineTakeoverService(context),
            new BudgetReleaseService(
                context, new BudgetLedgerService(context, positions), positions));
    }

    /// <summary>
    /// Seeds the approved request with its financial case, the attested supplier and the published
    /// policy route of the covered line, plus the reservation the authorization never commits (REQ-07).
    /// </summary>
    private static async Task<(Guid PositionId, Guid ReservedMovementId)> SeedAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        CancellationToken cancellationToken,
        bool allowDirectPurchase = true,
        bool withReservation = true)
    {
        await harness.Sourcing.SeedProposalFixturesAsync(context, cancellationToken);
        await harness.SeedRequestEvidenceAsync(context, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseRequest].[PurchaseRequests] SET [Status] = {0} WHERE [Id] = {1}",
            [(int)PurchaseRequestStatus.Approved, harness.RequestId],
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseRequest].[PurchaseRequestLineVersions] SET [SupplierJson] = {0} " +
            "WHERE [LineId] = {1} AND [LineVersion] = 1",
            [$"{{\"id\":\"{harness.SupplierId:D}\",\"version\":1}}", harness.LineId],
            cancellationToken);
        var bundleId = await AppendRouteBundleAsync(harness, context, allowDirectPurchase, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE [PurchaseRequest].[PurchaseRequestSubmissionAttempts] SET [PolicyEvaluationBundleId] = {0} " +
            "WHERE [RequestId] = {1} AND [RequestVersion] = 1",
            [bundleId, harness.RequestId],
            cancellationToken);
        return withReservation
            ? await harness.SeedReservationAsync(context, cancellationToken)
            : (Guid.Empty, Guid.Empty);
    }

    /// <summary>
    /// Publishes a real policy evaluation through the production persistence service: either the
    /// <c>ALLOW_DIRECT_PURCHASE</c> route of the covered line or the <c>REQUIRE_PO</c> control that
    /// must block the authorization (REQ-07).
    /// </summary>
    private static async Task<Guid> AppendRouteBundleAsync(
        PurchaseOrderHarness harness,
        ProcureToPayDbContext context,
        bool allowDirectPurchase,
        CancellationToken cancellationToken)
    {
        var control = new PolicyGeneratedControl(
            allowDirectPurchase ? "DIRECT_PURCHASE_ROUTE" : "PURCHASE_ORDER_REQUIRED",
            allowDirectPurchase ? PolicyEffectType.AllowDirectPurchase : PolicyEffectType.RequirePo,
            ImmutableHashSet.Create(PolicyScope.Line),
            ImmutableHashSet.Create(harness.LineId),
            "PRE_PROCUREMENT",
            null,
            null,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("DP-ROUTE"),
            "Direct Purchase route seed");
        var scope = new PolicyScopeEvaluation(
            PolicyScope.Line,
            ImmutableHashSet.Create(harness.LineId),
            ["DP-ROUTE"],
            [control],
            PolicyResult.RequirementsGenerated);
        var bundle = new PolicyEvaluationBundle(
            Guid.NewGuid(),
            $"dp-route-{Guid.NewGuid():N}",
            new PolicySubjectReference(harness.RequestId, 1),
            DateTimeOffset.UtcNow,
            Digest('c'),
            Digest('d'),
            [scope],
            [control],
            PolicyResult.RequirementsGenerated,
            Digest('e'))
        {
            Operation = "REQUEST_EVALUATE",
            FactsDigest = Digest('f'),
            ManifestDigest = Digest('a'),
            MaterialProjection = new PolicyMaterialProjection(
                "policy-material-projection/v1",
                new Dictionary<string, string>(StringComparer.Ordinal),
                [new PolicyMaterialTarget(harness.LineId, 1, PurchaseOrderHarness.TargetDigest)])
        };
        await new PolicyPersistenceService(context).AppendEvaluationAsync(
            bundle,
            new PolicyEvaluationCaller(
                harness.OrganizationId,
                "internal://procure-to-pay",
                "purchase-request-domain",
                "REQUEST_EVALUATE",
                bundle.EvaluationKey,
                harness.Sourcing.PolicySetVersionId,
                $"dp-route-{Guid.NewGuid():N}"),
            cancellationToken);
        return bundle.Id;
    }

    private static string Digest(char value) => new(value, 64);
}
