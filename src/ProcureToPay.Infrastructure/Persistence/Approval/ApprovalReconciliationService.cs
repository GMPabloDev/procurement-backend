using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalReconciliationOutcome(
    Guid RunId,
    Guid OrganizationId,
    int Scanned,
    int Reassigned,
    int Unassigned,
    int Unchanged,
    bool Completed,
    DateTimeOffset CompletedAt);

/// <summary>
/// Durable, idempotent authority reconciliation (REQ-04, REQ-10, NFR-03). Every run has exactly
/// one root audit; a conditional UPDATE claims a 30 second lease with a fencing token, the holder
/// renews at most every ten seconds, each case transaction re-validates the lease, and a crash
/// reuses the same run, root and UUID-D cursor. A decided task never changes.
/// </summary>
public sealed class ApprovalReconciliationService(
    ProcureToPayDbContext dbContext,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalReconciliationService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    /// <summary>A due reconciliation must complete within 60 seconds (NFR-03).</summary>
    public static readonly TimeSpan MaxReconciliationAge = TimeSpan.FromSeconds(60);

    /// <summary>Persistent lease of the run holder (REQ-10).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>The holder renews the lease at most every ten seconds (REQ-10).</summary>
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);

    public async Task<bool> IsDueAsync(
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
                          record.RequestedAt <= utcNow - MaxReconciliationAge,
                cancellationToken);
    }

    /// <summary>
    /// ADMIN requests a reconciliation with its idempotency key; the run and its USER root audit
    /// are created atomically and an identical retry returns the same run (REQ-04, REQ-08).
    /// </summary>
    public async Task<Guid> RequestAdminAsync(
        Guid organizationId,
        Guid actorUserId,
        string reconciliationKey,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var key = ApprovalLimits.RequireKey(reconciliationKey, "reconciliation_key");
        var correlation = ApprovalLimits.RequireCorrelation(correlationReference);
        var utcNow = occurredAt.ToUniversalTime();
        var existing = await dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.ActorUserId == actorUserId &&
                             record.ReconciliationKey == key)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var runId = Guid.NewGuid();
        var rootAuditId = Guid.NewGuid();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        dbContext.ApprovalReconciliationRuns.Add(Row(ApprovalReconciliationRun.CreateAdmin(
            runId, organizationId, actorUserId, key, rootAuditId, utcNow)));
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.RootForOrganization(
            organizationId,
            ApprovalAuditActor.User(actorUserId),
            rootAuditId,
            "RECONCILIATION_REQUESTED",
            "RECONCILIATION_RUN",
            runId,
            "[]",
            "Administrative reconciliation request.",
            utcNow,
            correlation));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.ApprovalReconciliationRuns
                .AsNoTracking()
                .Where(record => record.OrganizationId == organizationId &&
                                 record.ActorUserId == actorUserId &&
                                 record.ReconciliationKey == key)
                .Select(record => (Guid?)record.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner.Value;
        }

        return runId;
    }

    /// <summary>
    /// A confirmed organization change requests exactly one run per administrative audit; its root
    /// audit is SYSTEM with the immutable ORGANIZATION causal link and no effect key (REQ-08).
    /// </summary>
    public async Task<Guid> RequestOrganizationChangeAsync(
        Guid organizationId,
        Guid triggerAuditId,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var correlation = ApprovalLimits.RequireCorrelation(correlationReference);
        var existing = await dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.TriggerAuditId == triggerAuditId)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var runId = Guid.NewGuid();
        var rootAuditId = Guid.NewGuid();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        // requested_at is the UTC of the triggering organizational audit (REQ-10); the root
        // audit carries the real creation instant, never a backdated one.
        dbContext.ApprovalReconciliationRuns.Add(Row(ApprovalReconciliationRun.CreateOrganizationChange(
            runId, organizationId, triggerAuditId, rootAuditId, occurredAt.ToUniversalTime())));
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.OrganizationRoot(
            organizationId,
            triggerAuditId,
            rootAuditId,
            "RECONCILIATION_REQUESTED",
            runId,
            "Reconciliation requested by an organization change.",
            timeProvider.GetUtcNow(),
            correlation));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.ApprovalReconciliationRuns
                .AsNoTracking()
                .Where(record => record.OrganizationId == organizationId &&
                                 record.TriggerAuditId == triggerAuditId)
                .Select(record => (Guid?)record.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner.Value;
        }

        return runId;
    }

    /// <summary>
    /// A confirmed activation or revocation requests exactly one run per delegation version,
    /// transition and scheduled instant; its root audit is the audit of the command that caused
    /// it, so the run, its cause and its cases share one immutable chain (SPEC 04 REQ-03).
    /// </summary>
    public async Task<Guid> RequestDelegationChangeAsync(
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt,
        Guid triggerAuditId,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var correlation = ApprovalLimits.RequireCorrelation(correlationReference);
        var existing = await dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.DelegationId == delegationId &&
                             record.DelegationVersion == delegationVersion &&
                             record.DelegationTransition == ApprovalDelegationCodes.Code(transition) &&
                             record.DelegationScheduledAt == scheduledAt)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var runId = Guid.NewGuid();
        dbContext.ApprovalReconciliationRuns.Add(Row(ApprovalReconciliationRun.CreateDelegationChange(
            runId, organizationId, delegationId, delegationVersion, transition, scheduledAt, triggerAuditId,
            rootAuditId: triggerAuditId, requestedAt: scheduledAt)));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            var winner = await FindDelegationRunAsync(
                organizationId, delegationId, delegationVersion, transition, scheduledAt, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner.Value;
        }

        _ = correlation;
        return runId;
    }

    /// <summary>
    /// A confirmed expiry requests exactly one SYSTEM run caused by the delegation version and
    /// its <c>valid_to</c> (SPEC 04 REQ-03).
    /// </summary>
    public async Task<Guid> RequestDelegationExpiryAsync(
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        DateTimeOffset scheduledAt,
        Guid expiryAuditId,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindDelegationRunAsync(
            organizationId, delegationId, delegationVersion, ApprovalDelegationTransition.Expire, scheduledAt,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var runId = Guid.NewGuid();
        dbContext.ApprovalReconciliationRuns.Add(Row(ApprovalReconciliationRun.CreateDelegationExpiry(
            runId, organizationId, delegationId, delegationVersion, scheduledAt, expiryAuditId,
            requestedAt: scheduledAt)));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            var winner = await FindDelegationRunAsync(
                organizationId, delegationId, delegationVersion, ApprovalDelegationTransition.Expire, scheduledAt,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner.Value;
        }

        return runId;
    }

    private Task<Guid?> FindDelegationRunAsync(
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken) =>
        dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.DelegationId == delegationId &&
                             record.DelegationVersion == delegationVersion &&
                             record.DelegationTransition == ApprovalDelegationCodes.Code(transition) &&
                             record.DelegationScheduledAt == scheduledAt)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Processes one run under its lease. A holder that lost the lease stops; an expired lease can
    /// be reclaimed by another instance and continues from the persisted cursor (REQ-10).
    /// </summary>
    public async Task<ApprovalReconciliationOutcome> ProcessAsync(
        Guid runId,
        string owner,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var correlation = ApprovalLimits.RequireCorrelation(correlationReference);
        var utcNow = timeProvider.GetUtcNow();

        var claimed = await dbContext.ApprovalReconciliationRuns
            .Where(record => record.Id == runId &&
                             record.Status != ApprovalReconciliationCodes.StatusCompleted &&
                             (record.LeaseOwner == null ||
                              record.LockedUntil == null ||
                              record.LockedUntil <= utcNow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.LeaseOwner, owner.Length > 120 ? owner[..120] : owner)
                    .SetProperty(record => record.LockedUntil, utcNow + LeaseDuration)
                    .SetProperty(record => record.FencingToken, record => record.FencingToken + 1)
                    .SetProperty(record => record.Attempts, record => record.Attempts + 1)
                    .SetProperty(record => record.StartedAt, record => record.StartedAt ?? utcNow)
                    .SetProperty(record => record.Status, ApprovalReconciliationCodes.StatusRunning)
                    .SetProperty(record => record.Version, record => record.Version + 1),
                cancellationToken);
        var runRecord = await dbContext.ApprovalReconciliationRuns
            .AsNoTracking()
            .SingleAsync(record => record.Id == runId, cancellationToken);
        if (claimed == 0)
        {
            // Another holder owns the lease, or the run already completed. Nothing to do.
            return Outcome(runRecord, scanned: 0, reassigned: 0, unassigned: 0, unchanged: 0, completed: false, utcNow);
        }

        var fencingToken = runRecord.FencingToken;
        var rootAuditId = runRecord.RootAuditId;
        var cursor = runRecord.CursorCaseId;
        // A delegation run only re-evaluates the tasks covered by the delegation that caused it
        // (SPEC 04 REQ-03); the organization reconciliation keeps covering every open case.
        var coverage = await DelegationCoverageAsync(runRecord, cancellationToken);
        var caseIds = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record => record.OrganizationId == runRecord.OrganizationId &&
                             (record.Status == (int)ApprovalCaseStatus.Open ||
                              record.Status == (int)ApprovalCaseStatus.Blocked))
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        // Cursor and case order are UUID-D ascending (REQ-10), not SQL Server byte order.
        var ordered = caseIds
            .Select(id => (Id: id, D: id.ToString("D")))
            .OrderBy(entry => entry.D, StringComparer.Ordinal)
            .Where(entry => cursor is null ||
                            string.CompareOrdinal(entry.D, cursor.Value.ToString("D")) > 0)
            .Select(entry => entry.Id)
            .ToArray();

        var scanned = 0;
        var reassigned = 0;
        var unassigned = 0;
        var unchanged = 0;
        foreach (var caseId in ordered)
        {
            var remaining = runRecord.LockedUntil is null
                ? TimeSpan.Zero
                : runRecord.LockedUntil.Value - timeProvider.GetUtcNow();
            if (remaining < LeaseDuration - RenewalInterval)
            {
                var renewedAt = timeProvider.GetUtcNow();
                var renewed = await dbContext.ApprovalReconciliationRuns
                    .Where(record => record.Id == runId &&
                                     record.LeaseOwner == owner &&
                                     record.FencingToken == fencingToken &&
                                     record.LockedUntil > renewedAt)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(record => record.LockedUntil, renewedAt + LeaseDuration)
                            .SetProperty(record => record.Version, record => record.Version + 1),
                        cancellationToken);
                if (renewed == 0)
                {
                    // The lease was reclaimed by another instance: stop, the new holder continues.
                    logger.LogWarning(
                        "Reconciliation run {RunId} lease was lost by {Owner}; another holder continues it.",
                        runId,
                        owner);
                    return Outcome(runRecord, scanned, reassigned, unassigned, unchanged, completed: false, utcNow);
                }

                runRecord.LockedUntil = renewedAt + LeaseDuration;
            }

            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            await ApprovalAssignmentLock.AcquireAsync(
                dbContext, runRecord.OrganizationId, cancellationToken);
            var holder = await dbContext.ApprovalReconciliationRuns
                .AsNoTracking()
                .Where(record => record.Id == runId)
                .Select(record => new { record.LeaseOwner, record.FencingToken, record.LockedUntil, record.Status })
                .SingleAsync(cancellationToken);
            if (holder.Status == ApprovalReconciliationCodes.StatusCompleted ||
                !string.Equals(holder.LeaseOwner, owner, StringComparison.Ordinal) ||
                holder.FencingToken != fencingToken ||
                holder.LockedUntil is null ||
                holder.LockedUntil <= timeProvider.GetUtcNow())
            {
                await transaction.RollbackAsync(cancellationToken);
                return Outcome(runRecord, scanned, reassigned, unassigned, unchanged, completed: false, utcNow);
            }

            var caseRecord = await dbContext.ApprovalCases
                .SingleOrDefaultAsync(record => record.Id == caseId, cancellationToken);
            if (caseRecord is null)
            {
                await transaction.CommitAsync(cancellationToken);
                cursor = caseId;
                continue;
            }

            var requirementRecords = await dbContext.ApprovalRequirements
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var prerequisiteRecords = await dbContext.ApprovalPrerequisites
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var taskRecords = await dbContext.ApprovalTasks
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var assignmentRecords = await dbContext.ApprovalAssignments
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);

            var approvalCase = ApprovalCaseHydrator.Hydrate(
                caseRecord, requirementRecords, prerequisiteRecords, taskRecords);
            scanned++;
            if (approvalCase.Status is ApprovalCaseStatus.Open or ApprovalCaseStatus.Blocked)
            {
                var outcomes = await assignmentEngine.ReconcilePendingAsync(
                    approvalCase, assignmentRecords, utcNow, correlation, rootAuditId, cancellationToken, coverage);
                reassigned += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Reassigned);
                unassigned += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Unassigned);
                unchanged += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Unchanged);
                if (outcomes.Count > 0)
                {
                    ApprovalStateSync.Apply(
                        approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);
                }
            }

            var runRow = await dbContext.ApprovalReconciliationRuns
                .SingleAsync(record => record.Id == runId, cancellationToken);
            runRow.CursorCaseId = caseId;
            runRow.Version++;
            // The lease is re-validated with the current clock immediately before persisting the
            // case effects: a holder whose lease expired while processing must not confirm the
            // cursor or the effects (REQ-10). The rollback discards them and a reclaimer
            // continues from the last committed cursor.
            var stillLeaseHolder = await dbContext.ApprovalReconciliationRuns
                .AnyAsync(
                    record => record.Id == runId &&
                              record.LeaseOwner == owner &&
                              record.FencingToken == fencingToken &&
                              record.Status != ApprovalReconciliationCodes.StatusCompleted &&
                              record.LockedUntil != null &&
                              record.LockedUntil > timeProvider.GetUtcNow(),
                    cancellationToken);
            if (!stillLeaseHolder)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return Outcome(runRecord, scanned, reassigned, unassigned, unchanged, completed: false, utcNow);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cursor = caseId;
        }

        var completedAt = timeProvider.GetUtcNow();
        var completed = await dbContext.ApprovalReconciliationRuns
            .Where(record => record.Id == runId &&
                             record.LeaseOwner == owner &&
                             record.FencingToken == fencingToken &&
                             record.LockedUntil > completedAt)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Status, ApprovalReconciliationCodes.StatusCompleted)
                    .SetProperty(record => record.CompletedAt, completedAt)
                    .SetProperty(record => record.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(record => record.LeaseOwner, (string?)null)
                    .SetProperty(record => record.Version, record => record.Version + 1),
                cancellationToken);

        if (completed > 0)
        {
            await TouchWorkflowStateAsync(runRecord.OrganizationId, completedAt, owner, cancellationToken);
        }

        logger.LogInformation(
            "Approval reconciliation run {RunId} for organization {OrganizationId} scanned {Scanned} cases: " +
            "{Reassigned} reassigned, {Unassigned} unassigned, {Unchanged} unchanged.",
            runId,
            runRecord.OrganizationId,
            scanned,
            reassigned,
            unassigned,
            unchanged);

        ApprovalTelemetry.RecordReconciliation(reassigned, unassigned);

        return Outcome(runRecord, scanned, reassigned, unassigned, unchanged, completed > 0, utcNow);
    }

    /// <summary>
    /// ADMIN requests and executes a reconciliation in one call; the API and worker share the same
    /// durable run, so an identical retry is idempotent (REQ-04, REQ-09).
    /// </summary>
    public async Task<ApprovalReconciliationOutcome> ReconcileAsync(
        Guid organizationId,
        Guid actorUserId,
        string reconciliationKey,
        string owner,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var runId = await RequestAdminAsync(
            organizationId, actorUserId, reconciliationKey, correlationReference, occurredAt, cancellationToken);
        return await ProcessAsync(runId, owner, correlationReference, cancellationToken);
    }

    private async Task<ApprovalDelegationCoverage?> DelegationCoverageAsync(
        ApprovalReconciliationRunRecord runRecord,
        CancellationToken cancellationToken)
    {
        if (runRecord.Trigger is not (ApprovalReconciliationCodes.TriggerDelegationChange or
            ApprovalReconciliationCodes.TriggerDelegationExpiry) ||
            runRecord.DelegationId is null)
        {
            return null;
        }

        var delegation = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == runRecord.DelegationId, cancellationToken);
        if (delegation is null)
        {
            return null;
        }

        var descriptor = DecisionScopeDescriptor.Parse(delegation.DecisionScopeJson);
        var scope = await new ApprovalScopeResolver(dbContext).ResolveAsync(
            descriptor, delegation.OrganizationId, cancellationToken);
        return new ApprovalDelegationCoverage(
            delegation.Id,
            delegation.Version,
            delegation.DelegatorUserId,
            delegation.DelegateeUserId,
            (SystemRole)delegation.Role,
            scope);
    }

    private async Task TouchWorkflowStateAsync(
        Guid organizationId,
        DateTimeOffset utcNow,
        string owner,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.ApprovalWorkflowStates
            .SingleOrDefaultAsync(record => record.OrganizationId == organizationId, cancellationToken);
        if (state is null)
        {
            state = new ApprovalWorkflowStateRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId
            };
            dbContext.ApprovalWorkflowStates.Add(state);
        }

        state.LastReconciliationCompletedAt = utcNow;
        state.LastReconciliationOwner = owner.Length > 120 ? owner[..120] : owner;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Persistence mapping of a run, shared with the delegation service (SPEC 04 REQ-03).</summary>
    public static ApprovalReconciliationRunRecord Row(ApprovalReconciliationRun run) => new()
    {
        Id = run.Id,
        OrganizationId = run.OrganizationId,
        Trigger = ApprovalReconciliationCodes.Code(run.Trigger),
        ActorUserId = run.ActorUserId,
        ReconciliationKey = run.ReconciliationKey,
        TriggerAuditId = run.TriggerAuditId,
        DelegationId = run.DelegationId,
        DelegationVersion = run.DelegationVersion,
        DelegationTransition = run.DelegationTransition is null
            ? null
            : ApprovalDelegationCodes.Code(run.DelegationTransition.Value),
        DelegationScheduledAt = run.DelegationScheduledAt,
        Status = ApprovalReconciliationCodes.Code(run.Status),
        RequestedAt = run.RequestedAt,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        CursorCaseId = run.CursorCaseId,
        LeaseOwner = run.LeaseOwner,
        LockedUntil = run.LockedUntil,
        FencingToken = run.FencingToken,
        Attempts = run.Attempts,
        LastError = run.LastError,
        RootAuditId = run.RootAuditId,
        Version = run.Version
    };

    private static ApprovalReconciliationOutcome Outcome(
        ApprovalReconciliationRunRecord runRecord,
        int scanned,
        int reassigned,
        int unassigned,
        int unchanged,
        bool completed,
        DateTimeOffset now) =>
        new(
            runRecord.Id,
            runRecord.OrganizationId,
            scanned,
            reassigned,
            unassigned,
            unchanged,
            completed,
            runRecord.CompletedAt ?? now);
}
