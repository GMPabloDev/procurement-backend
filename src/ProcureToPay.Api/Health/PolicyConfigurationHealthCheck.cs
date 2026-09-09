using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Api.Health;

public sealed class PolicyConfigurationHealthCheck(ProcureToPayDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var organizationId = await dbContext.Organizations
                .Select(organization => (Guid?)organization.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (organizationId is null)
            {
                return HealthCheckResult.Unhealthy("Policy configuration cannot be checked without an organization.");
            }

            var active = await dbContext.PolicyActivations
                .Include(activation => activation.PolicySetVersion)
                .Where(activation => activation.OrganizationId == organizationId &&
                                     activation.EffectiveFrom <= DateTimeOffset.UtcNow)
                .Where(activation => !dbContext.PolicyRetirements.Any(retirement =>
                    retirement.PolicyActivationId == activation.Id && retirement.EffectiveTo <= DateTimeOffset.UtcNow))
                .ToArrayAsync(cancellationToken);
            if (active.Length == 0)
            {
                return HealthCheckResult.Unhealthy("POLICY_CONFIGURATION_REQUIRED");
            }
            if (active.Length != 1)
            {
                return HealthCheckResult.Unhealthy("POLICY_CONFIGURATION_CORRUPT");
            }

            var version = active[0].PolicySetVersion;
            var digestValid = version.Status == (int)PolicySetStatus.Published &&
                              string.Equals(PolicyCanonicalizer.Hash(version.ContentJson), version.ContentDigest,
                                  StringComparison.OrdinalIgnoreCase);
            return digestValid
                ? HealthCheckResult.Healthy("Exactly one valid policy activation is current.")
                : HealthCheckResult.Unhealthy("POLICY_CONFIGURATION_CORRUPT");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Policy configuration could not be checked.", exception);
        }
    }
// pi-lens-ignore: CS0825
}
