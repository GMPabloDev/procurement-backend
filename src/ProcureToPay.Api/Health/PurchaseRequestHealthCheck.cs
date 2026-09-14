using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using System.Data.Common;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Operational health of the Purchase Requests boundary (SPEC 06 NFR-04, T-08): it distinguishes
/// a missing or ambiguous fact provider, a missing adapter v2, an unallowlisted workload, the
/// exact reference-owner slot that is absent or ambiguous, a missing or corrupted attestation and
/// stuck or failed submission attempts. It never exposes ids, content, digests or personal data.
/// </summary>
public sealed class PurchaseRequestHealthCheck(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : IHealthCheck
{
    /// <summary>Owner slots every purchase request needs before it can be attested (REQ-04).</summary>
    private static readonly (PurchaseRequestAssertionType Assertion, PurchaseRequestReferenceType Reference)[] RequiredOwners =
    [
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.LegalEntity),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.User),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Department),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.CostCenter),
        (PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.SpendCategory),
        (PurchaseRequestAssertionType.CostCenterOwnedByDepartment, PurchaseRequestReferenceType.CostCenter)
    ];

    /// <summary>Policy catalogs the SPEC 07 reference catalogs must resolve exactly once (REQ-05).</summary>
    private static readonly string[] RequiredPolicyCatalogs =
    [
        CostCenterPolicyReferenceCatalog.Catalog,
        SpendCategoryPolicyReferenceCatalog.Catalog
    ];

    private static readonly TimeSpan StuckAttemptBudget = TimeSpan.FromMinutes(15);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var reasons = new List<string>();

            var providers = scope.ServiceProvider.GetRequiredService<IPolicyFactProviderRegistry>();
            try
            {
                _ = providers.Resolve(PurchaseRequestCodes.SubjectType, PurchaseRequestCodes.EvaluationOperation);
            }
            catch (DomainException)
            {
                reasons.Add("PURCHASE_REQUEST_PROVIDER_UNAVAILABLE");
            }

            var adapters = scope.ServiceProvider.GetRequiredService<IApprovalSubmissionAdapterRegistry>();
            try
            {
                var adapter = adapters.ResolveExactlyOne(
                    PurchaseRequestCodes.SubjectType, PurchaseRequestCodes.SubmitOperation,
                    PolicyApprovalAdapter.ContractVersion);
                if (!adapter.Descriptor.RequesterRequired ||
                    !adapter.Descriptor.AllowsRequesterAsOriginator ||
                    !adapter.Descriptor.SupersessionDeltaSupported)
                {
                    reasons.Add("PURCHASE_REQUEST_ADAPTER_INCOMPATIBLE");
                }
            }
            catch (DomainException)
            {
                reasons.Add("PURCHASE_REQUEST_ADAPTER_UNAVAILABLE");
            }

            var policyAllowlist = scope.ServiceProvider.GetRequiredService<IPolicyWorkloadAllowlist>();
            var approvalAllowlist = scope.ServiceProvider.GetRequiredService<IApprovalWorkloadAllowlist>();
            var workloadIssuer = configuration["PurchaseRequests:Workload:Issuer"] ?? "internal://procure-to-pay";
            var workloadClientId = configuration["PurchaseRequests:Workload:ClientId"]
                ?? PurchaseRequestCodes.ProviderId;
            if (!policyAllowlist.IsAllowed(new PolicyWorkloadIdentity(workloadIssuer, workloadClientId)) ||
                !approvalAllowlist.IsAllowed(new ApprovalWorkloadIdentity(workloadIssuer, workloadClientId)))
            {
                reasons.Add("PURCHASE_REQUEST_WORKLOAD_UNAVAILABLE");
            }

            var owners = scope.ServiceProvider.GetRequiredService<IPurchaseRequestReferenceOwnerRegistry>();
            foreach (var slot in RequiredOwners)
            {
                try
                {
                    _ = owners.ResolveExactlyOne(slot.Assertion, slot.Reference);
                }
                catch (DomainException)
                {
                    // SPEC 07 REQ-09: the grammar distinguishes the two Cost Center slots and never
                    // exposes ids, names or digests.
                    reasons.Add(
                        $"PURCHASE_REQUEST_OWNER_UNAVAILABLE:{PurchaseRequestCodes.Code(slot.Assertion)}:" +
                        $"{PurchaseRequestCodes.Code(slot.Reference)}");
                }
            }

            var catalogRegistry = scope.ServiceProvider.GetRequiredService<PolicyReferenceCatalogRegistry>();
            foreach (var catalog in RequiredPolicyCatalogs)
            {
                try
                {
                    _ = catalogRegistry.Resolve(catalog);
                }
                catch (DomainException)
                {
                    reasons.Add($"POLICY_REFERENCE_CATALOG_UNAVAILABLE:{catalog}");
                }
            }

            var dbContext = scope.ServiceProvider.GetRequiredService<ProcureToPayDbContext>();
            await CheckReferenceCatalogStorageAsync(dbContext, reasons, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var overdue = now - StuckAttemptBudget;
            var stuck = await dbContext.PurchaseRequestSubmissionAttempts
                .AsNoTracking()
                .CountAsync(
                    record => (record.Status == (int)PurchaseRequestSubmissionStatus.Pending ||
                               record.Status == (int)PurchaseRequestSubmissionStatus.PolicyConfirmed) &&
                              record.UpdatedAt < overdue,
                    cancellationToken);
            if (stuck > 0)
            {
                reasons.Add("PURCHASE_REQUEST_SUBMISSION_STUCK");
            }

            var failed = await dbContext.PurchaseRequestSubmissionAttempts
                .AsNoTracking()
                .CountAsync(
                    record => record.Status == (int)PurchaseRequestSubmissionStatus.DependencyFailed &&
                              record.UpdatedAt < overdue,
                    cancellationToken);
            if (failed > 0)
            {
                reasons.Add("PURCHASE_REQUEST_SUBMISSION_FAILED");
            }

            var confirmed = dbContext.PurchaseRequestSubmissionAttempts
                .AsNoTracking()
                .Where(record => record.Status != (int)PurchaseRequestSubmissionStatus.Pending);
            var missingManifest = await (
                    from attempt in confirmed
                    join manifest in dbContext.PurchaseRequestCompletenessManifests.AsNoTracking()
                        on new { attempt.RequestId, Version = attempt.RequestVersion }
                        equals new { manifest.RequestId, Version = manifest.RequestVersion } into manifests
                    where !manifests.Any()
                    select attempt.Id)
                .AnyAsync(cancellationToken);
            if (missingManifest)
            {
                reasons.Add("PURCHASE_REQUEST_ATTESTATION_MISSING");
            }

            var corrupted = await dbContext.PurchaseRequestCompletenessManifests
                .AsNoTracking()
                .AnyAsync(
                    record => record.PolicyManifestDigest.Length != 64 ||
                              record.DomainAttestationDigest.Length != 64 ||
                              record.ReferenceAttestationDigest.Length != 64,
                    cancellationToken);
            if (corrupted)
            {
                reasons.Add("PURCHASE_REQUEST_MANIFEST_CORRUPTED");
            }

            return reasons.Count == 0
                ? HealthCheckResult.Healthy("The purchase request boundary is fully configured.")
                : HealthCheckResult.Degraded(string.Join(",", reasons));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Purchase request health could not be determined.", exception);
        }
    }

    /// <summary>
    /// Storage access plus current-pointer/digest consistency of both reference catalogs (REQ-09).
    /// An empty catalog is a valid business configuration and never degrades by itself.
    /// </summary>
    private static async Task CheckReferenceCatalogStorageAsync(
        ProcureToPayDbContext dbContext,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        try
        {
            var costCenterRoots = await dbContext.CostCenters.CountAsync(cancellationToken);
            var currentCostCenters = await (
                from root in dbContext.CostCenters.AsNoTracking()
                join version in dbContext.CostCenterVersions.AsNoTracking()
                    on new { root.Id, Version = root.CurrentVersion } equals new
                    {
                        Id = version.CostCenterId,
                        version.Version
                    }
                join department in dbContext.Departments.AsNoTracking()
                    on version.DepartmentId equals department.Id
                where version.Status == (int)EntityStatus.Active ||
                      version.Status == (int)EntityStatus.Inactive
                select root.Id).CountAsync(cancellationToken);
            if (currentCostCenters != costCenterRoots)
            {
                reasons.Add($"REFERENCE_CATALOG_CORRUPTED:{CostCenterPolicyReferenceCatalog.Catalog}");
            }

            var spendCategoryRoots = await dbContext.SpendCategories.CountAsync(cancellationToken);
            var currentSpendCategories = await (
                from root in dbContext.SpendCategories.AsNoTracking()
                join version in dbContext.SpendCategoryVersions.AsNoTracking()
                    on new { root.Id, Version = root.CurrentVersion } equals new
                    {
                        Id = version.SpendCategoryId,
                        version.Version
                    }
                select new
                {
                    version.Code,
                    version.Name,
                    root.OrganizationId,
                    version.Version,
                    version.Digest
                }).ToArrayAsync(cancellationToken);
            var spendCategoryCorrupted = currentSpendCategories.Length != spendCategoryRoots ||
                currentSpendCategories.Any(version => !ReferenceCatalogCanonicalizer.MatchesSpendCategoryDigest(
                    version.Code,
                    version.Name,
                    version.OrganizationId,
                    version.Version,
                    version.Digest));
            if (spendCategoryCorrupted)
            {
                reasons.Add($"REFERENCE_CATALOG_CORRUPTED:{SpendCategoryPolicyReferenceCatalog.Catalog}");
            }
        }
        catch (DbException)
        {
            reasons.Add("REFERENCE_CATALOG_STORAGE_UNAVAILABLE");
        }
    }
}
