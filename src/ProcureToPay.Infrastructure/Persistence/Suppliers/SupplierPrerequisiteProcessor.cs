using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>Outcome of one processed supplier prerequisite (REQ-10).</summary>
public sealed record SupplierPrerequisiteOutcome(
    Guid AttemptId,
    string State,
    string? SignalResult,
    bool Eligible);

/// <summary>Durable owner identity of one processor instance, used as the lease holder.</summary>
public sealed class SupplierProcessorIdentity
{
    public SupplierProcessorIdentity()
    {
        // Stable per process instance and never longer than the lease-owner column (REQ-10).
        var candidate = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        Owner = candidate.Length <= 120 ? candidate : candidate[^120..];
    }

    public string Owner { get; }
}

/// <summary>
/// Real processor of the <c>active-supplier-owner/v1</c> prerequisite (SPEC 09 REQ-10). It reclaims
/// a due attempt, re-checks that the referenced supplier version is still the current ACTIVE one,
/// signals SATISFIED or FAILED with reproducible evidence and keeps technical failures retryable.
/// </summary>
public sealed class SupplierPrerequisiteProcessor(
    ProcureToPayDbContext dbContext,
    ApprovalWorkflowService workflow,
    ProcureToPay.Application.Abstractions.IApprovalOwnerWorkloadRegistry ownerWorkloads,
    SupplierProcessorIdentity instanceIdentity,
    ILogger<SupplierPrerequisiteProcessor> logger)
{
    /// <summary>Lease duration of one attempt; an expired lease is reclaimable (REQ-10).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>The lease is renewed every ten seconds at most (REQ-10, NFR-05).</summary>
    public static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim leaseGate = new(1, 1);

    private static string Pending => "PENDING";

    private static string Processing => "PROCESSING";

    private static string Checked => "CHECKED";

    private static string Signalling => "SIGNALLING";

    private static string Completed => "COMPLETED";

    /// <summary>
    /// True when this deployment is the registered owner of the prerequisite: exactly one
    /// registration row exists and its processor identity matches the workload Approval persisted.
    /// Zero or two registrations, or a different workload, block every claim (REQ-10).
    /// </summary>
    public async Task<bool> IsRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.SupplierPrerequisiteProcessorRegistrations
            .AsNoTracking()
            .Where(row => row.AdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId &&
                          row.AdapterVersion == SupplierCodes.ActiveSupplierOwnerAdapterVersion)
            .ToArrayAsync(cancellationToken);
        if (rows.Length != 1 ||
            !string.Equals(
                rows[0].ProcessorId, SupplierCodes.ActiveSupplierProcessorId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var workload = ownerWorkloads.ResolveExactlyOne(
                SupplierCodes.ActiveSupplierOwnerAdapterId, SupplierCodes.ActiveSupplierOwnerAdapterVersion);
            return string.Equals(
                workload.ClientId, SupplierCodes.ActiveSupplierProcessorId, StringComparison.Ordinal);
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>Claims every processable attempt and advances it once (REQ-10).</summary>
    public async Task<IReadOnlyList<SupplierPrerequisiteOutcome>> ProcessDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await IsRegisteredAsync(cancellationToken))
        {
            return [];
        }

        var waiting = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.OwnerAdapterId == SupplierCodes.ActiveSupplierOwnerAdapterId &&
                             record.OwnerAdapterVersion == SupplierCodes.ActiveSupplierOwnerAdapterVersion &&
                             (record.Status == (int)PrerequisiteStatus.Waiting ||
                              record.Status == (int)PrerequisiteStatus.Satisfied ||
                              record.Status == (int)PrerequisiteStatus.Failed))
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        // A durable attempt that never reached a terminal state is reclaimed even when the
        // prerequisite already decided, so a crash between the confirmed check and the terminal
        // checkpoint stays recoverable instead of leaking a lease.
        var recoverable = await dbContext.SupplierPrerequisiteAttempts
            .AsNoTracking()
            .Where(record => record.State != Completed)
            .Select(record => record.PrerequisiteId)
            .ToArrayAsync(cancellationToken);
        var outcomes = new List<SupplierPrerequisiteOutcome>();
        foreach (var prerequisiteId in waiting.Union(recoverable).OrderBy(id => id))
        {
            var outcome = await ProcessOneAsync(prerequisiteId, now, cancellationToken);
            if (outcome is not null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>Processes one prerequisite end to end; null when another live lease owns it.</summary>
    public async Task<SupplierPrerequisiteOutcome?> ProcessOneAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await IsRegisteredAsync(cancellationToken))
        {
            return null;
        }

        var attempt = await FindOrCreateAttemptAsync(prerequisiteId, now, cancellationToken);
        if (!await ClaimAsync(attempt, now, cancellationToken))
        {
            return null;
        }

        await using var heartbeat = new LeaseHeartbeat(
            dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The supplier processor has no configured connection."),
            attempt.Id,
            instanceIdentity.Owner,
            attempt.FencingToken,
            LeaseRenewalInterval,
            LeaseDuration,
            leaseGate);
        heartbeat.Start();
        return await ProcessClaimedAsync(attempt, now, cancellationToken);
    }

    private async Task<SupplierPrerequisiteOutcome?> ProcessClaimedAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == attempt.PrerequisiteId, cancellationToken);
        var ownerWorkload = new ApprovalWorkloadIdentity(
            prerequisite.OwnerWorkloadIssuer, prerequisite.OwnerWorkloadClientId);
        var targets = ApprovalJsonPersistence.DeserializeTargets(prerequisite.TargetsJson);
        var parametersDigest = Sha256(prerequisite.ParametersJson);

        // A crash after a confirmed signal leaves the attempt non-terminal: the recorded signal is
        // finalized with its own key instead of signalling a second time (REQ-10, NFR-03).
        if (prerequisite.Status is (int)PrerequisiteStatus.Satisfied or (int)PrerequisiteStatus.Failed &&
            attempt.State != Completed)
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

                var recordedResult = recorded.Satisfied ? "SATISFIED" : "FAILED";
                if (!await CompleteAsync(attempt, recordedResult, recorded.EvidenceDigest, now, cancellationToken))
                {
                    return null;
                }

                return new SupplierPrerequisiteOutcome(attempt.Id, Completed, recordedResult, recorded.Satisfied);
            }
        }

        if (attempt.State == Completed)
        {
            return new SupplierPrerequisiteOutcome(
                attempt.Id, Completed, attempt.SignalResult,
                string.Equals(attempt.SignalResult, "SATISFIED", StringComparison.Ordinal));
        }

        var supplierRef = ParseSupplierRef(prerequisite.ParametersJson);
        if (!await SaveAttemptAsync(
                attempt, current => current.State = Processing, cancellationToken))
        {
            return null;
        }

        // The authoritative check: only the current approved pointer of an ACTIVE supplier counts.
        var eligible = false;
        try
        {
            var root = await dbContext.Suppliers
                .AsNoTracking()
                .Where(supplier => supplier.OrganizationId == attempt.OrganizationId &&
                                   supplier.Id == supplierRef.Id)
                .Select(supplier => new { supplier.OperationalVersion })
                .SingleOrDefaultAsync(cancellationToken);
            if (root?.OperationalVersion == supplierRef.Version)
            {
                var status = await dbContext.SupplierVersions
                    .AsNoTracking()
                    .Where(row => row.SupplierId == supplierRef.Id && row.Version == supplierRef.Version)
                    .Select(row => (int?)row.Status)
                    .SingleOrDefaultAsync(cancellationToken);
                eligible = status is not null &&
                           SupplierStatusCodes.IsUsable((SupplierOperationalStatus)status.Value);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A technical failure stays retryable: the prerequisite is never marked as a business
            // refusal because the dependency was unavailable (REQ-10).
            logger.LogWarning(
                exception,
                "The supplier prerequisite {PrerequisiteId} could not be checked and stays retryable.",
                attempt.PrerequisiteId);
            await RetryAsync(attempt, "SUPPLIER_CHECK_UNAVAILABLE", now, cancellationToken);
            return null;
        }

        var checkedAt = DateTimeOffset.UtcNow;
        var evidenceDigest = SupplierCanonicalizer.ActiveSupplierEvidenceDigest(
            attempt.Id,
            attempt.PrerequisiteId,
            attempt.OrganizationId,
            parametersDigest,
            supplierRef,
            eligible,
            attempt.SignalKey,
            checkedAt,
            targets);
        if (!await SaveAttemptAsync(
                attempt,
                current =>
                {
                    current.State = Checked;
                    current.SignalResult = eligible ? "SATISFIED" : "FAILED";
                    current.EvidenceDigest = evidenceDigest;
                    current.NextAttemptAt = now;
                },
                cancellationToken))
        {
            return null;
        }

        if (!await SaveAttemptAsync(
                attempt, current => current.State = Signalling, cancellationToken))
        {
            return null;
        }

        var signalKey = attempt.SignalKey;
        try
        {
            _ = await workflow.SignalAsync(
                attempt.PrerequisiteId,
                new ApprovalSignalCommand(
                    ownerWorkload,
                    eligible,
                    signalKey,
                    prerequisite.Version,
                    $"supplier://{attempt.Id:D}",
                    evidenceDigest,
                    signalKey),
                checkedAt,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "The supplier prerequisite {PrerequisiteId} could not be signalled and stays retryable.",
                attempt.PrerequisiteId);
            await RetryAsync(attempt, "SUPPLIER_SIGNAL_UNAVAILABLE", now, cancellationToken);
            return null;
        }

        if (!await CompleteAsync(
                attempt, eligible ? "SATISFIED" : "FAILED", evidenceDigest, now, cancellationToken))
        {
            return null;
        }

        logger.LogInformation(
            "The supplier prerequisite {PrerequisiteId} was signalled {Result}.",
            attempt.PrerequisiteId,
            eligible ? "SATISFIED" : "FAILED");
        return new SupplierPrerequisiteOutcome(
            attempt.Id, Completed, eligible ? "SATISFIED" : "FAILED", eligible);
    }

    private async Task<SupplierPrerequisiteAttemptRecord> FindOrCreateAttemptAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.SupplierPrerequisiteAttempts
            .SingleOrDefaultAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken)
            ?? throw new DomainNotFoundException("The supplier prerequisite is not visible.");
        if (!string.Equals(
                prerequisite.OwnerAdapterId, SupplierCodes.ActiveSupplierOwnerAdapterId, StringComparison.Ordinal) ||
            !string.Equals(
                prerequisite.OwnerAdapterVersion, SupplierCodes.ActiveSupplierOwnerAdapterVersion,
                StringComparison.Ordinal))
        {
            throw new DomainValidationException("The prerequisite is not owned by the supplier domain.");
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisite.CaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var supplierRef = ParseSupplierRef(prerequisite.ParametersJson);
        var attempt = new SupplierPrerequisiteAttemptRecord
        {
            Id = Guid.NewGuid(),
            PrerequisiteId = prerequisiteId,
            OrganizationId = caseRecord.OrganizationId,
            CaseId = prerequisite.CaseId,
            SupplierId = supplierRef.Id,
            SupplierVersion = supplierRef.Version,
            TargetsJson = prerequisite.TargetsJson,
            State = Pending,
            CheckKey = $"supplier:{prerequisiteId:D}:check",
            SignalKey = $"supplier:{prerequisiteId:D}:signal",
            DueAt = caseRecord.CreatedAt,
            NextAttemptAt = caseRecord.CreatedAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SupplierPrerequisiteAttempts.Add(attempt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.SupplierPrerequisiteAttempts
                .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        }

        return attempt;
    }

    /// <summary>
    /// Takes the lease of one attempt; a live lease of another instance returns false, and an
    /// expired one is reclaimed by incrementing the fencing token (REQ-10).
    /// </summary>
    private async Task<bool> ClaimAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.State == Completed)
        {
            return false;
        }

        if (attempt.NextAttemptAt > now)
        {
            return false;
        }

        if (attempt.LeaseExpiresAt is DateTimeOffset until && until > now)
        {
            return false;
        }

        attempt.LeaseOwner = instanceIdentity.Owner;
        attempt.LeaseExpiresAt = now + LeaseDuration;
        attempt.FencingToken += 1;
        attempt.Attempts += 1;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    private Task<bool> HoldLeaseAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        CancellationToken cancellationToken) =>
        SaveAttemptAsync(
            attempt,
            current => current.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration,
            cancellationToken);

    /// <summary>
    /// Applies one checkpoint under the lease gate: the row is reloaded and the exact claimed lease
    /// is verified before mutating, so a stale worker never overwrites the new holder (REQ-10).
    /// </summary>
    private async Task<bool> SaveAttemptAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        Action<SupplierPrerequisiteAttemptRecord> mutate,
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
            attempt.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        finally
        {
            leaseGate.Release();
        }
    }

    private async Task RetryAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _ = await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Pending;
                current.LastErrorCode = errorCode;
                current.NextAttemptAt = now + TimeSpan.FromSeconds(5);
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
            },
            cancellationToken);

    private async Task<bool> CompleteAsync(
        SupplierPrerequisiteAttemptRecord attempt,
        string signalResult,
        string? evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Completed;
                current.SignalResult = signalResult;
                current.EvidenceDigest ??= evidenceDigest;
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
            },
            cancellationToken);

    internal static VersionedEntityRef ParseSupplierRef(string parametersJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(["supplier_ref"], StringComparer.Ordinal) == false)
            {
                throw new DomainValidationException("The supplier prerequisite parameters have an unknown shape.");
            }

            var supplier = root.GetProperty("supplier_ref");
            var names = supplier.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(["id", "version"], StringComparer.Ordinal))
            {
                throw new DomainValidationException("The supplier reference has an unknown shape.");
            }

            return new VersionedEntityRef(
                SupplierCodes.SupplierCatalog,
                supplier.GetProperty("id").GetGuid(),
                supplier.GetProperty("version").GetInt32());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                              InvalidOperationException or FormatException)
        {
            throw new DomainValidationException("The supplier prerequisite parameters are not readable.");
        }
    }

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Independent lease heartbeat (REQ-10): a dedicated connection renews the 30-second lease every
    /// ten seconds so a blocked signal never lets the lease expire under a live worker.
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
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellation.Token);
                    await using var command = connection.CreateCommand();
                    // The row is locked by its own statement first, so the clock of the following
                    // UPDATE is read after the wait and a delayed renewal never revives a lease.
                    command.CommandText =
                        "SET XACT_ABORT ON; BEGIN TRANSACTION; " +
                        "DECLARE @seen int; " +
                        "SELECT @seen = 1 FROM [Supplier].[PrerequisiteAttempts] " +
                        "WITH (UPDLOCK, ROWLOCK) WHERE [Id] = @id; " +
                        "UPDATE [Supplier].[PrerequisiteAttempts] " +
                        "SET [LeaseExpiresAt] = DATEADD(SECOND, @seconds, SYSUTCDATETIME()) " +
                        "WHERE [Id] = @id AND [LeaseOwner] = @owner AND [FencingToken] = @token " +
                        "AND [LeaseExpiresAt] > SYSUTCDATETIME(); " +
                        "SET @affected = @@ROWCOUNT; COMMIT TRANSACTION;";
                    command.Parameters.AddWithValue("@seconds", (int)duration.TotalSeconds);
                    command.Parameters.AddWithValue("@id", attemptId);
                    command.Parameters.AddWithValue("@owner", owner);
                    command.Parameters.AddWithValue("@token", fencingToken);
                    var affectedParameter = command.Parameters.Add("@affected", SqlDbType.Int);
                    affectedParameter.Direction = ParameterDirection.Output;
                    await command.ExecuteNonQueryAsync(cancellation.Token);
                    if (affectedParameter.Value is not int affected || affected == 0)
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
                    // and every effect is still fenced.
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
