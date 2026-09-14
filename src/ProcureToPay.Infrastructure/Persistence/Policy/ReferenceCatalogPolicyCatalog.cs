using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>
/// Policy catalog of the Cost Center reference catalog (SPEC 07 REQ-05): confirms the Cost Center
/// id/version is current and active inside the requesting organization.
/// </summary>
public sealed class CostCenterPolicyReferenceCatalog(ProcureToPayDbContext dbContext) : IPolicyReferenceCatalog
{
    public const string Catalog = "COST_CENTER";
    public const string Version = "cost-center-db/v1";

    string IPolicyReferenceCatalog.CatalogId => Catalog;
    string IPolicyReferenceCatalog.ContractVersion => Version;

    public async Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ReferenceType, Catalog, StringComparison.Ordinal) ||
            reference.OrganizationId == Guid.Empty ||
            reference.Id is not Guid costCenterId ||
            reference.Version is not int version)
        {
            return false;
        }

        var current = await dbContext.CostCenters
            .AsNoTracking()
            .Where(root => root.Id == costCenterId && root.OrganizationId == reference.OrganizationId)
            .Select(root => root.CurrentVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (current != version)
        {
            return false;
        }

        return await dbContext.CostCenterVersions.AnyAsync(
            record => record.CostCenterId == costCenterId &&
                      record.Version == version &&
                      record.OrganizationId == reference.OrganizationId &&
                      record.Status == (int)EntityStatus.Active,
            cancellationToken);
    }
}

/// <summary>
/// Policy catalog of Spend Categories (SPEC 07 REQ-05): confirms code, version and reproducible
/// digest of the current active version inside the requesting organization.
/// </summary>
public sealed class SpendCategoryPolicyReferenceCatalog(ProcureToPayDbContext dbContext) : IPolicyReferenceCatalog
{
    public const string Catalog = "SPEND_CATEGORY";
    public const string Version = "spend-category-db/v1";

    string IPolicyReferenceCatalog.CatalogId => Catalog;
    string IPolicyReferenceCatalog.ContractVersion => Version;

    public async Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ReferenceType, Catalog, StringComparison.Ordinal) ||
            reference.OrganizationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reference.Code) ||
            reference.Version is not int version)
        {
            return false;
        }

        string normalizedCode;
        try
        {
            normalizedCode = ReferenceCatalogCodes.RequireCode(reference.Code, "SpendCategory code");
        }
        catch (DomainValidationException)
        {
            return false;
        }

        var row = await dbContext.SpendCategories
            .AsNoTracking()
            .Where(root => root.OrganizationId == reference.OrganizationId &&
                           root.Code == normalizedCode)
            .Join(
                dbContext.SpendCategoryVersions.AsNoTracking(),
                root => new { root.Id, Version = root.CurrentVersion },
                candidate => new { Id = candidate.SpendCategoryId, candidate.Version },
                (root, candidate) => new
                {
                    candidate.Code,
                    candidate.Name,
                    candidate.Version,
                    candidate.Digest,
                    candidate.Status
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null || row.Version != version || row.Status != (int)EntityStatus.Active)
        {
            return false;
        }

        var digest = reference.Digest is { Length: 64 } candidateDigest
            ? candidateDigest.ToLowerInvariant()
            : null;
        if (digest is null || !string.Equals(digest, row.Digest, StringComparison.Ordinal))
        {
            return false;
        }

        // The stored digest must still reproduce from the stored version itself.
        return ReferenceCatalogCanonicalizer.MatchesSpendCategoryDigest(
            row.Code,
            row.Name,
            reference.OrganizationId,
            row.Version,
            row.Digest);
    }
}
