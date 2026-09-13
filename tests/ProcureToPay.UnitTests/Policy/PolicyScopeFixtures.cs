using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>
/// Canonical <c>decision-scope/v1</c> descriptors for policy tests (SPEC 03 REQ-02,
/// SPEC 05 REQ-02). Legacy tokens are intentionally not offered: policies must carry
/// the exact canonical JSON.
/// </summary>
internal static class PolicyScopeFixtures
{
    public static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static string Organization() => Canonical((ScopeDimension.Organization, null, null));

    public static string Department(Guid id, int version) =>
        Canonical((ScopeDimension.Department, id, version));

    public static string LegalEntity(Guid id, int version) =>
        Canonical((ScopeDimension.LegalEntity, id, version));

    private static string Canonical(params (ScopeDimension Dimension, Guid? Id, int? Version)[] entries) =>
        DecisionScopeDescriptor.Create(
            OrganizationId,
            entries.Select(entry => new DecisionScopeEntry(entry.Dimension, entry.Id, entry.Version)))
            .ToCanonicalJson();
}
