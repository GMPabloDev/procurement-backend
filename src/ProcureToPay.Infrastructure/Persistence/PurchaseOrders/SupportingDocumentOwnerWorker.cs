using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>Outcome of one processed supporting document prerequisite (REQ-08).</summary>
public sealed record SupportingDocumentProcessorOutcome(
    Guid AttemptId,
    string State,
    string? SignalResult,
    string? EvidenceDigest);

/// <summary>
/// Real processor of the <c>supporting-document-owner/v1</c> prerequisite (SPEC 11 REQ-08, NFR-03).
/// It counts the unique confirmed bytes of each target whose business type belongs to the union the
/// prerequisite admits, signals <c>SATISFIED</c> with reproducible evidence only when every target
/// reaches the minimum, and keeps one durable attempt with lease, heartbeat and fencing so a restart
/// never emits a second signal. No attachment ever reaches Approval.
/// </summary>
public sealed class SupportingDocumentOwnerProcessor(
    ProcureToPayDbContext dbContext,
    ApprovalWorkflowService workflow,
    ApprovalInstanceIdentity instanceIdentity)
{
    /// <summary>Lease of one attempt before another instance may reclaim it (REQ-08).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>Interval of the independent lease heartbeat (REQ-08).</summary>
    public static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(10);

    /// <summary>Delay before an unsatisfied attempt is recounted (REQ-08).</summary>
    public static readonly TimeSpan WaitingDelay = TimeSpan.FromSeconds(5);

    public const string Pending = "PENDING";
    public const string Waiting = "WAITING";
    public const string Satisfied = "SATISFIED";
    public const string Failed = "FAILED";
    public const string Abandoned = "ABANDONED";

    private readonly SemaphoreSlim leaseGate = new(1, 1);

    /// <summary>Processes every processable prerequisite of the owner adapter once (REQ-08).</summary>
    public async Task<IReadOnlyList<SupportingDocumentProcessorOutcome>> ProcessDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var registration = await RequireRegistrationAsync(cancellationToken);
        var waiting = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Status == (int)PrerequisiteStatus.Waiting &&
                             record.OwnerAdapterId == PurchaseOrderCodes.SupportingDocumentOwnerAdapterId &&
                             record.OwnerAdapterVersion == PurchaseOrderCodes.OwnerAdapterVersion)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var recoverable = await dbContext.SupportingDocumentOwnerAttempts
            .AsNoTracking()
            .Where(record => record.State != Satisfied && record.State != Failed && record.State != Abandoned)
            .Select(record => record.PrerequisiteId)
            .ToArrayAsync(cancellationToken);
        var outcomes = new List<SupportingDocumentProcessorOutcome>();
        foreach (var prerequisiteId in waiting.Union(recoverable).OrderBy(record => record).ToArray())
        {
            var outcome = await ProcessOneAsync(prerequisiteId, registration, now, cancellationToken);
            if (outcome is not null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>Advances one prerequisite end to end, or skips it when another instance holds the lease.</summary>
    public async Task<SupportingDocumentProcessorOutcome?> ProcessOneAsync(
        Guid prerequisiteId,
        SupportingDocumentProcessorRegistrationRecord? registration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var registrationRow = registration ?? await RequireRegistrationAsync(cancellationToken);
        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document prerequisite is not visible.");
        if (!string.Equals(
                prerequisite.OwnerAdapterId, PurchaseOrderCodes.SupportingDocumentOwnerAdapterId,
                StringComparison.Ordinal) ||
            !string.Equals(
                prerequisite.OwnerAdapterVersion, PurchaseOrderCodes.OwnerAdapterVersion, StringComparison.Ordinal))
        {
            return null;
        }

        // REQ-08: the registered processor is bound to the owner workload the case resolved.
        if (!string.Equals(registrationRow.WorkloadIssuer, prerequisite.OwnerWorkloadIssuer, StringComparison.Ordinal) ||
            !string.Equals(
                registrationRow.WorkloadClientId, prerequisite.OwnerWorkloadClientId, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The supporting document processor is not bound to the owner workload of the prerequisite.");
        }

        var attempt = await FindOrCreateAttemptAsync(prerequisite, now, cancellationToken);
        if (!await ClaimAsync(attempt, now, cancellationToken))
        {
            return null;
        }

        await using var heartbeat = new LeaseHeartbeat(
            dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The supporting document processor has no connection."),
            attempt.Id,
            instanceIdentity.Owner,
            attempt.FencingToken,
            LeaseRenewalInterval,
            LeaseDuration,
            leaseGate);
        heartbeat.Start();
        return await ProcessClaimedAsync(attempt, prerequisite, now, cancellationToken);
    }

    private async Task<SupportingDocumentProcessorOutcome?> ProcessClaimedAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == attempt.CaseId, cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The approval case of the supporting document prerequisite is not visible.");
        if (caseRecord.OrganizationId != attempt.OrganizationId ||
            !string.Equals(caseRecord.SubjectType, "PURCHASE_REQUEST", StringComparison.Ordinal) ||
            caseRecord.SubjectId != attempt.RequestId ||
            caseRecord.SubjectVersion != attempt.RequestVersion)
        {
            await FailAsync(attempt, "PREREQUISITE_MISMATCH", now, cancellationToken);
            return new SupportingDocumentProcessorOutcome(attempt.Id, Failed, null, null);
        }

        // A case that is already terminal is abandoned instead of signalled (REQ-08).
        if (caseRecord.Status is (int)ApprovalCaseStatus.Cancelled or (int)ApprovalCaseStatus.Superseded)
        {
            await AbandonAsync(attempt, now, cancellationToken);
            return new SupportingDocumentProcessorOutcome(attempt.Id, Abandoned, null, null);
        }

        // A crash between the confirmed signal and the terminal checkpoint is recovered from the
        // recorded signal of this attempt, which is never sent twice (REQ-08).
        if (prerequisite.Status is (int)PrerequisiteStatus.Satisfied or (int)PrerequisiteStatus.Failed &&
            !string.Equals(attempt.State, Satisfied, StringComparison.Ordinal))
        {
            var recorded = await dbContext.ApprovalPrerequisiteSignals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.PrerequisiteId == attempt.PrerequisiteId &&
                              record.SignalKey == attempt.SignalKey,
                    cancellationToken);
            if (recorded is not null)
            {
                if (!await HoldLeaseAsync(attempt, cancellationToken))
                {
                    return null;
                }

                var result = recorded.Satisfied ? Satisfied : Failed;
                if (!await CompleteAsync(attempt, result, recorded.EvidenceDigest, now, cancellationToken))
                {
                    return null;
                }

                return new SupportingDocumentProcessorOutcome(attempt.Id, result, result, recorded.EvidenceDigest);
            }
        }

        var parameters = new SupportingDocumentOwnerParameters(
            System.Text.Json.JsonSerializer.Deserialize<string[]>(attempt.AllowedDocumentTypesJson) ?? [],
            attempt.MinimumCount);
        var targets = ApprovalJsonPersistence.DeserializeTargets(attempt.TargetsJson)
            .Select(target => new PurchaseOrderContentRef(
                target.Id, target.Version, PurchaseOrderCodes.Digest(
                    target.MaterialSnapshotDigest, "material snapshot digest")))
            .ToArray();
        if (targets.Length == 0)
        {
            await FailAsync(attempt, "PREREQUISITE_TARGETS_MISSING", now, cancellationToken);
            return new SupportingDocumentProcessorOutcome(attempt.Id, Failed, null, null);
        }

        var documents = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(record => record.OrganizationId == attempt.OrganizationId &&
                             record.RequestId == attempt.RequestId &&
                             record.RequestVersion == attempt.RequestVersion &&
                             record.State == (int)SupportingDocumentState.Confirmed)
            .ToArrayAsync(cancellationToken);
        var counts = new Dictionary<string, List<SupportingDocumentEvidenceRef>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            counts[target.CanonicalIdentity] = [];
        }

        foreach (var row in documents)
        {
            ProcurementSupportingDocumentVersion document;
            try
            {
                document = PurchaseOrderSerialization.ReadSupportingDocument(
                    row.DocumentJson, row.Id, row.ActorUserId, row.OccurredAt, row.ConfirmedAt, row.ContentDigest);
            }
            catch (Exception exception) when (exception is DomainException or
                                                  System.Text.Json.JsonException or
                                                  KeyNotFoundException or InvalidOperationException or
                                                  FormatException)
            {
                // Corruption never satisfies a control; the operator fixes the row and the attempt
                // stays terminal without a signal (REQ-08).
                await FailAsync(attempt, "DOCUMENT_CORRUPTED", now, cancellationToken);
                return new SupportingDocumentProcessorOutcome(attempt.Id, Failed, null, null);
            }

            if (!parameters.DocumentTypes.Contains(document.BusinessType, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var target in document.CoveredTargets)
            {
                if (!counts.TryGetValue(target.CanonicalIdentity, out var references))
                {
                    continue;
                }

                references.Add(new SupportingDocumentEvidenceRef(
                    new PurchaseOrderContentRef(document.DocumentId, document.Version, document.Digest),
                    target,
                    document.FileRef.Sha256,
                    document.FileRef.Length));
            }
        }

        var evidenceRefs = counts
            .SelectMany(entry => entry.Value)
            .GroupBy(reference => $"{reference.TargetRef.CanonicalIdentity}|{reference.ByteIdentity}",
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(reference => reference.DocumentRef.CanonicalIdentity,
                StringComparer.Ordinal).First())
            .OrderBy(reference => reference.TargetRef.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(reference => reference.ByteIdentity, StringComparer.Ordinal)
            .ToArray();
        var satisfied = targets.All(target =>
            evidenceRefs.Count(reference =>
                string.Equals(reference.TargetRef.CanonicalIdentity, target.CanonicalIdentity,
                    StringComparison.Ordinal)) >= parameters.MinimumCount);
        if (!satisfied)
        {
            await WaitAsync(attempt, now, cancellationToken);
            return new SupportingDocumentProcessorOutcome(attempt.Id, Waiting, null, null);
        }

        var evidence = new SupportingDocumentEvidence(
            attempt.Id,
            attempt.OrganizationId,
            attempt.PrerequisiteId,
            attempt.ParametersDigest,
            attempt.SignalKey,
            targets,
            evidenceRefs,
            now);
        dbContext.SupportingDocumentOwnerEvidence.Add(new SupportingDocumentOwnerEvidenceRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = attempt.OrganizationId,
            PrerequisiteId = attempt.PrerequisiteId,
            AttemptId = attempt.Id,
            ContractVersion = SupportingDocumentEvidence.ContractVersion,
            Result = evidence.Result,
            SignalKey = attempt.SignalKey,
            DocumentJson = evidence.CanonicalDocument(),
            ContentDigest = evidence.Digest,
            ActorUserId = Guid.Empty,
            OccurredAt = now
        });
        if (!await HoldLeaseAsync(attempt, cancellationToken))
        {
            return null;
        }

        await workflow.SignalAsync(
            attempt.PrerequisiteId,
            new ApprovalSignalCommand(
                new ApprovalWorkloadIdentity(
                    prerequisite.OwnerWorkloadIssuer, prerequisite.OwnerWorkloadClientId),
                Satisfied: true,
                attempt.SignalKey,
                prerequisite.Version,
                $"supporting-document://{attempt.Id:D}",
                evidence.Digest,
                attempt.SignalKey),
            now,
            cancellationToken);
        if (!await HoldLeaseAsync(attempt, cancellationToken))
        {
            return null;
        }

        if (!await CompleteAsync(attempt, Satisfied, evidence.Digest, now, cancellationToken))
        {
            return null;
        }

        return new SupportingDocumentProcessorOutcome(attempt.Id, Satisfied, Satisfied, evidence.Digest);
    }

    private async Task<SupportingDocumentProcessorRegistrationRecord> RequireRegistrationAsync(
        CancellationToken cancellationToken)
    {
        var registrations = await dbContext.SupportingDocumentProcessorRegistrations
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        if (registrations.Length != 1)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                registrations.Length == 0
                    ? "The supporting document processor is not registered."
                    : "More than one supporting document processor is registered.");
        }

        var registration = registrations[0];

        if (!string.Equals(
                registration.AdapterId, PurchaseOrderCodes.SupportingDocumentOwnerAdapterId, StringComparison.Ordinal) ||
            !string.Equals(
                registration.AdapterVersion, PurchaseOrderCodes.OwnerAdapterVersion, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The registered processor does not serve the supporting-document-owner adapter.");
        }

        return registration;
    }

    private async Task<SupportingDocumentOwnerAttemptRecord> FindOrCreateAttemptAsync(
        ApprovalPrerequisiteRecord prerequisite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.SupportingDocumentOwnerAttempts
            .SingleOrDefaultAsync(record => record.PrerequisiteId == prerequisite.Id, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisite.CaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case of the prerequisite is not visible.");
        var attempt = new SupportingDocumentOwnerAttemptRecord
        {
            Id = Guid.NewGuid(),
            PrerequisiteId = prerequisite.Id,
            OrganizationId = caseRecord.OrganizationId,
            CaseId = prerequisite.CaseId,
            SubjectType = caseRecord.SubjectType,
            OwnerAdapterId = prerequisite.OwnerAdapterId,
            OwnerAdapterVersion = prerequisite.OwnerAdapterVersion,
            RequestId = caseRecord.SubjectId,
            RequestVersion = caseRecord.SubjectVersion,
            TargetsJson = prerequisite.TargetsJson,
            ParametersDigest = Sha256(prerequisite.ParametersJson),
            AllowedDocumentTypesJson = "[]",
            MinimumCount = 1,
            State = Pending,
            CheckKey = $"supporting-document:{prerequisite.Id:D}:check",
            SignalKey = $"supporting-document:{prerequisite.Id:D}:signal",
            DueAt = caseRecord.CreatedAt,
            NextAttemptAt = caseRecord.CreatedAt,
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        var parameters = SupportingDocumentOwnerParameters.Parse(prerequisite.ParametersJson);
        attempt.AllowedDocumentTypesJson = System.Text.Json.JsonSerializer.Serialize(parameters.DocumentTypes);
        attempt.MinimumCount = parameters.MinimumCount;
        dbContext.SupportingDocumentOwnerAttempts.Add(attempt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.SupportingDocumentOwnerAttempts
                .SingleAsync(record => record.PrerequisiteId == prerequisite.Id, cancellationToken);
        }

        return attempt;
    }

    private async Task<bool> ClaimAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.State is Satisfied or Failed or Abandoned)
        {
            return false;
        }

        if (attempt.NextAttemptAt > now || (attempt.LeaseExpiresAt is DateTimeOffset until && until > now))
        {
            return false;
        }

        attempt.LeaseOwner = instanceIdentity.Owner;
        attempt.LeaseExpiresAt = now + LeaseDuration;
        attempt.FencingToken += 1;
        attempt.Attempts += 1;
        attempt.UpdatedAt = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DomainConflictException or DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    private async Task<bool> HoldLeaseAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current => current.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration,
            cancellationToken);

    private async Task<bool> WaitAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Waiting;
                current.NextAttemptAt = now + WaitingDelay;
                current.LastErrorCode = null;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
                current.UpdatedAt = now;
            },
            cancellationToken);

    private async Task<bool> CompleteAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        string signalResult,
        string? evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = signalResult;
                current.SignalResult = signalResult;
                current.EvidenceDigest ??= evidenceDigest;
                current.LastErrorCode = null;
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
                current.UpdatedAt = now;
            },
            cancellationToken);

    private async Task<bool> FailAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Failed;
                current.LastErrorCode = errorCode;
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
                current.UpdatedAt = now;
            },
            cancellationToken);

    private async Task<bool> AbandonAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Abandoned;
                current.AbandonedAt = now;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
                current.UpdatedAt = now;
            },
            cancellationToken);

    /// <summary>
    /// Applies one checkpoint under the lease gate: the row is reloaded and the exact claimed lease
    /// (owner, fencing token, unexpired lease) is verified before mutating, so this instance never
    /// overwrites the row of a new holder nor revives an expired lease (REQ-08).
    /// </summary>
    private async Task<bool> SaveAttemptAsync(
        SupportingDocumentOwnerAttemptRecord attempt,
        Action<SupportingDocumentOwnerAttemptRecord> mutate,
        CancellationToken cancellationToken)
    {
        var expectedToken = attempt.FencingToken;
        var owner = instanceIdentity.Owner;
        await leaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = dbContext.Entry(attempt);
            if (entry.State == EntityState.Detached)
            {
                dbContext.Attach(attempt);
            }

            await entry.ReloadAsync(cancellationToken);
            if (!string.Equals(attempt.LeaseOwner, owner, StringComparison.Ordinal) ||
                attempt.FencingToken != expectedToken ||
                attempt.LeaseExpiresAt is not DateTimeOffset until ||
                until <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            mutate(attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DomainConflictException or DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        finally
        {
            leaseGate.Release();
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Independent lease heartbeat of one attempt: a dedicated connection renews the 30-second lease
    /// every 10 seconds while this instance owns it, so a blocked count never lets the lease expire
    /// under a live worker (REQ-08).
    /// </summary>
    private sealed class LeaseHeartbeat(
        string connectionString,
        Guid attemptId,
        string owner,
        long fencingToken,
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
                    await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                    await connection.OpenAsync(cancellation.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SET XACT_ABORT ON; BEGIN TRANSACTION; " +
                        "DECLARE @seen int; " +
                        "SELECT @seen = 1 FROM [PurchaseOrders].[SupportingDocumentOwnerAttempts] " +
                        "WITH (UPDLOCK, ROWLOCK) WHERE [Id] = @id; " +
                        "UPDATE [PurchaseOrders].[SupportingDocumentOwnerAttempts] " +
                        "SET [LeaseExpiresAt] = DATEADD(SECOND, @seconds, SYSUTCDATETIME()) " +
                        "WHERE [Id] = @id AND [LeaseOwner] = @owner AND [FencingToken] = @token " +
                        "AND [LeaseExpiresAt] > SYSUTCDATETIME(); " +
                        "SET @affected = @@ROWCOUNT; COMMIT TRANSACTION;";
                    command.Parameters.AddWithValue("@seconds", (int)duration.TotalSeconds);
                    command.Parameters.AddWithValue("@id", attemptId);
                    command.Parameters.AddWithValue("@owner", owner);
                    command.Parameters.AddWithValue("@token", fencingToken);
                    var affectedParameter = command.Parameters.Add(
                        "@affected", System.Data.SqlDbType.Int);
                    affectedParameter.Direction = System.Data.ParameterDirection.Output;
                    await command.ExecuteNonQueryAsync(cancellation.Token);
                    if (affectedParameter.Value is int affected && affected == 0)
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
                    // Transient renewal failures retry on the next tick; every effect stays fenced.
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
}
