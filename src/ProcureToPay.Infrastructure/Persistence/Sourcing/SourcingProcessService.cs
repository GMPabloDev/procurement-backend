using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>One requested line of a new sourcing process (REQ-01).</summary>
public sealed record SourcingLineDraft(Guid LineId, int LineVersion, decimal RequestedQuantity, string UnitCode);

public sealed record CreateSourcingProcessCommand(
    Guid OrganizationId,
    Guid RequestId,
    int RequestVersion,
    IReadOnlyList<SourcingLineDraft> Lines,
    string CommandKey);

public sealed record CreateRfqCommand(
    Guid OrganizationId,
    Guid ProcessId,
    int ExpectedProcessVersion,
    string Currency,
    CommercialTerms Terms,
    EvaluationWeightSet Weights,
    DateTimeOffset ResponseDeadline,
    string CommandKey);

public sealed record TransitionRfqCommand(
    Guid OrganizationId,
    Guid RfqId,
    int ExpectedVersion,
    string CommandKey,
    string? Reason = null,
    DateTimeOffset? NewDeadline = null);

public sealed record CancelSourcingProcessCommand(
    Guid OrganizationId,
    Guid ProcessId,
    int ExpectedProcessVersion,
    string Reason,
    string CommandKey);

/// <summary>Operator projection of one sourcing process (REQ-01, REQ-14).</summary>
public sealed record SourcingProcessView(
    Guid ProcessId,
    Guid OrganizationId,
    Guid RequestId,
    int RequestVersion,
    string State,
    int Version,
    bool TakeoverHeld,
    IReadOnlyList<SourcingProcessLine> Lines,
    Guid ActorUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Result of one RFQ lifecycle command: the version the command settled on (REQ-02).</summary>
public sealed record SourcingRfqOutcome(
    Guid RfqId,
    int Version,
    string Status,
    string ContentDigest,
    bool Replayed);

/// <summary>
/// Sourcing process, takeover and RFQ lifecycle (SPEC 10 REQ-01, REQ-02). Every accepted command
/// appends exactly one version, resolves its replay from a persisted command key and keeps the
/// Purchase Request untouched: the selected supplier will live in the award, never in the request.
/// </summary>
public sealed class SourcingProcessService(
    ProcureToPayDbContext dbContext,
    SourcingPrerequisiteProcessor? prerequisiteProcessor = null,
    PurchaseRequestLineTakeoverService? takeovers = null)
{
    /// <summary>
    /// SPEC 11 REQ-10: the per-line takeover table is authoritative for new operations. A caller
    /// that does not inject the service still gets the real one over its own context, so no call
    /// path can silently fall back to the legacy request-wide row.
    /// </summary>
    private readonly PurchaseRequestLineTakeoverService takeovers = takeovers ??
        new PurchaseRequestLineTakeoverService(dbContext);

    /// <summary>
    /// Owners of the two PROCUREMENT-stage prerequisites this module satisfies itself. Every other
    /// prerequisite of the current case precedes them, so it must already be satisfied (REQ-01).
    /// </summary>
    private static readonly IReadOnlySet<string> SourcingOwnedOwners = new HashSet<string>(StringComparer.Ordinal)
    {
        SourcingCodes.QuotationStatusOwnerAdapterId,
        SourcingCodes.ProcurementStageOwnerAdapterId
    };

    public async Task<SourcingProcessView> CreateProcessAsync(
        CreateSourcingProcessCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OrganizationId == Guid.Empty || command.RequestId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A sourcing process requires its complete actor identity.");
        }

        if (command.RequestVersion < 1)
        {
            throw new DomainValidationException("A sourcing process requires a positive request version.");
        }

        var drafts = (command.Lines ?? []).ToImmutableArray();
        if (drafts.Length == 0)
        {
            throw new DomainValidationException("A sourcing process requires at least one line.");
        }

        if (drafts.Length > SourcingCodes.MaxLinesPerProcess)
        {
            throw new SourcingPayloadTooLargeException(
                $"A sourcing process supports at most {SourcingCodes.MaxLinesPerProcess} lines.");
        }

        if (drafts.Select(draft => draft.LineId).Distinct().Count() != drafts.Length)
        {
            throw new DomainConflictException("A sourcing process cannot repeat a request line.");
        }

        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.RequestId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request does not exist in this organization.");
        if (request.Status == (int)PurchaseRequestStatus.Cancelled)
        {
            throw new DomainConflictException("A cancelled purchase request cannot start sourcing.");
        }

        if (request.CurrentVersion != command.RequestVersion)
        {
            throw new DomainConflictException(
                "Sourcing must start from the current version of the purchase request.");
        }

        var versionLines = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(line => line.RequestId == command.RequestId && line.RequestVersion == command.RequestVersion)
            .ToDictionaryAsync(line => line.LineId, cancellationToken);
        var lines = ImmutableArray.CreateBuilder<SourcingProcessLine>(drafts.Length);
        foreach (var draft in drafts)
        {
            if (!versionLines.TryGetValue(draft.LineId, out var versionLine) ||
                versionLine.LineVersion != draft.LineVersion)
            {
                throw new DomainConflictException(
                    "Every sourcing line must belong to the presented version of the purchase request.");
            }

            lines.Add(new SourcingProcessLine(
                new SourcingContentRef(versionLine.LineId, versionLine.LineVersion, versionLine.ContentDigest),
                draft.RequestedQuantity,
                draft.UnitCode));
        }

        await RequirePredecessorsSatisfiedAsync(command, cancellationToken);
        var fingerprint = SourcingCommandFingerprints.Process(
            command.OrganizationId,
            actorUserId,
            command.RequestId,
            command.RequestVersion,
            lines,
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId,
            actorUserId,
            SourcingCommandFingerprints.CreateProcess,
            command.CommandKey,
            cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null)
            {
                throw new DomainConflictException("The command key was reused with a different process.");
            }

            return await GetProcessAsync(command.OrganizationId, replay.ResultId.Value, cancellationToken);
        }

        var processId = Guid.NewGuid();
        dbContext.SourcingProcesses.Add(new SourcingProcessRecord
        {
            Id = processId,
            OrganizationId = command.OrganizationId,
            RequestId = command.RequestId,
            RequestVersion = command.RequestVersion,
            State = (int)SourcingProcessState.Draft,
            Version = 1,
            ActorUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        });
        foreach (var line in lines)
        {
            dbContext.SourcingProcessLines.Add(new SourcingProcessLineRecord
            {
                ProcessId = processId,
                LineId = line.LineRef.Id,
                LineVersion = line.LineRef.Version,
                LineContentDigest = line.LineRef.ContentDigest,
                RequestedQuantity = line.RequestedQuantity,
                UnitCode = line.UnitCode
            });
        }

        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CreateProcess,
            command.CommandKey, fingerprint, processId, 1, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionProcessCreated, Actor(actorUserId), null,
            ["request", "lines"], correlationReference,
            $"process:{processId:D}:created",
            Target(SourcingCodes.TargetProcess, processId, 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProcessAsync(command.OrganizationId, processId, cancellationToken);
    }

    /// <summary>
    /// Creates the draft RFQ of one process, freezing currency, terms, weights, deadline, the line
    /// set and the quotation minimums of every covered prerequisite (REQ-02). No takeover yet.
    /// </summary>
    public async Task<SourcingRfqOutcome> CreateRfqAsync(
        CreateRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var process = await RequireProcessAsync(command.OrganizationId, command.ProcessId, cancellationToken);
        if (process.State != (int)SourcingProcessState.Draft)
        {
            throw new DomainConflictException("Only a draft sourcing process can declare its RFQ.");
        }

        if (process.Version != command.ExpectedProcessVersion)
        {
            throw new DomainConflictException("The sourcing process version is stale.");
        }

        var fingerprint = SourcingCommandFingerprints.Rfq(
            command.OrganizationId,
            actorUserId,
            command.ProcessId,
            command.ExpectedProcessVersion,
            command.Currency,
            command.Terms,
            command.Weights,
            command.ResponseDeadline,
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId,
            actorUserId,
            SourcingCommandFingerprints.CreateRfq,
            command.CommandKey,
            cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different RFQ.");
            }

            var stored = await RequireRfqVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingRfqOutcome(
                stored.RfqId, stored.Version, SourcingStateCodes.RfqStatusCode((RfqStatus)stored.Status),
                stored.ContentDigest, Replayed: true);
        }

        var existing = await dbContext.Rfqs
            .AsNoTracking()
            .AnyAsync(rfq => rfq.ProcessId == command.ProcessId, cancellationToken);
        if (existing)
        {
            throw new DomainConflictException("The sourcing process already declared its RFQ.");
        }

        var lines = await LoadRfqLinesAsync(process, cancellationToken);
        var requestedCurrency = await RequireSharedCurrencyAsync(process, lines, cancellationToken);
        var currency = SourcingCodes.Currency(command.Currency, "RFQ currency");
        if (!string.Equals(currency, requestedCurrency, StringComparison.Ordinal))
        {
            throw new DomainConflictException(
                "Every covered line must share the requested currency of the RFQ.");
        }

        var parameters = await FreezeQuotationParametersAsync(process, cancellationToken);
        var rfqId = Guid.NewGuid();
        var deadline = command.ResponseDeadline.ToUniversalTime();
        var digest = SourcingCanonicalizer.RfqContentDigest(
            rfqId, 1, command.OrganizationId, process.Id, process.RequestId, process.RequestVersion,
            RfqStatus.Draft, currency, command.Terms, command.Weights, openedAt: null, deadline,
            predecessorVersion: null, lines);
        dbContext.Rfqs.Add(new RfqRecord
        {
            Id = rfqId,
            OrganizationId = command.OrganizationId,
            ProcessId = process.Id,
            RequestId = process.RequestId,
            RequestVersion = process.RequestVersion,
            CurrentVersion = 1
        });
        AddRfqVersion(
            rfqId, 1, command.OrganizationId, process, RfqStatus.Draft, currency, command.Terms,
            command.Weights, lines, openedAt: null, deadline, predecessorVersion: null,
            parameters, digest, actorUserId, now, reason: null);
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CreateRfq,
            command.CommandKey, fingerprint, rfqId, 1, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionRfqOpened, Actor(actorUserId), null,
            ["currency", "terms", "weights", "deadline", "lines"], correlationReference,
            $"rfq:{rfqId:D}:created",
            Target(SourcingCodes.TargetRfq, rfqId, 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingRfqOutcome(rfqId, 1, "DRAFT", digest, Replayed: false);
    }

    /// <summary>
    /// Opens the RFQ once (REQ-02): the opened instant becomes part of an immutable successor, the
    /// process changes <c>DRAFT→ACTIVE</c> and the <c>SOURCING</c> takeover is registered (REQ-01).
    /// </summary>
    public async Task<SourcingRfqOutcome> OpenRfqAsync(
        TransitionRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (rfq, current, process) = await RequireRfqAsync(
            command.OrganizationId, command.RfqId, command.ExpectedVersion, cancellationToken);
        if ((RfqStatus)current.Status != RfqStatus.Draft)
        {
            throw new DomainConflictException("Only a draft RFQ can be opened, and it is opened once.");
        }

        if (current.ResponseDeadline <= now)
        {
            throw new DomainConflictException("An RFQ cannot be opened after its response deadline.");
        }

        if (process.State != (int)SourcingProcessState.Draft)
        {
            throw new DomainConflictException("Only a draft sourcing process can be activated.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            SourcingCommandFingerprints.OpenRfq, command.OrganizationId, actorUserId, command.RfqId,
            command.ExpectedVersion, reason: null, newDeadline: null, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.OpenRfq,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different RFQ opening.");
            }

            var stored = await RequireRfqVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingRfqOutcome(
                stored.RfqId, stored.Version, SourcingStateCodes.RfqStatusCode((RfqStatus)stored.Status),
                stored.ContentDigest, Replayed: true);
        }

        var version = Successor(
            rfq, current, RfqStatus.Open, openedAt: now, deadline: current.ResponseDeadline, occurredAt: now);
        process.State = (int)SourcingProcessState.Active;
        process.Version += 1;
        process.UpdatedAt = now;
        // SPEC 11 REQ-10: the takeover of a new operation is recorded per line, so a subset can be
        // fenced without locking the whole request. The legacy request-wide row stays historical.
        {
            var processLines = await dbContext.SourcingProcessLines
                .AsNoTracking()
                .Where(line => line.ProcessId == process.Id)
                .OrderBy(line => line.LineId)
                .ToArrayAsync(cancellationToken);
            var requestDigest = await dbContext.PurchaseRequestVersions
                .AsNoTracking()
                .Where(row => row.RequestId == process.RequestId && row.Version == process.RequestVersion)
                .Select(row => row.ContentDigest)
                .SingleAsync(cancellationToken);
            await takeovers.AcquireAsync(
                command.OrganizationId,
                process.RequestId,
                process.RequestVersion,
                requestDigest,
                processLines
                    .Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                        line.LineId, line.LineVersion, line.LineContentDigest))
                    .ToArray(),
                PurchaseRequestLineOwner.Sourcing,
                process.Id,
                process.Version,
                SourcingCanonicalizer.RefSetDigest(processLines
                    .Select(line => new SourcingContentRef(
                        line.LineId, line.LineVersion, line.LineContentDigest))),
                "SOURCING",
                predecessorConsumerId: null,
                predecessorConsumerVersion: null,
                actorUserId.ToString("D"),
                now,
                cancellationToken);
        }
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.OpenRfq,
            command.CommandKey, fingerprint, rfq.Id, version.Record.Version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionProcessActivated, Actor(actorUserId), null,
            ["state", "takeover"], correlationReference,
            $"process:{process.Id:D}:v{process.Version}:activated",
            Target(SourcingCodes.TargetProcess, process.Id, process.Version), now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionRfqOpened, Actor(actorUserId), null,
            ["status", "opened_at"], correlationReference,
            $"rfq:{rfq.Id:D}:v{version.Record.Version}:opened",
            Target(SourcingCodes.TargetRfq, rfq.Id, version.Record.Version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingRfqOutcome(
            rfq.Id, version.Record.Version, "OPEN", version.Record.ContentDigest, Replayed: false);
    }

    /// <summary>
    /// Extends the deadline from <c>OPEN</c> with an expected version, a strictly later deadline and
    /// a reason; the previous deadline stays recorded so a late answer is never reclassified (REQ-02).
    /// </summary>
    public async Task<SourcingRfqOutcome> ExtendDeadlineAsync(
        TransitionRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = SourcingCodes.Reason(command.Reason);
        var newDeadline = (command.NewDeadline
            ?? throw new DomainValidationException("An extension requires its new deadline")).ToUniversalTime();
        var (rfq, current, process) = await RequireRfqAsync(
            command.OrganizationId, command.RfqId, command.ExpectedVersion, cancellationToken);
        RequireLiveProcess(process, allowDraft: false);
        if ((RfqStatus)current.Status != RfqStatus.Open)
        {
            throw new DomainConflictException("Only an open RFQ can extend its deadline.");
        }

        if (newDeadline <= current.ResponseDeadline)
        {
            throw new DomainConflictException(
                "A new deadline must be strictly later than the deadline in force.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            SourcingCommandFingerprints.ExtendRfqDeadline, command.OrganizationId, actorUserId, command.RfqId,
            command.ExpectedVersion, reason, newDeadline, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.ExtendRfqDeadline,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different extension.");
            }

            var stored = await RequireRfqVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingRfqOutcome(
                stored.RfqId, stored.Version, SourcingStateCodes.RfqStatusCode((RfqStatus)stored.Status),
                stored.ContentDigest, Replayed: true);
        }

        var version = Successor(
            rfq, current, RfqStatus.Open, openedAt: current.OpenedAt, deadline: newDeadline, occurredAt: now);
        dbContext.RfqDeadlineExtensions.Add(new RfqDeadlineExtensionRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            RfqId = rfq.Id,
            FromVersion = current.Version,
            ToVersion = version.Record.Version,
            PreviousDeadline = current.ResponseDeadline,
            NewDeadline = newDeadline,
            ActorUserId = actorUserId,
            OccurredAt = now,
            Reason = reason
        });
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.ExtendRfqDeadline,
            command.CommandKey, fingerprint, rfq.Id, version.Record.Version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionRfqExtended, Actor(actorUserId), null,
            ["response_deadline"], correlationReference,
            $"rfq:{rfq.Id:D}:v{version.Record.Version}:extended",
            Target(SourcingCodes.TargetRfq, rfq.Id, version.Record.Version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingRfqOutcome(
            rfq.Id, version.Record.Version, "OPEN", version.Record.ContentDigest, Replayed: false);
    }

    /// <summary>Closes an RFQ that already reached its deadline (REQ-02).</summary>
    public async Task<SourcingRfqOutcome> CloseRfqAsync(
        TransitionRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (rfq, current, process) = await RequireRfqAsync(
            command.OrganizationId, command.RfqId, command.ExpectedVersion, cancellationToken);
        RequireLiveProcess(process, allowDraft: false);
        if ((RfqStatus)current.Status != RfqStatus.Open)
        {
            throw new DomainConflictException("Only an open RFQ can be closed.");
        }

        if (current.ResponseDeadline > now)
        {
            throw new DomainConflictException("The RFQ cannot be closed before its response deadline.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            SourcingCommandFingerprints.CloseRfq, command.OrganizationId, actorUserId, command.RfqId,
            command.ExpectedVersion, reason: null, newDeadline: null, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CloseRfq,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different closing.");
            }

            var stored = await RequireRfqVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingRfqOutcome(
                stored.RfqId, stored.Version, SourcingStateCodes.RfqStatusCode((RfqStatus)stored.Status),
                stored.ContentDigest, Replayed: true);
        }

        var version = Successor(
            rfq, current, RfqStatus.Closed, openedAt: current.OpenedAt, deadline: current.ResponseDeadline,
            occurredAt: now);
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CloseRfq,
            command.CommandKey, fingerprint, rfq.Id, version.Record.Version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionRfqClosed, Actor(actorUserId), null,
            ["status"], correlationReference,
            $"rfq:{rfq.Id:D}:v{version.Record.Version}:closed",
            Target(SourcingCodes.TargetRfq, rfq.Id, version.Record.Version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingRfqOutcome(
            rfq.Id, version.Record.Version, "CLOSED", version.Record.ContentDigest, Replayed: false);
    }

    /// <summary>
    /// Cancels an RFQ with a mandatory reason. Received answers are never hidden or deleted: they
    /// stay consultable as history (REQ-02, REQ-03).
    /// </summary>
    public async Task<SourcingRfqOutcome> CancelRfqAsync(
        TransitionRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = SourcingCodes.Reason(command.Reason);
        var (rfq, current, process) = await RequireRfqAsync(
            command.OrganizationId, command.RfqId, command.ExpectedVersion, cancellationToken);
        if ((RfqStatus)current.Status is RfqStatus.Closed or RfqStatus.Cancelled)
        {
            throw new DomainConflictException("A closed or cancelled RFQ is terminal.");
        }

        if (process.State is not ((int)SourcingProcessState.Draft or (int)SourcingProcessState.Active))
        {
            throw new DomainConflictException("A cancelled or awarded process cannot cancel its RFQ.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            SourcingCommandFingerprints.CancelRfq, command.OrganizationId, actorUserId, command.RfqId,
            command.ExpectedVersion, reason, newDeadline: null, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CancelRfq,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different cancellation.");
            }

            var stored = await RequireRfqVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingRfqOutcome(
                stored.RfqId, stored.Version, SourcingStateCodes.RfqStatusCode((RfqStatus)stored.Status),
                stored.ContentDigest, Replayed: true);
        }

        var version = Successor(
            rfq, current, RfqStatus.Cancelled, openedAt: current.OpenedAt, deadline: current.ResponseDeadline,
            occurredAt: now);
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CancelRfq,
            command.CommandKey, fingerprint, rfq.Id, version.Record.Version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionRfqCancelled, Actor(actorUserId), null,
            ["status"], correlationReference,
            $"rfq:{rfq.Id:D}:v{version.Record.Version}:cancelled",
            Target(SourcingCodes.TargetRfq, rfq.Id, version.Record.Version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingRfqOutcome(
            rfq.Id, version.Record.Version, "CANCELLED", version.Record.ContentDigest, Replayed: false);
    }

    /// <summary>
    /// Cancels a pre-award process through a conditional update guarded by the expected version
    /// (REQ-01): the takeover is released and the RFQ history is preserved.
    /// </summary>
    public async Task<SourcingProcessView> CancelProcessAsync(
        CancelSourcingProcessCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = SourcingCodes.Reason(command.Reason);
        var process = await RequireProcessAsync(command.OrganizationId, command.ProcessId, cancellationToken);
        if (process.State is not ((int)SourcingProcessState.Draft or (int)SourcingProcessState.Active))
        {
            throw new DomainConflictException("Only a pre-award sourcing process can be cancelled.");
        }

        if (process.Version != command.ExpectedProcessVersion)
        {
            throw new DomainConflictException("The sourcing process version is stale.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            SourcingCommandFingerprints.CancelProcess, command.OrganizationId, actorUserId, command.ProcessId,
            command.ExpectedProcessVersion, reason, newDeadline: null, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CancelProcess,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null)
            {
                throw new DomainConflictException("The command key was reused with a different cancellation.");
            }

            return await GetProcessAsync(command.OrganizationId, replay.ResultId.Value, cancellationToken);
        }

        var applied = await dbContext.SourcingProcesses
            .Where(record =>
                record.Id == process.Id &&
                record.OrganizationId == command.OrganizationId &&
                record.State == (int)SourcingProcessState.Active &&
                record.Version == command.ExpectedProcessVersion)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(record => record.State, (int)SourcingProcessState.Cancelled)
                    .SetProperty(record => record.Version, command.ExpectedProcessVersion + 1)
                    .SetProperty(record => record.CancellationReason, reason)
                    .SetProperty(record => record.UpdatedAt, now),
                cancellationToken);
        if (applied == 0)
        {
            // The loser of a concurrent cancel/publish race re-reads and reports the conflict (REQ-01).
            var current = await RequireProcessAsync(command.OrganizationId, command.ProcessId, cancellationToken);
            if (current.Version != command.ExpectedProcessVersion + 1 ||
                current.State != (int)SourcingProcessState.Cancelled)
            {
                throw new DomainConflictException("The sourcing process changed concurrently.");
            }
        }

        var takeover = await dbContext.SourcingTakeovers
            .SingleOrDefaultAsync(
                record => record.ProcessId == process.Id && record.ReleasedAt == null,
                cancellationToken);
        if (takeover is not null)
        {
            takeover.ReleasedAt = now;
            takeover.ReleaseReason = reason;
        }

        {
            // SPEC 11 REQ-10: a pre-award cancellation releases every per-line takeover of the
            // process so another consumer can take the lines over.
            await takeovers.ReleaseAsync(
                command.OrganizationId,
                "SOURCING",
                process.Id,
                TakeoverState.Released,
                reason,
                now,
                cancellationToken);
        }

        var rfq = await dbContext.Rfqs
            .SingleOrDefaultAsync(record => record.ProcessId == process.Id, cancellationToken);
        if (rfq is not null)
        {
            var rfqVersion = await dbContext.RfqVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.RfqId == rfq.Id && record.Version == rfq.CurrentVersion,
                    cancellationToken);
            if ((RfqStatus)rfqVersion.Status is RfqStatus.Draft or RfqStatus.Open)
            {
                var cancelled = Successor(
                    rfq, rfqVersion, RfqStatus.Cancelled, rfqVersion.OpenedAt, rfqVersion.ResponseDeadline, now);
                AddAudit(
                    command.OrganizationId, SourcingCodes.ActionRfqCancelled, Actor(actorUserId), null,
                    ["status"], correlationReference,
                    $"rfq:{rfq.Id:D}:v{cancelled.Record.Version}:cancelled",
                    Target(SourcingCodes.TargetRfq, rfq.Id, cancelled.Record.Version), now);
            }
        }

        // REQ-13: the local attempts of the cancelled process are abandoned without a signal, so the
        // Purchase Request prerequisites stay WAITING for another process. The processor is optional
        // so the process service keeps working in a composition without owners.
        if (prerequisiteProcessor is not null)
        {
            _ = await prerequisiteProcessor.AbandonAttemptsAsync(process.Id, now, cancellationToken);
        }

        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.CancelProcess,
            command.CommandKey, fingerprint, process.Id, command.ExpectedProcessVersion + 1, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionProcessCancelled, Actor(actorUserId), null,
            ["state", "takeover"], correlationReference,
            $"process:{process.Id:D}:v{command.ExpectedProcessVersion + 1}:cancelled",
            Target(SourcingCodes.TargetProcess, process.Id, command.ExpectedProcessVersion + 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetProcessAsync(command.OrganizationId, process.Id, cancellationToken);
    }

    /// <summary>
    /// True while a live takeover of the request exists. SPEC 06 uses it to refuse revising or
    /// cancelling a consumed Purchase Request (REQ-01).
    /// </summary>
    public async Task<bool> HasLiveTakeoverAsync(
        Guid organizationId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (await takeovers.HasActiveAsync(organizationId, requestId, cancellationToken))
        {
            return true;
        }

        return await dbContext.SourcingTakeovers
            .AsNoTracking()
            .AnyAsync(
                takeover => takeover.OrganizationId == organizationId &&
                            takeover.RequestId == requestId &&
                            takeover.ReleasedAt == null,
                cancellationToken);
    }

    public async Task<SourcingProcessView> GetProcessAsync(
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken = default)
    {
        // Read-only projection: a conditional update bypasses the change tracker, so the projection
        // must never be served from a stale tracked entity (REQ-01).
        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == processId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The sourcing process does not exist in this organization.");
        var lines = await dbContext.SourcingProcessLines
            .AsNoTracking()
            .Where(line => line.ProcessId == processId)
            .OrderBy(line => line.LineId)
            .ToArrayAsync(cancellationToken);
        var live = await dbContext.SourcingTakeovers
            .AsNoTracking()
            .AnyAsync(
                takeover => takeover.ProcessId == processId && takeover.ReleasedAt == null,
                cancellationToken) ||
            await takeovers.HasActiveAsync(process.OrganizationId, process.RequestId, cancellationToken);
        return new SourcingProcessView(
            process.Id,
            process.OrganizationId,
            process.RequestId,
            process.RequestVersion,
            SourcingStateCodes.ProcessState((SourcingProcessState)process.State),
            process.Version,
            live,
            lines.Select(line => new SourcingProcessLine(
                new SourcingContentRef(line.LineId, line.LineVersion, line.LineContentDigest),
                line.RequestedQuantity,
                line.UnitCode)).ToArray(),
            process.ActorUserId,
            process.CreatedAt,
            process.UpdatedAt);
    }

    public async Task<IReadOnlyList<SourcingProcessView>> ListProcessesAsync(
        Guid organizationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var processes = await dbContext.SourcingProcesses
            .AsNoTracking()
            .Where(process => process.OrganizationId == organizationId)
            .OrderByDescending(process => process.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(process => process.Id)
            .ToArrayAsync(cancellationToken);
        var views = new List<SourcingProcessView>(processes.Length);
        foreach (var id in processes)
        {
            views.Add(await GetProcessAsync(organizationId, id, cancellationToken));
        }

        return views;
    }

    /// <summary>Full RFQ projection with its append-only versions and recorded extensions (REQ-14).</summary>
    public async Task<RfqView> GetRfqAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var rfq = await dbContext.Rfqs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == rfqId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        var versions = await dbContext.RfqVersions
            .AsNoTracking()
            .Where(version => version.RfqId == rfqId)
            .OrderBy(version => version.Version)
            .ToArrayAsync(cancellationToken);
        var extensions = await dbContext.RfqDeadlineExtensions
            .AsNoTracking()
            .Where(extension => extension.RfqId == rfqId)
            .OrderBy(extension => extension.ToVersion)
            .ToArrayAsync(cancellationToken);
        var current = versions.Single(version => version.Version == rfq.CurrentVersion);
        return new RfqView(
            rfq.Id,
            rfq.ProcessId,
            rfq.RequestId,
            rfq.RequestVersion,
            (RfqStatus)current.Status,
            rfq.CurrentVersion,
            current.ResponseDeadline,
            current.OpenedAt,
            versions.Select(ReadRfqVersion).ToArray(),
            extensions.Select(extension => new RfqDeadlineExtension(
                extension.RfqId,
                extension.FromVersion,
                extension.ToVersion,
                extension.PreviousDeadline,
                extension.NewDeadline,
                extension.ActorUserId,
                extension.OccurredAt,
                extension.Reason)).ToArray());
    }

    private async Task RequirePredecessorsSatisfiedAsync(
        CreateSourcingProcessCommand command,
        CancellationToken cancellationToken)
    {
        var approvalCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == command.OrganizationId &&
                record.SubjectId == command.RequestId &&
                record.SubjectVersion == command.RequestVersion &&
                record.SubjectType == "PURCHASE_REQUEST")
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException(
                "Sourcing requires the current approval case of the presented purchase request.");
        var prerequisites = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == approvalCase.Id)
            .ToArrayAsync(cancellationToken);
        var blocking = prerequisites
            .Where(record => !SourcingOwnedOwners.Contains(record.OwnerAdapterId))
            .Where(record => record.Status != (int)PrerequisiteStatus.Satisfied)
            .ToArray();
        if (blocking.Length != 0)
        {
            // Fail closed: sourcing never starts while a preceding node is waiting or failed (REQ-01).
            throw new DomainConflictException(
                "Sourcing requires every predecessor node of the current case to be satisfied.");
        }
    }

    private async Task<string> RequireSharedCurrencyAsync(
        SourcingProcessRecord process,
        IReadOnlyList<RfqLine> lines,
        CancellationToken cancellationToken)
    {
        var lineIds = lines.Select(line => line.LineRef.Id).ToArray();
        var versions = lines.ToDictionary(line => line.LineRef.Id, line => line.LineRef.Version);
        var currencies = await dbContext.PurchaseRequestLineVersions
            .AsNoTracking()
            .Where(record => record.RequestId == process.RequestId && lineIds.Contains(record.LineId))
            .Select(record => new { record.LineId, record.LineVersion, record.TransactionCurrency })
            .ToArrayAsync(cancellationToken);
        var covered = currencies
            .Where(record => versions.TryGetValue(record.LineId, out var version) && version == record.LineVersion)
            .Select(record => record.TransactionCurrency)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return covered.Length == 1
            ? covered[0]
            : throw new DomainConflictException(
                "Every covered line must share one requested currency and one effective configuration.");
    }

    /// <summary>
    /// Freezes the quotation minimums of every prerequisite the current case already declares: the
    /// owner later satisfies them with the original minimum or a verified waiver (REQ-02, REQ-05).
    /// </summary>
    private async Task<IReadOnlyList<SourcingQuotationParameter>> FreezeQuotationParametersAsync(
        SourcingProcessRecord process,
        CancellationToken cancellationToken)
    {
        var caseId = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == process.OrganizationId &&
                record.SubjectId == process.RequestId &&
                record.SubjectVersion == process.RequestVersion &&
                record.SubjectType == "PURCHASE_REQUEST")
            .OrderByDescending(record => record.Version)
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (caseId is null)
        {
            return [];
        }

        var prerequisites = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record =>
                record.CaseId == caseId.Value &&
                record.OwnerAdapterId == SourcingCodes.QuotationStatusOwnerAdapterId &&
                record.OwnerAdapterVersion == SourcingCodes.OwnerAdapterVersion)
            .ToArrayAsync(cancellationToken);
        var parameters = new List<SourcingQuotationParameter>(prerequisites.Length);
        foreach (var prerequisite in prerequisites)
        {
            parameters.Add(new SourcingQuotationParameter(
                prerequisite.Id,
                prerequisite.Key,
                ReadParameterInt(prerequisite.ParametersJson, "minimum_quotations"),
                ReadParameterInt(prerequisite.ParametersJson, "minimum_allowed_quotations"),
                ReadParameterTargets(prerequisite.TargetsJson)));
        }

        return parameters;
    }

    private static int? ReadParameterInt(string parametersJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            return document.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;
        }
        catch (JsonException exception)
        {
            throw new DomainConflictException(
                $"A quotation prerequisite carries corrupted parameters: {exception.Message}");
        }
    }

    private static IReadOnlyList<SourcingQuotationTarget> ReadParameterTargets(string targetsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(targetsJson);
            var targets = new List<SourcingQuotationTarget>();
            foreach (var target in document.RootElement.EnumerateArray())
            {
                targets.Add(new SourcingQuotationTarget(
                    target.GetProperty("id").GetGuid(),
                    target.GetProperty("type").GetString() ?? string.Empty,
                    target.GetProperty("version").GetInt32()));
            }

            return targets;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DomainConflictException("A quotation prerequisite carries corrupted targets.");
        }
    }

    private async Task<IReadOnlyList<RfqLine>> LoadRfqLinesAsync(
        SourcingProcessRecord process,
        CancellationToken cancellationToken)
    {
        var lines = await dbContext.SourcingProcessLines
            .AsNoTracking()
            .Where(line => line.ProcessId == process.Id)
            .OrderBy(line => line.LineId)
            .ToArrayAsync(cancellationToken);
        if (lines.Length == 0)
        {
            throw new SourcingDependencyUnavailableException("The sourcing process has no lines.");
        }

        return lines.Select(line => new RfqLine(
            new SourcingContentRef(line.LineId, line.LineVersion, line.LineContentDigest),
            line.RequestedQuantity,
            line.UnitCode)).ToArray();
    }

    /// <summary>
    /// Appends the successor version of one RFQ transition. The content keeps every frozen field of
    /// its predecessor and only changes status and deadline (REQ-02, NFR-02). The transition instant
    /// is the caller clock, so the version in force at any received answer is reproducible.
    /// </summary>
    private RfqSuccessor Successor(
        RfqRecord rfq,
        RfqVersionRecord current,
        RfqStatus status,
        DateTimeOffset? openedAt,
        DateTimeOffset deadline,
        DateTimeOffset occurredAt)
    {
        var terms = SourcingSerialization.ReadTerms(current.TermsJson);
        var weights = SourcingSerialization.ReadWeights(current.WeightsJson);
        var lines = SourcingSerialization.ReadRfqLines(current.LinesJson);
        var version = current.Version + 1;
        var digest = SourcingCanonicalizer.RfqContentDigest(
            rfq.Id, version, current.OrganizationId, current.ProcessId, current.RequestId,
            current.RequestVersion, status, current.Currency, terms, weights, openedAt, deadline,
            current.Version, lines);
        var record = new RfqVersionRecord
        {
            RfqId = rfq.Id,
            Version = version,
            OrganizationId = current.OrganizationId,
            ProcessId = current.ProcessId,
            RequestId = current.RequestId,
            RequestVersion = current.RequestVersion,
            Status = (int)status,
            Currency = current.Currency,
            TermsJson = current.TermsJson,
            WeightsJson = current.WeightsJson,
            LinesJson = current.LinesJson,
            ParametersJson = current.ParametersJson,
            OpenedAt = openedAt,
            ResponseDeadline = deadline,
            PredecessorVersion = current.Version,
            ContentDigest = digest,
            ActorUserId = current.ActorUserId,
            OccurredAt = occurredAt,
            Reason = current.Reason
        };
        dbContext.RfqVersions.Add(record);
        rfq.CurrentVersion = version;
        return new RfqSuccessor(record);
    }

    private void AddRfqVersion(
        Guid rfqId,
        int version,
        Guid organizationId,
        SourcingProcessRecord process,
        RfqStatus status,
        string currency,
        CommercialTerms terms,
        EvaluationWeightSet weights,
        IReadOnlyList<RfqLine> lines,
        DateTimeOffset? openedAt,
        DateTimeOffset responseDeadline,
        int? predecessorVersion,
        IReadOnlyList<SourcingQuotationParameter> parameters,
        string digest,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        string? reason)
    {
        dbContext.RfqVersions.Add(new RfqVersionRecord
        {
            RfqId = rfqId,
            Version = version,
            OrganizationId = organizationId,
            ProcessId = process.Id,
            RequestId = process.RequestId,
            RequestVersion = process.RequestVersion,
            Status = (int)status,
            Currency = currency,
            TermsJson = SourcingSerialization.Terms(terms),
            WeightsJson = SourcingSerialization.Weights(weights),
            LinesJson = SourcingSerialization.RfqLines(lines),
            ParametersJson = SourcingSerialization.Parameters(parameters),
            OpenedAt = openedAt,
            ResponseDeadline = responseDeadline,
            PredecessorVersion = predecessorVersion,
            ContentDigest = digest,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = reason
        });
    }

    private async Task<(RfqRecord Rfq, RfqVersionRecord Current, SourcingProcessRecord Process)> RequireRfqAsync(
        Guid organizationId,
        Guid rfqId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var rfq = await dbContext.Rfqs
            .SingleOrDefaultAsync(
                record => record.Id == rfqId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        if (rfq.CurrentVersion != expectedVersion)
        {
            throw new DomainConflictException("The RFQ version is stale.");
        }

        var current = await dbContext.RfqVersions
            .AsNoTracking()
            .SingleAsync(record => record.RfqId == rfqId && record.Version == expectedVersion, cancellationToken);
        var process = await RequireProcessAsync(organizationId, rfq.ProcessId, cancellationToken);
        return (rfq, current, process);
    }

    private async Task<RfqVersionRecord> RequireRfqVersionAsync(
        Guid organizationId,
        Guid rfqId,
        int version,
        CancellationToken cancellationToken) =>
        await dbContext.RfqVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RfqId == rfqId && record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The RFQ version does not exist.");

    private async Task<SourcingProcessRecord> RequireProcessAsync(
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken) =>
        await dbContext.SourcingProcesses
            .SingleOrDefaultAsync(
                record => record.Id == processId && record.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The sourcing process does not exist in this organization.");

    private static void RequireLiveProcess(SourcingProcessRecord process, bool allowDraft)
    {
        var state = (SourcingProcessState)process.State;
        var allowed = state == SourcingProcessState.Active || (allowDraft && state == SourcingProcessState.Draft);
        if (!allowed)
        {
            throw new DomainConflictException("The sourcing process is not live.");
        }
    }

    private async Task<SourcingCommandRecord?> FindCommandAsync(
        Guid organizationId,
        Guid actorUserId,
        string commandType,
        string commandKey,
        CancellationToken cancellationToken) =>
        await dbContext.SourcingCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record =>
                    record.OrganizationId == organizationId &&
                    record.ActorUserId == actorUserId &&
                    record.CommandType == commandType &&
                    record.CommandKey == commandKey,
                cancellationToken);

    private void AddCommand(
        Guid organizationId,
        Guid actorUserId,
        string commandType,
        string commandKey,
        string fingerprint,
        Guid resultId,
        int resultVersion,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingCommands.Add(new SourcingCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            CommandType = commandType,
            CommandKey = commandKey,
            Fingerprint = fingerprint,
            ResultId = resultId,
            ResultVersion = resultVersion,
            CreatedAt = occurredAt
        });

    private void AddAudit(
        Guid organizationId,
        string action,
        string actorJson,
        string? causeJson,
        IEnumerable<string> changedFields,
        string correlationReference,
        string effectKey,
        string targetJson,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = action,
            ActorJson = actorJson,
            CauseJson = causeJson,
            ChangedFieldsJson = JsonSerializer.Serialize(
                (changedFields ?? []).Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal)),
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = targetJson
        });

    private static RfqVersionView ReadRfqVersion(RfqVersionRecord record) => new(
        record.RfqId,
        record.Version,
        record.OrganizationId,
        record.ProcessId,
        record.RequestId,
        record.RequestVersion,
        (RfqStatus)record.Status,
        record.Currency,
        SourcingSerialization.ReadTerms(record.TermsJson),
        SourcingSerialization.ReadWeights(record.WeightsJson),
        record.OpenedAt,
        record.ResponseDeadline,
        record.PredecessorVersion,
        SourcingSerialization.ReadRfqLines(record.LinesJson),
        record.ContentDigest,
        record.ActorUserId,
        record.OccurredAt,
        record.Reason);

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";

    private static string Target(string type, Guid id, int version) =>
        $"{{\"id\":\"{id:D}\",\"type\":\"{type}\",\"version\":{version}}}";

    private sealed record RfqSuccessor(RfqVersionRecord Record);
}
