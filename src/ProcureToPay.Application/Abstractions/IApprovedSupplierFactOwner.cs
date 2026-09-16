using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;

namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// In-process request of the approved supplier fact lookup (SPEC 09 REQ-08). The caller only
/// identifies the persisted Purchase Request line version and the references it already attested; no
/// boolean, price, agreement status or catalogue entry is accepted from the outside.
/// </summary>
public sealed record ApprovedSupplierFactQuery(
    Guid OrganizationId,
    SupplierFactSourceLine SourceLine,
    VersionedEntityRef? SupplierRef,
    VersionedCodeRef SpendCategoryRef,
    VersionedEntityRef? ProductRef,
    DateTimeOffset AttestedAt);

/// <summary>
/// Exactly-one owner that freezes the two supplier Policy facts of one line (REQ-08). Absence,
/// ambiguity or corruption fails closed: the caller never falls back to a synthetic value.
/// </summary>
public interface IApprovedSupplierFactOwner
{
    string OwnerId { get; }

    string ContractVersion { get; }

    Task<SupplierPolicyFactSnapshot> ResolveAsync(
        ApprovedSupplierFactQuery query,
        CancellationToken cancellationToken = default);
}
