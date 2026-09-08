using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Organization;

public enum OrganizationStatus
{
    Active = 1
}

public enum EntityStatus
{
    Active = 1,
    Inactive = 2
}

public enum UserProfileStatus
{
    PendingSetup = 1,
    Active = 2,
    Inactive = 3
}

public enum SystemRole
{
    Requester = 1,
    DepartmentApprover = 2,
    FinanceApprover = 3,
    ProcurementBuyer = 4,
    ProcurementApprover = 5,
    ApSpecialist = 6,
    PaymentApprover = 7,
    ItReviewer = 8,
    ItProvisioner = 9,
    LegalReviewer = 10,
    Admin = 11,
    Auditor = 12
}

public enum ApprovalAuthorityType
{
    BusinessNeed = 1,
    Financial = 2,
    Procurement = 3,
    SupplierMaster = 4,
    MatchException = 5,
    Payment = 6
}

public enum AuthorityRequirementKind
{
    None = 1,
    Required = 2
}

public enum ScopeDimension
{
    Organization = 1,
    LegalEntity = 2,
    Department = 3,
    CostCenter = 4
}

public enum AssignmentStatus
{
    Active = 1,
    Revoked = 2
}

public enum GrantStatus
{
    Active = 1,
    Revoked = 2
}

public sealed record AuthorizationScope(ScopeDimension Dimension, string? Reference)
{
    public static AuthorizationScope Global() => new(ScopeDimension.Organization, null);

    public static AuthorizationScope For(ScopeDimension dimension, string reference)
    {
        if (dimension == ScopeDimension.Organization)
        {
            throw new DomainValidationException("Organization scope must be explicitly global.");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new DomainValidationException("A non-organization scope requires a reference.");
        }

        return new AuthorizationScope(dimension, reference.Trim());
    }

    public bool Covers(AuthorizationScope required)
    {
        if (Dimension == ScopeDimension.Organization)
        {
            return required.Dimension != ScopeDimension.CostCenter || Reference is null;
        }

        return Dimension == required.Dimension
            && string.Equals(Reference, required.Reference, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AuthorizationScopeSet
{
    private readonly IReadOnlyList<AuthorizationScope> _scopes;

    private AuthorizationScopeSet(IEnumerable<AuthorizationScope> scopes)
    {
        _scopes = scopes.ToArray();
    }

    public IReadOnlyList<AuthorizationScope> Scopes => _scopes;

    public static AuthorizationScopeSet Create(IEnumerable<AuthorizationScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var materialized = scopes.ToArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("At least one explicit authorization scope is required.");
        }

        if (materialized.Any(scope => scope.Dimension == ScopeDimension.CostCenter))
        {
            throw new DomainValidationException("Cost Center scope is not available in this release.");
        }

        var duplicate = materialized
            .GroupBy(scope => (scope.Dimension, Reference: scope.Reference?.ToUpperInvariant()))
            .Any(group => group.Count() > 1);
        if (duplicate)
        {
            throw new DomainConflictException("Authorization scopes cannot be duplicated.");
        }

        var hasGlobal = materialized.Any(scope => scope.Dimension == ScopeDimension.Organization);
        if (hasGlobal && materialized.Length > 1)
        {
            throw new DomainValidationException("Global organization scope cannot be combined with narrower scopes.");
        }

        return new AuthorizationScopeSet(materialized);
    }

    public bool Covers(AuthorizationScopeSet required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return required._scopes.All(requiredScope => _scopes.Any(scope => scope.Covers(requiredScope)));
    }

    public bool Overlaps(AuthorizationScopeSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _scopes.Any(scope => other._scopes.Any(otherScope =>
            scope.Covers(otherScope) || otherScope.Covers(scope)));
    }

    public override string ToString() => string.Join(",", _scopes.Select(scope =>
        scope.Dimension == ScopeDimension.Organization
            ? "ORGANIZATION"
            : $"{scope.Dimension}:{scope.Reference}"));
}

public sealed class Organization
{
    public Organization(
        Guid id,
        string code,
        string name,
        string baseCurrency,
        string timeZoneId,
        int fiscalYearStartMonth)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException("Organization id is required.") : id;
        Code = NormalizeCode(code, "Organization code");
        Name = NormalizeName(name, "Organization name");
        BaseCurrency = NormalizeCurrency(baseCurrency);
        TimeZoneId = ValidateTimeZone(timeZoneId);
        FiscalYearStartMonth = ValidateFiscalMonth(fiscalYearStartMonth);
        Status = OrganizationStatus.Active;
    }

    public Guid Id { get; }
    public string Code { get; }
    public string Name { get; private set; }
    public string BaseCurrency { get; }
    public string TimeZoneId { get; private set; }
    public int FiscalYearStartMonth { get; }
    public OrganizationStatus Status { get; }
    public int Version { get; private set; } = 1;

    public void UpdateProfile(string name, string timeZoneId)
    {
        Name = NormalizeName(name, "Organization name");
        TimeZoneId = ValidateTimeZone(timeZoneId);
        Version++;
    }

    private static string NormalizeCode(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 80)
        {
            throw new DomainValidationException($"{field} must contain between 1 and 80 characters.");
        }

        return value.Trim();
    }

