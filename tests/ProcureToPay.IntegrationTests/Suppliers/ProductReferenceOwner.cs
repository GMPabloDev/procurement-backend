using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.PurchaseRequests;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>
/// Test-only owner of the optional PRODUCT family. SPEC 09 leaves the product owner on demand, so a
/// deployment without one cannot publish a product-scoped catalog entry; this double simulates the
/// deployment that does have it and answers the frozen verification contract exactly.
/// </summary>
public sealed class ProductReferenceOwner : IPurchaseRequestReferenceOwner
{
    public string AssertionType => PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization);

    public PurchaseRequestReferenceType ReferenceType => PurchaseRequestReferenceType.Product;

    public string OwnerId => "product-domain";

    public string ContractVersion => "product-db/v1";

    public Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.SourceRef
            ?? throw new InvalidOperationException("A verification request requires its source reference.");
        return Task.FromResult(new PurchaseRequestVerificationResponse(
            Active: true,
            request.AssertionType,
            request.OrganizationId,
            ContractVersion,
            OwnerId,
            source,
            PurchaseRequestAssertionCodes.StatusActive,
            request.TargetRef,
            request.VerifiedAt));
    }
}
