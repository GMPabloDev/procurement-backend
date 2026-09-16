using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Readiness of the Supplier module (SPEC 09 REQ-12): the supplier activity owner, its Policy
/// catalog, the encryption key provider, both governance adapters, the registered processor, the
/// attachment storage and the integrity of the pointers, digests and ciphertext. The codes never
/// expose ids, names, tax ids, suffixes, prices or digests.
/// </summary>
public sealed class SupplierHealthCheck(
    IServiceScopeFactory scopeFactory) : IHealthCheck
{
    /// <summary>Budget of one due attempt before readiness degrades (REQ-10, NFR-05).</summary>
    public static readonly TimeSpan DueBudget = TimeSpan.FromSeconds(60);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var reasons = new List<string>();
            var owners = scope.ServiceProvider.GetRequiredService<IPurchaseRequestReferenceOwnerRegistry>();
            try
            {
                var owner = owners.ResolveExactlyOne(
                    PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Supplier);
                if (!string.Equals(owner.OwnerId, SupplierCodes.SupplierOwnerId, StringComparison.Ordinal) ||
                    !string.Equals(
                        owner.ContractVersion, SupplierCodes.SupplierOwnerContractVersion, StringComparison.Ordinal))
                {
                    reasons.Add("PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:SUPPLIER");
                }
            }
            catch (DomainException)
            {
                reasons.Add("PURCHASE_REQUEST_OWNER_UNAVAILABLE:ACTIVE_IN_ORGANIZATION:SUPPLIER");
            }

            var catalogs = scope.ServiceProvider.GetRequiredService<PolicyReferenceCatalogRegistry>();
            try
            {
                var catalog = catalogs.Resolve(SupplierPolicyReferenceCatalog.Catalog);
                if (!string.Equals(
                        catalog.ContractVersion, SupplierCodes.SupplierOwnerContractVersion, StringComparison.Ordinal))
                {
                    reasons.Add($"POLICY_REFERENCE_CATALOG_UNAVAILABLE:{SupplierPolicyReferenceCatalog.Catalog}");
                }
            }
            catch (DomainException)
            {
                reasons.Add($"POLICY_REFERENCE_CATALOG_UNAVAILABLE:{SupplierPolicyReferenceCatalog.Catalog}");
            }

            var factOwner = scope.ServiceProvider.GetService<IApprovedSupplierFactOwner>();
            if (factOwner is null ||
                !string.Equals(factOwner.OwnerId, SupplierCodes.ApprovedSupplierFactOwnerId, StringComparison.Ordinal))
            {
                reasons.Add("APPROVED_SUPPLIER_CATALOG_CORRUPTED");
            }

            try
            {
                _ = scope.ServiceProvider.GetRequiredService<ISupplierBankingKeyProvider>().KeyVersion;
            }
            catch (DomainException)
            {
                reasons.Add("SUPPLIER_ENCRYPTION_UNAVAILABLE");
            }

            var adapters = scope.ServiceProvider.GetRequiredService<IApprovalSubmissionAdapterRegistry>();
            foreach (var (subjectType, operation) in new[]
                     {
                         (SupplierCodes.ApprovalSubjectType, SupplierCodes.SupplierApprovalOperation),
                         (SupplierCodes.CatalogApprovalSubjectType, SupplierCodes.CatalogApprovalOperation)
                     })
            {
                try
                {
                    var adapter = adapters.ResolveExactlyOne(
                        subjectType, operation, SupplierCodes.GovernanceAdapterVersion);
                    if (!string.Equals(
                            adapter.Descriptor.AdapterId, SupplierCodes.GovernanceAdapterId, StringComparison.Ordinal))
                    {
                        reasons.Add("SUPPLIER_APPROVAL_ADAPTER_UNAVAILABLE");
                    }
                }
                catch (DomainException)
                {
                    reasons.Add("SUPPLIER_APPROVAL_ADAPTER_UNAVAILABLE");
                }
            }

            var processor = scope.ServiceProvider.GetService<SupplierPrerequisiteProcessor>();
            if (processor is null || !await processor.IsRegisteredAsync(cancellationToken))
            {
                reasons.Add("ACTIVE_SUPPLIER_PROCESSOR_UNAVAILABLE");
            }

            var attachmentStorage = scope.ServiceProvider.GetService<IFileStorage>();
            if (attachmentStorage is null)
            {
                reasons.Add("SUPPLIER_ATTACHMENT_STORAGE_UNAVAILABLE");
            }

            var dbContext = scope.ServiceProvider.GetRequiredService<ProcureToPayDbContext>();
            var diagnostics = await new SupplierPersistenceService(
                    dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierPersistenceService>.Instance)
                .DiagnoseAsync(cancellationToken);
            if (diagnostics.CorruptedPointers > 0)
            {
                reasons.Add("SUPPLIER_DATA_CORRUPTED");
            }

            var now = DateTimeOffset.UtcNow;
            var overdue = await dbContext.SupplierPrerequisiteAttempts
                .AsNoTracking()
                .AnyAsync(
                    record => record.State != "COMPLETED" && record.DueAt < now - DueBudget,
                    cancellationToken);
            if (overdue)
            {
                reasons.Add("ACTIVE_SUPPLIER_BACKLOG");
            }

            var corruptedCatalog = await dbContext.ApprovedSupplierCatalogEntries
                .AsNoTracking()
                .AnyAsync(
                    entry => entry.CurrentVersion > 0 &&
                             !dbContext.ApprovedSupplierCatalogVersions.Any(
                                 version => version.CatalogEntryId == entry.Id &&
                                            version.Version == entry.CurrentVersion),
                    cancellationToken);
            if (corruptedCatalog)
            {
                reasons.Add("APPROVED_SUPPLIER_CATALOG_CORRUPTED");
            }

            return reasons.Count == 0
                ? HealthCheckResult.Healthy("SUPPLIER_OK")
                : HealthCheckResult.Degraded(reasons[0]);
        }
        catch (DomainException)
        {
            return HealthCheckResult.Degraded("SUPPLIER_STORAGE_UNAVAILABLE");
        }
        catch (Exception)
        {
            return HealthCheckResult.Degraded("SUPPLIER_STORAGE_UNAVAILABLE");
        }
    }
}
