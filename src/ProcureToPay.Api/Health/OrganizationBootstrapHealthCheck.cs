using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Infrastructure.Persistence;

namespace ProcureToPay.Api.Health;

public sealed class OrganizationBootstrapHealthCheck(ProcureToPayDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var markerExists = await dbContext.BootstrapStates.AnyAsync(cancellationToken);
            var organizationCount = await dbContext.Organizations.CountAsync(cancellationToken);
            var legalEntityCount = await dbContext.LegalEntities.CountAsync(cancellationToken);
            var departmentCount = await dbContext.Departments.CountAsync(cancellationToken);
            var consistent = markerExists && organizationCount == 1 && legalEntityCount == 1 && departmentCount >= 1;
            return consistent
                ? HealthCheckResult.Healthy("Organization bootstrap is complete.")
                : HealthCheckResult.Unhealthy("Organization bootstrap state is incomplete or inconsistent.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Organization bootstrap state could not be checked.", exception);
        }
    }
}
