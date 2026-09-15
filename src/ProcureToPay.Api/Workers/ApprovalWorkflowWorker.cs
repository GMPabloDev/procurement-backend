using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Api.Workers;

/// <summary>
/// Productive approval workers (REQ-10, NFR-03, CA-04): the outbox dispatcher and the durable
/// reconciliation runs execute automatically instead of relying on an administrative endpoint,
/// and every confirmed organization change requests exactly one run per administrative audit.
/// Startup runs the v2 baseline preflight and fails closed if legacy events exist.
/// </summary>
public sealed class ApprovalWorkflowWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ApprovalWorkflowWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>Bounded batch per sweep so one busy organization cannot starve the others.</summary>
    private static readonly int MaxRunsPerSweep = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Preflight is deliberately outside the retry loop: a v1 baseline must stop the host.
        using (var scope = scopeFactory.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ApprovalContractPreflight>()
                .EnsureV2BaselineAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Approval workflow worker sweep failed; it will retry.");
            }

            try
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProcureToPayDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ApprovalOutboxDispatcher>();
        var reconciliation = scope.ServiceProvider.GetRequiredService<ApprovalReconciliationService>();
        var delegations = scope.ServiceProvider.GetRequiredService<ApprovalDelegationTransitionProcessor>();
        // SPEC 08 REQ-07: the real budget owner advances its due prerequisites in the same sweep, so a
        // reservation and its signal land inside the 60-second budget instead of waiting for an
        // administrative call.
        var budgetPrerequisites = scope.ServiceProvider
            .GetRequiredService<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrerequisiteProcessor>();
        var identity = scope.ServiceProvider.GetRequiredService<ApprovalInstanceIdentity>();
        var utcNow = DateTimeOffset.UtcNow;

        await budgetPrerequisites.ProcessDueAsync(utcNow, cancellationToken);

        await RequestOrganizationChangeRunsAsync(dbContext, reconciliation, utcNow, cancellationToken);

        var organizationIds = await dbContext.Organizations
            .AsNoTracking()
            .Select(organization => organization.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var organizationId in organizationIds)
        {
            await dispatcher.DispatchAsync(
                organizationId, identity.Owner, utcNow, cancellationToken: cancellationToken);

            // Scheduled delegation transitions run before their reconciliation runs so a due
            // activation or expiry confirms its audit and run within the 60 second budget
            // (SPEC 04 REQ-03, CA-03).
            await delegations.ProcessDueAsync(
                identity.Owner, utcNow, cancellationToken: cancellationToken);

            var dueRuns = await dbContext.ApprovalReconciliationRuns
                .AsNoTracking()
                .Where(record =>
                    record.OrganizationId == organizationId &&
                    record.Status != ApprovalReconciliationCodes.StatusCompleted)
                .OrderBy(record => record.RequestedAt)
                .ThenBy(record => record.Id)
                .Select(record => record.Id)
                .Take(MaxRunsPerSweep)
                .ToArrayAsync(cancellationToken);
            foreach (var runId in dueRuns)
            {
                await reconciliation.ProcessAsync(
                    runId,
                    identity.Owner,
                    $"corr-{Guid.NewGuid():N}",
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// A confirmed profile, role, grant or scope change requests a run through its administrative
    /// audit (REQ-04). The unique <c>(organization, trigger_audit_id)</c> index keeps it idempotent
    /// and a run already linked to the audit is never requested twice.
    /// </summary>
    private async Task RequestOrganizationChangeRunsAsync(
        ProcureToPayDbContext dbContext,
        ApprovalReconciliationService reconciliation,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var pendingAudits = await dbContext.AdministrativeAuditRecords
            .AsNoTracking()
            .Where(audit =>
                (audit.TargetType == "UserProfile" ||
                 audit.TargetType == "RoleAssignment" ||
                 audit.TargetType == "AuthorityGrant") &&
                !dbContext.ApprovalReconciliationRuns.Any(run => run.TriggerAuditId == audit.Id))
            .OrderBy(audit => audit.OccurredAt)
            .ThenBy(audit => audit.Id)
            .Select(audit => new { audit.Id, audit.TargetType, audit.TargetId, audit.OccurredAt })
            .Take(MaxRunsPerSweep)
            .ToArrayAsync(cancellationToken);

        foreach (var audit in pendingAudits)
        {
            var organizationIds = await OrganizationsForTargetAsync(
                dbContext, audit.TargetType, audit.TargetId, cancellationToken);
            foreach (var organizationId in organizationIds)
            {
                // requested_at is the UTC of the triggering organizational audit (REQ-10),
                // never the sweep instant: an overdue run keeps its original budget.
                await reconciliation.RequestOrganizationChangeAsync(
                    organizationId,
                    audit.Id,
                    $"corr-{Guid.NewGuid():N}",
                    audit.OccurredAt.ToUniversalTime(),
                    cancellationToken);
            }
        }
    }

    private static async Task<Guid[]> OrganizationsForTargetAsync(
        ProcureToPayDbContext dbContext,
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken) => targetType switch
    {
        "UserProfile" => await dbContext.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == targetId)
            .Select(profile => profile.OrganizationId)
            .Take(1)
            .ToArrayAsync(cancellationToken),
        "RoleAssignment" => await (
                from assignment in dbContext.RoleAssignments.AsNoTracking()
                join profile in dbContext.UserProfiles.AsNoTracking()
                    on assignment.UserProfileId equals profile.Id
                where assignment.Id == targetId
                select profile.OrganizationId)
            .Take(1)
            .ToArrayAsync(cancellationToken),
        "AuthorityGrant" => await (
                from grant in dbContext.AuthorityGrants.AsNoTracking()
                join profile in dbContext.UserProfiles.AsNoTracking()
                    on grant.UserProfileId equals profile.Id
                where grant.Id == targetId
                select profile.OrganizationId)
            .Take(1)
            .ToArrayAsync(cancellationToken),
        _ => []
    };
}
