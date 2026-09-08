namespace ProcureToPay.Infrastructure.Persistence.Organization;

public sealed class BootstrapStateRecord
{
    public int Id { get; set; } = 1;
    public string ConfigurationFingerprint { get; set; } = null!;
    public DateTimeOffset CompletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class OrganizationRecord
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string BaseCurrency { get; set; } = null!;
    public string TimeZoneId { get; set; } = null!;
    public int FiscalYearStartMonth { get; set; }
    public int SingletonKey { get; set; } = 1;
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<LegalEntityRecord> LegalEntities { get; } = [];
    public ICollection<DepartmentRecord> Departments { get; } = [];
    public ICollection<UserProfileRecord> Users { get; } = [];
}

public sealed class LegalEntityRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Status { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
}

public sealed class DepartmentRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Status { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public ICollection<UserProfileRecord> Users { get; } = [];
}

public sealed class UserProfileRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Issuer { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? JobTitle { get; set; }
    public int Status { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public DepartmentRecord? Department { get; set; }
    public ICollection<RoleAssignmentRecord> RoleAssignments { get; } = [];
    public ICollection<AuthorityGrantRecord> AuthorityGrants { get; } = [];
}

public sealed class RoleAssignmentRecord
{
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public int Role { get; set; }
    public string ScopeJson { get; set; } = null!;
    public int Status { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public Guid AssignedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public UserProfileRecord UserProfile { get; set; } = null!;
}

public sealed class AuthorityLevelRecord
{
    public Guid Id { get; set; }
    public int Type { get; set; }
    public string Code { get; set; } = null!;
    public int Rank { get; set; }
    public int LevelVersion { get; set; }
    public bool IsActive { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<AuthorityGrantRecord> Grants { get; } = [];
}

public sealed class AuthorityGrantRecord
{
    public Guid Id { get; set; }
    public Guid UserProfileId { get; set; }
    public Guid AuthorityLevelId { get; set; }
    public decimal? MaxAmountBase { get; set; }
    public string BaseCurrency { get; set; } = null!;
    public string ScopeJson { get; set; } = null!;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public int Status { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public Guid GrantedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public UserProfileRecord UserProfile { get; set; } = null!;
    public AuthorityLevelRecord AuthorityLevel { get; set; } = null!;
}

public sealed class AdministrativeAuditRecord
{
    public Guid Id { get; set; }
    public string ActorType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = null!;
    public string TargetType { get; set; } = null!;
    public Guid TargetId { get; set; }
    public int? PreviousVersion { get; set; }
    public int? NewVersion { get; set; }
    public string ScopeJson { get; set; } = null!;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string Reason { get; set; } = null!;
    public string CorrelationReference { get; set; } = null!;
}
