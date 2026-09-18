using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Aggregate of the line projections of one Purchase Request version (SPEC 11 REQ-10): every line
/// ordered, every line authorized as a Direct Purchase, or a subset/mixed set. The per-line rows stay
/// the source of truth; this read never invents a projection for a line without a consumer.
/// </summary>
public sealed record PurchaseRequestOrderingView(
    Guid RequestId,
    int RequestVersion,
    int LineCount,
    int OrderedLines,
    int DirectPurchaseLines,
    string? Summary);

/// <summary>
/// Read model of the Purchase Request ordering projection (REQ-10, REQ-11). It is derived from the
/// append-only per-line rows and the attested line set of the request version, so a subset or a mixed
/// route reports <c>PARTIALLY_ORDERED</c> instead of claiming a complete route.
/// </summary>
public sealed class PurchaseRequestOrderingQuery(ProcureToPayDbContext dbContext)
{
    public const string Ordered = "ORDERED";
    public const string DirectPurchaseAuthorized = "DIRECT_PURCHASE_AUTHORIZED";
    public const string PartiallyOrdered = "PARTIALLY_ORDERED";

    public async Task<PurchaseRequestOrderingView> SummarizeAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken = default)
    {
        var manifest = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(record => record.RequestId == requestId && record.RequestVersion == requestVersion)
            .Select(record => new { record.LineId, record.LineVersion })
            .ToArrayAsync(cancellationToken);
        if (manifest.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The projected request version carries no attested line set.");
        }

        var projections = await dbContext.PurchaseRequestLineProjections
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.RequestId == requestId &&
                             record.RequestVersion == requestVersion)
            .ToArrayAsync(cancellationToken);
        var byLine = projections
            .GroupBy(record => (record.LineId, record.LineVersion))
            .ToDictionary(group => group.Key, group => group.Last());
        var ordered = 0;
        var direct = 0;
        var projected = 0;
        foreach (var line in manifest)
        {
            if (!byLine.TryGetValue((line.LineId, line.LineVersion), out var row))
            {
                continue;
            }

            projected++;
            switch ((PurchaseRequestLineProjection)row.Projection)
            {
                case PurchaseRequestLineProjection.Ordered:
                    ordered++;
                    break;
                case PurchaseRequestLineProjection.DirectPurchaseAuthorized:
                    direct++;
                    break;
                default:
                    throw new PurchaseOrderDependencyUnavailableException(
                        "The stored line projection is not recognized.");
            }
        }

        var summary = projected == 0
            ? null
            : projected == manifest.Length && ordered == manifest.Length
                ? Ordered
                : projected == manifest.Length && direct == manifest.Length
                    ? DirectPurchaseAuthorized
                    : PartiallyOrdered;
        return new PurchaseRequestOrderingView(
            requestId, requestVersion, manifest.Length, ordered, direct, summary);
    }
}
