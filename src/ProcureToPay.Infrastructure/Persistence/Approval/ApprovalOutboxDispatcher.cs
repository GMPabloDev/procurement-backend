using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Exact-one consumer registry (REQ-10): zero or several matches for a contract version fail
/// closed instead of silently dropping durable results.
/// </summary>
// pi-lens-ignore: lsp:CS0246
public sealed class ApprovalResultConsumerRegistry(IEnumerable<IApprovalResultConsumer> consumers)
    // pi-lens-ignore: lsp:CS0246
    : IApprovalResultConsumerRegistry
{
    // pi-lens-ignore: lsp:CS0246
    private readonly IReadOnlyList<IApprovalResultConsumer> registered = consumers.ToArray();

    // pi-lens-ignore: lsp:CS0246
    public IApprovalResultConsumer ResolveExactlyOne(string contractVersion)
    {
        var matches = registered
            .Where(consumer => string.Equals(consumer.ContractVersion, contractVersion, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                "No approval result consumer is registered for the event contract version.");
        }

        if (matches.Length > 1)
        {
            throw new ApprovalDependencyUnavailableException(
                "More than one approval result consumer matches the event contract version.");
        }

        return matches[0];
    }
}

public sealed record ApprovalDispatchOutcome(int Leased, int Delivered, int Failed, int DeadLettered);

