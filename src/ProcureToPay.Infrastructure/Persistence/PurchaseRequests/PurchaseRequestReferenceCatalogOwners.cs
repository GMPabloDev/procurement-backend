using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// In-process owner of the Cost Center reference family (SPEC 07 REQ-06, REQ-07). It answers
/// activity for one explicit slot and the exact Cost Center -> Department relation: only a current
/// active version whose stable <c>department_id</c> matches the submitted department version
/// succeeds. Absence, a stale version, an inactive root or a different relation never degrades to
/// ACTIVE.
/// </summary>
public sealed class CostCenterReferenceOwner(
    ProcureToPayDbContext dbContext,
    PurchaseRequestAssertionType assertionType) : IPurchaseRequestReferenceOwner
{
    public string AssertionType => PurchaseRequestCodes.Code(assertionType);

    public PurchaseRequestReferenceType ReferenceType => PurchaseRequestReferenceType.CostCenter;

    public string OwnerId => ReferenceCatalogCodes.CostCenterOwnerId;

    public string ContractVersion => ReferenceCatalogCodes.CostCenterOwnerContractVersion;

    public async Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.SourceRef
            ?? throw new DomainValidationException("A verification request requires its source reference.");
        if (source.Type != PurchaseRequestReferenceType.CostCenter ||
            source.Kind != PurchaseRequestReferenceKind.Entity ||
            source.Id is not Guid costCenterId ||
            source.Version is not int version)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The cost center owner requires a versioned COST_CENTER entity reference.");
        }

        var organizationId = ParseOrganization(request);
        var root = await dbContext.CostCenters
            .AsNoTracking()
            .Where(record => record.Id == costCenterId && record.OrganizationId == organizationId)
            .Select(record => new { record.CurrentVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (root is null)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        var stored = await dbContext.CostCenterVersions
            .AsNoTracking()
            .Where(record => record.CostCenterId == costCenterId && record.Version == version)
            .Select(record => new { record.Status, record.DepartmentId })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        if (version != root.CurrentVersion || stored.Status != (int)EntityStatus.Active)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusInactive);
        }

        if (assertionType == PurchaseRequestAssertionType.ActiveInOrganization)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusActive);
        }

        var target = request.TargetRef;
        if (target is null ||
            target.Type != PurchaseRequestReferenceType.Department ||
            target.Kind != PurchaseRequestReferenceKind.Entity ||
            target.Id is not Guid departmentId ||
            target.Version is not int departmentVersion ||
            departmentId != stored.DepartmentId)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        var department = await dbContext.Departments
            .AsNoTracking()
            .Where(record => record.Id == departmentId && record.OrganizationId == organizationId)
            .Select(record => new { record.Status, record.Version })
            .SingleOrDefaultAsync(cancellationToken);
        if (department is null || department.Version != departmentVersion)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        return Response(
            request,
            source,
            department.Status == (int)EntityStatus.Active
                ? PurchaseRequestAssertionCodes.StatusActive
                : PurchaseRequestAssertionCodes.StatusInactive);
    }

    private PurchaseRequestVerificationResponse Response(
        PurchaseRequestVerificationRequest request,
        PurchaseRequestAttestedRef source,
        string status) =>
        new(
            status == PurchaseRequestAssertionCodes.StatusActive,
            request.AssertionType,
            request.OrganizationId,
            ContractVersion,
            OwnerId,
            source,
            status,
            request.TargetRef,
            request.VerifiedAt);

    private static Guid ParseOrganization(PurchaseRequestVerificationRequest request) =>
        Guid.TryParse(request.OrganizationId, out var organizationId) && organizationId != Guid.Empty
            ? organizationId
            : throw new DomainValidationException("A verification request requires its organization.");
}

/// <summary>
/// In-process owner of the Spend Category reference family (SPEC 07 REQ-06, REQ-07): the code is
/// the identity and the digest must reproduce the stored <c>spend-category/v1</c> version. A
/// different digest is not the same reference, so it answers NOT_FOUND.
/// </summary>
public sealed class SpendCategoryReferenceOwner(ProcureToPayDbContext dbContext) : IPurchaseRequestReferenceOwner
{
    public string AssertionType => PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization);

    public PurchaseRequestReferenceType ReferenceType => PurchaseRequestReferenceType.SpendCategory;

    public string OwnerId => ReferenceCatalogCodes.SpendCategoryOwnerId;

    public string ContractVersion => ReferenceCatalogCodes.SpendCategoryOwnerContractVersion;

    public async Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.SourceRef
            ?? throw new DomainValidationException("A verification request requires its source reference.");
        if (source.Type != PurchaseRequestReferenceType.SpendCategory ||
            source.Kind != PurchaseRequestReferenceKind.Code ||
            source.Code is null ||
            source.Version is not int version ||
            source.Digest is null)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The spend category owner requires a versioned SPEND_CATEGORY code reference.");
        }

        var organizationId = Guid.TryParse(request.OrganizationId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new DomainValidationException("A verification request requires its organization.");
        var root = await dbContext.SpendCategories
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.Code == source.Code)
            .Select(record => new { record.Id, record.CurrentVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (root is null)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        var stored = await dbContext.SpendCategoryVersions
            .AsNoTracking()
            .Where(record => record.SpendCategoryId == root.Id && record.Version == version)
            .Select(record => new { record.Status, record.Digest })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        if (version != root.CurrentVersion || stored.Status != (int)EntityStatus.Active)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusInactive);
        }

        if (!string.Equals(stored.Digest, source.Digest, StringComparison.OrdinalIgnoreCase))
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        return Response(request, source, PurchaseRequestAssertionCodes.StatusActive);
    }

    private PurchaseRequestVerificationResponse Response(
        PurchaseRequestVerificationRequest request,
        PurchaseRequestAttestedRef source,
        string status) =>
        new(
            status == PurchaseRequestAssertionCodes.StatusActive,
            request.AssertionType,
            request.OrganizationId,
            ContractVersion,
            OwnerId,
            source,
            status,
            request.TargetRef,
            request.VerifiedAt);
}
