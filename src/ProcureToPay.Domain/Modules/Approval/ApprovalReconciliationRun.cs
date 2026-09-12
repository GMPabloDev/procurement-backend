using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public enum ApprovalReconciliationTrigger
{
    Admin = 1,
    OrganizationChange = 2
}

public enum ApprovalReconciliationRunStatus
{
    Pending = 1,
    Running = 2,
    Completed = 3
}

/// <summary>Canonical codes persisted for the run trigger and state (SPEC 03, REQ-10).</summary>
public static class ApprovalReconciliationCodes
{
    public const string TriggerAdmin = "ADMIN";
    public const string TriggerOrganizationChange = "ORGANIZATION_CHANGE";
    public const string StatusPending = "PENDING";
    public const string StatusRunning = "RUNNING";
    public const string StatusCompleted = "COMPLETED";

    public static string Code(ApprovalReconciliationTrigger trigger) => trigger switch
    {
        ApprovalReconciliationTrigger.Admin => TriggerAdmin,
        ApprovalReconciliationTrigger.OrganizationChange => TriggerOrganizationChange,
        _ => throw new DomainValidationException("The reconciliation trigger is invalid.")
    };

    public static string Code(ApprovalReconciliationRunStatus status) => status switch
    {
        ApprovalReconciliationRunStatus.Pending => StatusPending,
        ApprovalReconciliationRunStatus.Running => StatusRunning,
        ApprovalReconciliationRunStatus.Completed => StatusCompleted,
        _ => throw new DomainValidationException("The reconciliation run status is invalid.")
    };

    public static ApprovalReconciliationTrigger ParseTrigger(string? code) => code switch
    {
        TriggerAdmin => ApprovalReconciliationTrigger.Admin,
        TriggerOrganizationChange => ApprovalReconciliationTrigger.OrganizationChange,
        _ => throw new DomainValidationException("The stored reconciliation trigger is invalid.")
    };

    public static ApprovalReconciliationRunStatus ParseStatus(string? code) => code switch
    {
        StatusPending => ApprovalReconciliationRunStatus.Pending,
        StatusRunning => ApprovalReconciliationRunStatus.Running,
        StatusCompleted => ApprovalReconciliationRunStatus.Completed,
        _ => throw new DomainValidationException("The stored reconciliation status is invalid.")
    };
}