    private static string NormalizeName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160)
        {
            throw new DomainValidationException($"{field} must contain between 1 and 160 characters.");
        }

        return value.Trim();
    }

    private static string NormalizeCurrency(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (normalized is null || normalized.Length != 3 ||
            normalized.Any(character => character is < 'A' or > 'Z') ||
            !CurrencyCatalog.IsIso4217(normalized))
        {
            throw new DomainValidationException("Base currency must be a three-letter ISO 4217 code.");
        }

        return normalized;
    }

    private static string ValidateTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TimeZoneInfo.TryFindSystemTimeZoneById(value.Trim(), out _))
        {
            throw new DomainValidationException("Time zone must be a valid IANA identifier.");
        }

        return value.Trim();
    }

    private static int ValidateFiscalMonth(int value)
    {
        if (value is < 1 or > 12)
        {
            throw new DomainValidationException("Fiscal year start month must be between 1 and 12.");
        }

        return value;
    }
}

public sealed class LegalEntity
{
    public LegalEntity(Guid id, Guid organizationId, string code, string name)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException("Legal Entity id is required.") : id;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("Organization id is required.")
            : organizationId;
        Code = Normalize(code, "Legal Entity code");
        Name = Normalize(name, "Legal Entity name");
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public string Code { get; }
    public string Name { get; private set; }
    public EntityStatus Status { get; private set; } = EntityStatus.Active;
    public int Version { get; private set; } = 1;

    public void Rename(string name)
    {
        Name = Normalize(name, "Legal Entity name");
        Version++;
    }

    private static string Normalize(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160)
        {
            throw new DomainValidationException($"{field} must contain between 1 and 160 characters.");
        }

        return value.Trim();
    }
}

public sealed class Department
{
    public Department(Guid id, string code, string name)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException("Department id is required.") : id;
        Code = NormalizeCode(code);
        Name = NormalizeName(name);
    }

    public Guid Id { get; }
    public string Code { get; }
    public string Name { get; private set; }
    public EntityStatus Status { get; private set; } = EntityStatus.Active;
    public int Version { get; private set; } = 1;

    public void Rename(string name)
    {
        Name = NormalizeName(name);
        Version++;
    }

    public void Deactivate(bool hasActiveUsers, bool hasReferencedAssignmentsOrGrants)
    {
        if (hasActiveUsers || hasReferencedAssignmentsOrGrants)
        {
            throw new DomainConflictException("Department cannot be deactivated while it has active references.");
        }

        Status = EntityStatus.Inactive;
        Version++;
    }

    public void Reactivate()
    {
        Status = EntityStatus.Active;
        Version++;
    }

    private static string NormalizeCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 80)
        {
            throw new DomainValidationException("Department code must contain between 1 and 80 characters.");
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160)
        {
            throw new DomainValidationException("Department name must contain between 1 and 160 characters.");
        }

        return value.Trim();
    }
}

public sealed class UserProfile
{
    public UserProfile(
        Guid id,
        string issuer,
        string subject,
        string? email,
        string? displayName)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException("User id is required.") : id;
        Issuer = NormalizeRequired(issuer, "Issuer");
        Subject = NormalizeRequired(subject, "Subject");
        Email = NormalizeOptional(email);
        DisplayName = NormalizeOptional(displayName);
        Status = UserProfileStatus.PendingSetup;
    }

    public Guid Id { get; }
    public string Issuer { get; }
    public string Subject { get; }
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public Guid? DepartmentId { get; private set; }
    public string? JobTitle { get; private set; }
    public UserProfileStatus Status { get; private set; }
    public int Version { get; private set; } = 1;

    public void RefreshIdentityClaims(string? email, string? displayName)
    {
        Email = NormalizeOptional(email);
        DisplayName = NormalizeOptional(displayName);
        Version++;
    }

    public void CompleteSetup(Guid departmentId, string jobTitle)
    {
        if (Status != UserProfileStatus.PendingSetup)
        {
            throw new DomainConflictException("Only a pending user can complete setup.");
        }

        if (departmentId == Guid.Empty)
        {
            throw new DomainValidationException("An active Department is required.");
        }

        DepartmentId = departmentId;
        JobTitle = NormalizeRequired(jobTitle, "Job Title");
        Version++;
    }

    public void Activate(bool departmentIsActive)
    {
        if (Status != UserProfileStatus.PendingSetup)
        {
            throw new DomainConflictException("Only a pending user can be activated.");
        }

        if (DepartmentId is null || string.IsNullOrWhiteSpace(JobTitle) || !departmentIsActive)
        {
            throw new DomainValidationException("User requires an active Department and Job Title before activation.");
        }

        Status = UserProfileStatus.Active;
        Version++;
    }

    public void Deactivate()
    {
        if (Status != UserProfileStatus.Active)
        {
            throw new DomainConflictException("Only an active user can be deactivated.");
        }

        Status = UserProfileStatus.Inactive;
        Version++;
    }

    public void ReturnToSetup()
    {
        if (Status != UserProfileStatus.Inactive)
        {
            throw new DomainConflictException("Only an inactive user can return to setup.");
        }

        Status = UserProfileStatus.PendingSetup;
        Version++;
    }

    private static string NormalizeRequired(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 320)
        {
            throw new DomainValidationException($"{field} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
