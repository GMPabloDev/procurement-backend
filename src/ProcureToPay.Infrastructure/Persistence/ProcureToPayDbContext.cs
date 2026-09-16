using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

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
    public DbSet<ApprovalPolicyExceptionRequestRecord> ApprovalPolicyExceptionRequests => Set<ApprovalPolicyExceptionRequestRecord>();
    public DbSet<ApprovalPolicyExceptionVerificationRecord> ApprovalPolicyExceptionVerifications => Set<ApprovalPolicyExceptionVerificationRecord>();
    public DbSet<ApprovalWorkflowStateRecord> ApprovalWorkflowStates => Set<ApprovalWorkflowStateRecord>();
    public DbSet<ApprovalReconciliationRunRecord> ApprovalReconciliationRuns => Set<ApprovalReconciliationRunRecord>();
    public DbSet<ApprovalDelegationRecord> ApprovalDelegations => Set<ApprovalDelegationRecord>();
    public DbSet<ApprovalDelegationCommandRecord> ApprovalDelegationCommands => Set<ApprovalDelegationCommandRecord>();
    public DbSet<ApprovalDelegationTransitionJobRecord> ApprovalDelegationTransitionJobs =>
        Set<ApprovalDelegationTransitionJobRecord>();
    public DbSet<CaseSupersessionRecord> CaseSupersessions => Set<CaseSupersessionRecord>();
    public DbSet<DecisionAuthorityEvidenceRecord> DecisionAuthorityEvidences => Set<DecisionAuthorityEvidenceRecord>();
    public DbSet<DecisionCarryForwardEntryRecord> DecisionCarryForwardEntries =>
        Set<DecisionCarryForwardEntryRecord>();
    public DbSet<DecisionEvidenceRevocationRecord> DecisionEvidenceRevocations =>
        Set<DecisionEvidenceRevocationRecord>();
    public DbSet<PurchaseRequestRecord> PurchaseRequests => Set<PurchaseRequestRecord>();
    public DbSet<PurchaseRequestVersionRecord> PurchaseRequestVersions => Set<PurchaseRequestVersionRecord>();
    public DbSet<PurchaseRequestLineVersionRecord> PurchaseRequestLineVersions => Set<PurchaseRequestLineVersionRecord>();
    public DbSet<PurchaseRequestVersionLineRecord> PurchaseRequestVersionLines => Set<PurchaseRequestVersionLineRecord>();
    public DbSet<PurchaseRequestRevisionDeltaRecord> PurchaseRequestRevisionDeltas =>
        Set<PurchaseRequestRevisionDeltaRecord>();
    public DbSet<PurchaseRequestReferenceAttestationRecord> PurchaseRequestReferenceAttestations =>
        Set<PurchaseRequestReferenceAttestationRecord>();
    public DbSet<PurchaseRequestCompletenessManifestRecord> PurchaseRequestCompletenessManifests =>
        Set<PurchaseRequestCompletenessManifestRecord>();
    public DbSet<PurchaseRequestSubmissionAttemptRecord> PurchaseRequestSubmissionAttempts =>
        Set<PurchaseRequestSubmissionAttemptRecord>();
    public DbSet<PurchaseRequestApprovalResultRecord> PurchaseRequestApprovalResults =>
        Set<PurchaseRequestApprovalResultRecord>();
    public DbSet<PurchaseRequestLifecycleEventRecord> PurchaseRequestLifecycleEvents =>
        Set<PurchaseRequestLifecycleEventRecord>();
    public DbSet<PurchaseRequestCommandRecord> PurchaseRequestCommands => Set<PurchaseRequestCommandRecord>();
    public DbSet<CostCenterRecord> CostCenters => Set<CostCenterRecord>();
    public DbSet<CostCenterVersionRecord> CostCenterVersions => Set<CostCenterVersionRecord>();
    public DbSet<SpendCategoryRecord> SpendCategories => Set<SpendCategoryRecord>();
    public DbSet<SpendCategoryVersionRecord> SpendCategoryVersions => Set<SpendCategoryVersionRecord>();
    public DbSet<BudgetPositionRecord> BudgetPositions => Set<BudgetPositionRecord>();
    public DbSet<BudgetAllocationVersionRecord> BudgetAllocationVersions => Set<BudgetAllocationVersionRecord>();
    public DbSet<BudgetBalanceRecord> BudgetBalances => Set<BudgetBalanceRecord>();
    public DbSet<BudgetOperationRecord> BudgetOperations => Set<BudgetOperationRecord>();
    public DbSet<BudgetMovementRecord> BudgetMovements => Set<BudgetMovementRecord>();
    public DbSet<BudgetPrerequisiteAttemptRecord> BudgetPrerequisiteAttempts =>
        Set<BudgetPrerequisiteAttemptRecord>();
    public DbSet<PurchaseRequestBudgetReleaseAttemptRecord> PurchaseRequestBudgetReleaseAttempts =>
        Set<PurchaseRequestBudgetReleaseAttemptRecord>();
    public DbSet<BudgetMovementProducerRegistrationRecord> BudgetMovementProducerRegistrations =>
        Set<BudgetMovementProducerRegistrationRecord>();
    public DbSet<BudgetPrerequisiteProcessorRegistrationRecord> BudgetPrerequisiteProcessorRegistrations =>
        Set<BudgetPrerequisiteProcessorRegistrationRecord>();
    public DbSet<BudgetAuditRecord> BudgetAuditRecords => Set<BudgetAuditRecord>();
    public DbSet<SupplierRecord> Suppliers => Set<SupplierRecord>();
    public DbSet<SupplierFiscalIdentityRecord> SupplierFiscalIdentities => Set<SupplierFiscalIdentityRecord>();
    public DbSet<SupplierVersionRecord> SupplierVersions => Set<SupplierVersionRecord>();
    public DbSet<SupplierBankingDetailRecord> SupplierBankingDetails => Set<SupplierBankingDetailRecord>();
    public DbSet<SupplierBankingVersionRecord> SupplierBankingVersions => Set<SupplierBankingVersionRecord>();
    public DbSet<SupplierBankingDefaultRecord> SupplierBankingDefaults => Set<SupplierBankingDefaultRecord>();
    public DbSet<SupplierBankingCommandRecord> SupplierBankingCommands => Set<SupplierBankingCommandRecord>();
    public DbSet<SupplierChangeProposalRecord> SupplierChangeProposals => Set<SupplierChangeProposalRecord>();
    public DbSet<ApprovedSupplierCatalogEntryRecord> ApprovedSupplierCatalogEntries =>
        Set<ApprovedSupplierCatalogEntryRecord>();
    public DbSet<ApprovedSupplierCatalogVersionRecord> ApprovedSupplierCatalogVersions =>
        Set<ApprovedSupplierCatalogVersionRecord>();
    public DbSet<SupplierAgreementAttachmentRecord> SupplierAgreementAttachments =>
        Set<SupplierAgreementAttachmentRecord>();
    public DbSet<SupplierPolicyFactSnapshotRecord> SupplierPolicyFactSnapshots =>
        Set<SupplierPolicyFactSnapshotRecord>();
    public DbSet<SupplierPrerequisiteAttemptRecord> SupplierPrerequisiteAttempts =>
        Set<SupplierPrerequisiteAttemptRecord>();
    public DbSet<SupplierPrerequisiteProcessorRegistrationRecord> SupplierPrerequisiteProcessorRegistrations =>
        Set<SupplierPrerequisiteProcessorRegistrationRecord>();
    public DbSet<SupplierAuditRecord> SupplierAuditRecords => Set<SupplierAuditRecord>();
    public DbSet<SupplierApprovalResultRecord> SupplierApprovalResults => Set<SupplierApprovalResultRecord>();
    public DbSet<SupplierStatusOutboxRecord> SupplierStatusOutbox => Set<SupplierStatusOutboxRecord>();
    public DbSet<SourcingProcessRecord> SourcingProcesses => Set<SourcingProcessRecord>();
    public DbSet<SourcingProcessLineRecord> SourcingProcessLines => Set<SourcingProcessLineRecord>();
    public DbSet<SourcingTakeoverRecord> SourcingTakeovers => Set<SourcingTakeoverRecord>();
    public DbSet<RfqRecord> Rfqs => Set<RfqRecord>();
    public DbSet<RfqVersionRecord> RfqVersions => Set<RfqVersionRecord>();
    public DbSet<RfqDeadlineExtensionRecord> RfqDeadlineExtensions => Set<RfqDeadlineExtensionRecord>();
    public DbSet<QuotationRecord> Quotations => Set<QuotationRecord>();
    public DbSet<QuotationVersionRecord> QuotationVersions => Set<QuotationVersionRecord>();
    public DbSet<QuotationLineScopeRecord> QuotationLineScopes => Set<QuotationLineScopeRecord>();
    public DbSet<SourcingAttachmentRecord> SourcingAttachments => Set<SourcingAttachmentRecord>();
    public DbSet<SourcingCommandRecord> SourcingCommands => Set<SourcingCommandRecord>();
    public DbSet<SourcingAuditRecord> SourcingAuditRecords => Set<SourcingAuditRecord>();
    public DbSet<SourcingOutboxRecord> SourcingOutbox => Set<SourcingOutboxRecord>();
    public DbSet<SourcingEvaluationRecord> SourcingEvaluations => Set<SourcingEvaluationRecord>();
    public DbSet<QuoteEvaluationVersionRecord> QuoteEvaluationVersions => Set<QuoteEvaluationVersionRecord>();
    public DbSet<SourcingFxSnapshotRecord> SourcingFxSnapshots => Set<SourcingFxSnapshotRecord>();
    public DbSet<SourcingManualScoreRecord> SourcingManualScores => Set<SourcingManualScoreRecord>();

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
        ConfigureApprovalPolicyException(modelBuilder);
        ConfigureApprovalWorkflowState(modelBuilder);
        ConfigureApprovalReconciliationRun(modelBuilder);
        ConfigureApprovalDelegation(modelBuilder);
        ConfigureApprovalDelegationCommand(modelBuilder);
        ConfigureApprovalDelegationTransitionJob(modelBuilder);
        ConfigureCaseSupersession(modelBuilder);
        ConfigureDecisionAuthorityEvidence(modelBuilder);
        ConfigureDecisionCarryForwardEntry(modelBuilder);
        ConfigureDecisionEvidenceRevocation(modelBuilder);
        ConfigurePurchaseRequest(modelBuilder);
        ConfigurePurchaseRequestVersion(modelBuilder);
        ConfigurePurchaseRequestLineVersion(modelBuilder);
        ConfigurePurchaseRequestVersionLine(modelBuilder);
        ConfigurePurchaseRequestRevisionDelta(modelBuilder);
        ConfigurePurchaseRequestReferenceAttestation(modelBuilder);
        ConfigurePurchaseRequestCompletenessManifest(modelBuilder);
        ConfigurePurchaseRequestSubmissionAttempt(modelBuilder);
        ConfigurePurchaseRequestApprovalResult(modelBuilder);
        ConfigurePurchaseRequestLifecycleEvent(modelBuilder);
        ConfigurePurchaseRequestCommand(modelBuilder);
        ConfigureCostCenter(modelBuilder);
        ConfigureCostCenterVersion(modelBuilder);
        ConfigureSpendCategory(modelBuilder);
        ConfigureSpendCategoryVersion(modelBuilder);
        ConfigureBudgetPosition(modelBuilder);
        ConfigureBudgetAllocationVersion(modelBuilder);
        ConfigureBudgetBalance(modelBuilder);
        ConfigureBudgetOperation(modelBuilder);
        ConfigureBudgetMovement(modelBuilder);
        ConfigureBudgetPrerequisiteAttempt(modelBuilder);
        ConfigurePurchaseRequestBudgetReleaseAttempt(modelBuilder);
        ConfigureBudgetMovementProducerRegistration(modelBuilder);
        ConfigureBudgetPrerequisiteProcessorRegistration(modelBuilder);
        ConfigureBudgetAudit(modelBuilder);
        SupplierModelConfiguration.Configure(modelBuilder);
        SourcingModelConfiguration.Configure(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigurePurchaseRequest(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestRecord>();
        entity.ToTable("PurchaseRequests", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Status).IsRequired();
        entity.Property(record => record.CurrentVersion).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.RequesterId, record.Status });
    }

    private static void ConfigurePurchaseRequestVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestVersionRecord>();
        entity.ToTable("PurchaseRequestVersions", "PurchaseRequest");
        entity.HasKey(record => new { record.RequestId, record.Version });
        entity.Property(record => record.BusinessJustification).HasMaxLength(2000).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RevisionKey).HasMaxLength(128);
        entity.Property(record => record.Reason).HasMaxLength(4000);
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId });
    }

    private static void ConfigurePurchaseRequestLineVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestLineVersionRecord>();
        entity.ToTable("PurchaseRequestLineVersions", "PurchaseRequest");
        entity.HasKey(record => new { record.LineId, record.LineVersion });
        entity.Property(record => record.TransactionCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.PurchaseType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.AgreementStatus).HasMaxLength(32).IsRequired();
        entity.Property(record => record.NeedSummary).HasMaxLength(2000).IsRequired();
        entity.Property(record => record.SpendCategoryJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CostCenterJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CostCenterDepartmentJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.BeneficiaryDepartmentJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.RequestedForUserJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.SupplierJson).HasMaxLength(1000);
        entity.Property(record => record.PreferredProductJson).HasMaxLength(1000);
        entity.Property(record => record.RequiredProductJson).HasMaxLength(1000);
        entity.Property(record => record.RiskAnswersJson).HasMaxLength(200_000).IsRequired();
        entity.Property(record => record.FxAttestationJson).HasMaxLength(1000);
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EstimatedGrossAmount).HasPrecision(38, 12);
        entity.Property(record => record.BaseAmount).HasPrecision(38, 12);
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId });
    }

    private static void ConfigurePurchaseRequestVersionLine(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestVersionLineRecord>();
        entity.ToTable("PurchaseRequestVersionLines", "PurchaseRequest");
        entity.HasKey(record => new { record.RequestId, record.RequestVersion, record.LineId });
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion, record.LineVersion });
    }

    private static void ConfigurePurchaseRequestRevisionDelta(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestRevisionDeltaRecord>();
        entity.ToTable("PurchaseRequestRevisionDeltas", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RevisionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.DeltaJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => new { record.RequestId, record.ToRequestVersion }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.RevisionKey }).IsUnique();
    }

    private static void ConfigurePurchaseRequestReferenceAttestation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestReferenceAttestationRecord>();
        entity.ToTable("PurchaseRequestReferenceAttestations", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Digest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AssertionsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion }).IsUnique();
    }

    private static void ConfigurePurchaseRequestCompletenessManifest(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestCompletenessManifestRecord>();
        entity.ToTable("PurchaseRequestCompletenessManifests", "PurchaseRequest");
        entity.HasKey(record => new { record.RequestId, record.RequestVersion });
        entity.Property(record => record.RequestContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ReferenceAttestationDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PolicyManifestDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.DomainAttestationDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.LinesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        // SPEC 09 REQ-09: the frozen supplier fact snapshot set of a v2 manifest.
        entity.Property(record => record.SupplierFactSnapshotsJson).HasColumnType("nvarchar(max)");
    }

    private static void ConfigurePurchaseRequestSubmissionAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestSubmissionAttemptRecord>();
        entity.ToTable("PurchaseRequestSubmissionAttempts", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SubmissionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PolicyResultDigest).HasMaxLength(64);
        entity.Property(record => record.ApprovalContractVersion).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One submission key per requester and organization (REQ-06): replay resolves on the key.
        entity.HasIndex(record => new { record.OrganizationId, record.RequesterId, record.SubmissionKey })
            .IsUnique();
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion }).IsUnique();
    }

    private static void ConfigurePurchaseRequestApprovalResult(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestApprovalResultRecord>();
        entity.ToTable("PurchaseRequestApprovalResults", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.SourceKey).HasMaxLength(128);
        entity.Property(record => record.MaterialSnapshotDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        // Deduplication of an at-least-once delivery includes the exact target (REQ-09).
        entity.HasIndex(record => new
        {
            record.EventId,
            record.ContractVersion,
            record.SourceType,
            record.SourceId,
            record.LineId,
            record.LineVersion
        }).IsUnique();
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion });
    }

    private static void ConfigurePurchaseRequestLifecycleEvent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestLifecycleEventRecord>();
        entity.ToTable("PurchaseRequestLifecycleEvents", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Action).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ReasonCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion, record.OccurredAt });
    }

    private static void ConfigurePurchaseRequestCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestCommandRecord>();
        entity.ToTable("PurchaseRequestCommands", "PurchaseRequest");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.CommandType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.ActorUserId,
            record.CommandType,
            record.CommandKey
        }).IsUnique();
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
        entity.Property(record => record.ActionsJson).HasColumnType("nvarchar(max)").IsRequired();
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
        // Real delegation applied to the assignment, if any (SPEC 04 REQ-02, CA-02).
        entity.HasIndex(record => new { record.OrganizationId, record.AssigneeUserId, record.ReleasedAt });
        entity.HasIndex(record => record.DelegationId);
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
        // Null for a derived decision: the carry-forward has no replayable command key (REQ-05).
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.DecisionKey).HasMaxLength(128);
        entity.Property(record => record.Fingerprint).HasMaxLength(64);
        entity.Property(record => record.DecisionDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AuthorityEvidenceDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EligibilityEvidenceJson).HasColumnType("nvarchar(max)").IsRequired();
        // Stored fingerprint preimage versions (REQ-06): a replay must reproduce the digest.
        // They stay null for a derived decision, which has no task to match (SPEC 04 REQ-05).
        // Artifacts of the decision instant so a replay returns them instead of the current state.
        entity.Property(record => record.RequirementStatusAfter).IsRequired();
        entity.Property(record => record.TaskStatusAfter).IsRequired();
        entity.Property(record => record.CaseStatusAfter).IsRequired();
        entity.Property(record => record.CaseVersionAfter).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // A human decision key is unique per actor; derived decisions never carry one (REQ-05, REQ-06).
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.DecisionKey })
            .IsUnique()
            .HasFilter("[DecisionKey] IS NOT NULL")
            .HasDatabaseName("IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey");
        entity.HasIndex(record => record.RequirementId);
        entity.HasIndex(record => record.EvidenceId);
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

    private static void ConfigureApprovalPolicyException(ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<ApprovalPolicyExceptionRequestRecord>();
        request.ToTable("PolicyExceptionRequests", "Approval");
        request.HasKey(record => record.Id);
        request.Property(record => record.RequirementKey).HasMaxLength(128).IsRequired();
        request.Property(record => record.SubjectType).HasMaxLength(128).IsRequired();
        request.Property(record => record.BaseResultDigest).HasMaxLength(64).IsRequired();
        request.Property(record => record.PolicyContentDigest).HasMaxLength(64).IsRequired();
        request.Property(record => record.ManifestDigest).HasMaxLength(64).IsRequired();
        request.Property(record => record.TargetRequirementKey).HasMaxLength(128).IsRequired();
        request.Property(record => record.CoveredLinesJson).HasColumnType("nvarchar(max)").IsRequired();
        request.Property(record => record.Nonce).HasMaxLength(128).IsRequired();
        request.Property(record => record.Binding).HasMaxLength(64).IsRequired();
        request.Property(record => record.RowVersion).IsRowVersion();
        request.HasOne<ApprovalCaseRecord>()
            .WithMany()
            .HasForeignKey(record => record.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
        request.HasOne<ApprovalRequirementRecord>()
            .WithMany()
            .HasForeignKey(record => record.RequirementId)
            .OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(record => new
        {
            record.OrganizationId,
            record.BaseBundleId,
            record.TargetRequirementKey,
            record.Binding
        }).IsUnique();
        request.HasIndex(record => new { record.CaseId, record.RequirementKey }).IsUnique();
        request.HasIndex(record => record.RequirementId).IsUnique();

        var verification = modelBuilder.Entity<ApprovalPolicyExceptionVerificationRecord>();
        verification.ToTable("PolicyExceptionVerifications", "Approval");
        verification.HasKey(record => record.Id);
        verification.Property(record => record.VerifierType).HasMaxLength(128).IsRequired();
        verification.Property(record => record.WorkflowDecisionDigest).HasMaxLength(64).IsRequired();
        verification.Property(record => record.EvidenceDigest).HasMaxLength(64).IsRequired();
        verification.Property(record => record.Binding).HasMaxLength(64).IsRequired();
        verification.Property(record => record.Nonce).HasMaxLength(128).IsRequired();
        verification.Property(record => record.FailureCode).HasMaxLength(64).IsRequired();
        verification.Property(record => record.RevocationReference).HasMaxLength(256);
        verification.Property(record => record.RowVersion).IsRowVersion();
        verification.HasOne<ApprovalPolicyExceptionRequestRecord>()
            .WithMany()
            .HasForeignKey(record => record.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        verification.HasIndex(record => new { record.OrganizationId, record.VerifierType, record.Nonce })
            .IsUnique();
        verification.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkflowDecisionId,
            record.WorkflowDecisionVersion
        }).IsUnique();
        verification.HasIndex(record => record.RequestId);
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
        entity.Property(record => record.DelegationTransition).HasMaxLength(32);
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
        // Exactly one run per delegation version, transition and scheduled instant (SPEC 04 REQ-03).
        entity.HasIndex(record => new
            {
                record.OrganizationId,
                record.DelegationId,
                record.DelegationVersion,
                record.DelegationTransition,
                record.DelegationScheduledAt
            })
            .IsUnique()
            .HasFilter("[DelegationId] IS NOT NULL")
            .HasDatabaseName("IX_ApprovalReconciliationRuns_DelegationTransition");
        entity.HasIndex(record => new { record.OrganizationId, record.Status, record.RequestedAt });
    }

    private static void ConfigureApprovalDelegation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalDelegationRecord>();
        entity.ToTable("ApprovalDelegations", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.DecisionScopeJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.DelegationCommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Status).HasMaxLength(32).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One command key per actor scope (REQ-01): replay resolves on the same key.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.ActorType,
            record.ActorUserId,
            record.DelegationCommandKey
        }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.DelegatorUserId, record.Role, record.Status });
        entity.HasIndex(record => new { record.OrganizationId, record.DelegateeUserId, record.Status });
    }

    private static void ConfigureApprovalDelegationCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalDelegationCommandRecord>();
        entity.ToTable("ApprovalDelegationCommands", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.DelegationCommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Action).HasMaxLength(16).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One command key per actor scope across CREATE and REVOKE (SPEC 04 REQ-01).
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.ActorType,
            record.ActorUserId,
            record.DelegationCommandKey
        }).IsUnique();
        entity.HasIndex(record => record.DelegationId);
    }

    private static void ConfigureApprovalDelegationTransitionJob(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovalDelegationTransitionJobRecord>();
        entity.ToTable("ApprovalDelegationTransitionJobs", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Transition).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Status).HasMaxLength(32).IsRequired();
        entity.Property(record => record.LeaseOwner).HasMaxLength(120);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // Unique per delegation, transition and scheduled instant (SPEC 04 REQ-03).
        entity.HasIndex(record => new { record.DelegationId, record.Transition, record.ScheduledAt }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Status, record.ScheduledAt });
    }

    private static void ConfigureCaseSupersession(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CaseSupersessionRecord>();
        entity.ToTable("CaseSupersessions", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SupersessionKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.TargetMappingJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One supersession key per owning workload scope (REQ-04): replay resolves on the same key.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.WorkloadIssuer,
            record.WorkloadClientId,
            record.SupersessionKey
        }).IsUnique();
        entity.HasIndex(record => record.PreviousCaseId).IsUnique();
        entity.HasIndex(record => record.NewCaseId).IsUnique();
    }

    private static void ConfigureDecisionAuthorityEvidence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DecisionAuthorityEvidenceRecord>();
        entity.ToTable("DecisionAuthorityEvidences", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Digest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EvidenceJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.Status).HasMaxLength(32).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One evidence identity per human root decision (REQ-06): derivatives share the same row.
        entity.HasIndex(record => record.RootHumanDecisionId).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.Status });
    }

    private static void ConfigureDecisionCarryForwardEntry(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DecisionCarryForwardEntryRecord>();
        entity.ToTable("DecisionCarryForwardRecords", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.NewRequirementKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SourceRequirementContractDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.NewRequirementContractDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProofDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceTargetJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.NewTargetJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.TargetMappingJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => record.NewDecisionId).IsUnique();
        entity.HasIndex(record => record.SourceDecisionId);
        entity.HasIndex(record => record.NewRequirementId).IsUnique();
        entity.HasIndex(record => record.EvidenceId);
    }

    private static void ConfigureDecisionEvidenceRevocation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DecisionEvidenceRevocationRecord>();
        entity.ToTable("DecisionEvidenceRevocations", "Approval");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RevocationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ActorType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ActorWorkloadIssuer).HasMaxLength(320);
        entity.Property(record => record.ActorWorkloadClientId).HasMaxLength(128);
        entity.Property(record => record.ReasonCode).HasMaxLength(32).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(1000).IsRequired();
        // One revocation key per evidence scope (REQ-06): replay resolves on the same key.
        entity.HasIndex(record => new { record.OrganizationId, record.EvidenceId, record.RevocationKey }).IsUnique();
        entity.HasIndex(record => record.EvidenceId);
    }

    private static void ConfigureCostCenter(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CostCenterRecord>();
        // SPEC 07 NFR-01: the table carries an identity/current-pointer trigger, and SQL Server
        // forbids OUTPUT without INTO on a table with enabled triggers. EF re-reads the rowversion
        // with a follow-up SELECT instead of using OUTPUT.
        entity.ToTable("CostCenters", "ReferenceCatalog", table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.Code }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCostCenterVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CostCenterVersionRecord>();
        entity.ToTable("CostCenterVersions", "ReferenceCatalog");
        entity.HasKey(record => new { record.CostCenterId, record.Version });
        entity.Property(record => record.Name).HasMaxLength(400).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4000).IsRequired();
        entity.HasIndex(record => new { record.CostCenterId, record.Version }).IsUnique();
        entity.HasIndex(record => record.DepartmentId);
        entity.HasOne(record => record.CostCenter)
            .WithMany(record => record.Versions)
            .HasForeignKey(record => record.CostCenterId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(record => record.Department)
            .WithMany()
            .HasForeignKey(record => record.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSpendCategory(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SpendCategoryRecord>();
        entity.ToTable("SpendCategories", "ReferenceCatalog", table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Code).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.Code }).IsUnique();
        entity.HasOne(record => record.Organization)
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSpendCategoryVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SpendCategoryVersionRecord>();
        entity.ToTable("SpendCategoryVersions", "ReferenceCatalog");
        entity.HasKey(record => new { record.SpendCategoryId, record.Version });
        entity.Property(record => record.Code).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Name).HasMaxLength(400).IsRequired();
        entity.Property(record => record.Digest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.Code, record.Version }).IsUnique();
        entity.HasOne(record => record.SpendCategory)
            .WithMany(record => record.Versions)
            .HasForeignKey(record => record.SpendCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBudgetPosition(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetPositionRecord>();
        // SPEC 08 NFR-01: the root has an identity/current-pointer trigger, so no OUTPUT clause.
        entity.ToTable("Positions", "Budget", table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SpendCategoryCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PositionKeyDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.PositionKeyDigest }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.CostCenterId, record.FiscalYear });
    }

    private static void ConfigureBudgetAllocationVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetAllocationVersionRecord>();
        entity.ToTable("AllocationVersions", "Budget");
        entity.HasKey(record => new { record.PositionId, record.Version });
        entity.Property(record => record.SpendCategoryCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SpendCategoryDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AllocatedAmount).HasPrecision(38, 12);
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.AllocationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.PositionId, record.AllocationKey }).IsUnique();
        entity.HasOne(record => record.Position)
            .WithMany()
            .HasForeignKey(record => record.PositionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBudgetBalance(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetBalanceRecord>();
        entity.ToTable("Balances", "Budget");
        entity.HasKey(record => record.PositionId);
        entity.Property(record => record.Allocated).HasPrecision(38, 12);
        entity.Property(record => record.Reserved).HasPrecision(38, 12);
        entity.Property(record => record.Committed).HasPrecision(38, 12);
        entity.Property(record => record.Consumed).HasPrecision(38, 12);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasOne<BudgetPositionRecord>()
            .WithMany()
            .HasForeignKey(record => record.PositionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBudgetOperation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetOperationRecord>();
        entity.ToTable("Operations", "Budget");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.OperationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ActorJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CauseStream).HasMaxLength(32);
        entity.Property(record => record.ReasonCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.HasIndex(record => record.OrganizationId);
        entity.HasIndex(record => new { record.OrganizationId, record.SourceType, record.SourceId });
    }

    private static void ConfigureBudgetMovement(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetMovementRecord>();
        entity.ToTable("Movements", "Budget");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Amount).HasPrecision(38, 12);
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.TargetMaterialSnapshotDigest).HasMaxLength(64);
        entity.Property(record => record.TargetType).HasMaxLength(64);
        entity.Property(record => record.AllocatedBefore).HasPrecision(38, 12);
        entity.Property(record => record.ReservedBefore).HasPrecision(38, 12);
        entity.Property(record => record.CommittedBefore).HasPrecision(38, 12);
        entity.Property(record => record.ConsumedBefore).HasPrecision(38, 12);
        entity.Property(record => record.AllocatedAfter).HasPrecision(38, 12);
        entity.Property(record => record.ReservedAfter).HasPrecision(38, 12);
        entity.Property(record => record.CommittedAfter).HasPrecision(38, 12);
        entity.Property(record => record.ConsumedAfter).HasPrecision(38, 12);
        entity.HasIndex(record => new { record.OrganizationId, record.PositionId });
        entity.HasIndex(record => new { record.OperationId });
        entity.HasIndex(record => new { record.ParentMovementId });
        entity.HasIndex(record => new { record.OrganizationId, record.TargetId });
        entity.HasOne(record => record.Operation)
            .WithMany()
            .HasForeignKey(record => record.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(record => record.Position)
            .WithMany()
            .HasForeignKey(record => record.PositionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBudgetPrerequisiteAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetPrerequisiteAttemptRecord>();
        entity.ToTable("PrerequisiteAttempts", "Budget", table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.PrerequisiteKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.ParametersJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.ParametersDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceControlDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RequestKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.ReserveKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.CompensateKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.State).HasMaxLength(32).IsRequired();
        entity.Property(record => record.SignalResult).HasMaxLength(16);
        entity.Property(record => record.EvidenceDigest).HasMaxLength(64);
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.LeaseOwner).HasMaxLength(120);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.PrerequisiteId).IsUnique();
        entity.HasIndex(record => new { record.State, record.NextAttemptAt });
    }

    private static void ConfigurePurchaseRequestBudgetReleaseAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestBudgetReleaseAttemptRecord>();
        entity.ToTable("PurchaseRequestReleaseAttempts", "Budget", table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ReleaseKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.State).HasMaxLength(32).IsRequired();
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.RequestVersion }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.ReleaseKey }).IsUnique();
    }

    private static void ConfigureBudgetMovementProducerRegistration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetMovementProducerRegistrationRecord>();
        entity.ToTable("MovementProducerRegistrations", "Budget");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Operation).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProducerId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.HasIndex(record => new { record.Operation, record.ContractVersion, record.SourceType }).IsUnique();
    }

    private static void ConfigureBudgetPrerequisiteProcessorRegistration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetPrerequisiteProcessorRegistrationRecord>();
        entity.ToTable("PrerequisiteProcessorRegistrations", "Budget");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.AdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.AdapterVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProcessorId).HasMaxLength(128).IsRequired();
        entity.HasIndex(record => new { record.AdapterId, record.AdapterVersion }).IsUnique();
    }

    private static void ConfigureBudgetAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BudgetAuditRecord>();
        entity.ToTable("AuditRecords", "Budget");
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorJson).HasMaxLength(1000).IsRequired();
        entity.Property(record => record.CauseStream).HasMaxLength(32);
        entity.Property(record => record.Action).HasMaxLength(64).IsRequired();
        entity.Property(record => record.TargetType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.DeltasJson).HasMaxLength(4000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.TargetType, record.TargetId });
    }
}
