using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public enum ApprovalDelegationStatus
{
    Scheduled = 1,
    Active = 2,
    Revoked = 3,
    Expired = 4
}

/// <summary>Closed actor union of the delegation command (REQ-01, Datos y contratos).</summary>
public enum ApprovalDelegationActorType
{
    Delegator = 1,
    Admin = 2
}

public enum ApprovalDelegationTransition
{
    Activate = 1,
    Expire = 2,
    /// <summary>Confirmed revocation of a scheduled or active delegation (REQ-03).</summary>
    Revoke = 3
}

public enum ApprovalDelegationJobStatus
{
    Pending = 1,
    Completed = 2,
    Cancelled = 3
}

/// <summary>Canonical codes persisted for delegation state, actor, transition and jobs.</summary>
public static class ApprovalDelegationCodes
{
    public const string StatusScheduled = "SCHEDULED";
    public const string StatusActive = "ACTIVE";
    public const string StatusRevoked = "REVOKED";
    public const string StatusExpired = "EXPIRED";
    public const string ActorDelegator = "DELEGATOR";
    public const string ActorAdmin = "ADMIN";
    public const string TransitionActivate = "ACTIVATE";
    public const string TransitionExpire = "EXPIRE";
    public const string TransitionRevoke = "REVOKE";
    public const string JobPending = "PENDING";
    public const string JobCompleted = "COMPLETED";
    public const string JobCancelled = "CANCELLED";

    public static string Code(ApprovalDelegationStatus status) => status switch
    {
        ApprovalDelegationStatus.Scheduled => StatusScheduled,
        ApprovalDelegationStatus.Active => StatusActive,
        ApprovalDelegationStatus.Revoked => StatusRevoked,
        ApprovalDelegationStatus.Expired => StatusExpired,
        _ => throw new DomainValidationException("The delegation status is invalid.")
    };

    public static string Code(ApprovalDelegationActorType actorType) => actorType switch
    {
        ApprovalDelegationActorType.Delegator => ActorDelegator,
        ApprovalDelegationActorType.Admin => ActorAdmin,
        _ => throw new DomainValidationException("The delegation actor type is invalid.")
    };

    public static string Code(ApprovalDelegationTransition transition) => transition switch
    {
        ApprovalDelegationTransition.Activate => TransitionActivate,
        ApprovalDelegationTransition.Expire => TransitionExpire,
        ApprovalDelegationTransition.Revoke => TransitionRevoke,
        _ => throw new DomainValidationException("The delegation transition is invalid.")
    };

    public static string Code(ApprovalDelegationJobStatus status) => status switch
    {
        ApprovalDelegationJobStatus.Pending => JobPending,
        ApprovalDelegationJobStatus.Completed => JobCompleted,
        ApprovalDelegationJobStatus.Cancelled => JobCancelled,
        _ => throw new DomainValidationException("The delegation job status is invalid.")
    };

    public static ApprovalDelegationStatus ParseStatus(string? code) => code switch
    {
        StatusScheduled => ApprovalDelegationStatus.Scheduled,
        StatusActive => ApprovalDelegationStatus.Active,
        StatusRevoked => ApprovalDelegationStatus.Revoked,
        StatusExpired => ApprovalDelegationStatus.Expired,
        _ => throw new DomainValidationException("The stored delegation status is invalid.")
    };

    public static ApprovalDelegationActorType ParseActorType(string? code) => code switch
    {
        ActorDelegator => ApprovalDelegationActorType.Delegator,
        ActorAdmin => ApprovalDelegationActorType.Admin,
        _ => throw new DomainValidationException("The stored delegation actor type is invalid.")
    };

    public static ApprovalDelegationTransition ParseTransition(string? code) => code switch
    {
        TransitionActivate => ApprovalDelegationTransition.Activate,
        TransitionExpire => ApprovalDelegationTransition.Expire,
        TransitionRevoke => ApprovalDelegationTransition.Revoke,
        _ => throw new DomainValidationException("The stored delegation transition is invalid.")
    };

