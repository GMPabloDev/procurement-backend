using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;

namespace ProcureToPay.IntegrationTests;

/// <summary>
/// Test double of the approved supplier fact lookup that mirrors the one documented outcome of an
/// empty catalogue: a declared supplier without any effective entry is not preferred and has no
/// agreement. It never invents a preferred supplier, a price or an agreement status, so the suites
/// that assert the v1 fact values keep testing the real contract (SPEC 09 REQ-08).
/// </summary>
public sealed class NoApprovedSupplierCatalog : IApprovedSupplierFactOwner
{
    public static NoApprovedSupplierCatalog Instance { get; } = new();

    public string OwnerId => SupplierCodes.ApprovedSupplierFactOwnerId;

    public string ContractVersion => SupplierCodes.ApprovedSupplierFactOwnerContractVersion;

    public Task<SupplierPolicyFactSnapshot> ResolveAsync(
        ApprovedSupplierFactQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(new SupplierPolicyFactSnapshot(
            query.OrganizationId,
            query.SourceLine,
            query.SupplierRef,
            query.SpendCategoryRef,
            query.ProductRef,
            catalogEntryRef: null,
            catalogContentDigest: null,
            preferredSupplier: false,
            ApprovedSupplierCatalogMatcher.AgreementNone,
            query.AttestedAt));
    }
}
