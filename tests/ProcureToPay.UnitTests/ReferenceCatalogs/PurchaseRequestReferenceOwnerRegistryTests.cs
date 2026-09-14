using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.UnitTests.ReferenceCatalogs;

/// <summary>
/// SPEC 07 REQ-06 / CA-05: the registry resolves the exact (assertion, family) pair, never treats a
/// registration as a wildcard and fails closed on absence or ambiguity.
/// </summary>
public sealed class PurchaseRequestReferenceOwnerRegistryTests
{
    private static readonly (PurchaseRequestAssertionType Assertion, PurchaseRequestReferenceType Reference)[] RequiredSlots =
    [
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.LegalEntity),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.User),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Department),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.SpendCategory),
        (PurchaseRequestAssertionType.CostCenterOwnedByDepartment, PurchaseRequestReferenceType.CostCenter)
    ];

    [Fact]
    public void Six_required_slots_resolve_exactly_one_owner()
    {
        var owners = RequiredSlots.Select(slot => new StubOwner(slot.Assertion, slot.Reference)).ToArray();
        var registry = new PurchaseRequestReferenceOwnerRegistry(owners);

        foreach (var slot in RequiredSlots)
        {
            var resolved = registry.ResolveExactlyOne(slot.Assertion, slot.Reference);
            Assert.Equal(PurchaseRequestCodes.Code(slot.Assertion), resolved.AssertionType);
            Assert.Equal(slot.Reference, resolved.ReferenceType);
        }

        // A family with no registration is never served by a foreign or generic owner.
        Assert.Throws<PurchaseRequestDependencyUnavailableException>(() => registry.ResolveExactlyOne(
            PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Product));
        Assert.Throws<PurchaseRequestDependencyUnavailableException>(() => registry.ResolveExactlyOne(
            PurchaseRequestAssertionType.FxAttestationValid, PurchaseRequestReferenceType.Fx));
    }

    [Fact]
    public void Missing_or_ambiguous_registrations_fail_closed()
    {
        var empty = new PurchaseRequestReferenceOwnerRegistry([]);
        Assert.Throws<PurchaseRequestDependencyUnavailableException>(() => empty.ResolveExactlyOne(
            PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter));

        var duplicate = new PurchaseRequestReferenceOwnerRegistry([
            new StubOwner(PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter),
            new StubOwner(PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter)
        ]);
        Assert.Throws<PurchaseRequestDependencyUnavailableException>(() => duplicate.ResolveExactlyOne(
            PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter));
    }

    private sealed class StubOwner(
        PurchaseRequestAssertionType assertion,
        PurchaseRequestReferenceType reference) : IPurchaseRequestReferenceOwner
    {
        public string AssertionType => PurchaseRequestCodes.Code(assertion);

        public PurchaseRequestReferenceType ReferenceType => reference;

        public string OwnerId => "stub";

        public string ContractVersion => "stub/v1";

        public Task<PurchaseRequestVerificationResponse> VerifyAsync(
            PurchaseRequestVerificationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
