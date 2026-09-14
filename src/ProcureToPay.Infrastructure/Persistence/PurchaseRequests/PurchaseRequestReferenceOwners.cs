using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Exactly-one owner resolution (REQ-04): a missing or ambiguous registration throws the
/// dependency-unavailable condition that the API projects as 503, before any partial artifact is
/// written.
/// </summary>
public sealed class PurchaseRequestReferenceOwnerRegistry(
    IEnumerable<IPurchaseRequestReferenceOwner> owners) : IPurchaseRequestReferenceOwnerRegistry
{
    private readonly IReadOnlyList<IPurchaseRequestReferenceOwner> registered = owners.ToArray();

    public IPurchaseRequestReferenceOwner ResolveExactlyOne(
        PurchaseRequestAssertionType assertionType,
        PurchaseRequestReferenceType referenceType)
    {
        var assertionCode = PurchaseRequestCodes.Code(assertionType);
        var matches = registered
            .Where(owner =>
                string.Equals(owner.AssertionType, assertionCode, StringComparison.Ordinal) &&
                owner.ReferenceType == referenceType)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                $"No reference owner is registered for '{assertionCode}' of that reference family.");
        }

        if (matches.Length > 1)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                $"More than one reference owner matches '{assertionCode}' for that reference family.");
        }

        return matches[0];
    }
}

/// <summary>
/// In-process owner of one organization-owned reference family (SPEC 01 / SPEC 07 REQ-06): legal
/// entities, users and departments, declared explicitly per slot so the registry never interprets a
/// missing family as a wildcard. Cost Centers and Spend Categories are owned by their own catalog
/// adapters; products, suppliers, risk schemas and FX stay unregistered until they exist.
/// </summary>
public sealed class OrganizationReferenceOwner(
    ProcureToPayDbContext dbContext,
    PurchaseRequestReferenceType referenceType) : IPurchaseRequestReferenceOwner
{
    public const string OwnerIdentity = "organization-domain";

    public string AssertionType => PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization);

    public PurchaseRequestReferenceType ReferenceType => referenceType;

    public string OwnerId => OwnerIdentity;

    public string ContractVersion => "organization-db/v1";

    public async Task<PurchaseRequestVerificationResponse> VerifyAsync(
        PurchaseRequestVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.SourceRef
            ?? throw new DomainValidationException("A verification request requires its source reference.");
        if (reference.Type != ReferenceType)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The organization owner was asked for a reference family it does not own.");
        }

        var active = reference.Type switch
        {
            PurchaseRequestReferenceType.LegalEntity => await IsActiveLegalEntityAsync(request, reference, cancellationToken),
            PurchaseRequestReferenceType.Department => await IsActiveDepartmentAsync(request, reference, cancellationToken),
            PurchaseRequestReferenceType.User => await IsActiveUserAsync(request, reference, cancellationToken),
            _ => throw new PurchaseRequestDependencyUnavailableException(
                "The organization owner does not own that reference family.")
        };
        return new PurchaseRequestVerificationResponse(
            active,
            request.AssertionType,
            request.OrganizationId,
            ContractVersion,
            OwnerId,
            reference,
            active ? PurchaseRequestAssertionCodes.StatusActive : PurchaseRequestAssertionCodes.StatusNotFound,
            request.TargetRef,
            request.VerifiedAt);
    }

    private Task<bool> IsActiveLegalEntityAsync(
        PurchaseRequestVerificationRequest request,
        PurchaseRequestAttestedRef reference,
        CancellationToken cancellationToken) =>
        dbContext.LegalEntities.AsNoTracking().AnyAsync(
            record =>
                record.Id == reference.Id &&
                record.OrganizationId == Guid.Parse(request.OrganizationId) &&
                record.Status == (int)EntityStatus.Active &&
                record.Version == reference.Version,
            cancellationToken);

    private Task<bool> IsActiveDepartmentAsync(
        PurchaseRequestVerificationRequest request,
        PurchaseRequestAttestedRef reference,
        CancellationToken cancellationToken) =>
        dbContext.Departments.AsNoTracking().AnyAsync(
            record =>
                record.Id == reference.Id &&
                record.OrganizationId == Guid.Parse(request.OrganizationId) &&
                record.Status == (int)EntityStatus.Active &&
                record.Version == reference.Version,
            cancellationToken);

    private Task<bool> IsActiveUserAsync(
        PurchaseRequestVerificationRequest request,
        PurchaseRequestAttestedRef reference,
        CancellationToken cancellationToken) =>
        dbContext.UserProfiles.AsNoTracking().AnyAsync(
            record =>
                record.Id == reference.Id &&
                record.OrganizationId == Guid.Parse(request.OrganizationId) &&
                record.Status == (int)UserProfileStatus.Active &&
                record.Version == reference.Version,
            cancellationToken);
}

/// <summary>
/// Fail-closed condition of the Purchase Requests boundary: an owner, provider, Policy or
/// Approval dependency is absent, ambiguous, corrupted or unavailable (REQ-11).
/// </summary>
public sealed class PurchaseRequestDependencyUnavailableException(string message) : DomainException(message)
{
}
