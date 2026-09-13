using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Operational health of the Policy→Workflow policy exception integration (SPEC 05 REQ-01,
/// REQ-03, REQ-06, NFR-03): it distinguishes an absent or ambiguous adapter, an unresolved
/// prerequisite owner, an incompatible wire contract and a missing service credential without
/// exposing bindings, nonces or personal data.
/// </summary>
public sealed class PolicyExceptionHealthCheck(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : IHealthCheck
{
    private static readonly (string AdapterId, string Version)[] RequiredOwners =
    [
        ("budget-check-owner", "v1"),
        ("supporting-document-owner", "v1"),
        ("active-supplier-owner", "v1"),
        ("quotation-status-owner", "v1"),
        ("procurement-stage-owner", "v1")
    ];

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var reasons = new List<string>();
            var adapters = scope.ServiceProvider.GetRequiredService<IApprovalSubmissionAdapterRegistry>();
            try
            {
                adapters.ResolveExactlyOne(
                    PolicyApprovalAdapter.SubjectType, PolicyApprovalAdapter.Operation,
                    PolicyApprovalAdapter.ContractVersion);
            }
            catch (DomainException)
            {
                reasons.Add("POLICY_EXCEPTION_ADAPTER_UNAVAILABLE");
            }

            var owners = scope.ServiceProvider.GetRequiredService<IApprovalOwnerWorkloadRegistry>();
            foreach (var owner in RequiredOwners)
            {
                try
                {
                    owners.ResolveExactlyOne(owner.AdapterId, owner.Version);
                }
                catch (DomainException)
                {
                    reasons.Add("POLICY_EXCEPTION_OWNER_UNAVAILABLE");
                    break;
                }
            }

            var verifier = scope.ServiceProvider.GetRequiredService<IQuotationWaiverVerifier>();
            if (verifier is not HttpQuotationWaiverVerifier)
            {
                reasons.Add("POLICY_EXCEPTION_VERIFIER_DEFAULT_DENY");
            }
            else if (string.IsNullOrWhiteSpace(configuration["Policy:ExceptionWorkflow:BaseUrl"]) ||
                     string.IsNullOrWhiteSpace(configuration["Policy:ExceptionWorkflow:ClientId"]) ||
                     string.IsNullOrWhiteSpace(configuration["Policy:ExceptionWorkflow:ClientSecret"]))
            {
                reasons.Add("POLICY_EXCEPTION_CREDENTIAL_MISSING");
            }

            await Task.CompletedTask;
            return reasons.Count == 0
                ? HealthCheckResult.Healthy("The policy exception integration is fully configured.")
                : HealthCheckResult.Degraded(string.Join(",", reasons));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                "Policy exception integration health could not be determined.", exception);
        }
    }
}
