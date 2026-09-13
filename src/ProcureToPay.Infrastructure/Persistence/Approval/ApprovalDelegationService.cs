using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalDelegationCreateCommand(
    ApprovalDelegationActorType ActorType,
    Guid ActorUserId,
    Guid OrganizationId,
    Guid DelegatorUserId,
    Guid DelegateeUserId,
    SystemRole Role,
    DecisionScopeDescriptor Scope,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string Reason,
    string DelegationCommandKey,
    string CorrelationReference);

public sealed record ApprovalDelegationRevokeCommand(
    ApprovalDelegationActorType ActorType,
    Guid ActorUserId,
    Guid OrganizationId,
    Guid DelegationId,
    int ExpectedVersion,
    string Reason,
    string DelegationCommandKey,
    string CorrelationReference);

public sealed record ApprovalDelegationOutcome(Guid DelegationId, string Status, int Version, bool Replayed);

/// <summary>
/// Authorized, idempotent delegation commands (SPEC 04 REQ-01, REQ-03): the delegator must hold a
/// RoleAssignment covering the delegated role and scope for the whole interval, ADMIN can only act
/// as operational relief with a reason, self-delegation, chains, overlaps and invalid intervals
/// fail closed, and every confirmed command writes its audit, its transition jobs and its
/// reconciliation run atomically.
/// </summary>
public sealed class ApprovalDelegationService(
    ProcureToPayDbContext dbContext,
    ApprovalScopeResolver scopeResolver,
    ILogger<ApprovalDelegationService> logger)
{
    /// <summary>Persistent lease of the scheduled transition worker (SPEC 04 REQ-03).</summary>
    public static readonly TimeSpan TransitionLeaseDuration = TimeSpan.FromSeconds(30);

    public async Task<ApprovalDelegationOutcome> CreateAsync(
        ApprovalDelegationCreateCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Scope is null)
        {
            throw new DomainValidationException("A delegation requires its decision scope.");
        }

        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var key = ApprovalLimits.RequireKey(command.DelegationCommandKey, "delegation_command_key");
        var reason = ApprovalLimits.RequireReason(command.Reason, "Delegation reason");
        var utcNow = occurredAt.ToUniversalTime();
        if (command.Scope.OrganizationId != command.OrganizationId)
        {
            throw new DomainValidationException("The delegated scope belongs to another organization.");
        }

        if (command.ActorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A delegation command requires its actor.");
        }

        if (command.ActorType == ApprovalDelegationActorType.Delegator && command.ActorUserId != command.DelegatorUserId)
        {
            throw new DomainForbiddenException("Only the delegator can create their own delegation.");
        }

        var fingerprint = ApprovalFingerprints.DelegationFingerprint(
            ApprovalFingerprints.DelegationActionCreate,
            command.ActorType,
            command.ActorUserId,
            command.DelegatorUserId,
            command.DelegateeUserId,
            command.Role,
            command.Scope.ToCanonicalValue(),
            key,
            delegationId: null,
            expectedVersion: null,
            command.OrganizationId,
            reason,
            command.ValidFrom,
            command.ValidTo);

        var existing = await FindByCommandKeyAsync(
            command.OrganizationId, command.ActorType, command.ActorUserId, key, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        await EnsureDelegatorAuthorityAsync(command, cancellationToken);
        await EnsureNoChainOrOverlapAsync(command, utcNow, cancellationToken);

        var delegationId = Guid.NewGuid();
        var rootAuditId = Guid.NewGuid();
        var immediate = command.ValidFrom.ToUniversalTime() <= utcNow && utcNow < command.ValidTo.ToUniversalTime();
        var delegation = ApprovalDelegation.Create(
            delegationId,
            command.OrganizationId,
            command.DelegatorUserId,
            command.DelegateeUserId,
            command.Role,
            command.Scope.ToCanonicalJson(),
            command.ValidFrom,
            command.ValidTo,
            command.ActorType,
            command.ActorUserId,
            reason,
            key,
            fingerprint,
            rootAuditId,
            utcNow);
        if (immediate)
        {
            delegation.Activate(utcNow);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            dbContext.ApprovalDelegations.Add(Row(delegation));
            AddTransitionJobs(delegation, immediate, utcNow);
            dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.RootForOrganization(
                delegation.OrganizationId,
                ApprovalAuditActor.User(command.ActorUserId),
                rootAuditId,
                immediate ? "DELEGATION_ACTIVATED" : "DELEGATION_CREATED",
                "DELEGATION",
                delegation.Id,
                delegation.DecisionScopeJson,
                reason,
                utcNow,
                correlation));
            if (immediate)
            {
                AddDelegationRun(
                    delegation,
                    ApprovalDelegationTransition.Activate,
                    delegation.ValidFrom,
                    triggerAuditId: rootAuditId,
                    rootAuditId);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await FindByCommandKeyAsync(
                command.OrganizationId, command.ActorType, command.ActorUserId, key, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return Replay(winner, fingerprint);
        }

        logger.LogInformation(
            "Approval delegation {DelegationId} from {DelegatorUserId} to {DelegateeUserId} was created {State}.",
            delegation.Id,
            delegation.DelegatorUserId,
            delegation.DelegateeUserId,
            immediate ? "active" : "scheduled");
        ApprovalTelemetry.RecordDelegation(immediate ? "CREATED_ACTIVE" : "CREATED_SCHEDULED");
        return new ApprovalDelegationOutcome(
            delegation.Id,
            ApprovalDelegationCodes.Code(delegation.Status),
            delegation.Version,
            Replayed: false);
    }

    public async Task<ApprovalDelegationOutcome> RevokeAsync(
        ApprovalDelegationRevokeCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var key = ApprovalLimits.RequireKey(command.DelegationCommandKey, "delegation_command_key");
        var reason = ApprovalLimits.RequireReason(command.Reason, "Delegation reason");
        var utcNow = occurredAt.ToUniversalTime();

        var existing = await FindByCommandKeyAsync(
            command.OrganizationId, command.ActorType, command.ActorUserId, key, cancellationToken);
        if (existing is not null)
        {
            var replayedFingerprint = RevocationFingerprint(existing, command, reason, key);
            return Replay(existing, replayedFingerprint);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var record = await dbContext.ApprovalDelegations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.DelegationId &&
                             candidate.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval delegation is not visible.");
        if (command.ActorType == ApprovalDelegationActorType.Delegator &&
            command.ActorUserId != record.DelegatorUserId)
        {
            throw new DomainForbiddenException("Only the delegator or an ADMIN can revoke this delegation.");
        }

        if (record.Version != command.ExpectedVersion)
        {
            ApprovalTelemetry.RecordConflict("DELEGATION_REVOKE");
            throw new DomainConflictException("The delegation was modified by another request.");
        }

        var fingerprint = RevocationFingerprint(record, command, reason, key);
        var delegation = Hydrate(record);
        delegation.Revoke(utcNow, command.ActorUserId);

        var rootAuditId = Guid.NewGuid();
        WriteDelegation(record, delegation);
        await CancelPendingJobsAsync(delegation.Id, utcNow, cancellationToken);
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.RootForOrganization(
            delegation.OrganizationId,
            ApprovalAuditActor.User(command.ActorUserId),
            rootAuditId,
            "DELEGATION_REVOKED",
            "DELEGATION",
            delegation.Id,
            delegation.DecisionScopeJson,
            reason,
            utcNow,
            correlation));
        AddDelegationRun(
            delegation,
            ApprovalDelegationTransition.Revoke,
            utcNow,
            triggerAuditId: rootAuditId,
            rootAuditId);
        record.Fingerprint = delegation.Fingerprint;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Approval delegation {DelegationId} was revoked.", delegation.Id);
        ApprovalTelemetry.RecordDelegation("REVOKED");
        return new ApprovalDelegationOutcome(
            delegation.Id,
            ApprovalDelegationCodes.Code(delegation.Status),
            delegation.Version,
            Replayed: false);
    }

    /// <summary>
    /// Confirms scheduled activation under the transition worker lease (SPEC 04 REQ-03): the
    /// transition, its audit, its run and the job completion commit together, and the run keeps
    /// the create audit as its immutable trigger.
    /// </summary>
    public async Task<bool> ActivateScheduledAsync(
        Guid jobId,
        string owner,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var claimed = await ClaimJobAsync(jobId, owner, utcNow, cancellationToken);
        if (claimed is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var (job, record) = claimed.Value;
        if (record is null)
        {
            await dbContext.ApprovalDelegationTransitionJobs
                .Where(candidate => candidate.Id == jobId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, ApprovalDelegationCodes.JobCompleted)
                        .SetProperty(candidate => candidate.CompletedAt, utcNow)
                        .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                        .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1),
                    cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (record.Status == ApprovalDelegationCodes.StatusActive)
        {
            job.Complete(utcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (record.Status != ApprovalDelegationCodes.StatusScheduled)
        {
            job.Cancel(utcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var delegation = Hydrate(record);
        delegation.Activate(utcNow);
        WriteDelegation(record, delegation);
        var transitionAuditId = Guid.NewGuid();
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.DelegationRoot(
            delegation.OrganizationId,
            delegation.Id,
            transitionAuditId,
            "DELEGATION_ACTIVATED",
            delegation.Version,
            delegation.ValidFrom,
            delegation.ValidTo,
            utcNow,
            $"delegation-activation-{delegation.Id:N}"));
        AddDelegationRun(
            delegation,
            ApprovalDelegationTransition.Activate,
            delegation.ValidFrom,
            triggerAuditId: delegation.RootAuditId,
            transitionAuditId);
        job.Complete(utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Approval delegation {DelegationId} became active.", delegation.Id);
        ApprovalTelemetry.RecordDelegation("ACTIVATED");
        return true;
    }

    /// <summary>
    /// Confirms scheduled expiry under the transition worker lease (SPEC 04 REQ-03): the SYSTEM
    /// expiry audit becomes the root of a single DELEGATION_EXPIRY run caused by the delegation
    /// version and its <c>valid_to</c>.
    /// </summary>
    public async Task<bool> ExpireScheduledAsync(
        Guid jobId,
        string owner,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var claimed = await ClaimJobAsync(jobId, owner, utcNow, cancellationToken);
        if (claimed is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var (job, record) = claimed.Value;
        if (record is null || record.Status == ApprovalDelegationCodes.StatusExpired)
        {
            job.Complete(utcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (record.Status != ApprovalDelegationCodes.StatusActive)
        {
            job.Cancel(utcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var delegation = Hydrate(record);
        delegation.Expire(utcNow);
        WriteDelegation(record, delegation);
        var expiryAuditId = Guid.NewGuid();
        var correlation = $"delegation-expiry-{delegation.Id:N}";
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.DelegationRoot(
            delegation.OrganizationId,
            delegation.Id,
            expiryAuditId,
            "DELEGATION_EXPIRED",
            delegation.Version,
            delegation.ValidFrom,
            delegation.ValidTo,
            utcNow,
            correlation));
        dbContext.ApprovalReconciliationRuns.Add(ApprovalReconciliationService.Row(
            ApprovalReconciliationRun.CreateDelegationExpiry(
                Guid.NewGuid(),
                delegation.OrganizationId,
                delegation.Id,
                delegation.Version,
                delegation.ValidTo,
                expiryAuditId,
                requestedAt: delegation.ValidTo)));
        job.Complete(utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Approval delegation {DelegationId} expired.", delegation.Id);
        ApprovalTelemetry.RecordDelegation("EXPIRED");
        return true;
    }

    private async Task<(ApprovalDelegationTransitionJob Job, ApprovalDelegationRecord? Record)?> ClaimJobAsync(
        Guid jobId,
        string owner,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var leaseOwner = owner.Length > 120 ? owner[..120] : owner;
        var claimed = await dbContext.ApprovalDelegationTransitionJobs
            .Where(candidate =>
                candidate.Id == jobId &&
                candidate.Status == ApprovalDelegationCodes.JobPending &&
                (candidate.LockedUntil == null || candidate.LockedUntil <= utcNow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.LeaseOwner, leaseOwner)
                    .SetProperty(candidate => candidate.LockedUntil, utcNow + TransitionLeaseDuration)
                    .SetProperty(candidate => candidate.FencingToken, candidate => candidate.FencingToken + 1)
                    .SetProperty(candidate => candidate.Attempts, candidate => candidate.Attempts + 1)
                    .SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1),
                cancellationToken);
        if (claimed == 0)
        {
            return null;
        }

        var jobRecord = await dbContext.ApprovalDelegationTransitionJobs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        var job = ApprovalDelegationTransitionJob.Restore(
            jobRecord.Id,
            jobRecord.OrganizationId,
            jobRecord.DelegationId,
            jobRecord.DelegationVersion,
            ApprovalDelegationCodes.ParseTransition(jobRecord.Transition),
            jobRecord.ScheduledAt,
            ApprovalDelegationCodes.ParseJobStatus(jobRecord.Status),
            jobRecord.CompletedAt,
            jobRecord.CancelledAt,
            jobRecord.LeaseOwner,
            jobRecord.LockedUntil,
            jobRecord.FencingToken,
            jobRecord.Attempts,
            jobRecord.Version);
        var delegationRecord = await dbContext.ApprovalDelegations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == jobRecord.DelegationId, cancellationToken);
        return (job, delegationRecord);
    }

    private async Task EnsureDelegatorAuthorityAsync(
        ApprovalDelegationCreateCommand command,
        CancellationToken cancellationToken)
    {
        var requiredScope = await scopeResolver.ResolveAsync(
            command.Scope, command.OrganizationId, cancellationToken);
        var profile = await dbContext.UserProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.DelegatorUserId &&
                          record.OrganizationId == command.OrganizationId &&
                          record.Status == (int)UserProfileStatus.Active,
                cancellationToken)
            ?? throw new DomainForbiddenException("The delegator is not an active member of the organization.");
        _ = profile;
        var assignments = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(record =>
                record.UserProfileId == command.DelegatorUserId &&
                record.Role == (int)command.Role &&
                record.Status == (int)AssignmentStatus.Active)
            .ToArrayAsync(cancellationToken);
        var from = command.ValidFrom.ToUniversalTime();
        var to = command.ValidTo.ToUniversalTime();
        var covering = assignments.Any(record =>
            record.AssignedAt <= from &&
            (record.RevokedAt is null || record.RevokedAt >= to) &&
            OrganizationEligibilityService.ParseScope(record.ScopeJson).Covers(requiredScope));
        if (!covering)
        {
            throw new DomainForbiddenException(
                "The delegator does not hold a role assignment covering the whole delegated interval.");
        }
    }

    private async Task EnsureNoChainOrOverlapAsync(
        ApprovalDelegationCreateCommand command,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var requiredScope = await scopeResolver.ResolveAsync(
            command.Scope, command.OrganizationId, cancellationToken);
        var from = command.ValidFrom.ToUniversalTime();
        var to = command.ValidTo.ToUniversalTime();
        var candidates = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == command.OrganizationId &&
                record.Role == (int)command.Role &&
                record.Status != ApprovalDelegationCodes.StatusRevoked &&
                record.ValidFrom < to &&
                record.ValidTo > from &&
                (record.DelegatorUserId == command.DelegatorUserId ||
                 record.DelegateeUserId == command.DelegateeUserId ||
                 record.DelegatorUserId == command.DelegateeUserId ||
                 record.DelegateeUserId == command.DelegatorUserId))
            .ToArrayAsync(cancellationToken);
        foreach (var record in candidates)
        {
            var recordScope = await scopeResolver.ResolveAsync(
                DecisionScopeDescriptor.Parse(record.DecisionScopeJson),
                command.OrganizationId,
                cancellationToken);
            if (!recordScope.Overlaps(requiredScope))
            {
                continue;
            }

            if (record.DelegatorUserId == command.DelegatorUserId)
            {
                throw new DomainConflictException(
                    "Another non-revoked delegation of the same delegator, role and scope overlaps this interval.");
            }

            if (record.DelegateeUserId == command.DelegatorUserId ||
                record.DelegatorUserId == command.DelegateeUserId)
            {
                throw new DomainConflictException("A delegation cannot chain with another delegation.");
            }
        }
    }

    private async Task CancelPendingJobsAsync(
        Guid delegationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken) =>
        await dbContext.ApprovalDelegationTransitionJobs
            .Where(record =>
                record.DelegationId == delegationId &&
                record.Status == ApprovalDelegationCodes.JobPending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Status, ApprovalDelegationCodes.JobCancelled)
                    .SetProperty(record => record.CancelledAt, utcNow)
                    .SetProperty(record => record.LeaseOwner, (string?)null)
                    .SetProperty(record => record.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(record => record.Version, record => record.Version + 1),
                cancellationToken);

    private void AddTransitionJobs(ApprovalDelegation delegation, bool immediate, DateTimeOffset utcNow)
    {
        var activate = ApprovalDelegationTransitionJob.Create(
            Guid.NewGuid(),
            delegation.OrganizationId,
            delegation.Id,
            delegation.Version,
            ApprovalDelegationTransition.Activate,
            delegation.ValidFrom);
        if (immediate)
        {
            activate.Complete(utcNow);
        }

        dbContext.ApprovalDelegationTransitionJobs.Add(new ApprovalDelegationTransitionJobRecord
        {
            Id = activate.Id,
            OrganizationId = activate.OrganizationId,
            DelegationId = activate.DelegationId,
            DelegationVersion = activate.DelegationVersion,
            Transition = ApprovalDelegationCodes.Code(activate.Transition),
            ScheduledAt = activate.ScheduledAt,
            Status = ApprovalDelegationCodes.Code(activate.Status),
            CompletedAt = activate.CompletedAt,
            CancelledAt = activate.CancelledAt,
            FencingToken = activate.FencingToken,
            Attempts = activate.Attempts,
            Version = activate.Version
        });
        dbContext.ApprovalDelegationTransitionJobs.Add(new ApprovalDelegationTransitionJobRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = delegation.OrganizationId,
            DelegationId = delegation.Id,
            DelegationVersion = delegation.Version,
            Transition = ApprovalDelegationCodes.TransitionExpire,
            ScheduledAt = delegation.ValidTo,
            Status = ApprovalDelegationCodes.JobPending,
            Version = 1
        });
    }

    private void AddDelegationRun(
        ApprovalDelegation delegation,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt,
        Guid triggerAuditId,
        Guid rootAuditId)
    {
        dbContext.ApprovalReconciliationRuns.Add(ApprovalReconciliationService.Row(
            ApprovalReconciliationRun.CreateDelegationChange(
                Guid.NewGuid(),
                delegation.OrganizationId,
                delegation.Id,
                delegation.Version,
                transition,
                scheduledAt,
                triggerAuditId,
                rootAuditId,
                requestedAt: scheduledAt)));
    }

    private async Task<ApprovalDelegationRecord?> FindByCommandKeyAsync(
        Guid organizationId,
        ApprovalDelegationActorType actorType,
        Guid actorUserId,
        string key,
        CancellationToken cancellationToken) =>
        await dbContext.ApprovalDelegations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ActorType == ApprovalDelegationCodes.Code(actorType) &&
                          record.ActorUserId == actorUserId &&
                          record.DelegationCommandKey == key,
                cancellationToken);

    private static ApprovalDelegationOutcome Replay(ApprovalDelegationRecord record, string fingerprint)
    {
        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            ApprovalTelemetry.RecordConflict("DELEGATION");
            throw new DomainConflictException("The delegation command key was already used with different content.");
        }

        return new ApprovalDelegationOutcome(
            record.Id,
            record.Status,
            record.Version,
            Replayed: true);
    }

    private static string RevocationFingerprint(
        ApprovalDelegationRecord record,
        ApprovalDelegationRevokeCommand command,
        string reason,
        string key) =>
        ApprovalFingerprints.DelegationFingerprint(
            ApprovalFingerprints.DelegationActionRevoke,
            command.ActorType,
            command.ActorUserId,
            record.DelegatorUserId,
            record.DelegateeUserId,
            (SystemRole)record.Role,
            DecisionScopeDescriptor.Parse(record.DecisionScopeJson).ToCanonicalValue(),
            key,
            record.Id,
            command.ExpectedVersion,
            record.OrganizationId,
            reason,
            record.ValidFrom,
            record.ValidTo);

    private static ApprovalDelegationRecord Row(ApprovalDelegation delegation) => new()
    {
        Id = delegation.Id,
        OrganizationId = delegation.OrganizationId,
        DelegatorUserId = delegation.DelegatorUserId,
        DelegateeUserId = delegation.DelegateeUserId,
        Role = (int)delegation.Role,
        DecisionScopeJson = delegation.DecisionScopeJson,
        ValidFrom = delegation.ValidFrom,
        ValidTo = delegation.ValidTo,
        ActorType = ApprovalDelegationCodes.Code(delegation.ActorType),
        ActorUserId = delegation.ActorUserId,
        Reason = delegation.Reason,
        DelegationCommandKey = delegation.DelegationCommandKey,
        Fingerprint = delegation.Fingerprint,
        RootAuditId = delegation.RootAuditId,
        CreatedAt = delegation.CreatedAt,
        Status = ApprovalDelegationCodes.Code(delegation.Status),
        Version = delegation.Version,
        ActivatedAt = delegation.ActivatedAt,
        ExpiredAt = delegation.ExpiredAt,
        RevokedAt = delegation.RevokedAt,
        RevokedByUserId = delegation.RevokedByUserId
    };

    internal static ApprovalDelegation Hydrate(ApprovalDelegationRecord record) => ApprovalDelegation.Restore(
        record.Id,
        record.OrganizationId,
        record.DelegatorUserId,
        record.DelegateeUserId,
        (SystemRole)record.Role,
        record.DecisionScopeJson,
        record.ValidFrom,
        record.ValidTo,
        ApprovalDelegationCodes.ParseActorType(record.ActorType),
        record.ActorUserId,
        record.Reason,
        record.DelegationCommandKey,
        record.Fingerprint,
        record.RootAuditId,
        record.CreatedAt,
        ApprovalDelegationCodes.ParseStatus(record.Status),
        record.Version,
        record.ActivatedAt,
        record.ExpiredAt,
        record.RevokedAt,
        record.RevokedByUserId);

    private static void WriteDelegation(ApprovalDelegationRecord record, ApprovalDelegation delegation)
    {
        record.Status = ApprovalDelegationCodes.Code(delegation.Status);
        record.Version = delegation.Version;
        record.ActivatedAt = delegation.ActivatedAt;
        record.ExpiredAt = delegation.ExpiredAt;
        record.RevokedAt = delegation.RevokedAt;
        record.RevokedByUserId = delegation.RevokedByUserId;
    }
}
