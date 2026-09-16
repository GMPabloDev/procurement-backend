using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// In-process owner of the approved supplier fact lookup (SPEC 09 REQ-08). It resolves the
/// effective catalogue entry of one line at the frozen instant and returns the two Policy facts,
/// or fails closed when the catalogue data is ambiguous or corrupted.
/// </summary>
public sealed class ApprovedSupplierFactOwner(
    ProcureToPayDbContext dbContext,
    ApprovedSupplierCatalogGovernanceService catalog) : IApprovedSupplierFactOwner
{
    public string OwnerId => SupplierCodes.ApprovedSupplierFactOwnerId;

    public string ContractVersion => SupplierCodes.ApprovedSupplierFactOwnerContractVersion;

    public async Task<SupplierPolicyFactSnapshot> ResolveAsync(
        ApprovedSupplierFactQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.OrganizationId == Guid.Empty)
        {
            throw new DomainValidationException("The approved supplier lookup requires its organization.");
        }

        var versions = query.SupplierRef is null
            ? []
            : await catalog.ListCurrentVersionsAsync(
                query.OrganizationId, query.SupplierRef.Id, query.SpendCategoryRef.Code, cancellationToken);
        var selection = ApprovedSupplierCatalogMatcher.Select(
            query.SupplierRef, query.SpendCategoryRef, query.ProductRef, versions, query.AttestedAt);
        return new SupplierPolicyFactSnapshot(
            query.OrganizationId,
            query.SourceLine,
            query.SupplierRef,
            query.SpendCategoryRef,
            query.ProductRef,
            selection.Entry is null ? null : new ApprovedCatalogEntryRef(selection.Entry.CatalogEntryId, selection.Entry.Version),
            selection.Entry?.ContentDigest,
            selection.PreferredSupplier,
            selection.AgreementStatus,
            query.AttestedAt);
    }
}

/// <summary>
/// In-process owner of the SUPPLIER reference family (SPEC 09 REQ-09). Only the current operational
/// version of an ACTIVE supplier answers ACTIVE; a draft, pending, suspended, blocked or stale
/// version is not usable and never reveals whether it exists.
/// </summary>
public sealed class SupplierReferenceOwner(ProcureToPayDbContext dbContext) : IPurchaseRequestReferenceOwner
{
    public string AssertionType => PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization);

    public PurchaseRequestReferenceType ReferenceType => PurchaseRequestReferenceType.Supplier;

    public string OwnerId => SupplierCodes.SupplierOwnerId;

    public string ContractVersion => SupplierCodes.SupplierOwnerContractVersion;

    public async Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.SourceRef
            ?? throw new DomainValidationException("A verification request requires its source reference.");
        if (source.Type != PurchaseRequestReferenceType.Supplier ||
            source.Kind != PurchaseRequestReferenceKind.Entity ||
            source.Id is not Guid supplierId ||
            source.Version is not int version)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The supplier owner requires a versioned SUPPLIER entity reference.");
        }

        var organizationId = Guid.TryParse(request.OrganizationId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new DomainValidationException("A verification request requires its organization.");
        var root = await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == supplierId && supplier.OrganizationId == organizationId)
            .Select(supplier => new { supplier.OperationalVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (root is null)
        {
            return Response(request, source, PurchaseRequestAssertionCodes.StatusNotFound);
        }

        if (root.OperationalVersion != version)
        {
            // A version that exists but is not the approved pointer is not usable as ACTIVE; the
            // owner answers INACTIVE so the caller distinguishes it from absence.
            var exists = await dbContext.SupplierVersions
                .AsNoTracking()
                .AnyAsync(
                    row => row.SupplierId == supplierId && row.Version == version,
                    cancellationToken);
            return Response(
                request,
                source,
                exists ? PurchaseRequestAssertionCodes.StatusInactive : PurchaseRequestAssertionCodes.StatusNotFound);
        }

        var status = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId && row.Version == version)
            .Select(row => row.Status)
            .SingleAsync(cancellationToken);
        return Response(
            request,
            source,
            SupplierStatusCodes.IsUsable((SupplierOperationalStatus)status)
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
}

/// <summary>
/// In-process Policy catalog of the SUPPLIER reference family (SPEC 09 REQ-09): organization plus
/// UUID and version of the current operational ACTIVE supplier.
/// </summary>
public sealed class SupplierPolicyReferenceCatalog(ProcureToPayDbContext dbContext) : IPolicyReferenceCatalog
{
    public const string Catalog = SupplierCodes.SupplierCatalog;

    string IPolicyReferenceCatalog.CatalogId => Catalog;

    string IPolicyReferenceCatalog.ContractVersion => SupplierCodes.SupplierOwnerContractVersion;

    public async Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ReferenceType, SupplierCodes.SupplierCatalog, StringComparison.Ordinal) ||
            reference.OrganizationId == Guid.Empty ||
            reference.Id is not Guid supplierId ||
            reference.Version is not int version ||
            !string.IsNullOrWhiteSpace(reference.Code) ||
            reference.ValueKind is not null)
        {
            return false;
        }

        var root = await dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == supplierId && supplier.OrganizationId == reference.OrganizationId)
            .Select(supplier => new { supplier.OperationalVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (root?.OperationalVersion != version)
        {
            return false;
        }

        var status = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(row => row.SupplierId == supplierId && row.Version == version)
            .Select(row => row.Status)
            .SingleAsync(cancellationToken);
        return SupplierStatusCodes.IsUsable((SupplierOperationalStatus)status);
    }
}
