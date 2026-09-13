using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Fail-closed contract preflight (SPEC 03, SPEC 04 Migración): <c>approval-canonical-json/v2</c> is
/// the only canonicalization, and the outbox may only contain known contract versions. Any other
/// version is a legacy row the application refuses to run: there is no authorized consumer, no
/// dual-write and no rewriting or re-labelling of those rows. Producers of a new version deploy
/// after their consumers (migration order of SPEC 04).
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
            var known = ApprovalOutboxPolicy.KnownContractVersions.ToArray();
            legacyEvents = await dbContext.ApprovalOutboxEvents
                .AsNoTracking()
                .CountAsync(record => !known.Contains(record.ContractVersion), cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            // The Approval schema does not exist yet: an empty baseline has no legacy data.
            logger.LogInformation("Approval schema is not present yet; the contract preflight finds no legacy events.");
            return;
        }

        if (legacyEvents > 0)
        {
            throw new InvalidOperationException(
                "Approval contract preflight failed: unknown or legacy approval event contract versions exist. " +
                "Restore a clean baseline; legacy rows are never rewritten or dispatched.");
        }

        logger.LogInformation("Approval contract preflight passed: every outbox event uses a known contract version.");
    }
}
