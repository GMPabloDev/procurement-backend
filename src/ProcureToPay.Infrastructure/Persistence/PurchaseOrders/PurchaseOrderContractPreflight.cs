using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Startup preflight of the Purchase Orders module (SPEC 11 REQ-10, REQ-12, CA-10). Commands are only
/// safe to enable when every historical request-wide sourcing takeover is represented by its per-line
/// rows: an unexpandable takeover, a missing request version or an incomplete line set means the
/// migration could not preserve the fence, so the host fails closed instead of serving commands.
/// </summary>
public sealed class PurchaseOrderContractPreflight(
    ProcureToPayDbContext dbContext,
    PurchaseOrderCommandGate gate)
{
    public const string SourcingConsumerType = "SOURCING";

    public async Task EnsureTakeoverExpansionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await VerifyAsync(cancellationToken);
            gate.Open();
        }
        catch
        {
            // REQ-10/REQ-12: an unreadable database and an inconsistent fence both keep the commands
            // unavailable; a later successful preflight is what reopens them.
            gate.Close();
            throw;
        }
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var legacy = await dbContext.SourcingTakeovers
            .AsNoTracking()
            .Select(takeover => new
            {
                takeover.Id,
                takeover.OrganizationId,
                takeover.ProcessId,
                takeover.ProcessVersion,
                takeover.RequestId,
                takeover.RequestVersion,
                Released = takeover.ReleasedAt != null
            })
            .ToArrayAsync(cancellationToken);
        if (legacy.Length == 0)
        {
            return;
        }

        var processLines = await dbContext.SourcingProcessLines
            .AsNoTracking()
            .Select(line => new { line.ProcessId, line.LineId, line.LineVersion })
            .ToArrayAsync(cancellationToken);
        var requestVersions = (await dbContext.PurchaseRequestVersions
                .AsNoTracking()
                .Select(version => new { version.RequestId, version.Version })
                .ToArrayAsync(cancellationToken))
            .Select(version => (version.RequestId, version.Version))
            .ToHashSet();
        var expanded = await dbContext.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.ConsumerType == SourcingConsumerType)
            .Select(row => new { row.ConsumerId, row.ConsumerVersion, row.LineId, row.LineVersion })
            .ToArrayAsync(cancellationToken);

        var failures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var takeover in legacy)
        {
            if (!requestVersions.Contains((takeover.RequestId, takeover.RequestVersion)))
            {
                failures.Add($"TAKEOVER_REQUEST_VERSION_MISSING:{takeover.RequestId:D}:{takeover.RequestVersion}");
                continue;
            }

            var lines = processLines.Where(line => line.ProcessId == takeover.ProcessId).ToArray();
            if (lines.Length == 0)
            {
                failures.Add($"TAKEOVER_PROCESS_LINES_MISSING:{takeover.ProcessId:D}");
                continue;
            }

            foreach (var line in lines)
            {
                var present = expanded.Any(row =>
                    row.ConsumerId == takeover.ProcessId &&
                    row.ConsumerVersion == takeover.ProcessVersion &&
                    row.LineId == line.LineId &&
                    row.LineVersion == line.LineVersion);
                if (!present)
                {
                    failures.Add(
                        $"TAKEOVER_LINE_NOT_EXPANDED:{takeover.ProcessId:D}:{line.LineId:D}:{line.LineVersion}");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The legacy sourcing takeover set is not fully expanded to per-line rows: "
                + string.Join(", ", failures.Take(10)));
        }
    }
}
