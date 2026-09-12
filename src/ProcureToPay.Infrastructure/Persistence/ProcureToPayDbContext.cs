using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence.Approval;
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
    public DbSet<ApprovalCaseRecord> ApprovalCases => Set<ApprovalCaseRecord>();
    public DbSet<ApprovalRequirementRecord> ApprovalRequirements => Set<ApprovalRequirementRecord>();
    public DbSet<ApprovalPrerequisiteRecord> ApprovalPrerequisites => Set<ApprovalPrerequisiteRecord>();
    public DbSet<ApprovalPrerequisiteSignalRecord> ApprovalPrerequisiteSignals => Set<ApprovalPrerequisiteSignalRecord>();
    public DbSet<ApprovalTaskRecord> ApprovalTasks => Set<ApprovalTaskRecord>();
    public DbSet<ApprovalAssignmentRecord> ApprovalAssignments => Set<ApprovalAssignmentRecord>();
    public DbSet<ApprovalDecisionRecord> ApprovalDecisions => Set<ApprovalDecisionRecord>();
    public DbSet<ApprovalDecisionTargetRecord> ApprovalDecisionTargets => Set<ApprovalDecisionTargetRecord>();
    public DbSet<ApprovalOutboxEventRecord> ApprovalOutboxEvents => Set<ApprovalOutboxEventRecord>();
    public DbSet<ApprovalAuditEntryRecord> ApprovalAuditEntries => Set<ApprovalAuditEntryRecord>();
    public DbSet<ApprovalSubmissionReservationRecord> ApprovalSubmissionReservations => Set<ApprovalSubmissionReservationRecord>();
    public DbSet<ApprovalWorkflowStateRecord> ApprovalWorkflowStates => Set<ApprovalWorkflowStateRecord>();
    public DbSet<ApprovalReconciliationRunRecord> ApprovalReconciliationRuns => Set<ApprovalReconciliationRunRecord>();

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
        ConfigureApprovalCase(modelBuilder);
        ConfigureApprovalRequirement(modelBuilder);
        ConfigureApprovalPrerequisite(modelBuilder);
        ConfigureApprovalPrerequisiteSignal(modelBuilder);
        ConfigureApprovalTask(modelBuilder);
        ConfigureApprovalAssignment(modelBuilder);
        ConfigureApprovalDecision(modelBuilder);
        ConfigureApprovalDecisionTarget(modelBuilder);
        ConfigureApprovalOutboxEvent(modelBuilder);
        ConfigureApprovalAudit(modelBuilder);
        ConfigureApprovalSubmissionReservation(modelBuilder);
        ConfigureApprovalWorkflowState(modelBuilder);
        ConfigureApprovalReconciliationRun(modelBuilder);
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
        entity.Property(record => record.EvaluationSequence).IsRequired();
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
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.SubjectId,
            record.SubjectVersion,
            record.EvaluationSequence
        }).IsUnique();
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

    private static void ConfigureApprovalCase(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalCaseRecord>();
        entity.ToTable("ApprovalCases", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SubjectType).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Operation).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SourceSnapshotDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SubmissionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SubmissionFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.CancellationReason).HasMaxLength(1000);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkloadIssuer,
            record.WorkloadClientId,
            record.SubjectType,
            record.Operation,
            record.SubmissionKey
        }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.SubjectId, record.SubjectVersion });
    }

    private static void ConfigureApprovalRequirement(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalRequirementRecord>();
        entity.ToTable("ApprovalRequirements", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SourceRequirementKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.WorkflowRequirementKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.StageCode).HasMaxLength(128).IsRequired();
        entity.Property(record => record.AuthorityJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.DecisionScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.ExcludedUserIdsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.TargetsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.DependenciesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.CaseId, record.WorkflowRequirementKey }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Status });
    }

    private static void ConfigureApprovalPrerequisite(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalPrerequisiteRecord>();
        entity.ToTable("ApprovalPrerequisites", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Key).HasMaxLength(128).IsRequired();
        entity.Property(record => record.OwnerAdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.OwnerAdapterVersion).HasMaxLength(128).IsRequired();
        // Owner workload identity resolved exactly once at ingestion (REQ-03, DEC-11).
        entity.Property(record => record.OwnerWorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.OwnerWorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SourceControlType).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SourceControlDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ParametersJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.TargetsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(128);
        entity.Property(record => record.SignalFingerprint).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.CaseId, record.Key }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Status });
    }

    private static void ConfigureApprovalPrerequisiteSignal(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalPrerequisiteSignalRecord>();
        entity.ToTable("ApprovalPrerequisiteSignals", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SignalKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SignalFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.EvidenceReference).HasMaxLength(256);
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.PrerequisiteId, record.SignalKey }).IsUnique();
        entity.HasIndex(record => record.CaseId);
    }

    private static void ConfigureApprovalTask(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalTaskRecord>();
        entity.ToTable("ApprovalTasks", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.RequirementId).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Status, record.CurrentAssigneeUserId });
        entity.HasIndex(record => new { record.CaseId, record.Status });
    }

    private static void ConfigureApprovalAssignment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalAssignmentRecord>();
        entity.ToTable("ApprovalAssignments", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Cause).HasMaxLength(32).IsRequired();
        entity.Property(record => record.EligibilityEvidenceJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.AssigneeUserId, record.ReleasedAt });
        // Exactly one current assignment per task (REQ-10, CA-04): released history is append-only.
        entity.HasIndex(record => record.TaskId)
            .IsUnique()
            .HasFilter("[ReleasedAt] IS NULL")
            .HasDatabaseName("IX_ApprovalAssignments_TaskId_Current");
    }

    private static void ConfigureApprovalDecision(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalDecisionRecord>();
        entity.ToTable("ApprovalDecisions", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.DecisionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.DecisionDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AuthorityEvidenceDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EligibilityEvidenceJson).HasColumnType("nvarchar(max)").IsRequired();
        // Stored fingerprint preimage versions (REQ-06): a replay must reproduce the digest.
        entity.Property(record => record.RequirementVersion).IsRequired();
        entity.Property(record => record.TaskVersion).IsRequired();
        // Artifacts of the decision instant so a replay returns them instead of the current state.
        entity.Property(record => record.RequirementStatusAfter).IsRequired();
        entity.Property(record => record.TaskStatusAfter).IsRequired();
        entity.Property(record => record.CaseStatusAfter).IsRequired();
        entity.Property(record => record.CaseVersionAfter).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.DecisionKey }).IsUnique();
        entity.HasIndex(record => record.RequirementId);
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.DecidedAt });
    }

    private static void ConfigureApprovalDecisionTarget(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalDecisionTargetRecord>();
        entity.ToTable("ApprovalDecisionTargets", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.TargetType).HasMaxLength(128).IsRequired();
        entity.Property(record => record.MaterialSnapshotDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new
        {
            record.RequirementId,
            record.TargetType,
            record.TargetId,
            record.TargetVersion,
            record.MaterialSnapshotDigest
        }).IsUnique();
        entity.HasIndex(record => record.DecisionId);
    }

    private static void ConfigureApprovalOutboxEvent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalOutboxEventRecord>();
        entity.ToTable("ApprovalOutboxEvents", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.TargetType).HasMaxLength(128).IsRequired();
        entity.Property(record => record.MaterialSnapshotDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ResultSourceType).HasMaxLength(32);
        entity.Property(record => record.ResultSourceKey).HasMaxLength(128);
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.LastError).HasMaxLength(400);
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.LockOwner).HasMaxLength(120);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.State, record.NextAttemptAt, record.CreatedAt });
        entity.HasIndex(record => record.CaseId);
        // Replay artifacts: events produced by one decision or signal command (NFR-01).
        entity.HasIndex(record => new { record.SourceCommandId });
        // Dispatcher and backlog hot path: due events of one organization in creation order (REQ-10).
        entity.HasIndex(record => new { record.OrganizationId, record.State, record.NextAttemptAt, record.CreatedAt });
    }

    private static void ConfigureApprovalAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalAuditEntryRecord>();
        entity.ToTable("ApprovalAuditEntries", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        // Closed actor union: exactly one variant is populated and an empty UUID is never used.
        entity.Property(record => record.ActorWorkloadIssuer).HasMaxLength(320);
        entity.Property(record => record.ActorWorkloadClientId).HasMaxLength(128);
        entity.Property(record => record.ActorSystemId).HasMaxLength(64);
        entity.Property(record => record.CausedByAuditStream).HasMaxLength(32);
        entity.Property(record => record.AutomaticEffectKey).HasMaxLength(64);
        entity.Property(record => record.Action).HasMaxLength(120).IsRequired();
        entity.Property(record => record.TargetType).HasMaxLength(120).IsRequired();
        entity.Property(record => record.ScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.BeforeJson).HasColumnType("nvarchar(max)");
        entity.Property(record => record.AfterJson).HasColumnType("nvarchar(max)");
        entity.HasIndex(record => new { record.OrganizationId, record.OccurredAt });
        entity.HasIndex(record => new { record.TargetType, record.TargetId, record.OccurredAt });
        // Exactly one audit per automatic effect key in the organization (REQ-08).
        entity.HasIndex(record => new { record.OrganizationId, record.AutomaticEffectKey })
            .IsUnique()
            .HasFilter("[AutomaticEffectKey] IS NOT NULL")
            .HasDatabaseName("IX_ApprovalAuditEntries_OrganizationId_AutomaticEffectKey");
    }

    private static void ConfigureApprovalSubmissionReservation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalSubmissionReservationRecord>();
        entity.ToTable("ApprovalSubmissionReservations", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SubjectType).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Operation).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SubmissionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkloadIssuer,
            record.WorkloadClientId,
            record.SubjectType,
            record.Operation,
            record.SubmissionKey
        }).IsUnique();
        entity.HasIndex(record => record.CaseId).IsUnique();
    }

    private static void ConfigureApprovalWorkflowState(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalWorkflowStateRecord>();
        entity.ToTable("ApprovalWorkflowStates", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.LastReconciliationOwner).HasMaxLength(120);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.OrganizationId).IsUnique();
    }

    private static void ConfigureApprovalReconciliationRun(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalReconciliationRunRecord>();
        entity.ToTable("ApprovalReconciliationRuns", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Trigger).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Status).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ReconciliationKey).HasMaxLength(128);
        entity.Property(record => record.LeaseOwner).HasMaxLength(120);
        entity.Property(record => record.LastError).HasMaxLength(200);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // Idempotency of the two request triggers (REQ-10): one run per admin key and organization audit.
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.ReconciliationKey })
            .IsUnique()
            .HasFilter("[ReconciliationKey] IS NOT NULL")
            .HasDatabaseName("IX_ApprovalReconciliationRuns_AdminKey");
        entity.HasIndex(record => new { record.OrganizationId, record.TriggerAuditId })
            .IsUnique()
            .HasFilter("[TriggerAuditId] IS NOT NULL")
            .HasDatabaseName("IX_ApprovalReconciliationRuns_TriggerAudit");
        entity.HasIndex(record => new { record.OrganizationId, record.Status, record.RequestedAt });
    }
}