/// <summary>
/// At-least-once outbox dispatcher with persistent leases (REQ-10, NFR-04): due events are
/// claimed by compare-and-swap on the row version so two instances never dispatch the same
/// event, retried with the contractual backoff, and parked as dead letters after ten attempts.
/// Only a complete <c>approval-result/v2</c> payload reaches a consumer (REQ-08).
/// </summary>
public sealed class ApprovalOutboxDispatcher(
    ProcureToPayDbContext dbContext,
    // pi-lens-ignore: lsp:CS0246
    IApprovalResultConsumerRegistry consumers,
    ILogger<ApprovalOutboxDispatcher> logger)
{
    public const int DefaultBatchSize = 50;

    /// <summary>How long a claimed event stays invisible to other instances.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<ApprovalDispatchOutcome> DispatchAsync(
        Guid organizationId,
        string owner,
        DateTimeOffset occurredAt,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        var candidates = await dbContext.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.State == (int)ApprovalOutboxState.Pending &&
                record.NextAttemptAt != null &&
                record.NextAttemptAt <= utcNow &&
                (record.LockedUntil == null || record.LockedUntil < utcNow))
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .Select(record => record.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var delivered = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var eventId in candidates)
        {
            var outcome = await DispatchOneAsync(organizationId, eventId, owner, utcNow, cancellationToken);
            switch (outcome)
            {
                case DispatchResult.Delivered:
                    delivered++;
                    break;
                case DispatchResult.Failed:
                    failed++;
                    break;
                case DispatchResult.DeadLettered:
                    deadLettered++;
                    break;
                case DispatchResult.NotClaimed:
                    break;
            }
        }

        return new ApprovalDispatchOutcome(candidates.Length, delivered, failed, deadLettered);
    }

    private async Task<DispatchResult> DispatchOneAsync(
        Guid organizationId,
        Guid eventId,
        string owner,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        // Claim the event with a single atomic conditional UPDATE. A read-then-write transaction
        // lets two instances deadlock on the same row; this compare-and-swap cannot, and exactly
        // one instance observes a non-zero affected count (NFR-04, REQ-10).
        var leaseOwner = owner.Length > 120 ? owner[..120] : owner;
        var claimed = await dbContext.ApprovalOutboxEvents
            .Where(record =>
                record.Id == eventId &&
                record.OrganizationId == organizationId &&
                record.State == (int)ApprovalOutboxState.Pending &&
                record.NextAttemptAt != null &&
                record.NextAttemptAt <= utcNow &&
                (record.LockedUntil == null || record.LockedUntil < utcNow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.LockOwner, leaseOwner)
                    .SetProperty(record => record.LockedUntil, utcNow + LeaseDuration),
                cancellationToken);
        if (claimed == 0)
        {
            return DispatchResult.NotClaimed;
        }

        var outboxEvent = Hydrate(
            await dbContext.ApprovalOutboxEvents
                .AsNoTracking()
                .SingleAsync(record => record.Id == eventId, cancellationToken));
        var startedAt = ApprovalTelemetry.Now();
        using var activity = ApprovalTelemetry.Start("dispatch");
        var result = DispatchResult.Delivered;
        string? error = null;

        try
        {
            var consumer = consumers.ResolveExactlyOne(outboxEvent.ContractVersion);
            await consumer.DeliverAsync(
                // pi-lens-ignore: lsp:CS0246
                new ApprovalResultDelivery(
                    outboxEvent.Id,
                    outboxEvent.ContractVersion,
                    outboxEvent.PayloadJson,
                    outboxEvent.CorrelationReference),
                cancellationToken);
            outboxEvent.MarkDelivered(utcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type is persisted: never the message, which may carry payload
            // content or identifiers (NFR-05).
            error = exception.GetType().Name;
            outboxEvent.RegisterFailure(error, utcNow);
            result = outboxEvent.State == ApprovalOutboxState.DeadLetter
                ? DispatchResult.DeadLettered
                : DispatchResult.Failed;
        }

        var outcomeLabel = Label(result);
        await PersistAsync(outboxEvent, cancellationToken);
        ApprovalTelemetry.RecordDispatch(outcomeLabel, outboxEvent.Attempts);
        ApprovalTelemetry.Complete(activity, outcomeLabel, startedAt);

        logger.LogInformation(
            "Approval outbox event {EventId} dispatch result {Result} after {Attempts} attempt(s).",
            outboxEvent.Id,
            outcomeLabel,
            outboxEvent.Attempts);

        return result;
    }

    internal static string Label(DispatchResult result) => result switch
    {
        DispatchResult.Delivered => "DELIVERED",
        DispatchResult.Failed => "FAILED",
        DispatchResult.DeadLettered => "DEAD_LETTER",
        _ => "NOT_CLAIMED"
    };

    /// <summary>
    /// Persists the delivery outcome. A concurrent writer (another instance or an administrative
    /// replay) may win the row; the lease then expires and the next sweep retries, which keeps
    /// delivery at-least-once rather than losing the result (NFR-04, REQ-10).
    /// </summary>
    private async Task PersistAsync(ApprovalOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var record = await dbContext.ApprovalOutboxEvents
                .SingleAsync(candidate => candidate.Id == outboxEvent.Id, cancellationToken);
            record.State = (int)outboxEvent.State;
            record.Attempts = outboxEvent.Attempts;
            record.NextAttemptAt = outboxEvent.NextAttemptAt;
            record.DeliveredAt = outboxEvent.DeliveredAt;
            record.LastError = outboxEvent.LastError;
            record.Version = outboxEvent.Version;
            // The lease is released explicitly: the next attempt is governed by NextAttemptAt.
            record.LockOwner = null;
            record.LockedUntil = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            logger.LogWarning(
                "Approval outbox event {EventId} delivery state was not persisted; " +
                "the lease will expire and the next sweep will retry.",
                outboxEvent.Id);
        }
    }

    internal static ApprovalOutboxEvent Hydrate(ApprovalOutboxEventRecord record) => ApprovalOutboxEvent.Restore(
        record.Id,
        record.CaseId,
        record.OrganizationId,
        ApprovalEntitySource.Restore(
            ApprovalEntitySource.ParseCode(record.ResultSourceType),
            record.ResultSourceId ?? Guid.Empty,
            record.ResultSourceKey),
        new ApprovalTarget(record.TargetType, record.TargetId, record.TargetVersion, record.MaterialSnapshotDigest),
        record.Result,
        record.PayloadJson,
        record.CreatedAt,
        record.CorrelationReference,
        record.SourceCommandId,
        (ApprovalOutboxState)record.State,
        record.Attempts,
        record.NextAttemptAt,
        record.DeliveredAt,
        record.LastError,
        record.Version,
        record.ContractVersion);

    internal enum DispatchResult
    {
        NotClaimed,
        Delivered,
        Failed,
        DeadLettered
    }
}
