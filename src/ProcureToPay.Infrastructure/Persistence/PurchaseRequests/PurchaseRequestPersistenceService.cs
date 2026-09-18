using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

public sealed record PurchaseRequestCreation(Guid RequestId, int Version, string ContentDigest, bool Replayed);

public sealed record PurchaseRequestRevision(
    Guid RequestId,
    int Version,
    string ContentDigest,
    int AddedLines,
    int ChangedLines,
    int RemovedLines,
    bool Replayed);

public sealed record PurchaseRequestCancellation(Guid RequestId, int Version, bool Replayed);

public sealed record PurchaseRequestLineView(
    Guid LineId,
    int LineVersion,
    PurchaseRequestLineContent Content,
    string ContentDigest);

/// <summary>Persisted owner answers of one presented version, minimized for an AUDITOR (REQ-10).</summary>
public sealed record PurchaseRequestAttestationView(
    int Version,
    DateTimeOffset AttestedAt,
    IReadOnlyList<PurchaseRequestReferenceAssertion> Assertions);

public sealed record PurchaseRequestVersionView(
    int Version,
    int? PredecessorVersion,
    string BusinessJustification,
    Guid LegalEntityId,
    int LegalEntityVersion,
    string? RevisionKey,
    string? Reason,
    Guid ActorUserId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseRequestLineView> Lines);

public sealed record PurchaseRequestView(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterId,
    PurchaseRequestStatus Status,
    int CurrentVersion,
    IReadOnlyList<PurchaseRequestVersionView> Versions,
    IReadOnlyList<PurchaseRequestRevisionDelta> Deltas);