/// <summary>
/// Durable reconciliation run (REQ-10): one run per administrative key or organization audit,
/// a persistent lease with fencing token that only its holder may advance, a UUID-D cursor that
/// survives a crash, and exactly one root audit reused by every claim (REQ-08).
/// </summary>
public sealed class ApprovalReconciliationRun
{
    private ApprovalReconciliationRun(
        Guid id,
        Guid organizationId,
        ApprovalReconciliationTrigger trigger,
        Guid? actorUserId,
        string? reconciliationKey,
        Guid? triggerAuditId,
        Guid rootAuditId,
        DateTimeOffset requestedAt)
    {
        Id = id;
        OrganizationId = organizationId;
        Trigger = trigger;
        ActorUserId = actorUserId;
        ReconciliationKey = reconciliationKey;
        TriggerAuditId = triggerAuditId;
        RootAuditId = rootAuditId;
        RequestedAt = requestedAt.ToUniversalTime();
        Status = ApprovalReconciliationRunStatus.Pending;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public ApprovalReconciliationTrigger Trigger { get; }
    public Guid? ActorUserId { get; }
    public string? ReconciliationKey { get; }
    public Guid? TriggerAuditId { get; }
    public Guid RootAuditId { get; }
    public DateTimeOffset RequestedAt { get; }
    public ApprovalReconciliationRunStatus Status { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? CursorCaseId { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public long FencingToken { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public int Version { get; private set; } = 1;

    public static ApprovalReconciliationRun CreateAdmin(
        Guid id,
        Guid organizationId,
        Guid actorUserId,
        string reconciliationKey,
        Guid rootAuditId,
        DateTimeOffset requestedAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || actorUserId == Guid.Empty || rootAuditId == Guid.Empty)
        {
            throw new DomainValidationException("A reconciliation run needs complete identities.");
        }

        return new ApprovalReconciliationRun(
            id, organizationId, ApprovalReconciliationTrigger.Admin, actorUserId,
            ApprovalLimits.RequireKey(reconciliationKey, "reconciliation_key"), triggerAuditId: null,
            rootAuditId, requestedAt);
    }

    public static ApprovalReconciliationRun CreateOrganizationChange(
        Guid id,
        Guid organizationId,
        Guid triggerAuditId,
        Guid rootAuditId,
        DateTimeOffset requestedAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || triggerAuditId == Guid.Empty ||
            rootAuditId == Guid.Empty)
        {
            throw new DomainValidationException("A reconciliation run needs complete identities.");
        }

        return new ApprovalReconciliationRun(
            id, organizationId, ApprovalReconciliationTrigger.OrganizationChange, actorUserId: null,
            reconciliationKey: null, triggerAuditId, rootAuditId, requestedAt);
    }

    public static ApprovalReconciliationRun Restore(
        Guid id,
        Guid organizationId,
        ApprovalReconciliationTrigger trigger,
        Guid? actorUserId,
        string? reconciliationKey,
        Guid? triggerAuditId,
        Guid rootAuditId,
        ApprovalReconciliationRunStatus status,
        DateTimeOffset requestedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        Guid? cursorCaseId,
        string? leaseOwner,
        DateTimeOffset? lockedUntil,
        long fencingToken,
        int attempts,
        string? lastError,
        int version)
    {
        var run = new ApprovalReconciliationRun(
            id, organizationId, trigger, actorUserId, reconciliationKey, triggerAuditId, rootAuditId, requestedAt)
        {
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            CursorCaseId = cursorCaseId,
            LeaseOwner = leaseOwner,
            LockedUntil = lockedUntil,
            FencingToken = fencingToken,
            Attempts = attempts,
            LastError = lastError,
            Version = version
        };
        return run;
    }

    public bool IsLeaseExpired(DateTimeOffset now) => LockedUntil is null || LockedUntil <= now.ToUniversalTime();

    /// <summary>Reuses the same run, root and cursor after a timeout, crash or restart (REQ-10).</summary>
    public void ApplyClaim(string owner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status == ApprovalReconciliationRunStatus.Completed)
        {
            throw new DomainConflictException("A completed reconciliation run is never reopened.");
        }

        if (!IsLeaseExpired(now) && !string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            throw new DomainConflictException("The reconciliation run is leased by another holder.");
        }

        LeaseOwner = Normalize(owner);
        LockedUntil = now.ToUniversalTime() + leaseDuration;
        FencingToken++;
        Attempts++;
        StartedAt ??= now.ToUniversalTime();
        Status = ApprovalReconciliationRunStatus.Running;
        Version++;
    }

    /// <summary>The holder renews at most every ten seconds (REQ-10).</summary>
    public void Renew(string owner, long fencingToken, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsureHolder(owner, fencingToken, now);
        LockedUntil = now.ToUniversalTime() + leaseDuration;
        Version++;
    }

    public void AdvanceCursor(Guid caseId, string owner, long fencingToken, DateTimeOffset now)
    {
        EnsureHolder(owner, fencingToken, now);
        CursorCaseId = caseId;
        Version++;
    }

    /// <summary>Completion clears the lease, fixes the UTC instant and never reopens (REQ-10).</summary>
    public void Complete(string owner, long fencingToken, DateTimeOffset now)
    {
        EnsureHolder(owner, fencingToken, now);
        Status = ApprovalReconciliationRunStatus.Completed;
        CompletedAt = now.ToUniversalTime();
        LockedUntil = null;
        LeaseOwner = null;
        Version++;
    }

    /// <summary>Minimized failure note: no stack, payload or personal data (REQ-10).</summary>
    public void RegisterFailure(string error)
    {
        LastError = error.Length > 200 ? error[..200] : error;
        Version++;
    }

    private void EnsureHolder(string owner, long fencingToken, DateTimeOffset now)
    {
        if (Status != ApprovalReconciliationRunStatus.Running ||
            !string.Equals(LeaseOwner, Normalize(owner), StringComparison.Ordinal) ||
            FencingToken != fencingToken ||
            IsLeaseExpired(now))
        {
            throw new DomainConflictException("The reconciliation lease is no longer held by this holder.");
        }
    }

    private static string Normalize(string owner)
    {
        var normalized = new string(owner
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            .ToArray());
        if (normalized.Length is < 1 or > 120)
        {
            throw new DomainValidationException("A lease owner must contain 1-120 opaque characters.");
        }

        return normalized;
    }
}
