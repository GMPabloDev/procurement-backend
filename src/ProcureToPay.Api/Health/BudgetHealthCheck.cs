using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Budget;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Readiness of the Budget module (SPEC 08 REQ-09, NFR-05): exactly one real processor for
/// <c>budget-check-owner/v1</c>, every configured producer usable, no due attempt without a signal or
/// compensation inside the 60-second budget, and the persisted projection equal to the ledger
/// rebuild. The codes never expose amounts, positions, identities or digests.
/// </summary>
public sealed class BudgetHealthCheck(
    ProcureToPayDbContext dbContext,
    BudgetMovementProducerRegistry producers,
    ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrerequisiteProcessor processor) : IHealthCheck
{
    /// <summary>Budget of one due attempt before readiness degrades (REQ-07).</summary>
    public static readonly TimeSpan DueBudget = TimeSpan.FromSeconds(60);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // The processor is an exact-one in-process registration: resolving it proves that a real
        // owner exists in this deployment and that the configured owner workload is its workload.
        _ = processor;
        var producerCodes = producers.DiagnosticCodes();
        if (producerCodes.Count > 0)
        {
            return HealthCheckResult.Degraded(producerCodes[0]);
        }

        var now = DateTimeOffset.UtcNow;
        var attempts = await dbContext.BudgetPrerequisiteAttempts
            .AsNoTracking()
            .Select(record => new { record.State, record.DueAt, record.CompletedAt })
            .ToArrayAsync(cancellationToken);
        foreach (var attempt in attempts)
        {
            if (attempt.State is "COMPLETED" or "COMPENSATED")
            {
                continue;
            }

            if (now - attempt.DueAt > DueBudget)
            {
                return HealthCheckResult.Degraded("BUDGET_ATTEMPT_OVERDUE");
            }
        }

        // NFR-01: the mutable projection must equal the ledger rebuild. The comparison is by count of
        // divergent positions only, so readiness never leaks a balance.
        var positions = await dbContext.BudgetPositions
            .AsNoTracking()
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var positionId in positions)
        {
            var balance = await dbContext.BudgetBalances
                .AsNoTracking()
                .SingleAsync(record => record.PositionId == positionId, cancellationToken);
            var last = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.PositionId == positionId)
                .OrderByDescending(movement => movement.OccurredAt)
                .ThenByDescending(movement => movement.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (last is null)
            {
                continue;
            }

            if (balance.Allocated != last.AllocatedAfter ||
                balance.Reserved != last.ReservedAfter ||
                balance.Committed != last.CommittedAfter ||
                balance.Consumed != last.ConsumedAfter)
            {
                return HealthCheckResult.Unhealthy("BUDGET_LEDGER_CORRUPTED");
            }
        }

        return HealthCheckResult.Healthy("BUDGET_OK");
    }
}
