using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>One predecessor whose open reservations must count as available for its replacement (REQ-08).</summary>
public sealed record BudgetPredecessorHold(
    Guid AttemptId,
    Guid CaseId,
    IReadOnlyList<Guid> ReservedMovementIds);

/// <summary>Outcome of one processed budget prerequisite.</summary>
public sealed record BudgetProcessorOutcome(
    Guid AttemptId,
    string State,
    string? SignalResult,
    BudgetCheckResult CheckResult);

/// <summary>
/// Real processor of the <c>budget-check-owner/v1</c> prerequisite (SPEC 08 REQ-06, REQ-07, DEC-03).
/// It reserves every demand of the prerequisite all-or-nothing, then signals Approval with the
/// reproducible evidence; a technical failure leaves the prerequisite <c>WAITING</c> for a retry and
/// a terminal case is compensated instead of signalled. The attempt is durable, leased and fenced,
/// so a restart never posts a second reservation.
/// </summary>
public sealed class BudgetPrerequisiteProcessor(
    ProcureToPayDbContext dbContext,
    BudgetPersistenceService positions,
    BudgetLedgerService ledger,
    PurchaseRequests.PurchaseRequestBudgetDemandBuilder builder,
    ApprovalWorkflowService workflow,
    ApprovalInstanceIdentity instanceIdentity)
{
    /// <summary>Serializes this instance's lease heartbeat with its attempt checkpoints (REQ-07).</summary>
    private readonly SemaphoreSlim leaseGate = new(1, 1);

    /// <summary>Lease of one attempt before another instance may reclaim it (REQ-07).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>Readiness budget of one due attempt (REQ-07, NFR-05).</summary>
    public static readonly TimeSpan DueBudget = TimeSpan.FromSeconds(60);

    /// <summary>Interval of the independent lease heartbeat while an attempt is in flight (REQ-07).</summary>
    public static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(10);


    /// <summary>Claims every processable budget attempt and advances it once (REQ-07).</summary>
    public async Task<IReadOnlyList<BudgetProcessorOutcome>> ProcessDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // A waiting prerequisite needs its attempt created or advanced; a durable attempt that never
        // reached a terminal state is reclaimed even when its prerequisite already decided, so a
        // crash between the confirmed signal and the terminal checkpoint stays recoverable (REQ-07).
        var completed = BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed);
        var compensated = BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated);
        var waiting = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Status == (int)PrerequisiteStatus.Waiting &&
                             record.OwnerAdapterId == BudgetCodes.BudgetOwnerAdapterId &&
                             record.OwnerAdapterVersion == BudgetCodes.BudgetOwnerAdapterVersion)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var recoverable = await dbContext.BudgetPrerequisiteAttempts
            .AsNoTracking()
            .Where(record => record.State != completed && record.State != compensated)
            .Select(record => record.PrerequisiteId)
            .ToArrayAsync(cancellationToken);
        var candidates = waiting
            .Union(recoverable)
            .OrderBy(record => record)
            .ToArray();
        var outcomes = new List<BudgetProcessorOutcome>();
        foreach (var prerequisiteId in candidates)
        {
            var outcome = await ProcessOneAsync(prerequisiteId, now, cancellationToken);
            if (outcome is not null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Processes one prerequisite end to end. Returns null when the attempt is owned by another
    /// live lease, so two workers never post the same reservation twice (REQ-07).
    /// </summary>
    public async Task<BudgetProcessorOutcome?> ProcessOneAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var attempt = await FindOrCreateAttemptAsync(prerequisiteId, now, cancellationToken);
        if (!await ClaimAsync(attempt, now, cancellationToken))
        {
            return null;
        }

        // An independent heartbeat renews the lease while this instance processes the attempt, so a
        // blocked effect never lets the lease expire under a live worker (REQ-07).
        await using var heartbeat = new LeaseHeartbeat(
            dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The budget processor has no configured connection."),
            attempt.Id,
            instanceIdentity.Owner,
            attempt.FencingToken,
            LeaseRenewalInterval,
            LeaseDuration,
            leaseGate);
        heartbeat.Start();
        return await ProcessClaimedAsync(attempt, now, cancellationToken);
    }

    /// <summary>Advances one attempt whose lease is already held by this instance (REQ-07).</summary>
    private async Task<BudgetProcessorOutcome?> ProcessClaimedAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == attempt.CaseId, cancellationToken)
            ?? throw new BudgetDependencyUnavailableException("The approval case is not visible.");
        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == attempt.PrerequisiteId, cancellationToken);
        var ownerWorkload = new ApprovalWorkloadIdentity(
            prerequisite.OwnerWorkloadIssuer, prerequisite.OwnerWorkloadClientId);

        // A crash between the confirmed Approval signal and the terminal checkpoint leaves the
        // attempt non-terminal while Approval already decided. The recorded signal is consulted
        // first with the attempt's own key: a confirmed signal is finalized as-is and never
        // compensated nor signalled twice (REQ-07).
        var attemptCompleted = BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed);
        if (prerequisite.Status is (int)PrerequisiteStatus.Satisfied or (int)PrerequisiteStatus.Failed &&
            attempt.State != attemptCompleted)
        {
            var recorded = await dbContext.ApprovalPrerequisiteSignals
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.PrerequisiteId == attempt.PrerequisiteId &&
                                                record.SignalKey == attempt.SignalKey,
                    cancellationToken);
            if (recorded is not null)
            {
                if (!await HoldLeaseAsync(attempt, cancellationToken))
                {
                    return null;
                }

                var recordedResult = recorded.Satisfied ? "SATISFIED" : "FAILED";
                if (!await CompleteAsync(attempt, recordedResult, recorded.EvidenceDigest, now, cancellationToken))
                {
                    return null;
                }

                return new BudgetProcessorOutcome(
                    attempt.Id,
                    attemptCompleted,
                    recordedResult,
                    recorded.Satisfied ? BudgetCheckResult.Available : BudgetCheckResult.Insufficient);
            }
        }

        var demands = await BuildDemandsAsync(attempt, prerequisite.TargetsJson, cancellationToken);
        var parametersDigest = Sha256(prerequisite.ParametersJson);
        var source = new BudgetSource(
            BudgetCodes.PurchaseRequestSourceType,
            attempt.RequestId,
            attempt.RequestVersion,
            attempt.ParametersDigest);
        var actor = BudgetActor.ForWorkload(ownerWorkload.Issuer, ownerWorkload.ClientId);

        // A terminal case cannot be satisfied; any reservation already posted is compensated and the
        // prerequisite is never signalled as available (REQ-07, REQ-08).
        var caseStatus = (ApprovalCaseStatus)caseRecord.Status;
        if (caseStatus is ApprovalCaseStatus.Cancelled or ApprovalCaseStatus.Superseded)
        {
            if (!await HoldLeaseAsync(attempt, cancellationToken))
            {
                return null;
            }

            if (attempt.ReserveOperationId is not null)
            {
                var reversed = await ReleaseReservationsAsync(
                    attempt, source, actor, correlation: attempt.SignalKey, now, cancellationToken);
                if (reversed)
                {
                    if (!await HoldLeaseAsync(attempt, cancellationToken))
                    {
                        return null;
                    }

                    if (!await CompleteCompensatedAsync(attempt, now, cancellationToken))
                    {
                        return null;
                    }

                    return new BudgetProcessorOutcome(
                        attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated), null, BudgetCheckResult.Available);
                }
            }

            await MarkPendingAsync(attempt, "CASE_TERMINAL", now, cancellationToken);
            return new BudgetProcessorOutcome(
                attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending), null, BudgetCheckResult.Available);
        }

        var predecessors = await FindPredecessorHoldsAsync(attempt, cancellationToken);
        if (!await HoldLeaseAsync(attempt, cancellationToken))
        {
            return null;
        }

        BudgetReserveOutcome reserve;
        try
        {
            if (predecessors.Count > 0)
            {
                // REQ-08: with a superseded predecessor still holding funds, the replacement is
                // reserved through the serialized transfer, which counts that hold as available and
                // reverses it in the same transaction only when the whole new set fits.
                var transfer = await ledger.TransferReserveAsync(
                    attempt.OrganizationId,
                    attempt.RequestKey,
                    attempt.ReserveKey,
                    source,
                    actor,
                    "BUDGET_CHECK",
                    attempt.CaseId,
                    demands,
                    predecessors
                        .Select(hold => new BudgetPredecessorReservation(hold.CaseId, hold.ReservedMovementIds))
                        .ToArray(),
                    attempt.SignalKey,
                    cancellationToken,
                    LeaseFenceOf(attempt));
                reserve = new BudgetReserveOutcome(
                    transfer.Result,
                    transfer.RequestOperationId,
                    transfer.ReserveOperationId,
                    transfer.RequestedMovements,
                    transfer.ReservedMovements);
            }
            else
            {
                reserve = await ledger.ReserveAsync(
                    attempt.OrganizationId,
                    attempt.RequestKey,
                    attempt.ReserveKey,
                    source,
                    actor,
                    "BUDGET_CHECK",
                    attempt.CaseId,
                    demands,
                    attempt.SignalKey,
                    causeAuditId: null,
                    causeStream: null,
                    cancellationToken,
                    LeaseFenceOf(attempt));
            }
        }
        catch (Exception exception) when (exception is BudgetDependencyUnavailableException or
                                              BudgetReferenceInvalidException or DomainException)
        {
            await MarkPendingAsync(attempt, ErrorCode(exception), now, cancellationToken);
            throw new BudgetDependencyUnavailableException(
                $"{ErrorCode(exception)}: {exception.Message}", exception);
        }

        // The checkpoint that persists the confirmation also requires the exact current lease; an
        // expired lease never writes RESERVED|INSUFFICIENT (REQ-07).
        if (!await HoldLeaseAsync(attempt, cancellationToken))
        {
            return null;
        }

        try
        {
            if (!await RecordReserveAsync(attempt, reserve, now, cancellationToken))
            {
                return null;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // The lease was reclaimed while the checkpoint was saved: the new holder decides.
            dbContext.ChangeTracker.Clear();
            return null;
        }
        catch (Exception exception) when (exception is DomainException or DbUpdateException)
        {
            await MarkRetryAsync(attempt, ErrorCode(exception), now, cancellationToken);
            throw new BudgetDependencyUnavailableException(
                $"{ErrorCode(exception)}: {exception.Message}", exception);
        }

        if (!await HoldLeaseAsync(attempt, cancellationToken))
        {
            return null;
        }

        try
        {
            if (reserve.Result != BudgetCheckResult.Available)
            {
                await SignalAsync(
                    attempt,
                    prerequisite,
                    ownerWorkload,
                    satisfied: false,
                    evidenceDigest: null,
                    evidenceReference: null,
                    now,
                    cancellationToken);
                if (!await HoldLeaseAsync(attempt, cancellationToken))
                {
                    return null;
                }

                if (!await CompleteAsync(attempt, "FAILED", null, now, cancellationToken))
                {
                    return null;
                }

                return new BudgetProcessorOutcome(
                    attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed), "FAILED", reserve.Result);
            }

            var evidenceMovements = reserve.RequestedMovements.Concat(reserve.ReservedMovements).ToArray();
            var evidence = BudgetCanonicalJson.Digest(BudgetFingerprints.EvidencePreimage(
                attempt.OrganizationId,
                attempt.Id,
                attempt.CaseId,
                attempt.PrerequisiteId,
                now,
                BudgetCheckResult.Available,
                attempt.SignalKey,
                parametersDigest,
                attempt.SourceControlDigest,
                evidenceMovements));
            _ = predecessors;
            if (!await HoldLeaseAsync(attempt, cancellationToken))
            {
                return null;
            }

            await SignalAsync(
                attempt,
                prerequisite,
                ownerWorkload,
                satisfied: true,
                evidenceDigest: evidence,
                evidenceReference: $"budget://{attempt.Id:D}",
                now,
                cancellationToken);
            if (!await HoldLeaseAsync(attempt, cancellationToken))
            {
                return null;
            }

            if (!await CompleteAsync(attempt, "SATISFIED", evidence, now, cancellationToken))
            {
                return null;
            }

            return new BudgetProcessorOutcome(
                attempt.Id, BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed), "SATISFIED", BudgetCheckResult.Available);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The lease was reclaimed while this instance worked: it confirms no checkpoint.
            dbContext.ChangeTracker.Clear();
            return null;
        }
        catch (Exception exception) when (exception is DomainException or DbUpdateException)
        {
            // A failure after the operation was confirmed preserves the state, records the error
            // and releases the lease so the next sweep retries with the same keys (REQ-07).
            await MarkRetryAsync(attempt, ErrorCode(exception), now, cancellationToken);
            throw new BudgetDependencyUnavailableException(
                $"{ErrorCode(exception)}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Builds the demands from the persisted Purchase Request version and the material projection of
    /// the evaluation that created the case (REQ-05). The caller never supplies amounts or positions.
    /// </summary>
    private async Task<IReadOnlyList<BudgetDemand>> BuildDemandsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string targetsJson,
        CancellationToken cancellationToken)
    {
        var targets = ApprovalJsonPersistence.DeserializeTargets(targetsJson);
        var parameters = BudgetPrerequisiteParameters.Parse(attempt.ParametersJson);
        var targetSet = parameters.Demands
            .Select(demand => demand.Target)
            .Where(target => target is not null)
            .Select(target => target!)
            .ToArray();
        if (targetSet.Length == 0)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget prerequisite has no confirmed target to check.");
        }

        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == attempt.OrganizationId &&
                          record.RequestId == attempt.RequestId &&
                          record.RequestVersion == attempt.RequestVersion,
                cancellationToken)
            ?? throw new BudgetDependencyUnavailableException(
                "The budget prerequisite has no confirmed completeness manifest.");
        var build = await builder.BuildAsync(
            new BudgetDemandBuildRequest(
                BudgetCodes.DemandBuildVersion,
                attempt.OrganizationId,
                attempt.RequestId,
                attempt.RequestVersion,
                manifest.RequestContentDigest,
                manifest.PolicyManifestDigest,
                targetSet),
            cancellationToken);
        if (build.Demands.Count != targetSet.Length)
        {
            throw new BudgetDependencyUnavailableException(
                "The budget prerequisite targets do not belong to the confirmed request version.");
        }

        _ = targets;
        return build.Demands;
    }

    private async Task<BudgetPrerequisiteAttemptRecord> FindOrCreateAttemptAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.BudgetPrerequisiteAttempts
            .SingleOrDefaultAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken)
            ?? throw new DomainNotFoundException("The budget prerequisite is not visible.");
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisite.CaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var attempt = new BudgetPrerequisiteAttemptRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = caseRecord.OrganizationId,
            CaseId = prerequisite.CaseId,
            PrerequisiteId = prerequisiteId,
            PrerequisiteKey = prerequisite.Key,
            RequestId = caseRecord.SubjectId,
            RequestVersion = caseRecord.SubjectVersion,
            ParametersJson = prerequisite.ParametersJson,
            ParametersDigest = Sha256(prerequisite.ParametersJson),
            SourceControlDigest = prerequisite.SourceControlDigest,
            RequestKey = $"budget:{prerequisiteId:D}:request",
            ReserveKey = $"budget:{prerequisiteId:D}:reserve",
            SignalKey = $"budget:{prerequisiteId:D}:signal",
            CompensateKey = $"budget:{prerequisiteId:D}:compensate",
            State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending),
            DueAt = caseRecord.CreatedAt,
            NextAttemptAt = caseRecord.CreatedAt,
            Attempts = 0
        };
        dbContext.BudgetPrerequisiteAttempts.Add(attempt);
        try
        {
            await positions.SaveAsync(cancellationToken);
        }
        catch (DomainConflictException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.BudgetPrerequisiteAttempts
                .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        }

        return attempt;
    }

    /// <summary>
    /// Takes the lease of one attempt. A live lease held by another instance returns false, so the
    /// caller skips it; an expired lease is reclaimed by incrementing the fencing token (REQ-07).
    /// </summary>
    private async Task<bool> ClaimAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.State is "COMPLETED" or "COMPENSATED")
        {
            return false;
        }

        if (attempt.NextAttemptAt is DateTimeOffset due && due > now)
        {
            return false;
        }

        if (attempt.LeaseUntil is DateTimeOffset until && until > now)
        {
            return false;
        }

        attempt.LeaseOwner = instanceIdentity.Owner;
        attempt.LeaseUntil = now + LeaseDuration;
        attempt.FencingToken += 1;
        attempt.Attempts += 1;
        try
        {
            await positions.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DomainConflictException or
                                              DbUpdateConcurrencyException)
        {
            // Another instance claimed the same attempt first: this one skips it instead of
            // posting its own reservation (REQ-07).
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies that this instance still owns the attempt lease and renews it to a full lease
    /// before the next effect (REQ-07). The row is reloaded under the lease gate, so the
    /// independent heartbeat never surfaces as a false conflict; a lost, expired or reclaimed
    /// lease returns false and the caller aborts without posting a movement or a checkpoint.
    /// </summary>
    private async Task<bool> HoldLeaseAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        var expectedToken = attempt.FencingToken;
        var owner = instanceIdentity.Owner;
        return await SaveAttemptAsync(
            attempt,
            current =>
            {
                var now = DateTimeOffset.UtcNow;
                if (!string.Equals(current.LeaseOwner, owner, StringComparison.Ordinal) ||
                    current.FencingToken != expectedToken ||
                    current.LeaseUntil is not DateTimeOffset until ||
                    until <= now)
                {
                    throw new DomainConflictException("The budget attempt lease was lost.");
                }

                current.LeaseUntil = now + LeaseDuration;
            },
            cancellationToken);
    }

    /// <summary>
    /// Applies one checkpoint to the attempt row under the lease gate: the row is reloaded first,
    /// so this instance's heartbeat renewals never surface as a false concurrency conflict. A
    /// reclaim from another instance returns false without writing (REQ-07).
    /// </summary>
    private async Task<bool> SaveAttemptAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        Action<BudgetPrerequisiteAttemptRecord> mutate,
        CancellationToken cancellationToken)
    {
        await leaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = dbContext.Entry(attempt);
            if (entry.State == EntityState.Detached)
            {
                dbContext.Attach(attempt);
            }

            await entry.ReloadAsync(cancellationToken);
            mutate(attempt);
            await positions.SaveAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DomainConflictException or
                                              DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        finally
        {
            leaseGate.Release();
        }
    }

    /// <summary>
    /// Independent lease heartbeat (REQ-07): while this instance owns the attempt, a dedicated
    /// connection renews the 30-second lease every 10 seconds, so a blocked effect never lets the
    /// lease expire under a live worker. A renewal that affects no row means the attempt was
    /// reclaimed; the heartbeat stops and the transactional fence rejects every stale effect.
    /// </summary>
    private sealed class LeaseHeartbeat(
        string connectionString,
        Guid attemptId,
        string owner,
        int fencingToken,
        TimeSpan interval,
        TimeSpan duration,
        SemaphoreSlim gate) : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private Task? loop;

        public void Start() => loop = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(interval, cancellation.Token);
                    await gate.WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellation.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "UPDATE [Budget].[PrerequisiteAttempts] SET [LeaseUntil] = @until " +
                        "WHERE [Id] = @id AND [LeaseOwner] = @owner AND [FencingToken] = @token";
                    command.Parameters.AddWithValue("@until", DateTimeOffset.UtcNow + duration);
                    command.Parameters.AddWithValue("@id", attemptId);
                    command.Parameters.AddWithValue("@owner", owner);
                    command.Parameters.AddWithValue("@token", fencingToken);
                    var affected = await command.ExecuteNonQueryAsync(cancellation.Token);
                    if (affected == 0)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A transient renewal failure must not kill the attempt: the next tick retries
                    // and every effect is still fenced transactionally.
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop;
                }
                catch (OperationCanceledException)
                {
                    // The heartbeat stops with the attempt.
                }
            }

            cancellation.Dispose();
        }
    }

    /// <summary>The exact lease this instance holds for one attempt, verified transactionally (REQ-07).</summary>
    private BudgetLeaseFence LeaseFenceOf(BudgetPrerequisiteAttemptRecord attempt) =>
        new(attempt.Id, attempt.FencingToken, attempt.LeaseOwner ?? instanceIdentity.Owner);

    /// <summary>
    /// Records a failure after the operation was confirmed: the attempt keeps its state, the error
    /// and the next retry are recorded and the lease is released without clearing the ids. The next
    /// sweep reclaims it and retries the signal with the same key (REQ-07).
    /// </summary>
    private async Task MarkRetryAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.LastErrorCode = errorCode;
                current.NextAttemptAt = now.AddSeconds(1);
                current.LeaseOwner = null;
                current.LeaseUntil = null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Marks the attempt as reserved, or keeps the post-decision state of an insufficiency. The
    /// lease stays held until the attempt reaches a terminal state, so no other instance reclaims
    /// an in-flight attempt; only an expired lease (a dead worker) is reclaimable (REQ-07).
    /// </summary>
    private async Task<bool> RecordReserveAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        BudgetReserveOutcome reserve,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.RequestOperationId ??= reserve.RequestOperationId;
                current.ReserveOperationId ??= reserve.ReserveOperationId;
                current.State = reserve.Result == BudgetCheckResult.Available
                    ? BudgetAttemptStateCodes.Of(BudgetAttemptState.Reserved)
                    : BudgetAttemptStateCodes.Of(BudgetAttemptState.Insufficient);
                current.NextAttemptAt = now;
            },
            cancellationToken);

    private async Task SignalAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        ApprovalWorkloadIdentity ownerWorkload,
        bool satisfied,
        string? evidenceDigest,
        string? evidenceReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await SaveAttemptAsync(
                attempt,
                current => current.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Signalling),
                cancellationToken))
        {
            throw new DomainConflictException("The budget attempt lease was lost before signalling.");
        }

        // SPEC 03 owns the signal contract; Budget only supplies its owner identity, key and proof.
        _ = await workflow.SignalAsync(
            attempt.PrerequisiteId,
            new ApprovalSignalCommand(
                ownerWorkload,
                satisfied,
                attempt.SignalKey,
                prerequisite.Version,
                evidenceReference,
                evidenceDigest,
                attempt.SignalKey),
            now,
            cancellationToken);
    }

    private async Task<bool> CompleteAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string signalResult,
        string? evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed);
                current.SignalResult = signalResult;
                current.EvidenceDigest ??= evidenceDigest;
                current.LastErrorCode = null;
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseUntil = null;
            },
            cancellationToken);

    private async Task<bool> CompleteCompensatedAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated);
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseUntil = null;
            },
            cancellationToken);

    /// <summary>
    /// Reverses every open reservation of the attempt or none. Returns false when nothing was left
    /// to reverse, so a retry can distinguish "already compensated" from "compensation needed".
    /// </summary>
    private async Task<bool> ReleaseReservationsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        BudgetSource source,
        BudgetActor actor,
        string correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.ReserveOperationId is not Guid reserveOperationId)
        {
            return false;
        }

        var reserved = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.OperationId == reserveOperationId &&
                               movement.Type == (int)BudgetMovementType.Reserved)
            .ToArrayAsync(cancellationToken);
        if (reserved.Length == 0)
        {
            return false;
        }

        var reversedChildren = await dbContext.BudgetMovements
            .AsNoTracking()
            .Where(movement => movement.ParentMovementId != null &&
                               movement.Type == (int)BudgetMovementType.Reverse)
            .Select(movement => new { Parent = movement.ParentMovementId!.Value, movement.Amount })
            .ToArrayAsync(cancellationToken);
        var allocations = await dbContext.BudgetAllocationVersions
            .AsNoTracking()
            .Where(version => dbContext.BudgetPositions
                .Where(position => reserved.Select(movement => movement.PositionId).Contains(position.Id))
                .Select(position => position.Id)
                .Contains(version.PositionId) &&
                dbContext.BudgetPositions
                    .Where(position => position.CurrentAllocationVersion == version.Version)
                    .Select(position => position.Id)
                    .Contains(version.PositionId))
            .ToDictionaryAsync(version => version.PositionId, version => version.Currency, cancellationToken);
        var payloads = new Dictionary<BudgetPositionKey, BudgetPositionPayload>();
        var movements = new List<BudgetOperationMovement>();
        foreach (var movement in reserved.OrderBy(movement => movement.Id))
        {
            var reversed = reversedChildren
                .Where(child => child.Parent == movement.Id)
                .Sum(child => child.Amount);
            var remaining = movement.Amount - reversed;
            if (remaining <= 0m)
            {
                continue;
            }

            var position = await dbContext.BudgetPositions
                .AsNoTracking()
                .SingleAsync(record => record.Id == movement.PositionId, cancellationToken);
            var currency = allocations[movement.PositionId];
            _ = currency;
            var payload = await positions.ResolvePositionAsync(
                attempt.OrganizationId,
                position.CostCenterId,
                position.FiscalYear,
                position.SpendCategoryCode,
                await BaseCurrencyAsync(attempt.OrganizationId, cancellationToken),
                cancellationToken);
            payloads[payload.Position] = payload;
            movements.Add(new BudgetOperationMovement(
                payload.Position,
                new BudgetMovementRequest(
                    BudgetMovementType.Reverse,
                    remaining,
                    movement.Id,
                    movement.TargetId is Guid targetId && movement.TargetVersion is int targetVersion
                        ? new BudgetTarget(targetId, targetVersion, movement.TargetMaterialSnapshotDigest!, movement.TargetType!)
                        : null)));
        }

        if (movements.Count == 0)
        {
            return false;
        }

        var fingerprint = BudgetCanonicalJson.Digest(BudgetFingerprints.OperationPreimage(
            attempt.OrganizationId,
            actor,
            BudgetOperationKind.Release,
            attempt.CompensateKey,
            "BUDGET_RELEASE",
            source,
            movements.Select(entry => new BudgetMovementCommandItem(
                entry.Movement.Amount, entry.Movement.ParentMovementId, entry.Movement.Target))));
        var outcome = await ledger.ApplyOperationAsync(
            new BudgetOperationSpec(
                BudgetOperationKind.Release,
                attempt.OrganizationId,
                attempt.CompensateKey,
                fingerprint,
                source,
                actor,
                "BUDGET_RELEASE",
                "RELEASED",
                movements,
                LeaseFence: LeaseFenceOf(attempt)),
            payloads,
            correlation,
            cancellationToken);
        attempt.ReverseOperationId ??= outcome.OperationId;
        _ = now;
        return true;
    }

    /// <summary>
    /// Open reservations of the case that this one replaced, so the replacement can reuse the held
    /// funds instead of failing against its own predecessor (REQ-08, DEC-07).
    /// </summary>
    private async Task<IReadOnlyList<BudgetPredecessorHold>> FindPredecessorHoldsAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        var supersession = await dbContext.CaseSupersessions
            .AsNoTracking()
            .Where(record => record.NewCaseId == attempt.CaseId)
            .Select(record => record.PreviousCaseId)
            .ToArrayAsync(cancellationToken);
        if (supersession.Length == 0)
        {
            return [];
        }

        var holds = await dbContext.BudgetPrerequisiteAttempts
            .AsNoTracking()
            .Where(record => supersession.Contains(record.CaseId) &&
                             record.ReserveOperationId != null &&
                             record.State != BudgetAttemptStateCodes.Of(BudgetAttemptState.Compensated) &&
                             record.State != BudgetAttemptStateCodes.Of(BudgetAttemptState.Completed))
            .Select(record => new { record.Id, record.CaseId, ReserveOperationId = record.ReserveOperationId!.Value })
            .ToArrayAsync(cancellationToken);
        var result = new List<BudgetPredecessorHold>();
        foreach (var hold in holds)
        {
            // Only the RESERVED movements of the predecessor that no later transition advanced are
            // transferable; a hold already committed or reversed is not counted as available.
            var reserved = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.OperationId == hold.ReserveOperationId &&
                                   movement.Type == (int)BudgetMovementType.Reserved)
                .Select(movement => movement.Id)
                .ToArrayAsync(cancellationToken);
            if (reserved.Length == 0)
            {
                continue;
            }

            var advanced = await dbContext.BudgetMovements
                .AsNoTracking()
                .Where(movement => movement.ParentMovementId != null &&
                                   reserved.Contains(movement.ParentMovementId!.Value))
                .Select(movement => movement.ParentMovementId!.Value)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var open = reserved.Except(advanced).ToArray();
            if (open.Length > 0)
            {
                result.Add(new BudgetPredecessorHold(hold.Id, hold.CaseId, open));
            }
        }

        return result;
    }

    private async Task MarkPendingAsync(
        BudgetPrerequisiteAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = BudgetAttemptStateCodes.Of(BudgetAttemptState.Pending);
                current.LastErrorCode = errorCode;
                current.NextAttemptAt = now.AddSeconds(1);
                current.LeaseOwner = null;
                current.LeaseUntil = null;
            },
            cancellationToken);
    }

    private Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);

    private static string ErrorCode(Exception exception) => exception switch
    {
        BudgetReferenceInvalidException => "BUDGET_REFERENCE_INVALID",
        BudgetDependencyUnavailableException => "BUDGET_DEPENDENCY_UNAVAILABLE",
        DomainNotFoundException => "BUDGET_POSITION_MISSING",
        DomainConflictException => "BUDGET_CONFLICT",
        _ => "BUDGET_TECHNICAL_FAILURE"
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
