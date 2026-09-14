using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.IntegrationTests.ReferenceCatalogs;

/// <summary>
/// SQL Server evidence for the real Purchase Request owners (SPEC 07 REQ-06, REQ-07, CA-05): the
/// owner identity/contract is exact, activity is current-version bound and the ownership relation
/// never accepts a stale department or a foreign organization.
/// </summary>
public sealed class ReferenceCatalogOwnerTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOrganizationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DepartmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherDepartmentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Cost_center_activity_answers_active_inactive_and_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await HarnessAsync(cancellationToken);
        var costCenter = await harness.Service.CreateCostCenterAsync(
            OrganizationId, harness.ActorId, "CC-IT-DEV", "Development", DepartmentId,
            "Initial", "corr-1", cancellationToken);

        await using var context = harness.CreateContext();
        var owner = new CostCenterReferenceOwner(context, PurchaseRequestAssertionType.ActiveInOrganization);

        var active = await owner.VerifyAsync(request(costCenter.Id, 1), cancellationToken);
        Assert.True(active.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusActive, active.Status);
        Assert.Equal("cost-center-domain", active.OwnerId);
        Assert.Equal("cost-center-db/v1", active.OwnerContractVersion);

        var unknown = await owner.VerifyAsync(request(Guid.NewGuid(), 1), cancellationToken);
        Assert.False(unknown.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, unknown.Status);

        // A reference of another organization is never visible.
        var foreign = await owner.VerifyAsync(request(costCenter.Id, 1, OtherOrganizationId), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, foreign.Status);

        await harness.Service.UpdateCostCenterAsync(
            OrganizationId, harness.ActorId, costCenter.Id, 1, null, null, EntityStatus.Inactive,
            "Retire", "corr-2", cancellationToken);
        var inactive = await owner.VerifyAsync(request(costCenter.Id, 2), cancellationToken);
        Assert.False(inactive.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusInactive, inactive.Status);

        // The previous version is now stale, not the current one.
        var stale = await owner.VerifyAsync(request(costCenter.Id, 1), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusInactive, stale.Status);

        // A wrong assertion slot is a contract failure, not a negative answer.
        var wrongSlot = new CostCenterReferenceOwner(
            context, PurchaseRequestAssertionType.FxAttestationValid);
        Assert.Equal("FX_ATTESTATION_VALID", wrongSlot.AssertionType);
    }

    [Fact]
    public async Task Cost_center_ownership_relation_is_exact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await HarnessAsync(cancellationToken);
        var costCenter = await harness.Service.CreateCostCenterAsync(
            OrganizationId, harness.ActorId, "CC-IT-DEV", "Development", DepartmentId,
            "Initial", "corr-1", cancellationToken);

        await using var context = harness.CreateContext();
        var owner = new CostCenterReferenceOwner(
            context, PurchaseRequestAssertionType.CostCenterOwnedByDepartment);

        var matching = await owner.VerifyAsync(
            request(
                costCenter.Id, 1,
                target: (DepartmentId, 1)), cancellationToken);
        Assert.True(matching.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusActive, matching.Status);

        // A different department, even if active, is not the owner of this cost center.
        var mismatched = await owner.VerifyAsync(
            request(costCenter.Id, 1, target: (OtherDepartmentId, 1)), cancellationToken);
        Assert.False(mismatched.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, mismatched.Status);

        // Renaming the department advances its version: the old frozen target no longer matches.
        await using (var renaming = harness.CreateContext())
        {
            await renaming.Departments
                .Where(record => record.Id == DepartmentId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(record => record.Version, record => record.Version + 1),
                    cancellationToken);
        }

        var staleTarget = await owner.VerifyAsync(
            request(costCenter.Id, 1, target: (DepartmentId, 1)), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, staleTarget.Status);
        var currentTarget = await owner.VerifyAsync(
            request(costCenter.Id, 1, target: (DepartmentId, 2)), cancellationToken);
        Assert.True(currentTarget.Active);
    }

    [Fact]
    public async Task Spend_category_owner_validates_version_digest_and_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await HarnessAsync(cancellationToken);
        var category = await harness.Service.CreateSpendCategoryAsync(
            OrganizationId, harness.ActorId, "HARDWARE", "Hardware", "Initial", "corr-1", cancellationToken);

        await using var context = harness.CreateContext();
        var owner = new SpendCategoryReferenceOwner(context);

        var active = await owner.VerifyAsync(codeRequest(category.Code, 1, category.Digest), cancellationToken);
        Assert.True(active.Active);
        Assert.Equal("spend-category-domain", active.OwnerId);
        Assert.Equal("spend-category-db/v1", active.OwnerContractVersion);

        var alteredDigest = await owner.VerifyAsync(
            codeRequest(category.Code, 1, new string('a', 64)), cancellationToken);
        Assert.False(alteredDigest.Active);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, alteredDigest.Status);

        var unknown = await owner.VerifyAsync(
            codeRequest("SOFTWARE", 1, category.Digest), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, unknown.Status);

        var foreign = await owner.VerifyAsync(
            codeRequest(category.Code, 1, category.Digest, OtherOrganizationId), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusNotFound, foreign.Status);

        var renamed = await harness.Service.UpdateSpendCategoryAsync(
            OrganizationId, harness.ActorId, category.Code, 1, "Hardware and peripherals", null,
            "Rename", "corr-2", cancellationToken);
        var stale = await owner.VerifyAsync(codeRequest(category.Code, 1, category.Digest), cancellationToken);
        Assert.Equal(PurchaseRequestAssertionCodes.StatusInactive, stale.Status);
        var current = await owner.VerifyAsync(codeRequest(renamed.Code, 2, renamed.Digest), cancellationToken);
        Assert.True(current.Active);

        await harness.Service.UpdateSpendCategoryAsync(
            OrganizationId, harness.ActorId, category.Code, 2, null, EntityStatus.Inactive,
            "Retire", "corr-3", cancellationToken);
        var inactive = await owner.VerifyAsync(codeRequest(renamed.Code, 3, renamed.Digest), cancellationToken);
        Assert.False(inactive.Active);
    }

    private static Task<ReferenceCatalogHarness> HarnessAsync(CancellationToken cancellationToken) =>
        ReferenceCatalogHarness.StartAsync(
            OrganizationId, OtherOrganizationId, DepartmentId, OtherDepartmentId, cancellationToken);

    private static PurchaseRequestVerificationRequest request(
        Guid costCenterId,
        int version,
        Guid? organizationId = null,
        (Guid Id, int Version)? target = null) =>
        new(
            PurchaseRequestCodes.Code(
                target is null
                    ? PurchaseRequestAssertionType.ActiveInOrganization
                    : PurchaseRequestAssertionType.CostCenterOwnedByDepartment),
            (organizationId ?? OrganizationId).ToString("D"),
            PurchaseRequestAttestedRef.ForEntity(
                PurchaseRequestReferenceType.CostCenter, costCenterId, version),
            target is (Guid departmentId, int departmentVersion)
                ? PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.Department, departmentId, departmentVersion)
                : null,
            DateTimeOffset.UtcNow);

    private static PurchaseRequestVerificationRequest codeRequest(
        string code,
        int version,
        string digest,
        Guid? organizationId = null) =>
        new(
            PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
            (organizationId ?? OrganizationId).ToString("D"),
            PurchaseRequestAttestedRef.ForCode(
                PurchaseRequestReferenceType.SpendCategory, code, version, digest),
            null,
            DateTimeOffset.UtcNow);
}
