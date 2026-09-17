using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Read-only queries shared by the quotation service, the evaluation and the owner processors
/// (SPEC 10 REQ-04): the valid count per line and the ordered valid quotes of one line. They never
/// mutate anything and never aggregate across lines, so an insufficient line cannot hide behind a
/// healthy total.
/// </summary>
public sealed class SourcingQuotationQueries(ProcureToPayDbContext dbContext)
{
    public async Task<IReadOnlyDictionary<Guid, int>> CountValidAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentValidAsync(organizationId, rfqId, cancellationToken);
        if (current.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var validIds = current.Select(version => version.QuotationId).ToArray();
        var versionByQuotation = current.ToDictionary(version => version.QuotationId, version => version.Version);
        var suppliers = current.ToDictionary(version => version.QuotationId, version => version.SupplierId);
        var scopes = await dbContext.QuotationLineScopes
            .AsNoTracking()
            .Where(scope => validIds.Contains(scope.QuotationId))
            .Select(scope => new { scope.QuotationId, scope.Version, scope.LineId })
            .ToArrayAsync(cancellationToken);
        return scopes
            .Where(scope => versionByQuotation[scope.QuotationId] == scope.Version)
            .GroupBy(scope => scope.LineId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(scope => suppliers[scope.QuotationId]).Distinct().Count());
    }

    public async Task<IReadOnlyList<SourcingSupplierQuote>> ListValidAsync(
        Guid organizationId,
        Guid rfqId,
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentValidAsync(organizationId, rfqId, cancellationToken);
        var quotes = new List<SourcingSupplierQuote>(current.Count);
        foreach (var version in current)
        {
            var lines = SourcingSerialization.ReadQuotationLines(version.LinesJson);
            if (!lines.Any(line => line.LineRef.Id == lineId))
            {
                continue;
            }

            quotes.Add(new SourcingSupplierQuote(
                version.QuotationId,
                version.SupplierId,
                version.SupplierVersion,
                new SourcingEntityRef(version.SupplierId, version.SupplierVersion),
                version.Version,
                version.Currency,
                SourcingSerialization.ReadTerms(version.TermsJson),
                lines,
                QuotationTimeliness.OnTime,
                version.ContentDigest));
        }

        return quotes;
    }

    /// <summary>
    /// Current versions that are exactly <c>ON_TIME+VALID</c>: late, pending, invalid and withdrawn
    /// versions never participate in a count, a minimum or an evaluation (REQ-04).
    /// </summary>
    private async Task<IReadOnlyList<QuotationVersionRecord>> CurrentValidAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken) =>
        await (
            from quotation in dbContext.Quotations.AsNoTracking()
            join version in dbContext.QuotationVersions.AsNoTracking()
                on new { Id = quotation.Id, Version = quotation.CurrentVersion }
                equals new { Id = version.QuotationId, version.Version }
            where quotation.OrganizationId == organizationId &&
                  quotation.RfqId == rfqId &&
                  version.Timeliness == (int)QuotationTimeliness.OnTime &&
                  version.ReviewStatus == (int)QuotationReviewStatus.Valid
            orderby quotation.SupplierId
            select version).ToArrayAsync(cancellationToken);
}
