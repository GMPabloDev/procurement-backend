using Microsoft.EntityFrameworkCore;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// EF Core mapping of the Supplier module (SPEC 09 Datos y contratos). Roots keep a mutable pointer
/// and a rowversion; every version row is append-only and the uniqueness rules of REQ-01, REQ-05,
/// REQ-06 and REQ-10 are enforced by constraints, not only by application code.
/// </summary>
public static class SupplierModelConfiguration
{
    public const string Schema = "Supplier";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureSupplier(modelBuilder);
        ConfigureFiscalIdentity(modelBuilder);
        ConfigureVersion(modelBuilder);
        ConfigureBankingDetail(modelBuilder);
        ConfigureBankingVersion(modelBuilder);
        ConfigureBankingDefault(modelBuilder);
        ConfigureBankingCommand(modelBuilder);
        ConfigureProposal(modelBuilder);
        ConfigureCatalogEntry(modelBuilder);
        ConfigureCatalogVersion(modelBuilder);
        ConfigureAttachment(modelBuilder);
        ConfigureFactSnapshot(modelBuilder);
        ConfigurePrerequisiteAttempt(modelBuilder);
        ConfigureProcessorRegistration(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureApprovalResult(modelBuilder);
        ConfigureStatusOutbox(modelBuilder);
    }

