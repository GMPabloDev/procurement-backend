using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>
/// Concrete catalog for versioned organization-owned departments and legal entities. Every lookup
/// is bound to the requesting organization (SPEC 07 REQ-05): a reference of another organization is
/// never visible to Policy.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ReferenceType, CatalogId, StringComparison.Ordinal) ||
            reference.OrganizationId == Guid.Empty ||
            reference.Id is not Guid entityId ||
            reference.Version is not int entityVersion ||
            reference.Code is not null ||
            reference.Digest is not null ||
            reference.ValueKind is not null)
        {
            return false;
        }

        return reference.ReferenceType switch
        {
            "DEPARTMENT" => await dbContext.Departments.AnyAsync(item =>
                item.Id == entityId && item.OrganizationId == reference.OrganizationId &&
                item.Version == entityVersion && item.Status == (int)EntityStatus.Active,
                cancellationToken),
            "LEGAL_ENTITY" => await dbContext.LegalEntities.AnyAsync(item =>
                item.Id == entityId && item.OrganizationId == reference.OrganizationId &&
                item.Version == entityVersion && item.Status == (int)EntityStatus.Active,
                cancellationToken),
            _ => false
        };
    }
}
