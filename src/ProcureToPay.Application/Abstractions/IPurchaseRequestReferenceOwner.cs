using ProcureToPay.Domain.Modules.PurchaseRequests;

namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// Trusted owner of one versioned reference family (REQ-04, DEC-04). Owners answer the exact
/// <c>purchase-request-reference-verification/v1</c> contract in-process; Purchase Requests never
/// becomes the owner of the catalogs it consumes.
/// </summary>
public interface IPurchaseRequestReferenceOwner
{
    /// <summary>Assertion this owner answers: ACTIVE_IN_ORGANIZATION, COST_CENTER_OWNED_BY_DEPARTMENT or FX_ATTESTATION_VALID.</summary>
    string AssertionType { get; }

    /// <summary>Reference family it verifies, or null when it only answers ownership relations.</summary>
    PurchaseRequestReferenceType? ReferenceType { get; }

    string OwnerId { get; }

    string ContractVersion { get; }

    Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Exactly-one owner per assertion and reference family; absence or ambiguity fails closed.</summary>
public interface IPurchaseRequestReferenceOwnerRegistry
{
    IPurchaseRequestReferenceOwner ResolveExactlyOne(
        PurchaseRequestAssertionType assertionType,
        PurchaseRequestReferenceType referenceType);
}