    private static void ConfigureSupplier(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierRecord>();
        // The root carries the pointer-stability trigger, so EF re-reads the rowversion with a
        // follow-up SELECT instead of using OUTPUT (same reason as the reference catalogs).
        entity.ToTable("Suppliers", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.OrganizationId);
        entity.HasOne(record => record.Organization).WithMany().HasForeignKey(record => record.OrganizationId);
        entity.HasOne(record => record.FiscalIdentity)
            .WithOne(identity => identity.Supplier)
            .HasForeignKey<SupplierRecord>(record => record.FiscalIdentityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureFiscalIdentity(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierFiscalIdentityRecord>();
        entity.ToTable("SupplierFiscalIdentities", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.CountryCode).HasMaxLength(2).IsRequired();
        entity.Property(record => record.TaxIdKey).HasMaxLength(64).IsRequired();
        entity.Property(record => record.TaxId).HasMaxLength(64).IsRequired();
        entity.Property(record => record.IdentityKeyDigest).HasMaxLength(64).IsRequired();
        // REQ-01: Country + Tax ID is unique across roots and a reserved identity is never reused.
        entity.HasIndex(record => new { record.OrganizationId, record.IdentityKeyDigest }).IsUnique();
        entity.HasIndex(record => record.SupplierId).IsUnique();
    }

    private static void ConfigureVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierVersionRecord>();
        entity.ToTable("SupplierVersions", Schema);
        entity.HasKey(record => new { record.SupplierId, record.Version });
        entity.Property(record => record.LegalName).HasMaxLength(300).IsRequired();
        entity.Property(record => record.TradeName).HasMaxLength(300);
        entity.Property(record => record.CountryCode).HasMaxLength(2).IsRequired();
        entity.Property(record => record.TaxId).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AddressesJson).HasMaxLength(100_000).IsRequired();
        entity.Property(record => record.ContactsJson).HasMaxLength(100_000).IsRequired();
        entity.Property(record => record.PaymentTermsJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.SupportedCurrenciesJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.CategoriesSuppliedJson).HasMaxLength(100_000).IsRequired();
        entity.Property(record => record.PerformanceJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.BankingRefsJson).HasMaxLength(20_000).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ChangeKey).HasMaxLength(128);
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId });
        entity.HasOne(record => record.Supplier)
            .WithMany(supplier => supplier.Versions)
            .HasForeignKey(record => record.SupplierId);
    }

    private static void ConfigureBankingDetail(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierBankingDetailRecord>();
        entity.ToTable("SupplierBankingDetails", Schema);
        entity.HasKey(record => record.Id);
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId });
    }

    private static void ConfigureBankingVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierBankingVersionRecord>();
        entity.ToTable("SupplierBankingVersions", Schema);
        entity.HasKey(record => new { record.BankingDetailId, record.Version });

        entity.Property(record => record.BankName).HasMaxLength(300).IsRequired();
        entity.Property(record => record.BankCountryCode).HasMaxLength(2).IsRequired();
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.MaskedSuffix).HasMaxLength(4).IsRequired();
        entity.Property(record => record.KeyVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ChangeKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId });
        entity.HasOne(record => record.Detail)
            .WithMany(detail => detail.Versions)
            .HasForeignKey(record => record.BankingDetailId);
    }

    private static void ConfigureBankingDefault(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierBankingDefaultRecord>();
        entity.ToTable("SupplierBankingDefaults", Schema);
        entity.HasKey(record => new { record.SupplierId, record.Currency });
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
    }

    private static void ConfigureBankingCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierBankingCommandRecord>();
        entity.ToTable("SupplierBankingCommands", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ChangeKey).HasMaxLength(128).IsRequired();
        // One key per supplier identifies the attempt: a retry resolves to the same version.
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId, record.ChangeKey }).IsUnique();
    }

    private static void ConfigureProposal(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierChangeProposalRecord>();
        entity.ToTable("SupplierChangeProposals", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SensitiveFieldsJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.ChangeKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SubmissionKey).HasMaxLength(128);
        entity.Property(record => record.RequirementKey).HasMaxLength(128);
        entity.Property(record => record.CaseContractVersion).HasMaxLength(128);
        entity.Property(record => record.ErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One command key per editor is idempotent, and only one proposal of a supplier is open.
        entity.HasIndex(record => new { record.OrganizationId, record.EditorUserId, record.ChangeKey }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId })
            .IsUnique()
            .HasFilter("[State] IN (1, 2)");
        entity.HasIndex(record => record.ApprovalCaseId);
    }

    private static void ConfigureCatalogEntry(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovedSupplierCatalogEntryRecord>();
        entity.ToTable("ApprovedSupplierCatalogEntries", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SpendCategoryCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-06: one root per exact selector (supplier, category, optional product). EF would add
        // an implicit "ProductId IS NOT NULL" filter that would let several general entries coexist,
        // so the filter is replaced by a simple always-true predicate (a filtered index only admits
        // simple comparisons, and a unique index treats two NULL products as duplicates).
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId, record.SpendCategoryCode, record.ProductId })
            .IsUnique()
            .HasFilter("[OrganizationId] IS NOT NULL");
    }

    private static void ConfigureCatalogVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ApprovedSupplierCatalogVersionRecord>();
        entity.ToTable("ApprovedSupplierCatalogVersions", Schema);
        entity.HasKey(record => new { record.CatalogEntryId, record.Version });
        entity.Property(record => record.SpendCategoryJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.ProductJson).HasMaxLength(1_000);
        entity.Property(record => record.NegotiatedPrice).HasPrecision(38, 12);
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.UnitCode).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ExternalContractReference).HasMaxLength(200).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId });
        entity.HasIndex(record => record.AttachmentId);
        entity.HasOne(record => record.Entry)
            .WithMany()
            .HasForeignKey(record => record.CatalogEntryId);
    }

    private static void ConfigureAttachment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierAgreementAttachmentRecord>();
        entity.ToTable("SupplierAgreementAttachments", Schema);
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.ObjectKey).HasMaxLength(512).IsRequired();
        entity.Property(record => record.FileName).HasMaxLength(200).IsRequired();
        entity.Property(record => record.ContentType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Sha256).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.SupplierId });
    }

    private static void ConfigureFactSnapshot(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierPolicyFactSnapshotRecord>();
        entity.ToTable("SupplierPolicyFactSnapshots", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.LineContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SpendCategoryJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.ProductJson).HasMaxLength(1_000);
        entity.Property(record => record.CatalogContentDigest).HasMaxLength(64);
        entity.Property(record => record.AgreementStatus).HasMaxLength(16).IsRequired();
        entity.Property(record => record.SnapshotDigest).HasMaxLength(64).IsRequired();
        // One frozen lookup per line version of a presented Purchase Request version (REQ-09).
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion, record.LineId }).IsUnique();
    }

    private static void ConfigurePrerequisiteAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierPrerequisiteAttemptRecord>();
        entity.ToTable("SupplierPrerequisiteAttempts", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.State).HasMaxLength(16).IsRequired();
        entity.Property(record => record.SignalResult).HasMaxLength(16);
        entity.Property(record => record.CheckKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.EvidenceDigest).HasMaxLength(64);
        entity.Property(record => record.LeaseOwner).HasMaxLength(128);
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.TargetsJson).HasMaxLength(100_000).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.PrerequisiteId).IsUnique();
        entity.HasIndex(record => record.State);
        entity.HasIndex(record => record.DueAt);
    }

    private static void ConfigureProcessorRegistration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierPrerequisiteProcessorRegistrationRecord>();
        entity.ToTable("SupplierPrerequisiteProcessorRegistrations", Schema);
        entity.HasKey(record => new { record.AdapterId, record.AdapterVersion });
        entity.Property(record => record.AdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.AdapterVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProcessorId).HasMaxLength(128).IsRequired();
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierAuditRecord>();
        entity.ToTable("SupplierAuditRecords", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Action).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ActorJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.CauseJson).HasMaxLength(2_000);
        entity.Property(record => record.ChangedFieldsJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.EffectKey).HasMaxLength(160).IsRequired();
        entity.Property(record => record.TargetJson).HasMaxLength(2_000).IsRequired();
        // REQ-11: one effect key produces exactly one audit row even under redelivery.
        entity.HasIndex(record => new { record.OrganizationId, record.Action, record.EffectKey }).IsUnique();
    }

    private static void ConfigureApprovalResult(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierApprovalResultRecord>();
        entity.ToTable("SupplierApprovalResults", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        entity.HasIndex(record => new { record.EventId, record.ContractVersion, record.TargetId }).IsUnique();
    }

    private static void ConfigureStatusOutbox(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupplierStatusOutboxRecord>();
        entity.ToTable("SupplierStatusOutbox", Schema);
        entity.HasKey(record => record.EventId);
        entity.Property(record => record.PayloadJson).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.OccurredAt });
    }
}
