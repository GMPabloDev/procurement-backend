using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence;

public sealed class ProcureToPayDbContext(DbContextOptions<ProcureToPayDbContext> options)
    : DbContext(options)
{
    public DbSet<BootstrapStateRecord> BootstrapStates => Set<BootstrapStateRecord>();
    public DbSet<OrganizationRecord> Organizations => Set<OrganizationRecord>();
    public DbSet<LegalEntityRecord> LegalEntities => Set<LegalEntityRecord>();
    public DbSet<DepartmentRecord> Departments => Set<DepartmentRecord>();
    public DbSet<UserProfileRecord> UserProfiles => Set<UserProfileRecord>();
    public DbSet<RoleAssignmentRecord> RoleAssignments => Set<RoleAssignmentRecord>();
    public DbSet<AuthorityLevelRecord> AuthorityLevels => Set<AuthorityLevelRecord>();
    public DbSet<AuthorityGrantRecord> AuthorityGrants => Set<AuthorityGrantRecord>();
    public DbSet<AdministrativeAuditRecord> AdministrativeAuditRecords => Set<AdministrativeAuditRecord>();
    public DbSet<PolicySetVersionRecord> PolicySetVersions => Set<PolicySetVersionRecord>();
    public DbSet<PolicyActivationRecord> PolicyActivations => Set<PolicyActivationRecord>();
    public DbSet<PolicyRetirementRecord> PolicyRetirements => Set<PolicyRetirementRecord>();
    public DbSet<PolicyEvaluationBundleRecord> PolicyEvaluationBundles => Set<PolicyEvaluationBundleRecord>();
    public DbSet<PolicyEvaluationReservationRecord> PolicyEvaluationReservations => Set<PolicyEvaluationReservationRecord>();
    public DbSet<PolicyExceptionVerificationRecord> PolicyExceptionVerifications => Set<PolicyExceptionVerificationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureBootstrapState(modelBuilder);
        ConfigureOrganization(modelBuilder);
        ConfigureLegalEntity(modelBuilder);
        ConfigureDepartment(modelBuilder);
        ConfigureUserProfile(modelBuilder);
        ConfigureRoleAssignment(modelBuilder);
        ConfigureAuthorityLevel(modelBuilder);
        ConfigureAuthorityGrant(modelBuilder);
        ConfigureAdministrativeAudit(modelBuilder);
        ConfigurePolicySetVersion(modelBuilder);
        ConfigurePolicyActivation(modelBuilder);
        ConfigurePolicyRetirement(modelBuilder);
        ConfigurePolicyEvaluationReservation(modelBuilder);
        ConfigurePolicyEvaluationBundle(modelBuilder);
        ConfigurePolicyExceptionVerification(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureBootstrapState(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BootstrapStateRecord>();
        entity.ToTable("BootstrapStates", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Id).ValueGeneratedNever();
        entity.Property(record => record.ConfigurationFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
    }

    private static void ConfigureOrganization(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<OrganizationRecord>();
        entity.ToTable("Organizations", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Name).HasMaxLength(160).IsRequired();
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.TimeZoneId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.SingletonKey).IsUnique();
    }

    private static void ConfigureLegalEntity(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<LegalEntityRecord>();
        entity.ToTable("LegalEntities", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Name).HasMaxLength(160).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.OrganizationId).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Code }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany(record => record.LegalEntities)
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDepartment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DepartmentRecord>();
        entity.ToTable("Departments", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(80).IsRequired();
        entity.Property(record => record.Name).HasMaxLength(160).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.Code }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany(record => record.Departments)
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUserProfile(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserProfileRecord>();
        entity.ToTable("UserProfiles", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Issuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.Subject).HasMaxLength(320).IsRequired();
        entity.Property(record => record.Email).HasMaxLength(320);
        entity.Property(record => record.DisplayName).HasMaxLength(320);
        entity.Property(record => record.JobTitle).HasMaxLength(160);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.Issuer, record.Subject }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany(record => record.Users)
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(record => record.Department)
            .WithMany(record => record.Users)
            .HasForeignKey(record => record.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRoleAssignment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RoleAssignmentRecord>();
        entity.ToTable("RoleAssignments", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.UserProfileId, record.Role, record.ScopeJson })
            .IsUnique()
            .HasFilter("[Status] = 1");
        entity.HasOne(record => record.UserProfile)
            .WithMany(record => record.RoleAssignments)
            .HasForeignKey(record => record.UserProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAuthorityLevel(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuthorityLevelRecord>();
        entity.ToTable("AuthorityLevels", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(80).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.Type, record.Code, record.LevelVersion }).IsUnique();
        entity.HasIndex(record => new { record.Type, record.Rank, record.LevelVersion }).IsUnique();
    }

    private static void ConfigureAuthorityGrant(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuthorityGrantRecord>();
        entity.ToTable("AuthorityGrants", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.MaxAmountBase).HasPrecision(19, 4);
        entity.Property(record => record.ScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasOne(record => record.UserProfile)
            .WithMany(record => record.AuthorityGrants)
            .HasForeignKey(record => record.UserProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(record => record.AuthorityLevel)
            .WithMany(record => record.Grants)
            .HasForeignKey(record => record.AuthorityLevelId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePolicySetVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicySetVersionRecord>();
        entity.ToTable("PolicySetVersions", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ScopesJson).HasMaxLength(2000).IsRequired();
        entity.Property(record => record.ContentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.Sequence }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePolicyActivation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicyActivationRecord>();
        entity.ToTable("PolicyActivations", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.EffectiveFrom });
        entity.HasIndex(record => new { record.PolicySetVersionId, record.EffectiveFrom }).IsUnique();
        entity.HasOne(record => record.PolicySetVersion)
            .WithMany(record => record.Activations)
            .HasForeignKey(record => record.PolicySetVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePolicyRetirement(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicyRetirementRecord>();
        entity.ToTable("PolicyRetirements", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.HasIndex(record => record.PolicyActivationId).IsUnique();
        entity.HasOne(record => record.PolicyActivation)
            .WithOne(record => record.Retirement)
            .HasForeignKey<PolicyRetirementRecord>(record => record.PolicyActivationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePolicyExceptionVerification(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicyExceptionVerificationRecord>();
        entity.ToTable("PolicyExceptionVerifications", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.WorkflowDecisionId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.TargetRequirementKey).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Binding).HasMaxLength(256).IsRequired();
        entity.Property(record => record.Nonce).HasMaxLength(256).IsRequired();
        entity.Property(record => record.EvidenceDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.VerifierReference).HasMaxLength(256).IsRequired();
        entity.Property(record => record.SnapshotJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.WorkflowDecisionId, record.BaseBundleId, record.TargetRequirementKey })
            .IsUnique();
        entity.HasIndex(record => record.EvaluationBundleId);
    }

    private static void ConfigurePolicyEvaluationReservation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicyEvaluationReservationRecord>();
        entity.ToTable("PolicyEvaluationReservations", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Operation).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EvaluationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.IdempotencyFingerprint).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkloadIssuer,
            record.WorkloadClientId,
            record.Operation,
            record.EvaluationKey
        }).IsUnique();
    }

    private static void ConfigurePolicyEvaluationBundle(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PolicyEvaluationBundleRecord>();
        entity.ToTable("PolicyEvaluationBundles", "Policy");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.EvaluationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Operation).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PolicyContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.InputDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ResultDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.BundleJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.IdempotencyFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkloadIssuer,
            record.WorkloadClientId,
            record.Operation,
            record.EvaluationKey
        }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.SubjectId, record.SubjectVersion });
        entity.HasOne(record => record.PolicySetVersion)
            .WithMany(record => record.Evaluations)
            .HasForeignKey(record => record.PolicySetVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAdministrativeAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AdministrativeAuditRecord>();
        entity.ToTable("AdministrativeAuditRecords", "Organization");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Action).HasMaxLength(120).IsRequired();
        entity.Property(record => record.TargetType).HasMaxLength(120).IsRequired();
        entity.Property(record => record.ScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.BeforeJson).HasColumnType("nvarchar(max)");
        entity.Property(record => record.AfterJson).HasColumnType("nvarchar(max)");
        entity.HasIndex(record => new { record.TargetType, record.TargetId, record.OccurredAt });
    }
}
