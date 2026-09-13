using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Scheduled delegation transitions (SPEC 04 REQ-03): due ACTIVATE/EXPIRE jobs are claimed with a
/// persistent lease and fencing token, and each one confirms its transition, audit, run and job
/// completion atomically. A restart or a reclaim reuses the same job and never duplicates the run.
/// </summary>
public sealed class ApprovalDelegationTransitionProcessor(
    ProcureToPayDbContext dbContext,
    ApprovalDelegationService delegations,
    ILogger<ApprovalDelegationTransitionProcessor> logger)
{
    /// <summary>Bounded batch per sweep so one busy organization cannot starve the others.</summary>
    public const int DefaultBatchSize = 20;

    public async Task<int> ProcessDueAsync(
        string owner,
        DateTimeOffset occurredAt,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        var pending = ApprovalDelegationCodes.JobPending;
        var due = await dbContext.ApprovalDelegationTransitionJobs
            .AsNoTracking()
            .Where(record => record.Status == pending && record.ScheduledAt <= utcNow)
            .OrderBy(record => record.ScheduledAt)
            .ThenBy(record => record.Id)
            .Select(record => new { record.Id, record.Transition })
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var processed = 0;
        foreach (var job in due)
        {
            var transition = ApprovalDelegationCodes.ParseTransition(job.Transition);
            var confirmed = transition == ApprovalDelegationTransition.Activate
                ? await delegations.ActivateScheduledAsync(job.Id, owner, utcNow, cancellationToken)
                : await delegations.ExpireScheduledAsync(job.Id, owner, utcNow, cancellationToken);
            if (confirmed)
            {
                processed++;
            }
        }

        if (due.Length > 0)
        {
            logger.LogInformation(
                "Delegation transition sweep confirmed {Confirmed} of {Due} due jobs.",
                processed,
                due.Length);
        }

        return processed;
    }
}
