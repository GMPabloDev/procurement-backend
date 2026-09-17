using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Readiness of the Sourcing module (SPEC 10 REQ-13, REQ-14, NFR-04, NFR-05): the two owner
/// processors and their workload bindings, the typed Policy provider, the approval adapter, the
/// attachment storage and the owner backlog. A degraded reason is a typed code that never carries an
/// id, a name, a price or a digest.
/// </summary>
public sealed class SourcingHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    /// <summary>Budget of one due attempt before readiness degrades (REQ-13).</summary>
    public static readonly TimeSpan DueBudget = TimeSpan.FromSeconds(60);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var reasons = new List<string>();
            var provider = scope.ServiceProvider.GetService<ISourcingPolicyFactProviderRegistry>();
            if (provider is null)
            {
                reasons.Add("SOURCING_FACT_PROVIDER_REGISTRY_UNAVAILABLE");
            }
            else
            {
                try
                {
                    var resolved = provider.Resolve(
                        SourcingCodes.ApprovalSubjectType, SourcingCodes.PolicyOperation);
                    if (!string.Equals(
                            resolved.ProviderId, SourcingCodes.PolicyFactProviderId, StringComparison.Ordinal) ||
                        !string.Equals(
                            resolved.ContractVersion, SourcingCodes.PolicyFactProviderContractVersion,
                            StringComparison.Ordinal))
                    {
                        reasons.Add("SOURCING_FACT_PROVIDER_UNAVAILABLE");
                    }
                }
                catch (DomainException)
                {
                    reasons.Add("SOURCING_FACT_PROVIDER_UNAVAILABLE");
                }
            }

            var adapters = scope.ServiceProvider.GetServices<
                Application.Abstractions.IApprovalSubmissionAdapter>();
            var sourcingAdapters = adapters.Where(adapter =>
                string.Equals(
                    adapter.Descriptor.AdapterId, SourcingCodes.ApprovalAdapterId, StringComparison.Ordinal) &&
                string.Equals(
                    adapter.Descriptor.SubjectType, SourcingCodes.ApprovalSubjectType, StringComparison.Ordinal) &&
                string.Equals(
                    adapter.Descriptor.Operation, SourcingCodes.ApprovalOperation, StringComparison.Ordinal))
                .ToArray();
            if (sourcingAdapters.Length != 1 ||
                !string.Equals(
                    sourcingAdapters[0].Descriptor.ContractVersion, SourcingCodes.ApprovalAdapterVersion,
                    StringComparison.Ordinal))
            {
                reasons.Add("SOURCING_APPROVAL_ADAPTER_UNAVAILABLE");
            }

            var processor = scope.ServiceProvider.GetService<SourcingPrerequisiteProcessor>();
            if (processor is null || !await processor.IsReadyAsync(cancellationToken))
            {
                // Zero, two or mismatched owner registrations degrade readiness (REQ-13).
                reasons.Add("SOURCING_OWNER_REGISTRATION_UNAVAILABLE");
            }

            var dbContext = scope.ServiceProvider.GetRequiredService<ProcureToPayDbContext>();
            var due = await dbContext.SourcingOwnerAttempts
                .AsNoTracking()
                .Where(record => record.State != "COMPLETED" && record.State != "ABANDONED")
                .Select(record => (DateTimeOffset?)record.DueAt)
                .OrderBy(value => value)
                .FirstOrDefaultAsync(cancellationToken);
            if (due is DateTimeOffset oldest && DateTimeOffset.UtcNow - oldest > DueBudget)
            {
                reasons.Add("SOURCING_OWNER_BACKLOG_EXCEEDED");
            }

            var storage = scope.ServiceProvider.GetService<IFileStorage>();
            if (storage is null || !await storage.IsAvailableAsync(cancellationToken))
            {
                reasons.Add("SOURCING_ATTACHMENT_STORAGE_UNAVAILABLE");
            }

            return reasons.Count == 0
                ? HealthCheckResult.Healthy("Sourcing dependencies are available.")
                : HealthCheckResult.Unhealthy(string.Join(";", reasons));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unexpected failure is a degraded module, never a healthy one.
            return HealthCheckResult.Unhealthy("SOURCING_HEALTH_CHECK_FAILED");
        }
    }
}
