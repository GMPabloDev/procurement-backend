using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalOutboxBacklog(
    Guid OrganizationId,
    int Pending,
    int DeadLetter,
    int Delivered,
    DateTimeOffset? OldestPendingCreatedAt,
    TimeSpan? OldestPendingAge,
    bool IsOverdue,
    bool ReconciliationOverdue,
    bool DelegationTransitionOverdue);

/// <summary>
/// Administrative operation of the outbox (REQ-10, NFR-03): backlog visibility and replay of
/// dead letters. Replay never edits the payload; it only resets delivery state, so consumers
/// keep deduplicating by <c>event_id + contract_version</c>.
/// </summary>
public sealed class ApprovalOutboxAdministrationService(
    ProcureToPayDbContext dbContext,
    ILogger<ApprovalOutboxAdministrationService> logger)
{
    /// <summary>An event pending longer than this degrades health (REQ-10).</summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromMinutes(5);

    public async Task<ApprovalOutboxBacklog> GetBacklogAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utcNow = now.ToUniversalTime();
        var events = await dbContext.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId)
            .Select(record => new { record.State, record.CreatedAt })
            .ToArrayAsync(cancellationToken);

        var pending = events.Where(entry => entry.State == (int)ApprovalOutboxState.Pending).ToArray();
        var oldest = pending.Length == 0 ? (DateTimeOffset?)null : pending.Min(entry => entry.CreatedAt);
        var age = oldest is null ? (TimeSpan?)null : utcNow - oldest.Value;

        return new ApprovalOutboxBacklog(
            organizationId,
            pending.Length,
            events.Count(entry => entry.State == (int)ApprovalOutboxState.DeadLetter),
            events.Count(entry => entry.State == (int)ApprovalOutboxState.Delivered),
            oldest,
            age,
            age is not null && age.Value > MaxPendingAge,
            await IsReconciliationOverdueAsync(organizationId, utcNow, cancellationToken),
            await IsDelegationTransitionOverdueAsync(organizationId, utcNow, cancellationToken));
    }

    /// <summary>
    /// A delegation transition is due while a pending job has not been confirmed within the 60
    /// second budget (SPEC 04 REQ-03, NFR-01). Idle deployments with no scheduled job stay healthy.
    /// </summary>
    public async Task<bool> IsDelegationTransitionOverdueAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utcNow = now.ToUniversalTime();
        return await dbContext.ApprovalDelegationTransitionJobs
            .AsNoTracking()
            .AnyAsync(
                record => record.OrganizationId == organizationId &&
                          record.Status == ApprovalDelegationCodes.JobPending &&
                          record.ScheduledAt <= utcNow - ApprovalReconciliationService.MaxReconciliationAge,
                cancellationToken);
    }

    /// <summary>
    /// A reconciliation is due while an existing run has not completed within the 60 second budget
    /// (NFR-03, REQ-10). Idle deployments with no requested run stay healthy.
    /// </summary>
    public async Task<bool> IsReconciliationOverdueAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var utcNow = now.ToUniversalTime();
        return await dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .AnyAsync(
                record => record.OrganizationId == organizationId &&
                          record.Status != ApprovalReconciliationCodes.StatusCompleted &&
                          record.RequestedAt <= utcNow - ApprovalReconciliationService.MaxReconciliationAge,
                cancellationToken);
    }

    /// <summary>ADMIN replays a dead letter without editing its contractual payload (REQ-10).</summary>
    public async Task<ApprovalOutboxBacklogEntry> ReplayAsync(
        Guid organizationId,
        Guid eventId,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var record = await dbContext.ApprovalOutboxEvents
            .SingleOrDefaultAsync(
                candidate => candidate.Id == eventId && candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval outbox event is not visible.");

        var outboxEvent = ApprovalOutboxDispatcher.Hydrate(record);
        // Resets delivery state only; PayloadJson is never rebuilt or rewritten.
        outboxEvent.Replay(utcNow);

        record.State = (int)outboxEvent.State;
        record.Attempts = outboxEvent.Attempts;
        record.NextAttemptAt = outboxEvent.NextAttemptAt;
        record.DeliveredAt = outboxEvent.DeliveredAt;
        record.LastError = outboxEvent.LastError;
        record.Version = outboxEvent.Version;
        record.LockOwner = null;
        record.LockedUntil = null;

        dbContext.ApprovalAuditEntries.Add(new ApprovalAuditEntryRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            CaseId = record.CaseId,
            RequirementId = string.Equals(record.ResultSourceType, "APPROVAL_REQUIREMENT", StringComparison.Ordinal)
                ? record.ResultSourceId
                : null,
            ActorType = "USER",
            ActorUserId = actorUserId,
            ActorWorkloadIssuer = null,
            ActorWorkloadClientId = null,
            ActorSystemId = null,
            CausedByAuditStream = null,
            CausedByAuditId = null,
            AutomaticEffectKey = null,
            OccurredAt = utcNow,
            Action = "OUTBOX_REPLAYED",
            TargetType = nameof(ApprovalOutboxEvent),
            TargetId = record.Id,
            ScopeJson = "[]",
            Reason = "Administrative replay of a dead letter.",
            BeforeJson = null,
            AfterJson = ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                ("attempts", ApprovalCanonicalJson.Number(outboxEvent.Attempts)),
                ("contract_version", ApprovalCanonicalJson.String(record.ContractVersion)),
                ("state", ApprovalCanonicalJson.String(outboxEvent.State)))),
            CorrelationReference = correlationReference
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Approval outbox event {EventId} replayed administratively; payload was not edited.",
            eventId);

        return new ApprovalOutboxBacklogEntry(
            record.Id,
            record.ContractVersion,
            record.Result,
            outboxEvent.State.ToString().ToUpperInvariant(),
            outboxEvent.Attempts,
            record.NextAttemptAt);
    }

    public async Task<IReadOnlyList<ApprovalOutboxBacklogEntry>> GetDeadLettersAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.State == (int)ApprovalOutboxState.DeadLetter)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        return records
            .Select(record => new ApprovalOutboxBacklogEntry(
                record.Id,
                record.ContractVersion,
                record.Result,
                nameof(ApprovalOutboxState.DeadLetter),
                record.Attempts,
                record.NextAttemptAt))
            .ToArray();
    }
}

public sealed record ApprovalOutboxBacklogEntry(
    Guid EventId,
    string ContractVersion,
    string Result,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptAt);
