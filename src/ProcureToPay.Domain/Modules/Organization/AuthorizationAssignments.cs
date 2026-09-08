using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Organization;

public sealed class RoleAssignment
{
    private RoleAssignment(
        Guid id,
        Guid userId,
        SystemRole role,
        AuthorizationScopeSet scope,
        DateTimeOffset assignedAt,
        Guid assignedBy)
    {
        Id = id;
        UserId = userId;
        Role = role;
        Scope = scope;
        AssignedAt = assignedAt.ToUniversalTime();
        AssignedBy = assignedBy;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public SystemRole Role { get; }
    public AuthorizationScopeSet Scope { get; }
    public AssignmentStatus Status { get; private set; } = AssignmentStatus.Active;
    public DateTimeOffset AssignedAt { get; }
    public Guid AssignedBy { get; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }
    public int Version { get; private set; } = 1;

    public static RoleAssignment Create(
        Guid id,
        Guid userId,
        SystemRole role,
        AuthorizationScopeSet scope,
        DateTimeOffset assignedAt,
        Guid assignedBy)
    {
        if (id == Guid.Empty || userId == Guid.Empty || assignedBy == Guid.Empty)
        {
            throw new DomainValidationException("Assignment identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(scope);
        if (role == SystemRole.Admin &&
            !scope.Scopes.SequenceEqual([AuthorizationScope.Global()]))
        {
            throw new DomainValidationException("ADMIN assignments must have organization scope.");
        }

        return new RoleAssignment(id, userId, role, scope, assignedAt, assignedBy);
    }

    public bool Covers(AuthorizationScopeSet requiredScope) =>
        Status == AssignmentStatus.Active && Scope.Covers(requiredScope);

    public void Revoke(DateTimeOffset revokedAt, Guid revokedBy)
    {
        if (Status != AssignmentStatus.Active)
        {
            throw new DomainConflictException("Role assignment is already revoked.");
        }

        if (revokedBy == Guid.Empty)
        {
            throw new DomainValidationException("Revoking actor is required.");
        }

        Status = AssignmentStatus.Revoked;
        RevokedAt = revokedAt.ToUniversalTime();
        RevokedBy = revokedBy;
        Version++;
    }
}

public sealed class AuthorityLevelVersion
{
    public AuthorityLevelVersion(
        Guid id,
        ApprovalAuthorityType type,
        string code,
        int rank,
        int version,
        bool isActive = true)
    {
        if (id == Guid.Empty || version < 1 || rank < 1)
        {
            throw new DomainValidationException("Authority level identifiers, rank and version are required.");
        }

        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 80)
        {
            throw new DomainValidationException("Authority level code must contain between 1 and 80 characters.");
        }

        Id = id;
        Type = type;
        Code = code.Trim();
        Rank = rank;
        Version = version;
        IsActive = isActive;
    }

    public Guid Id { get; }
    public ApprovalAuthorityType Type { get; }
    public string Code { get; }
    public int Rank { get; }
    public int Version { get; }
    public bool IsActive { get; private set; }

    public void Retire()
    {
        IsActive = false;
    }
}

public sealed class ApprovalAuthorityGrant
{
    private ApprovalAuthorityGrant(
        Guid id,
        Guid userId,
        AuthorityLevelVersion level,
        decimal? maxAmountBase,
        string baseCurrency,
        AuthorizationScopeSet scope,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        DateTimeOffset grantedAt,
        Guid grantedBy)
    {
        Id = id;
        UserId = userId;
        Type = level.Type;
        Level = level;
        MaxAmountBase = maxAmountBase;
        BaseCurrency = baseCurrency;
        Scope = scope;
        ValidFrom = validFrom.ToUniversalTime();
        ValidTo = validTo?.ToUniversalTime();
        GrantedAt = grantedAt.ToUniversalTime();
        GrantedBy = grantedBy;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public ApprovalAuthorityType Type { get; }
    public AuthorityLevelVersion Level { get; }
    public decimal? MaxAmountBase { get; }
    public string BaseCurrency { get; }
    public AuthorizationScopeSet Scope { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset? ValidTo { get; }
    public GrantStatus Status { get; private set; } = GrantStatus.Active;
    public DateTimeOffset GrantedAt { get; }
    public Guid GrantedBy { get; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }
    public int Version { get; private set; } = 1;

    public static ApprovalAuthorityGrant Create(
        Guid id,
        Guid userId,
        AuthorityLevelVersion level,
        decimal? maxAmountBase,
        string baseCurrency,
        AuthorizationScopeSet scope,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        DateTimeOffset grantedAt,
        Guid grantedBy)
    {
        if (id == Guid.Empty || userId == Guid.Empty || grantedBy == Guid.Empty)
        {
            throw new DomainValidationException("Grant identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(scope);
        if (!level.IsActive)
        {
            throw new DomainValidationException("An inactive authority level cannot be granted.");
        }

        var normalizedCurrency = baseCurrency?.Trim().ToUpperInvariant();
        if (normalizedCurrency is null || normalizedCurrency.Length != 3 ||
            normalizedCurrency.Any(character => character is < 'A' or > 'Z') ||
            !CurrencyCatalog.IsIso4217(normalizedCurrency))
        {
            throw new DomainValidationException("Grant base currency must be a three-letter ISO 4217 code.");
        }

        if (maxAmountBase is < 0)
        {
            throw new DomainValidationException("Grant amount cannot be negative.");
        }

        var normalizedFrom = validFrom.ToUniversalTime();
        var normalizedTo = validTo?.ToUniversalTime();
        if (normalizedTo is not null && normalizedTo <= normalizedFrom)
        {
            throw new DomainValidationException("Grant valid_to must be later than valid_from.");
        }

        return new ApprovalAuthorityGrant(
            id,
            userId,
            level,
            maxAmountBase,
            normalizedCurrency,
            scope,
            normalizedFrom,
            normalizedTo,
            grantedAt,
            grantedBy);
    }

    public bool IsEffectiveAt(DateTimeOffset at) =>
        Status == GrantStatus.Active &&
        at.ToUniversalTime() >= ValidFrom &&
        (ValidTo is null || at.ToUniversalTime() < ValidTo);

    public bool Covers(ApprovalAuthorityType type, int minimumRank, decimal? amountBase, AuthorizationScopeSet requiredScope, DateTimeOffset at)
    {
        return Covers(type, minimumRank, amountBase, null, requiredScope, at);
    }

    public bool Covers(
        ApprovalAuthorityType type,
        int minimumRank,
        decimal? amountBase,
        string? requiredBaseCurrency,
        AuthorizationScopeSet requiredScope,
        DateTimeOffset at)
    {
        return Type == type &&
            Level.Rank >= minimumRank &&
            IsEffectiveAt(at) &&
            (requiredBaseCurrency is null ||
             string.Equals(BaseCurrency, requiredBaseCurrency, StringComparison.OrdinalIgnoreCase)) &&
            (amountBase is null || MaxAmountBase is not null && MaxAmountBase >= amountBase) &&
            Scope.Covers(requiredScope);
    }

    public void Revoke(DateTimeOffset revokedAt, Guid revokedBy)
    {
        if (Status != GrantStatus.Active)
        {
            throw new DomainConflictException("Authority grant is already revoked.");
        }

        if (revokedBy == Guid.Empty)
        {
            throw new DomainValidationException("Revoking actor is required.");
        }

        Status = GrantStatus.Revoked;
        RevokedAt = revokedAt.ToUniversalTime();
        RevokedBy = revokedBy;
        Version++;
    }
}

public static class AuthorizationRules
{
    public static void EnsureNoOverlappingAssignment(
        IEnumerable<RoleAssignment> existing,
        RoleAssignment candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        if (existing.Any(assignment =>
            assignment.Status == AssignmentStatus.Active &&
            assignment.UserId == candidate.UserId &&
            assignment.Role == candidate.Role &&
            assignment.Scope.Overlaps(candidate.Scope)))
        {
            throw new DomainConflictException("An active role assignment overlaps the requested scope.");
        }
    }
}
