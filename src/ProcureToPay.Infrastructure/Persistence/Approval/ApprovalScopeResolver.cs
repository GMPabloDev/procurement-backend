using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Converts the stored <c>decision-scope/v1</c> descriptor into the SPEC 01
/// <see cref="AuthorizationScopeSet"/> used for eligibility (REQ-02, REQ-04, SPEC 07 REQ-05): every
/// LEGAL_ENTITY, DEPARTMENT and COST_CENTER reference must resolve to an active catalog entry whose
/// version matches the version frozen in the requirement. Nothing degrades implicitly.
/// </summary>
public sealed class ApprovalScopeResolver(ProcureToPayDbContext dbContext)
{
    public async Task<AuthorizationScopeSet> ResolveAsync(
        DecisionScopeDescriptor descriptor,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.OrganizationId != organizationId)
        {
            throw new DomainValidationException(
                "The decision scope descriptor belongs to another organization.");
        }

        var scopes = new List<AuthorizationScope>(descriptor.Scopes.Count);
        foreach (var entry in descriptor.Scopes)
        {
            scopes.Add(entry.Dimension switch
            {
                ScopeDimension.Organization => AuthorizationScope.Global(),
                ScopeDimension.LegalEntity => await ResolveLegalEntityAsync(entry, organizationId, cancellationToken),
                ScopeDimension.Department => await ResolveDepartmentAsync(entry, cancellationToken),
                ScopeDimension.CostCenter => await ResolveCostCenterAsync(entry, organizationId, cancellationToken),
                _ => throw new DomainValidationException(
                    "The decision scope dimension is not available in this release.")
            });
        }

        return AuthorizationScopeSet.Create(scopes);
    }

    private async Task<AuthorizationScope> ResolveLegalEntityAsync(
        DecisionScopeEntry entry,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var referenceId = entry.ReferenceId
            ?? throw new DomainValidationException("A legal entity scope requires a reference.");
        var record = await dbContext.LegalEntities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == referenceId && entity.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainValidationException("The legal entity reference does not exist.");
        return ScopeFrom(record.Status, record.Version, entry, ScopeDimension.LegalEntity, record.Code);
    }

    private async Task<AuthorizationScope> ResolveDepartmentAsync(
        DecisionScopeEntry entry,
        CancellationToken cancellationToken)
    {
        var referenceId = entry.ReferenceId
            ?? throw new DomainValidationException("A department scope requires a reference.");
        var record = await dbContext.Departments
            .AsNoTracking()
            .SingleOrDefaultAsync(department => department.Id == referenceId, cancellationToken)
            ?? throw new DomainValidationException("The department reference does not exist.");
        return ScopeFrom(record.Status, record.Version, entry, ScopeDimension.Department, record.Code);
    }

    private async Task<AuthorizationScope> ResolveCostCenterAsync(
        DecisionScopeEntry entry,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var referenceId = entry.ReferenceId
            ?? throw new DomainValidationException("A cost center scope requires a reference.");
        var row = await dbContext.CostCenterVersions
            .AsNoTracking()
            .Where(version => version.CostCenterId == referenceId &&
                              version.OrganizationId == organizationId &&
                              version.Version == entry.ReferenceVersion)
            .Join(
                dbContext.CostCenters.AsNoTracking(),
                version => version.CostCenterId,
                root => root.Id,
                (version, root) => new
                {
                    root.Code,
                    root.CurrentVersion,
                    version.Status,
                    root.OrganizationId
                })
            .SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId, cancellationToken)
            ?? throw new DomainValidationException("The cost center reference does not exist.");
        return ScopeFrom(row.Status, row.CurrentVersion, entry, ScopeDimension.CostCenter, row.Code);
    }

    private static AuthorizationScope ScopeFrom(
        int status,
        int currentVersion,
        DecisionScopeEntry entry,
        ScopeDimension dimension,
        string code)
    {
        if (status != (int)EntityStatus.Active)
        {
            throw new DomainValidationException($"The referenced {dimension} is not active.");
        }

        if (entry.ReferenceVersion is null or < 1 || entry.ReferenceVersion != currentVersion)
        {
            throw new DomainValidationException(
                $"The referenced {dimension} is not at the version frozen in the requirement.");
        }

        return AuthorizationScope.For(dimension, code);
    }
}
