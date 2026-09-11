using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>Concrete catalog for versioned organization-owned departments and legal entities.</summary>
public sealed class DatabasePolicyReferenceCatalog(
    ProcureToPayDbContext dbContext,
    string catalogId) : IPolicyReferenceCatalog
{
    public string CatalogId => catalogId;
    public string ContractVersion => "organization-db/v1";

    public async Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(reference.ReferenceType, CatalogId, StringComparison.Ordinal))
        {
            return false;
        }

        return reference.ReferenceType switch
        {
            "DEPARTMENT" => await dbContext.Departments.AnyAsync(item =>
                item.Id == reference.Id && item.Version == reference.Version &&
                item.Status == (int)EntityStatus.Active, cancellationToken),
            "LEGAL_ENTITY" => await dbContext.LegalEntities.AnyAsync(item =>
                item.Id == reference.Id && item.Version == reference.Version &&
                item.Status == (int)EntityStatus.Active, cancellationToken),
            _ => false
        };
    }
}
