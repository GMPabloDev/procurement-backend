using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;

namespace ProcureToPay.Infrastructure.Persistence.Budget;

/// <summary>One movement of a workload transition command (SPEC 08 REQ-09).</summary>
public sealed record BudgetTransitionCommandMovement(
    decimal Amount,
    Guid ParentMovementId,
    int ParentMovementVersion,
    BudgetTarget? Target);

/// <summary>The source that triggered a financial transition.</summary>
public sealed record BudgetTransitionSource(string Type, Guid Id, int Version, string Digest);

/// <summary>
/// Workload-only transitions of the budget ledger (SPEC 08 REQ-09, DEC-09): COMMIT, CONSUME and
/// REVERSE. The command is idempotent by key, resolves exactly one registered producer, validates
/// that every parent belongs to the same source and producer, and delegates the atomic posting to
/// the ledger. No administrative role reaches this service.
/// </summary>
public sealed class BudgetTransitionService(
    ProcureToPayDbContext dbContext,
    BudgetLedgerService ledger,
    BudgetMovementProducerRegistry producers,
    BudgetPersistenceService positions)
{
    /// <summary>Applies one transition batch or resolves the recorded outcome on replay.</summary>
    public async Task<BudgetTransitionResult> ApplyAsync(
        Guid organizationId,
        ApprovalWorkloadIdentity workload,
        string operation,
        string operationKey,
        string reasonCode,
        BudgetTransitionSource source,
        IReadOnlyList<BudgetTransitionCommandMovement> movements,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(movements);
        if (movements.Count is < 1 or > BudgetCodes.MaxDemandCount)
        {
            throw new DomainValidationException(
                $"A budget transition accepts between 1 and {BudgetCodes.MaxDemandCount} movements.");
        }

        var producer = producers.ResolveExactlyOne(
            operation, BudgetMovementProducerRegistry.TransitionCommandContractVersion, source.Type, workload);
        var kind = operation switch
        {
            "COMMIT" => BudgetOperationKind.Commit,
            "CONSUME" => BudgetOperationKind.Consume,
            "REVERSE" => BudgetOperationKind.Reverse,
            _ => throw new DomainValidationException("The budget transition operation is not recognized.")
        };
        var actor = BudgetActor.ForWorkload(workload.Issuer, workload.ClientId);
        var normalizedKey = BudgetCodes.RequireKey(operationKey, "operation_key");
        var normalizedReason = BudgetCodes.RequireReasonCode(reasonCode);
        var budgetSource = new BudgetSource(
            BudgetCodes.RequireBoundedToken(source.Type, "Source type"),
            BudgetCodes.RequireIdentity(source.Id, "Source id"),
            source.Version,
            BudgetCodes.RequireDigest(source.Digest, "Source digest"));
        var requiresSourceMatch = string.Equals(operation, "REVERSE", StringComparison.Ordinal);
        var payloads = new Dictionary<BudgetPositionKey, BudgetPositionPayload>();
        var entryList = new List<BudgetOperationMovement>();
        foreach (var movement in movements)
        {
            if (movement.ParentMovementVersion != BudgetCodes.ParentMovementVersion)
            {
                throw new DomainConflictException(
                    "The parent movement version is stale; reload the ledger and retry.");
            }

            var parent = await dbContext.BudgetMovements
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.Id == movement.ParentMovementId, cancellationToken)
                ?? throw new DomainNotFoundException("The parent movement is not visible.");
            if (parent.OrganizationId != organizationId)
            {
                throw new DomainNotFoundException("The parent movement is not visible.");
            }

            if (requiresSourceMatch)
            {
                var parentOperation = await dbContext.BudgetOperations
                    .AsNoTracking()
                    .SingleAsync(record => record.Id == parent.OperationId, cancellationToken);
                if (!string.Equals(parentOperation.SourceType, budgetSource.Type, StringComparison.Ordinal) ||
                    parentOperation.SourceId != budgetSource.Id ||
                    parentOperation.SourceVersion != budgetSource.Version ||
                    !string.Equals(parentOperation.SourceDigest, budgetSource.Digest, StringComparison.Ordinal))
                {
                    throw new DomainForbiddenException(
                        "A REVERSE movement must come from the producer and source of its parent operation.");
                }
            }

            var position = await dbContext.BudgetPositions
                .AsNoTracking()
                .SingleAsync(record => record.Id == parent.PositionId, cancellationToken);
            var payload = await positions.ResolvePositionAsync(
                organizationId,
                position.CostCenterId,
                position.FiscalYear,
                position.SpendCategoryCode,
                await BaseCurrencyAsync(organizationId, cancellationToken),
                cancellationToken);
            payloads[payload.Position] = payload;
            entryList.Add(new BudgetOperationMovement(
                payload.Position,
                new BudgetMovementRequest(
                    MovementTypeFor(kind),
                    BudgetCodes.RequirePositiveAmount(movement.Amount, "Movement amount"),
                    movement.ParentMovementId,
                    movement.Target)));
        }

        var fingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            organizationId,
            actor,
            kind,
            normalizedKey,
            normalizedReason,
            budgetSource,
            entryList.Select(entry => new BudgetMovementCommandItem(
                entry.Movement.Amount, entry.Movement.ParentMovementId, entry.Movement.Target))));
        var outcome = await ledger.ApplyOperationAsync(
            new BudgetOperationSpec(
                kind,
                organizationId,
                normalizedKey,
                fingerprint,
                budgetSource,
                actor,
                normalizedReason,
                operation,
                entryList),
            payloads,
            correlationReference,
            cancellationToken);
        _ = producer;
        return new BudgetTransitionResult(
            outcome.OperationId,
            outcome.Replayed,
            outcome.EvidenceRows
                .Select(row => new BudgetTransitionMovement(
                    row.Id,
                    null,
                    row.PositionKeyDigest,
                    BudgetFingerprints.MovementTypeCode(row.Type),
                    row.Amount))
                .ToArray());
    }

    /// <summary>
    /// Contract movement type of one transition: the posted row records what actually happened, so a
    /// COMMIT posts a COMMITTED movement and a REVERSE posts a REVERSE movement.
    /// </summary>
    private static BudgetMovementType MovementTypeFor(BudgetOperationKind kind) => kind switch
    {
        BudgetOperationKind.Commit => BudgetMovementType.Committed,
        BudgetOperationKind.Consume => BudgetMovementType.Consumed,
        _ => BudgetMovementType.Reverse
    };

    private Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);
}
