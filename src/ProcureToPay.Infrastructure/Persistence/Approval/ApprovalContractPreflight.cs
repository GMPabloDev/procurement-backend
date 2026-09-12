using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Fail-closed contract preflight (SPEC 03, Migración): <c>approval-canonical-json/v2</c> and
/// <c>approval-result/v2</c> are the first publishable versions, so v2 must only start from a
/// clean baseline. If any v1 event exists in any state the application refuses to run: there is no
/// authorized v1 consumer, no dual-write and no rewriting or re-labelling of those rows.
/// </summary>
public sealed class ApprovalContractPreflight(
    ProcureToPayDbContext dbContext,
    ILogger<ApprovalContractPreflight> logger)
{
    public async Task EnsureV2BaselineAsync(CancellationToken cancellationToken = default)
    {
        int legacyEvents;
        try
        {
            legacyEvents = await dbContext.ApprovalOutboxEvents
                .AsNoTracking()
                .CountAsync(
                    record => record.ContractVersion != ApprovalOutboxPolicy.ContractVersion,
                    cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            // The Approval schema does not exist yet: an empty baseline has no v1 data.
            logger.LogInformation("Approval schema is not present yet; the v2 preflight finds no legacy events.");
            return;
        }

        if (legacyEvents > 0)
        {
            throw new InvalidOperationException(
                "Approval v2 preflight failed: legacy approval-result events exist. " +
                "Restore a clean pre-Approval baseline; v1 rows are never rewritten or dispatched.");
        }

        logger.LogInformation("Approval v2 preflight passed: no legacy approval-result events exist.");
    }
}
