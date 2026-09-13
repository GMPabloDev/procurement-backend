using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Operational health of the approval workflow (REQ-10): it degrades when a dead letter exists,
/// the oldest pending outbox event is older than five minutes, or a due reconciliation has not
/// completed within its sixty second budget.
/// </summary>
public sealed class ApprovalHealthCheck(ProcureToPayDbContext dbContext) : IHealthCheck
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
                return HealthCheckResult.Healthy("The approval workflow has no organization to report on yet.");
            }

            var backlog = await new ApprovalOutboxAdministrationService(
                    dbContext,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalOutboxAdministrationService>.Instance)
                .GetBacklogAsync(organizationId.Value, DateTimeOffset.UtcNow, cancellationToken);

            var reasons = new List<string>();
            if (backlog.DeadLetter > 0)
            {
                reasons.Add("APPROVAL_DEAD_LETTER");
            }

            if (backlog.IsOverdue)
            {
                reasons.Add("APPROVAL_BACKLOG_OVERDUE");
            }

            if (backlog.ReconciliationOverdue)
            {
                reasons.Add("APPROVAL_RECONCILIATION_OVERDUE");
            }

            if (backlog.DelegationTransitionOverdue)
            {
                reasons.Add("APPROVAL_DELEGATION_TRANSITION_OVERDUE");
            }

            return reasons.Count == 0
                ? HealthCheckResult.Healthy("Approval outbox and reconciliation are within contract.")
                : HealthCheckResult.Degraded(string.Join(",", reasons));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Approval workflow health could not be determined.", exception);
        }
    }
}