/// <summary>
/// Immutable version chain of a Purchase Request (REQ-01, REQ-02, REQ-10): every accepted command
/// appends exactly one successor, reuses unchanged line versions and never rewrites a snapshot.
/// </summary>
public sealed class PurchaseRequestPersistenceService(
    ProcureToPayDbContext dbContext,
    ILogger<PurchaseRequestPersistenceService> logger,
    ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseRequestLineTakeoverService? takeovers = null)
{
    /// <summary>
    /// SPEC 11 REQ-10: the per-line takeover fence always applies, even when a caller builds this
    /// service without injecting the takeover service.
    /// </summary>
    private readonly ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseRequestLineTakeoverService
        takeovers = takeovers ??
            new ProcureToPay.Infrastructure.Persistence.PurchaseOrders.PurchaseRequestLineTakeoverService(
                dbContext);

    public const string CommandCreate = "CREATE";
    public const string CommandRevision = "REVISION";
    public const string CommandCancel = "CANCEL";
    public const string CommandSubmit = "SUBMIT";

    public async Task<PurchaseRequestCreation> CreateAsync(
        PurchaseRequestCreateCommand command,
        Guid actorUserId,
        string correlation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OrganizationId == Guid.Empty || command.RequesterId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A create command requires the complete actor identity.");
        }

        var drafts = (command.Lines ?? []).ToImmutableArray();
        if (drafts.Length == 0)
        {
            throw new DomainValidationException("A purchase request requires at least one line.");
        }

        if (drafts.Length > PurchaseRequestLimits.MaxLines)
        {
            throw new PurchaseRequestPayloadTooLargeException(
                $"A purchase request supports at most {PurchaseRequestLimits.MaxLines} lines.");
        }

        var duplicatedKey = drafts
            .GroupBy(draft => draft.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatedKey is not null)
        {
            throw new DomainConflictException("A create command cannot repeat a client line key.");
        }

        var fingerprint = PurchaseRequestCanonicalizer.CreateFingerprint(
            command.OrganizationId,
            actorUserId,
            command.LegalEntityRef,
            command.BusinessJustification,
            command.RevisionKey,
            command.Reason,
            drafts);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, CommandCreate, command.RevisionKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The create key was reused with a different command.");
            }

            var existing = await dbContext.PurchaseRequestVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.RequestId == replay.RequestId && record.Version == replay.RequestVersion,
                    cancellationToken);
            return new PurchaseRequestCreation(replay.RequestId, replay.RequestVersion, existing.ContentDigest, true);
        }

        var requestId = Guid.NewGuid();
        var createdAt = occurredAt.ToUniversalTime();
        var lineRecords = drafts.Select(draft => MaterializeLine(
            requestId,
            Guid.NewGuid(),
            1,
            command.OrganizationId,
            draft.Content,
            actorUserId,
            createdAt)).ToArray();
        var references = lineRecords
            .Select(record => new PurchaseRequestLineRef(record.LineId, record.LineVersion, record.ContentDigest))
            .ToArray();
        var snapshot = new PurchaseRequestSnapshot(
            requestId,
            1,
            null,
            command.OrganizationId,
            command.RequesterId,
            command.LegalEntityRef,
            command.BusinessJustification,
            references);
        var contentDigest = PurchaseRequestCanonicalizer.RequestContentDigest(snapshot);

        dbContext.PurchaseRequests.Add(new PurchaseRequestRecord
        {
            Id = requestId,
            OrganizationId = command.OrganizationId,
            RequesterId = command.RequesterId,
            LegalEntityId = command.LegalEntityRef.Id,
            LegalEntityVersion = command.LegalEntityRef.Version,
            CurrentVersion = 1,
            Status = (int)PurchaseRequestStatus.Draft,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
        dbContext.PurchaseRequestVersions.Add(new PurchaseRequestVersionRecord
        {
            RequestId = requestId,
            Version = 1,
            PredecessorVersion = null,
            OrganizationId = command.OrganizationId,
            RequesterId = command.RequesterId,
            LegalEntityId = command.LegalEntityRef.Id,
            LegalEntityVersion = command.LegalEntityRef.Version,
            BusinessJustification = command.BusinessJustification,
            ContentDigest = contentDigest,
            RevisionKey = command.RevisionKey,
            Reason = command.Reason,
            ActorUserId = actorUserId,
            CreatedAt = createdAt
        });
        dbContext.PurchaseRequestLineVersions.AddRange(lineRecords);
        dbContext.PurchaseRequestVersionLines.AddRange(references.Select(reference =>
            new PurchaseRequestVersionLineRecord
            {
                RequestId = requestId,
                RequestVersion = 1,
                LineId = reference.Id,
                LineVersion = reference.Version,
                ContentDigest = reference.ContentDigest
            }));
        dbContext.PurchaseRequestCommands.Add(new PurchaseRequestCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            ActorUserId = actorUserId,
            CommandType = CommandCreate,
            CommandKey = command.RevisionKey,
            Fingerprint = fingerprint,
            RequestId = requestId,
            RequestVersion = 1,
            CreatedAt = createdAt
        });
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            RequestId = requestId,
            RequestVersion = 1,
            Action = "CREATED",
            BeforeStatus = null,
            AfterStatus = (int)PurchaseRequestStatus.Draft,
            ActorUserId = actorUserId,
            ReasonCode = "CREATE",
            Reason = command.Reason,
            CorrelationReference = correlation,
            OccurredAt = createdAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Purchase request {RequestId} version {Version} created with {LineCount} line(s).",
            requestId,
            1,
            references.Length);
        return new PurchaseRequestCreation(requestId, 1, contentDigest, false);
    }

    public async Task<PurchaseRequestRevision> ReviseAsync(
        PurchaseRequestRevisionCommand command,
        Guid organizationId,
        Guid actorUserId,
        string correlation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = PurchaseRequestCanonicalizer.RevisionFingerprint(
            organizationId,
            command.RequestId,
            actorUserId,
            command.ExpectedRequestVersion,
            command.BusinessJustification,
            command.RevisionKey,
            command.Reason,
            command.Retained,
            command.Changed,
            command.Added,
            command.Removed);
        var replay = await FindCommandAsync(
            organizationId, actorUserId, CommandRevision, command.RevisionKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The revision key was reused with a different command.");
            }

            var existing = await dbContext.PurchaseRequestVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.RequestId == replay.RequestId && record.Version == replay.RequestVersion,
                    cancellationToken);
            return new PurchaseRequestRevision(
                replay.RequestId, replay.RequestVersion, existing.ContentDigest, 0, 0, 0, true);
        }

        var request = await dbContext.PurchaseRequests
            .SingleOrDefaultAsync(
                record => record.Id == command.RequestId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (request.CurrentVersion != command.ExpectedRequestVersion)
        {
            throw new DomainConflictException(
                "The purchase request changed under the command; reload and retry.");
        }

        await RequireNoSourcingTakeoverAsync(request.Id, cancellationToken);
        var status = (PurchaseRequestStatus)request.Status;
        if (status is PurchaseRequestStatus.Rejected or PurchaseRequestStatus.Cancelled)
        {
            throw new DomainConflictException("A terminal purchase request cannot be revised.");
        }

        var previous = await LoadVersionAsync(request.Id, request.CurrentVersion, cancellationToken);
        var previousByLine = (await LoadLinesAsync(request.Id, request.CurrentVersion, cancellationToken))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var retained = (command.Retained ?? []).ToImmutableArray();
        var changed = (command.Changed ?? []).ToImmutableArray();
        var added = (command.Added ?? []).ToImmutableArray();
        var removed = (command.Removed ?? []).ToImmutableArray();

        foreach (var reference in retained)
        {
            if (!previousByLine.TryGetValue(reference.Id, out var stored) ||
                stored.LineVersion != reference.Version ||
                !string.Equals(stored.ContentDigest, reference.ContentDigest, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A retained line must keep the exact previous version and content digest.");
            }
        }

        var changedPlans = new List<(PurchaseRequestLineChange Change, PurchaseRequestLineView Previous, int Version)>();
        foreach (var entry in changed)
        {
            if (!previousByLine.TryGetValue(entry.LineId, out var stored))
            {
                throw new DomainConflictException("A changed line must exist in the previous version.");
            }

            if (stored.LineVersion != entry.ExpectedVersion ||
                !string.Equals(stored.ContentDigest, entry.ExpectedContentDigest, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A changed line must declare the exact previous version and content digest.");
            }

            changedPlans.Add((entry, stored, stored.LineVersion + 1));
        }

        foreach (var reference in removed)
        {
            if (!previousByLine.TryGetValue(reference.Id, out var stored) ||
                stored.LineVersion != reference.Version ||
                !string.Equals(stored.ContentDigest, reference.ContentDigest, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A removed line must declare the exact previous version and content digest.");
            }
        }

        foreach (var draft in added)
        {
            _ = draft.Key;
        }

        var previousIds = previousByLine.Keys.OrderBy(id => id).ToArray();
        var coveredPrevious = retained.Select(reference => reference.Id)
            .Concat(changed.Select(entry => entry.LineId))
            .Concat(removed.Select(reference => reference.Id))
            .ToArray();
        if (coveredPrevious.Length != coveredPrevious.Distinct().Count() ||
            !coveredPrevious.OrderBy(id => id).SequenceEqual(previousIds))
        {
            throw new DomainConflictException(
                "The revision delta must cover every previous line exactly once.");
        }

        var newVersion = request.CurrentVersion + 1;
        var createdAt = occurredAt.ToUniversalTime();
        var newLineRecords = new List<PurchaseRequestLineVersionRecord>();
        var newReferences = new List<PurchaseRequestLineRef>();
        foreach (var reference in retained)
        {
            newReferences.Add(reference);
        }

        foreach (var (entry, _, version) in changedPlans)
        {
            var record = MaterializeLine(
                request.Id, entry.LineId, version, organizationId, entry.Content, actorUserId, createdAt);
            newLineRecords.Add(record);
            newReferences.Add(new PurchaseRequestLineRef(record.LineId, record.LineVersion, record.ContentDigest));
        }

        var addedPlans = new List<(string Key, PurchaseRequestLineRef Reference)>();
        foreach (var draft in added)
        {
            var record = MaterializeLine(
                request.Id, Guid.NewGuid(), 1, organizationId, draft.Content, actorUserId, createdAt);
            newLineRecords.Add(record);
            var reference = new PurchaseRequestLineRef(record.LineId, record.LineVersion, record.ContentDigest);
            newReferences.Add(reference);
            addedPlans.Add((draft.Key, reference));
        }

        var snapshot = new PurchaseRequestSnapshot(
            request.Id,
            newVersion,
            request.CurrentVersion,
            request.OrganizationId,
            request.RequesterId,
            new VersionedEntityRefName(request.LegalEntityId, request.LegalEntityVersion).Value,
            command.BusinessJustification,
            newReferences);
        var contentDigest = PurchaseRequestCanonicalizer.RequestContentDigest(snapshot);
        var delta = new PurchaseRequestRevisionDelta(
            request.CurrentVersion,
            newVersion,
            retained,
            changedPlans.Select(plan => new PurchaseRequestLineReplacement(
                new PurchaseRequestLineRef(plan.Previous.LineId, plan.Previous.LineVersion, plan.Previous.ContentDigest),
                newReferences.Single(reference => reference.Id == plan.Change.LineId))),
            addedPlans.Select(plan => new PurchaseRequestLineAddition(plan.Key, plan.Reference)),
            removed);

        dbContext.PurchaseRequestVersions.Add(new PurchaseRequestVersionRecord
        {
            RequestId = request.Id,
            Version = newVersion,
            PredecessorVersion = request.CurrentVersion,
            OrganizationId = request.OrganizationId,
            RequesterId = request.RequesterId,
            LegalEntityId = request.LegalEntityId,
            LegalEntityVersion = request.LegalEntityVersion,
            BusinessJustification = command.BusinessJustification,
            ContentDigest = contentDigest,
            RevisionKey = command.RevisionKey,
            Reason = command.Reason,
            ActorUserId = actorUserId,
            CreatedAt = createdAt
        });
        dbContext.PurchaseRequestLineVersions.AddRange(newLineRecords);
        dbContext.PurchaseRequestVersionLines.AddRange(newReferences.Select(reference =>
            new PurchaseRequestVersionLineRecord
            {
                RequestId = request.Id,
                RequestVersion = newVersion,
                LineId = reference.Id,
                LineVersion = reference.Version,
                ContentDigest = reference.ContentDigest
            }));
        dbContext.PurchaseRequestRevisionDeltas.Add(new PurchaseRequestRevisionDeltaRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RequestId = request.Id,
            FromRequestVersion = request.CurrentVersion,
            ToRequestVersion = newVersion,
            DeltaJson = PurchaseRequestSerialization.Delta(delta),
            RevisionKey = command.RevisionKey,
            Fingerprint = fingerprint,
            ActorUserId = actorUserId,
            Reason = command.Reason,
            CreatedAt = createdAt
        });
        dbContext.PurchaseRequestCommands.Add(new PurchaseRequestCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            CommandType = CommandRevision,
            CommandKey = command.RevisionKey,
            Fingerprint = fingerprint,
            RequestId = request.Id,
            RequestVersion = newVersion,
            CreatedAt = createdAt
        });
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RequestId = request.Id,
            RequestVersion = newVersion,
            Action = "REVISED",
            BeforeStatus = request.Status,
            AfterStatus = (int)PurchaseRequestStatus.Draft,
            ActorUserId = actorUserId,
            ReasonCode = "REVISION",
            Reason = command.Reason,
            CorrelationReference = correlation,
            OccurredAt = createdAt
        });
        request.CurrentVersion = newVersion;
        request.Status = (int)PurchaseRequestStatus.Draft;
        request.UpdatedAt = createdAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Purchase request {RequestId} version {Version} created by revision {From}.",
            request.Id,
            newVersion,
            command.ExpectedRequestVersion);
        return new PurchaseRequestRevision(
            request.Id,
            newVersion,
            contentDigest,
            addedPlans.Count,
            changedPlans.Count,
            removed.Length,
            false);
    }

    public async Task<PurchaseRequestCancellation> CancelAsync(
        Guid requestId,
        int expectedVersion,
        string cancelKey,
        string reason,
        Guid organizationId,
        Guid actorUserId,
        string correlation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = PurchaseRequestCanonicalizer.SubmissionFingerprint(
            requestId,
            expectedVersion,
            organizationId,
            actorUserId,
            new string('0', 64),
            new string('0', 64),
            new string('0', 64),
            new string('0', 64),
            cancelKey,
            reason);
        var replay = await FindCommandAsync(
            organizationId, actorUserId, CommandCancel, cancelKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The cancellation key was reused with a different command.");
            }

            return new PurchaseRequestCancellation(replay.RequestId, replay.RequestVersion, true);
        }

        var request = await dbContext.PurchaseRequests
            .SingleOrDefaultAsync(
                record => record.Id == requestId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (request.CurrentVersion != expectedVersion)
        {
            throw new DomainConflictException(
                "The purchase request changed under the command; reload and retry.");
        }

        await RequireNoSourcingTakeoverAsync(request.Id, cancellationToken);

        var status = (PurchaseRequestStatus)request.Status;
        if (status is PurchaseRequestStatus.Rejected or PurchaseRequestStatus.Cancelled)
        {
            throw new DomainConflictException("A terminal purchase request cannot be cancelled.");
        }

        var occurredAtUtc = occurredAt.ToUniversalTime();
        dbContext.PurchaseRequestCommands.Add(new PurchaseRequestCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            CommandType = CommandCancel,
            CommandKey = cancelKey,
            Fingerprint = fingerprint,
            RequestId = request.Id,
            RequestVersion = request.CurrentVersion,
            CreatedAt = occurredAtUtc
        });
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RequestId = request.Id,
            RequestVersion = request.CurrentVersion,
            Action = "CANCELLED",
            BeforeStatus = request.Status,
            AfterStatus = (int)PurchaseRequestStatus.Cancelled,
            ActorUserId = actorUserId,
            ReasonCode = "REQUESTER_CANCEL",
            Reason = reason,
            CorrelationReference = correlation,
            OccurredAt = occurredAtUtc
        });
        request.Status = (int)PurchaseRequestStatus.Cancelled;
        request.UpdatedAt = occurredAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PurchaseRequestCancellation(request.Id, request.CurrentVersion, false);
    }

    /// <summary>Attestations and minimized references of a visible request, ordered by version.</summary>
    public async Task<IReadOnlyList<PurchaseRequestAttestationView>> ReadAttestationsAsync(
        Guid requestId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.PurchaseRequestReferenceAttestations
            .AsNoTracking()
            .Where(record => record.RequestId == requestId && record.OrganizationId == organizationId)
            .OrderBy(record => record.RequestVersion)
            .ToArrayAsync(cancellationToken);
        return records
            .Select(record => new PurchaseRequestAttestationView(
                record.RequestVersion,
                record.AttestedAt,
                PurchaseRequestSerialization.ReadAssertions(record.AssertionsJson)))
            .ToArray();
    }

    /// <summary>Projects the aggregate status of one immutable version, or null when not visible.</summary>
    public async Task<PurchaseRequestStatus?> FindStatusAsync(
        Guid requestId,
        int version,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await dbContext.PurchaseRequestLifecycleEvents
            .AsNoTracking()
            .Where(record => record.RequestId == requestId && record.RequestVersion == version &&
                             record.OrganizationId == organizationId)
            .OrderByDescending(record => record.OccurredAt)
            .ThenByDescending(record => record.Id)
            .Select(record => (int?)record.AfterStatus)
            .FirstOrDefaultAsync(cancellationToken) is int status
            ? (PurchaseRequestStatus)status
            : null;

    public async Task<PurchaseRequestView?> ReadAsync(
        Guid requestId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == requestId && record.OrganizationId == organizationId,
                cancellationToken);
        if (request is null)
        {
            return null;
        }

        var versions = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .Where(record => record.RequestId == requestId)
            .OrderBy(record => record.Version)
            .ToArrayAsync(cancellationToken);
        var views = new List<PurchaseRequestVersionView>(versions.Length);
        foreach (var version in versions)
        {
            var lines = await LoadLinesAsync(requestId, version.Version, cancellationToken);
            views.Add(new PurchaseRequestVersionView(
                version.Version,
                version.PredecessorVersion,
                version.BusinessJustification,
                version.LegalEntityId,
                version.LegalEntityVersion,
                version.RevisionKey,
                version.Reason,
                version.ActorUserId,
                version.CreatedAt,
                lines.Values.OrderBy(line => line.LineId).ToArray()));
        }

        var deltas = await dbContext.PurchaseRequestRevisionDeltas
            .AsNoTracking()
            .Where(record => record.RequestId == requestId)
            .OrderBy(record => record.ToRequestVersion)
            .Select(record => record.DeltaJson)
            .ToArrayAsync(cancellationToken);
        return new PurchaseRequestView(
            request.Id,
            request.OrganizationId,
            request.RequesterId,
            (PurchaseRequestStatus)request.Status,
            request.CurrentVersion,
            views,
            deltas.Select(ReadDelta).ToArray());
    }

    internal async Task<PurchaseRequestSnapshot> LoadVersionAsync(
        Guid requestId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.RequestId == requestId && candidate.Version == version,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request version does not exist.");
        var references = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(line => line.RequestId == requestId && line.RequestVersion == version)
            .OrderBy(line => line.LineId)
            .Select(line => new PurchaseRequestLineRef(line.LineId, line.LineVersion, line.ContentDigest))
            .ToArrayAsync(cancellationToken);
        var snapshot = new PurchaseRequestSnapshot(
            record.RequestId,
            record.Version,
            record.PredecessorVersion,
            record.OrganizationId,
            record.RequesterId,
            new VersionedEntityRefName(record.LegalEntityId, record.LegalEntityVersion).Value,
            record.BusinessJustification,
            references);
        var recomputed = PurchaseRequestCanonicalizer.RequestContentDigest(snapshot);
        if (!string.Equals(recomputed, record.ContentDigest, StringComparison.Ordinal))
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The stored request version content digest is not reproducible.");
        }

        return snapshot;
    }

    internal async Task<IReadOnlyDictionary<Guid, PurchaseRequestLineView>> LoadLinesAsync(
        Guid requestId,
        int version,
        CancellationToken cancellationToken)
    {
        var references = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(line => line.RequestId == requestId && line.RequestVersion == version)
            .OrderBy(line => line.LineId)
            .ToArrayAsync(cancellationToken);
        var views = new Dictionary<Guid, PurchaseRequestLineView>(references.Length);
        foreach (var reference in references)
        {
            var line = await dbContext.PurchaseRequestLineVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.LineId == reference.LineId &&
                                 candidate.LineVersion == reference.LineVersion,
                    cancellationToken)
                ?? throw new PurchaseRequestDependencyUnavailableException(
                    "A stored line version is missing from an attested manifest.");
            var content = PurchaseRequestSerialization.ReadLineContent(
                line.EstimatedGrossAmount,
                line.TransactionCurrency,
                line.BaseAmount,
                line.BaseCurrency,
                line.FiscalYear,
                line.PurchaseType,
                line.SpendCategoryJson,
                line.CostCenterJson,
                line.CostCenterDepartmentJson,
                line.BeneficiaryDepartmentJson,
                line.RequestedForUserJson,
                line.SupplierJson,
                line.PreferredProductJson,
                line.RequiredProductJson,
                line.ContractRequired,
                line.NonStandardTerms,
                line.AgreementStatus,
                line.NeedSummary,
                line.RiskAnswersJson,
                line.FxAttestationJson);
            views[reference.LineId] = new PurchaseRequestLineView(
                reference.LineId, reference.LineVersion, content, reference.ContentDigest);
        }

        return views;
    }

    private PurchaseRequestLineVersionRecord MaterializeLine(
        Guid requestId,
        Guid lineId,
        int lineVersion,
        Guid organizationId,
        PurchaseRequestLineContent content,
        Guid actorUserId,
        DateTimeOffset createdAt) =>
        new()
        {
            LineId = lineId,
            LineVersion = lineVersion,
            RequestId = requestId,
            OrganizationId = organizationId,
            EstimatedGrossAmount = content.EstimatedGrossAmount,
            TransactionCurrency = content.TransactionCurrency,
            BaseAmount = content.BaseAmount,
            BaseCurrency = content.BaseCurrency,
            FiscalYear = content.FiscalYear,
            PurchaseType = content.PurchaseType,
            SpendCategoryJson = PurchaseRequestSerialization.CodeRef(content.SpendCategoryRef),
            CostCenterJson = PurchaseRequestSerialization.EntityRef(content.CostCenterRef),
            CostCenterDepartmentJson = PurchaseRequestSerialization.EntityRef(content.CostCenterDepartmentRef),
            BeneficiaryDepartmentJson = PurchaseRequestSerialization.EntityRef(content.BeneficiaryDepartmentRef),
            RequestedForUserJson = PurchaseRequestSerialization.EntityRef(content.RequestedForUserRef),
            SupplierJson = content.SupplierRef is null
                ? null
                : PurchaseRequestSerialization.EntityRef(content.SupplierRef),
            PreferredProductJson = content.PreferredProductRef is null
                ? null
                : PurchaseRequestSerialization.EntityRef(content.PreferredProductRef),
            RequiredProductJson = content.RequiredProductRef is null
                ? null
                : PurchaseRequestSerialization.EntityRef(content.RequiredProductRef),
            ContractRequired = content.ContractRequired,
            NonStandardTerms = content.NonStandardTerms,
            AgreementStatus = content.AgreementStatus,
            NeedSummary = content.NeedSummary,
            RiskAnswersJson = PurchaseRequestSerialization.RiskAnswers(content.RiskAnswers),
            FxAttestationJson = content.FxAttestationRef is null
                ? null
                : PurchaseRequestSerialization.FxRef(content.FxAttestationRef),
            ContentDigest = PurchaseRequestCanonicalizer.LineContentDigest(
                organizationId, requestId, lineId, lineVersion, content),
            ActorUserId = actorUserId,
            CreatedAt = createdAt
        };

    private async Task RequireNoSourcingTakeoverAsync(Guid requestId, CancellationToken cancellationToken)
    {
        // SPEC 10 REQ-01 protects the exact request version consumed by a live sourcing takeover:
        // revising or cancelling it would orphan the proposal/award that is being built.
        var consumed = await dbContext.SourcingTakeovers
            .AsNoTracking()
            .AnyAsync(
                takeover => takeover.RequestId == requestId && takeover.ReleasedAt == null,
                cancellationToken);
        if (consumed)
        {
            throw new DomainConflictException(
                "The purchase request is consumed by a live sourcing takeover and cannot change.");
        }

        // SPEC 11 REQ-10: the per-line takeover table replaces the request-wide lock for new
        // operations, and an issued Purchase Order, a live claim or an authorized direct purchase
        // blocks the revision or cancellation of the request with an opaque conflict code.
        {
            var organizationId = await dbContext.PurchaseRequests
                .AsNoTracking()
                .Where(request => request.Id == requestId)
                .Select(request => request.OrganizationId)
                .SingleAsync(cancellationToken);
            if (await takeovers.HasActiveAsync(organizationId, requestId, cancellationToken))
            {
                throw new DomainConflictException("PR_REQUEST_CONSUMED");
            }

            var poIds = await dbContext.PurchaseOrders
                .AsNoTracking()
                .Where(order => order.RequestId == requestId)
                .Select(order => order.Id)
                .ToArrayAsync(cancellationToken);
            var liveClaim = poIds.Length != 0 && await dbContext.AwardConsumptionClaims
                .AsNoTracking()
                .AnyAsync(claim => poIds.Contains(claim.PoId) && claim.State < 3, cancellationToken);
            var livePurchaseOrder = await dbContext.PurchaseOrderVersions
                .AsNoTracking()
                .AnyAsync(
                    version => version.RequestId == requestId && version.State != 5,
                    cancellationToken);
            var authorizedDirectPurchase = await dbContext.DirectPurchaseAuthorizations
                .AsNoTracking()
                .AnyAsync(
                    authorization => authorization.OrganizationId == organizationId &&
                                     authorization.RequestId == requestId &&
                                     authorization.State == 1,
                    cancellationToken);
            if (liveClaim || livePurchaseOrder || authorizedDirectPurchase)
            {
                throw new DomainConflictException("PR_REQUEST_CONSUMED");
            }
        }
    }

    private static PurchaseRequestRevisionDelta ReadDelta(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var retained = PurchaseRequestSerialization.ReadLineRefs(root.GetProperty("retained").GetRawText());
        var removed = PurchaseRequestSerialization.ReadLineRefs(root.GetProperty("removed").GetRawText());
        var changed = root.GetProperty("changed").EnumerateArray()
            .Select(entry => new PurchaseRequestLineReplacement(
                PurchaseRequestSerialization.ReadLineRef(entry.GetProperty("previous").GetRawText()),
                PurchaseRequestSerialization.ReadLineRef(entry.GetProperty("replacement").GetRawText())))
            .ToArray();
        var added = root.GetProperty("added").EnumerateArray()
            .Select(entry => new PurchaseRequestLineAddition(
                entry.GetProperty("client_line_key").GetString()!,
                PurchaseRequestSerialization.ReadLineRef(entry.GetProperty("replacement").GetRawText())))
            .ToArray();
        return new PurchaseRequestRevisionDelta(
            root.GetProperty("from_request_version").GetInt32(),
            root.GetProperty("to_request_version").GetInt32(),
            retained,
            changed,
            added,
            removed);
    }

    private async Task<PurchaseRequestCommandRecord?> FindCommandAsync(
        Guid organizationId,
        Guid actorUserId,
        string commandType,
        string commandKey,
        CancellationToken cancellationToken) =>
        await dbContext.PurchaseRequestCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ActorUserId == actorUserId &&
                          record.CommandType == commandType &&
                          record.CommandKey == commandKey,
                cancellationToken);

    /// <summary>Typed helper so a stored legal entity can be re-projected into a snapshot.</summary>
    private sealed record VersionedEntityRefName(Guid Id, int Version)
    {
        public VersionedEntityRef Value { get; } = new("LEGAL_ENTITY", Id, Version);
    }
}

/// <summary>Payload-too-large condition of the Purchase Requests boundary (REQ-11).</summary>