    public static ApprovalDelegationJobStatus ParseJobStatus(string? code) => code switch
    {
        JobPending => ApprovalDelegationJobStatus.Pending,
        JobCompleted => ApprovalDelegationJobStatus.Completed,
        JobCancelled => ApprovalDelegationJobStatus.Cancelled,
        _ => throw new DomainValidationException("The stored delegation job status is invalid.")
    };
}

/// <summary>
/// Bounded delegation of routing for one role and decision scope inside an explicit UTC interval
/// (REQ-01, DEC-01, DEC-02): it never transfers authority, a grant, a limit or a role assignment,
/// and it cannot chain, self-delegate or overlap another non-revoked delegation of the same
/// delegator, role and scope.
/// </summary>
public sealed class ApprovalDelegation
{
    private ApprovalDelegation(
        Guid id,
        Guid organizationId,
        Guid delegatorUserId,
        Guid delegateeUserId,
        SystemRole role,
        string decisionScopeJson,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovalDelegationActorType actorType,
        Guid actorUserId,
        string reason,
        string delegationCommandKey,
        string fingerprint,
        Guid rootAuditId,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        DelegatorUserId = delegatorUserId;
        DelegateeUserId = delegateeUserId;
        Role = role;
        DecisionScopeJson = decisionScopeJson;
        ValidFrom = validFrom.ToUniversalTime();
        ValidTo = validTo.ToUniversalTime();
        ActorType = actorType;
        ActorUserId = actorUserId;
        Reason = reason;
        DelegationCommandKey = delegationCommandKey;
        Fingerprint = fingerprint;
        RootAuditId = rootAuditId;
        CreatedAt = createdAt.ToUniversalTime();
        Status = ApprovalDelegationStatus.Scheduled;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid DelegatorUserId { get; }
    public Guid DelegateeUserId { get; }
    public SystemRole Role { get; }
    public string DecisionScopeJson { get; }
    /// <summary>Inclusive lower bound; both bounds are UTC with seven decimals (Datos y contratos).</summary>
    public DateTimeOffset ValidFrom { get; }
    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset ValidTo { get; }
    public ApprovalDelegationActorType ActorType { get; }
    public Guid ActorUserId { get; }
    public string Reason { get; }
    public string DelegationCommandKey { get; }
    public string Fingerprint { get; }
    /// <summary>Root audit of the create/revoke command that scheduled this delegation (REQ-03).</summary>
    public Guid RootAuditId { get; }
    public DateTimeOffset CreatedAt { get; }
    public ApprovalDelegationStatus Status { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedByUserId { get; private set; }

    public bool IsEffectiveAt(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return Status == ApprovalDelegationStatus.Active && ValidFrom <= utc && utc < ValidTo;
    }

    /// <summary>A non-revoked delegation still shadows its interval while scheduled, active or expired.</summary>
    public bool HoldsInterval => Status != ApprovalDelegationStatus.Revoked;

    public static ApprovalDelegation Create(
        Guid id,
        Guid organizationId,
        Guid delegatorUserId,
        Guid delegateeUserId,
        SystemRole role,
        string decisionScopeJson,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovalDelegationActorType actorType,
        Guid actorUserId,
        string reason,
        string delegationCommandKey,
        string fingerprint,
        Guid rootAuditId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || delegatorUserId == Guid.Empty ||
            delegateeUserId == Guid.Empty || actorUserId == Guid.Empty || rootAuditId == Guid.Empty)
        {
            throw new DomainValidationException("A delegation requires complete identities.");
        }

        if (delegatorUserId == delegateeUserId)
        {
            throw new DomainValidationException("A user cannot delegate to themselves.");
        }

        // ADMIN and AUDITOR are reserved roles excluded from business candidacy (SPEC 03 REQ-05);
        // delegating them would transfer operational power, which is out of scope (DEC-01).
        if (role is SystemRole.Admin or SystemRole.Auditor)
        {
            throw new DomainValidationException("ADMIN and AUDITOR assignments cannot be delegated.");
        }

        var from = validFrom.ToUniversalTime();
        var to = validTo.ToUniversalTime();
        if (to <= from)
        {
            throw new DomainValidationException("valid_to must be exclusive and later than valid_from.");
        }

        var scope = DecisionScopeDescriptor.Parse(decisionScopeJson);
        if (scope.OrganizationId != organizationId)
        {
            throw new DomainValidationException("The delegated decision scope belongs to another organization.");
        }

        return new ApprovalDelegation(
            id,
            organizationId,
            delegatorUserId,
            delegateeUserId,
            role,
            scope.ToCanonicalJson(),
            from,
            to,
            actorType,
            actorUserId,
            ApprovalLimits.RequireReason(reason, "Delegation reason"),
            ApprovalLimits.RequireKey(delegationCommandKey, "delegation_command_key"),
            ApprovalLimits.RequireSha256(fingerprint, "delegation fingerprint"),
            rootAuditId,
            createdAt);
    }

    public static ApprovalDelegation Restore(
        Guid id,
        Guid organizationId,
        Guid delegatorUserId,
        Guid delegateeUserId,
        SystemRole role,
        string decisionScopeJson,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovalDelegationActorType actorType,
        Guid actorUserId,
        string reason,
        string delegationCommandKey,
        string fingerprint,
        Guid rootAuditId,
        DateTimeOffset createdAt,
        ApprovalDelegationStatus status,
        int version,
        DateTimeOffset? activatedAt,
        DateTimeOffset? expiredAt,
        DateTimeOffset? revokedAt,
        Guid? revokedByUserId)
    {
        var delegation = Create(
            id, organizationId, delegatorUserId, delegateeUserId, role, decisionScopeJson, validFrom, validTo,
            actorType, actorUserId, reason, delegationCommandKey, fingerprint, rootAuditId, createdAt);
        delegation.Status = status;
        delegation.Version = version;
        delegation.ActivatedAt = activatedAt;
        delegation.ExpiredAt = expiredAt;
        delegation.RevokedAt = revokedAt;
        delegation.RevokedByUserId = revokedByUserId;
        return delegation;
    }

    /// <summary>SCHEDULED → ACTIVE; confirms the exclusive activation instant (REQ-03).</summary>
    public void Activate(DateTimeOffset occurredAt)
    {
        if (Status != ApprovalDelegationStatus.Scheduled)
        {
            throw new DomainConflictException("Only a scheduled delegation can become active.");
        }

        Status = ApprovalDelegationStatus.Active;
        ActivatedAt = occurredAt.ToUniversalTime();
        Version++;
    }

    /// <summary>ACTIVE → EXPIRED; a terminal state that never reopens (REQ-03).</summary>
    public void Expire(DateTimeOffset occurredAt)
    {
        if (Status != ApprovalDelegationStatus.Active)
        {
            throw new DomainConflictException("Only an active delegation can expire.");
        }

        Status = ApprovalDelegationStatus.Expired;
        ExpiredAt = occurredAt.ToUniversalTime();
        Version++;
    }

    /// <summary>SCHEDULED|ACTIVE → REVOKED; irreversible (REQ-01, REQ-03).</summary>
    public void Revoke(DateTimeOffset occurredAt, Guid actorUserId)
    {
        if (Status is not (ApprovalDelegationStatus.Scheduled or ApprovalDelegationStatus.Active))
        {
            throw new DomainConflictException("Only a scheduled or active delegation can be revoked.");
        }

        if (actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A revocation requires the acting user.");
        }

        Status = ApprovalDelegationStatus.Revoked;
        RevokedAt = occurredAt.ToUniversalTime();
        RevokedByUserId = actorUserId;
        Version++;
    }
}

/// <summary>
/// Durable scheduler entry of one delegation transition (REQ-03): unique per delegation,
/// transition and scheduled instant, claimed with leases and never replayed twice.
/// </summary>
public sealed class ApprovalDelegationTransitionJob
{
    private ApprovalDelegationTransitionJob(
        Guid id,
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt)
    {
        Id = id;
        OrganizationId = organizationId;
        DelegationId = delegationId;
        DelegationVersion = delegationVersion;
        Transition = transition;
        ScheduledAt = scheduledAt.ToUniversalTime();
        Status = ApprovalDelegationJobStatus.Pending;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public Guid DelegationId { get; }
    public int DelegationVersion { get; }
    public ApprovalDelegationTransition Transition { get; }
    public DateTimeOffset ScheduledAt { get; }
    public ApprovalDelegationJobStatus Status { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public long FencingToken { get; private set; }
    public int Attempts { get; private set; }
    public int Version { get; private set; } = 1;

    public bool IsClaimableAt(DateTimeOffset now) =>
        Status == ApprovalDelegationJobStatus.Pending &&
        (LockedUntil is null || LockedUntil <= now.ToUniversalTime());

    public static ApprovalDelegationTransitionJob Create(
        Guid id,
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || delegationId == Guid.Empty)
        {
            throw new DomainValidationException("A delegation transition job requires complete identities.");
        }

        if (delegationVersion < 1)
        {
            throw new DomainValidationException("A delegation transition job requires its delegation version.");
        }

        return new ApprovalDelegationTransitionJob(
            id, organizationId, delegationId, delegationVersion, transition, scheduledAt);
    }

    public static ApprovalDelegationTransitionJob Restore(
        Guid id,
        Guid organizationId,
        Guid delegationId,
        int delegationVersion,
        ApprovalDelegationTransition transition,
        DateTimeOffset scheduledAt,
        ApprovalDelegationJobStatus status,
        DateTimeOffset? completedAt,
        DateTimeOffset? cancelledAt,
        string? leaseOwner,
        DateTimeOffset? lockedUntil,
        long fencingToken,
        int attempts,
        int version)
    {
        var job = Create(id, organizationId, delegationId, delegationVersion, transition, scheduledAt);
        job.Status = status;
        job.CompletedAt = completedAt;
        job.CancelledAt = cancelledAt;
        job.LeaseOwner = leaseOwner;
        job.LockedUntil = lockedUntil;
        job.FencingToken = fencingToken;
        job.Attempts = attempts;
        job.Version = version;
        return job;
    }

    /// <summary>Claim under a persistent lease; only the owner with the current fencing token advances it.</summary>
    public void ApplyClaim(string owner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != ApprovalDelegationJobStatus.Pending)
        {
            throw new DomainConflictException("Only a pending transition job can be claimed.");
        }

        LeaseOwner = Normalize(owner);
        LockedUntil = now.ToUniversalTime() + leaseDuration;
        FencingToken++;
        Attempts++;
        Version++;
    }

    public void Complete(DateTimeOffset occurredAt)
    {
        if (Status != ApprovalDelegationJobStatus.Pending)
        {
            throw new DomainConflictException("Only a pending transition job can complete.");
        }

        Status = ApprovalDelegationJobStatus.Completed;
        CompletedAt = occurredAt.ToUniversalTime();
        LeaseOwner = null;
        LockedUntil = null;
        Version++;
    }

    public void Cancel(DateTimeOffset occurredAt)
    {
        if (Status != ApprovalDelegationJobStatus.Pending)
        {
            throw new DomainConflictException("Only a pending transition job can be cancelled.");
        }

        Status = ApprovalDelegationJobStatus.Cancelled;
        CancelledAt = occurredAt.ToUniversalTime();
        LeaseOwner = null;
        LockedUntil = null;
        Version++;
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

/// <summary>Resolved coverage of one delegation against a requirement (REQ-02).</summary>
public sealed record ApprovalDelegationCoverage(
    Guid DelegationId,
    int DelegationVersion,
    Guid DelegatorUserId,
    Guid DelegateeUserId,
    SystemRole Role,
    AuthorizationScopeSet Scope);
