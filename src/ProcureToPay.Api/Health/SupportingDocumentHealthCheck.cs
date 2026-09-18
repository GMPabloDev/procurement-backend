using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Readiness of the supporting document owner (SPEC 11 REQ-08, CA-08): the storage must be reachable,
/// exactly one processor must be registered and bound to the owner workload the prerequisites already
/// resolved, and no due attempt may sit unprocessed beyond the backlog budget. The codes never expose
/// file names, locations, suppliers, amounts or digests.
/// </summary>
public sealed class SupportingDocumentHealthCheck(
    ProcureToPayDbContext dbContext,
    IFileStorage storage) : IHealthCheck
{
    /// <summary>Backlog budget of one due attempt before readiness degrades (REQ-08).</summary>
    public static readonly TimeSpan BacklogBudget = TimeSpan.FromSeconds(60);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!await storage.IsAvailableAsync(cancellationToken))
        {
            return HealthCheckResult.Degraded("SUPPORTING_DOCUMENT_STORAGE_UNAVAILABLE");
        }

        var registrations = await dbContext.SupportingDocumentProcessorRegistrations
            .AsNoTracking()
            .Where(record => record.AdapterId == PurchaseOrderCodes.SupportingDocumentOwnerAdapterId &&
                             record.AdapterVersion == PurchaseOrderCodes.OwnerAdapterVersion)
            .ToArrayAsync(cancellationToken);
        if (registrations.Length != 1)
        {
            return HealthCheckResult.Degraded("SUPPORTING_DOCUMENT_PROCESSOR_REGISTRATION_INVALID");
        }

        var registration = registrations[0];
        var owners = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.OwnerAdapterId == PurchaseOrderCodes.SupportingDocumentOwnerAdapterId &&
                             record.OwnerAdapterVersion == PurchaseOrderCodes.OwnerAdapterVersion)
            .Select(record => new { record.OwnerWorkloadIssuer, record.OwnerWorkloadClientId })
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (owners.Any(owner =>
                !string.Equals(owner.OwnerWorkloadIssuer, registration.WorkloadIssuer, StringComparison.Ordinal) ||
                !string.Equals(owner.OwnerWorkloadClientId, registration.WorkloadClientId, StringComparison.Ordinal)))
        {
            return HealthCheckResult.Degraded("SUPPORTING_DOCUMENT_PROCESSOR_BINDING_MISMATCH");
        }

        var now = DateTimeOffset.UtcNow;
        var overdue = await dbContext.SupportingDocumentOwnerAttempts
            .AsNoTracking()
            .AnyAsync(
                record => record.State != SupportingDocumentOwnerProcessor.Satisfied &&
                          record.State != SupportingDocumentOwnerProcessor.Failed &&
                          record.State != SupportingDocumentOwnerProcessor.Abandoned &&
                          record.NextAttemptAt < now - BacklogBudget,
                cancellationToken);
        return overdue
            ? HealthCheckResult.Degraded("SUPPORTING_DOCUMENT_ATTEMPT_OVERDUE")
            : HealthCheckResult.Healthy("SUPPORTING_DOCUMENT_OWNER_READY");
    }
}
