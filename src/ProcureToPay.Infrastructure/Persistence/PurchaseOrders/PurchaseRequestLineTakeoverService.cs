using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Durable per-line takeover of a Purchase Request version (<c>purchase-request-line-takeover/v1</c>,
/// REQ-10). It replaces the request-wide lock of SPEC 10 for new operations: at most one active
/// takeover exists per line version, the set is fenced under local locks taken in canonical order,
/// and the consumer is identified by its own reference.
/// </summary>
public sealed class PurchaseRequestLineTakeoverService(ProcureToPayDbContext dbContext)
{
    /// <summary>One line of a takeover set: the request line reference plus its attested digest.</summary>
    public sealed record TakeoverLine(Guid LineId, int LineVersion, string LineContentDigest);

    /// <summary>
    /// Acquires one active takeover per line of the set. A replay of the same consumer is idempotent;
    /// an active row of another owner or consumer is a conflict, unless the caller explicitly
    /// transfers it by naming the consumer it replaces.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseRequestLineTakeoverRecord>> AcquireAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        string requestContentDigest,
        IReadOnlyList<TakeoverLine> lines,
        PurchaseRequestLineOwner owner,
        Guid consumerId,
        int consumerVersion,
        string consumerDigest,
        string consumerType,
        Guid? predecessorConsumerId,
        int? predecessorConsumerVersion,
        string actorUserId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new DomainValidationException("A takeover requires at least one covered line.");
        }

        if (lines.Count > PurchaseOrderCodes.MaxLines)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A takeover covers at most {PurchaseOrderCodes.MaxLines} lines.");
        }

        var normalizedDigest = PurchaseOrderCodes.Digest(requestContentDigest, "Request content digest");
        var consumerDigestValue = PurchaseOrderCodes.Digest(consumerDigest, "Consumer digest");
        var actor = Guid.TryParse(actorUserId, out var parsedActor)
            ? parsedActor
            : throw new DomainValidationException("The takeover actor is not a user identity.");
        var ordered = lines
            .OrderBy(line => line.LineId)
            .ThenBy(line => line.LineVersion)
            .ToArray();
        if (ordered.Select(line => (line.LineId, line.LineVersion)).Distinct().Count() != ordered.Length)
        {
            throw new DomainConflictException("A takeover cannot repeat a line version.");
        }

        // The fence locks the whole set in canonical order, so a concurrent subset cannot interleave.
        // The locking read runs inside the caller's transaction; the rows are then loaded with LINQ
        // so the same transaction can mutate exactly the rows it just fenced.
        var lineIds = string.Join(", ", ordered.Select(line => $"'{line.LineId:D}'"));
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
             SELECT 1 FROM [PurchaseOrders].[PurchaseRequestLineTakeovers] WITH (UPDLOCK, HOLDLOCK)
             WHERE [OrganizationId] = '{organizationId:D}'
               AND [RequestId] = '{requestId:D}'
               AND [RequestVersion] = {requestVersion}
               AND [State] = 1
               AND [LineId] IN ({lineIds})
             ORDER BY [LineId], [LineVersion]
             """,
            cancellationToken);
        var activeLineIds = ordered.Select(line => line.LineId).ToArray();
        var active = await dbContext.PurchaseRequestLineTakeovers
            .Where(row => row.OrganizationId == organizationId &&
                          row.RequestId == requestId &&
                          row.RequestVersion == requestVersion &&
                          row.State == (int)TakeoverState.Active &&
                          activeLineIds.Contains(row.LineId))
            .OrderBy(row => row.LineId)
            .ThenBy(row => row.LineVersion)
            .ToArrayAsync(cancellationToken);
        var byLine = active.ToDictionary(row => (row.LineId, row.LineVersion));
        var results = new List<PurchaseRequestLineTakeoverRecord>(ordered.Length);
        foreach (var line in ordered)
        {
            if (!byLine.TryGetValue((line.LineId, line.LineVersion), out var current))
            {
                if (predecessorConsumerId is not null)
                {
                    throw new DomainConflictException(
                        "The takeover set no longer holds the line the caller expects to replace.");
                }

                var row = new PurchaseRequestLineTakeoverRecord
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    RequestId = requestId,
                    RequestVersion = requestVersion,
                    RequestContentDigest = normalizedDigest,
                    LineId = line.LineId,
                    LineVersion = line.LineVersion,
                    LineContentDigest = PurchaseOrderCodes.Digest(line.LineContentDigest, "Line content digest"),
                    Owner = (int)owner,
                    State = (int)TakeoverState.Active,
                    Version = 1,
                    ConsumerId = consumerId,
                    ConsumerVersion = consumerVersion,
                    ConsumerDigest = consumerDigestValue,
                    ConsumerType = consumerType,
                    ActorUserId = actor,
                    OccurredAt = occurredAt.ToUniversalTime()
                };
                dbContext.PurchaseRequestLineTakeovers.Add(row);
                results.Add(row);
                continue;
            }

            // The request reference of a takeover is immutable once recorded; a transfer keeps it.
            // Only the line content digest must still match the attested version.
            if (current.LineContentDigest !=
                PurchaseOrderCodes.Digest(line.LineContentDigest, "Line content digest"))
            {
                throw new DomainConflictException(
                    "The active takeover of the line does not match the attested line version.");
            }

            // Idempotent replay: the same operation already owns the line.
            if (current.ConsumerType == consumerType && current.ConsumerId == consumerId &&
                current.ConsumerVersion == consumerVersion &&
                current.ConsumerDigest == consumerDigestValue)
            {
                results.Add(current);
                continue;
            }

            if (predecessorConsumerId is null ||
                current.ConsumerId != predecessorConsumerId.Value ||
                current.ConsumerVersion != predecessorConsumerVersion)
            {
                throw new DomainConflictException(
                    "The line already has an active takeover of another owner.");
            }

            var successor = new PurchaseRequestLineTakeoverRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                RequestId = requestId,
                RequestVersion = requestVersion,
                RequestContentDigest = normalizedDigest,
                LineId = line.LineId,
                LineVersion = line.LineVersion,
                LineContentDigest = current.LineContentDigest,
                Owner = (int)owner,
                State = (int)TakeoverState.Active,
                Version = current.Version + 1,
                ConsumerId = consumerId,
                ConsumerVersion = consumerVersion,
                ConsumerDigest = consumerDigestValue,
                ConsumerType = consumerType,
                PredecessorId = current.ConsumerId,
                PredecessorVersion = current.ConsumerVersion,
                PredecessorDigest = current.ConsumerDigest,
                ActorUserId = actor,
                OccurredAt = occurredAt.ToUniversalTime()
            };
            current.State = (int)TakeoverState.Released;
            current.ReleasedAt = occurredAt.ToUniversalTime();
            current.ReleaseReason = "TRANSFERRED";
            dbContext.PurchaseRequestLineTakeovers.Add(successor);
            results.Add(successor);
        }

        return results;
    }

    /// <summary>
    /// Resolves the exactly-one active takeover of one line. Zero rows returns null; two or more is
    /// ambiguity and fails closed instead of choosing one.
    /// </summary>
    public async Task<PurchaseRequestLineTakeoverRecord?> ResolveActiveAsync(
        Guid organizationId,
        Guid lineId,
        int lineVersion,
        CancellationToken cancellationToken = default) =>
        await dbContext.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId &&
                       row.LineId == lineId &&
                       row.LineVersion == lineVersion &&
                       row.State == (int)TakeoverState.Active,
                cancellationToken);

    /// <summary>
    /// Resolves the active takeover of every line of a set, failing closed on a missing, foreign or
    /// ambiguous row. The caller compares all refs and versions of the set, never a subset.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseRequestLineTakeoverRecord>> ResolveSetAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        IReadOnlyList<TakeoverLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var resolved = new List<PurchaseRequestLineTakeoverRecord>(lines.Count);
        foreach (var line in lines.OrderBy(line => line.LineId).ThenBy(line => line.LineVersion))
        {
            var row = await ResolveActiveAsync(organizationId, line.LineId, line.LineVersion, cancellationToken)
                ?? throw new DomainConflictException(
                    "A covered line has no active takeover for the expected request version.");
            if (row.RequestId != requestId || row.RequestVersion != requestVersion)
            {
                throw new DomainConflictException(
                    "A covered line is taken over by another request version.");
            }

            resolved.Add(row);
        }

        return resolved;
    }

    /// <summary>True while any line of the request version carries an active takeover.</summary>
    public async Task<bool> HasActiveAsync(
        Guid organizationId,
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        await dbContext.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .AnyAsync(
                row => row.OrganizationId == organizationId &&
                       row.RequestId == requestId &&
                       row.State == (int)TakeoverState.Active,
                cancellationToken);

    /// <summary>Refuses a Purchase Request mutation while any line of the request is taken over.</summary>
    public async Task RequireNoActiveTakeoverAsync(
        Guid organizationId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (await HasActiveAsync(organizationId, requestId, cancellationToken))
        {
            throw new DomainConflictException("PR_REQUEST_CONSUMED");
        }
    }

    /// <summary>
    /// Releases every active takeover of one consumer. A partial release is a conflict: the consumer
    /// either owns all of its lines or none. The expected version is enforced by the caller through a
    /// compare-and-swap on its own root row inside the same transaction.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseRequestLineTakeoverRecord>> ReleaseAsync(
        Guid organizationId,
        string consumerType,
        Guid consumerId,
        TakeoverState terminalState,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (terminalState == TakeoverState.Active)
        {
            throw new DomainValidationException("A release cannot leave a takeover active.");
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            SELECT 1 FROM [PurchaseOrders].[PurchaseRequestLineTakeovers] WITH (UPDLOCK, HOLDLOCK)
            WHERE [OrganizationId] = {0}
              AND [ConsumerType] = {1}
              AND [ConsumerId] = {2}
              AND [State] = 1
            ORDER BY [LineId], [LineVersion]
            """,
            [organizationId, consumerType, consumerId],
            cancellationToken);
        var rows = await dbContext.PurchaseRequestLineTakeovers
            .Where(row => row.OrganizationId == organizationId &&
                          row.ConsumerType == consumerType &&
                          row.ConsumerId == consumerId &&
                          row.State == (int)TakeoverState.Active)
            .OrderBy(row => row.LineId)
            .ThenBy(row => row.LineVersion)
            .ToArrayAsync(cancellationToken);
        var released = new List<PurchaseRequestLineTakeoverRecord>(rows.Length);
        foreach (var row in rows)
        {
            row.State = (int)terminalState;
            row.ReleasedAt = occurredAt.ToUniversalTime();
            row.ReleaseReason = PurchaseOrderCodes.Reason(reason);
            released.Add(row);
        }

        return released;
    }

    /// <summary>
    /// Releases the active takeovers of a whole request version back to a target state. Used when a
    /// pre-issue cancellation returns the lines to their award.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseRequestLineTakeoverRecord>> ReleaseRequestAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        TakeoverState terminalState,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            $"""
             SELECT 1 FROM [PurchaseOrders].[PurchaseRequestLineTakeovers] WITH (UPDLOCK, HOLDLOCK)
             WHERE [OrganizationId] = '{organizationId:D}'
               AND [RequestId] = '{requestId:D}'
               AND [RequestVersion] = {requestVersion}
               AND [State] = 1
             ORDER BY [LineId], [LineVersion]
             """,
            cancellationToken);
        var rows = await dbContext.PurchaseRequestLineTakeovers
            .Where(row => row.OrganizationId == organizationId &&
                          row.RequestId == requestId &&
                          row.RequestVersion == requestVersion &&
                          row.State == (int)TakeoverState.Active)
            .OrderBy(row => row.LineId)
            .ThenBy(row => row.LineVersion)
            .ToArrayAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.State = (int)terminalState;
            row.ReleasedAt = occurredAt.ToUniversalTime();
            row.ReleaseReason = PurchaseOrderCodes.Reason(reason);
        }

        return rows;
    }

    /// <summary>Domain projection of one stored row.</summary>
    public static PurchaseRequestLineTakeover ToDomain(PurchaseRequestLineTakeoverRecord row) =>
        new(
            row.OrganizationId,
            new PurchaseOrderContentRef(row.RequestId, row.RequestVersion, row.RequestContentDigest),
            new PurchaseOrderContentRef(row.LineId, row.LineVersion, row.LineContentDigest),
            (PurchaseRequestLineOwner)row.Owner,
            new PurchaseOrderContentRef(row.ConsumerId, row.ConsumerVersion, row.ConsumerDigest),
            row.PredecessorId is null || row.PredecessorVersion is null || row.PredecessorDigest is null
                ? null
                : new PurchaseOrderContentRef(
                    row.PredecessorId.Value, row.PredecessorVersion.Value, row.PredecessorDigest),
            (TakeoverState)row.State,
            row.Version,
            row.OccurredAt,
            row.ActorUserId);
}
