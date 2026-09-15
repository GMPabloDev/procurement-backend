using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;

namespace ProcureToPay.Infrastructure.Persistence.Budget;

/// <summary>Request of one durable release (SPEC 08 REQ-08, <c>budget-release-command/v1</c>).</summary>
public sealed record BudgetReleaseRequest(
    Guid OrganizationId,
    Guid CaseId,
    Guid RequestId,
    int RequestVersion,
    string ReleaseKey,
    string ReasonCode,
    BudgetReleaseTrigger Trigger,
    BudgetTriggerEvent? TriggerEvent,
    IReadOnlyList<BudgetTarget> Targets);

/// <summary>Outcome of one release: the operation id, or null when nothing was left to release.</summary>
public sealed record BudgetReleaseResult(Guid? OperationId, bool Released, bool Replayed);

/// <summary>
/// Durable release of the open reservations of one case and target set (SPEC 08 REQ-08, DEC-07).
/// A released reservation is never rewritten: the release posts REVERSE movements for the remaining
/// amount, so replay, a duplicate event and a crash before confirmation all resolve to the same
/// operation. A reservation already committed or consumed is a downstream takeover and blocks the
/// release with a conflict instead of silently unholding another domain's funds.
/// </summary>
public sealed class BudgetReleaseService(
    ProcureToPayDbContext dbContext,
    BudgetLedgerService ledger,
    BudgetPersistenceService positions)
{
    /// <summary>Reason code published by the Approval-driven releases (REQ-08).</summary>
    public const string ApprovalReleaseReason = "BUDGET_RELEASE";

    /// <summary>Reason code published by the Purchase Request cancellation release (REQ-08).</summary>
    public const string PurchaseRequestReleaseReason = "PR_CANCELLED";

    public async Task<BudgetReleaseResult> ReleaseAsync(
        BudgetReleaseRequest request,
        BudgetActor actor,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Targets);
        var organizationId = BudgetCodes.RequireIdentity(request.OrganizationId, "Organization");
        var caseId = BudgetCodes.RequireIdentity(request.CaseId, "Case");
        var releaseKey = BudgetCodes.RequireKey(request.ReleaseKey, "release_key");
        var fingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.ReleasePreimage(
            organizationId,
            caseId,
            request.RequestId,
            request.RequestVersion,
            releaseKey,
            request.ReasonCode,
            request.Trigger,
            request.TriggerEvent,
            request.Targets));
        // The release digest is the reproducible identity of the command, so the transition source
        // and the operation fingerprint carry the same value (REQ-08).
        var source = new BudgetSource(
            BudgetCodes.PurchaseRequestSourceType,
            BudgetCodes.RequireIdentity(request.RequestId, "Request"),
            request.RequestVersion,
            fingerprint);
        var candidates = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.OrganizationId == organizationId &&
                               movement.Type == (int)BudgetMovementType.Reserved &&
                               movement.TargetId != null &&
                               request.Targets.Select(target => target.Id).Contains(movement.TargetId.Value))
            .OrderBy(movement => movement.Id)
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0)
        {
            return new BudgetReleaseResult(null, Released: false, Replayed: false);
        }

        var reverses = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.OrganizationId == organizationId &&
                               movement.Type == (int)BudgetMovementType.Reverse &&
                               movement.ParentMovementId != null)
            .Select(movement => new { Parent = movement.ParentMovementId!.Value, movement.Amount })
            .ToArrayAsync(cancellationToken);
        var payloads = new Dictionary<BudgetPositionKey, BudgetPositionPayload>();
        var entries = new List<BudgetOperationMovement>();
        foreach (var movement in candidates)
        {
            var remaining = movement.Amount - reverses
                .Where(reverse => reverse.Parent == movement.Id)
                .Sum(reverse => reverse.Amount);
            if (remaining <= 0m)
            {
                continue;
            }

            var state = await LoadPositionAsync(organizationId, movement.PositionId, cancellationToken);
            if (state.TakenOver)
            {
                throw new DomainConflictException(
                    "The reservation was already taken over by a downstream domain and cannot be released.");
            }

            payloads[state.Payload.Position] = state.Payload;
            entries.Add(new BudgetOperationMovement(
                state.Payload.Position,
                new BudgetMovementRequest(
                    BudgetMovementType.Reverse,
                    remaining,
                    movement.Id,
                    movement.TargetId is Guid targetId && movement.TargetVersion is int targetVersion
                        ? new BudgetTarget(
                            targetId,
                            targetVersion,
                            movement.TargetMaterialSnapshotDigest!,
                            movement.TargetType!)
                        : null)));
        }

        if (entries.Count == 0)
        {
            return new BudgetReleaseResult(null, Released: false, Replayed: true);
        }

        // The release key is unique per case scope: an Approval-driven release of one target set and
        // the cancellation release of the same case reuse the same recorded operation.
        var operationKey = $"{releaseKey}:{caseId:D}";
        var outcome = await ledger.ApplyOperationAsync(
            new BudgetOperationSpec(
                BudgetOperationKind.Release,
                organizationId,
                operationKey,
                fingerprint,
                source,
                actor,
                request.ReasonCode,
                "RELEASED",
                entries),
            payloads,
            correlationReference,
            cancellationToken);
        return new BudgetReleaseResult(outcome.OperationId, Released: true, outcome.Replayed);
    }

    /// <summary>
    /// True when the reservation of this position was already advanced by a COMMIT or CONSUME of a
    /// downstream producer; the release then fails closed instead of unholding live funds (REQ-08).
    /// </summary>
    private async Task<(BudgetPositionPayload Payload, bool TakenOver)> LoadPositionAsync(
        Guid organizationId,
        Guid positionId,
        CancellationToken cancellationToken)
    {
        var position = await dbContext.BudgetPositions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == positionId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The budget position of the reservation is not visible.");
        var takenOver = await dbContext.BudgetMovements
            .AsNoTracking()
            .AnyAsync(
                movement => movement.PositionId == positionId &&
                            (movement.Type == (int)BudgetMovementType.Committed ||
                             movement.Type == (int)BudgetMovementType.Consumed),
                cancellationToken);
        _ = positions;
        var baseCurrency = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);
        var payload = await positions.ResolvePositionAsync(
            organizationId,
            position.CostCenterId,
            position.FiscalYear,
            position.SpendCategoryCode,
            baseCurrency,
            cancellationToken);
        return (payload, takenOver);
    }
}
