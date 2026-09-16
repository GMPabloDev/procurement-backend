using Microsoft.EntityFrameworkCore;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// EF Core mapping of the Sourcing module (SPEC 10 Datos y contratos). Roots keep a mutable pointer
/// and a rowversion; version rows are append-only and their cardinality rules are enforced by
/// constraints and triggers, not only by application code (NFR-02).
/// </summary>
public static class SourcingModelConfiguration
{
    public const string Schema = "Sourcing";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureProcess(modelBuilder);
        ConfigureProcessLine(modelBuilder);
        ConfigureTakeover(modelBuilder);
        ConfigureRfq(modelBuilder);
        ConfigureRfqVersion(modelBuilder);
        ConfigureExtension(modelBuilder);
        ConfigureQuotation(modelBuilder);
        ConfigureQuotationVersion(modelBuilder);
        ConfigureQuotationLineScope(modelBuilder);
        ConfigureAttachment(modelBuilder);
        ConfigureCommand(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureOutbox(modelBuilder);
    }

    private static void ConfigureProcess(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingProcessRecord>();
        entity.ToTable("SourcingProcesses", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.CancellationReason).HasMaxLength(4_000);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId });
        // REQ-01: at most one non-cancelled process per Purchase Request so a takeover is unambiguous.
        entity.HasIndex(record => record.RequestId)
            .IsUnique()
            .HasFilter("[State] <> 4")
            .HasDatabaseName("IX_SourcingProcesses_RequestId_Active");
        entity.HasOne(record => record.Organization)
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProcessLine(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingProcessLineRecord>();
        entity.ToTable("SourcingProcessLines", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.ProcessId, record.LineId });
        entity.Property(record => record.LineContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RequestedQuantity).HasPrecision(38, 12);
        entity.Property(record => record.UnitCode).HasMaxLength(64).IsRequired();
        entity.HasOne(record => record.Process)
            .WithMany(process => process.Lines)
            .HasForeignKey(record => record.ProcessId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureTakeover(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingTakeoverRecord>();
        entity.ToTable("SourcingTakeovers", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ReleaseReason).HasMaxLength(4_000);
        // One takeover per process and, at most, one live takeover per Purchase Request (REQ-01).
        entity.HasIndex(record => record.ProcessId).IsUnique();
        entity.HasIndex(record => record.RequestId)
            .IsUnique()
            .HasFilter("[ReleasedAt] IS NULL")
            .HasDatabaseName("IX_SourcingTakeovers_RequestId_Live");
    }

    private static void ConfigureRfq(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RfqRecord>();
        entity.ToTable("Rfqs", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-01/REQ-02: one RFQ root per process; history stays in its append-only versions.
        entity.HasIndex(record => record.ProcessId).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId });
    }

    private static void ConfigureRfqVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RfqVersionRecord>();
        entity.ToTable("RfqVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.RfqId, record.Version });
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.TermsJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.WeightsJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.LinesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ParametersJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000);
        entity.HasIndex(record => new { record.OrganizationId, record.ProcessId });
        entity.HasOne(record => record.Rfq)
            .WithMany()
            .HasForeignKey(record => record.RfqId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureExtension(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RfqDeadlineExtensionRecord>();
        entity.ToTable("RfqDeadlineExtensions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        // One extension record per resulting RFQ version (REQ-02).
        entity.HasIndex(record => new { record.RfqId, record.ToVersion }).IsUnique();
    }

    private static void ConfigureQuotation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QuotationRecord>();
        entity.ToTable("Quotations", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-03: one quotation root per RFQ and supplier; every answer appends a version.
        entity.HasIndex(record => new { record.RfqId, record.SupplierId }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
    }

    private static void ConfigureQuotationVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QuotationVersionRecord>();
        entity.ToTable("QuotationVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.QuotationId, record.Version });
        entity.Property(record => record.Currency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.TermsJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.LinesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.AttachmentsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ReviewJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000);
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasOne(record => record.Quotation)
            .WithMany()
            .HasForeignKey(record => record.QuotationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureQuotationLineScope(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QuotationLineScopeRecord>();
        entity.ToTable("QuotationLineScopes", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.QuotationId, record.Version, record.LineId });
        entity.HasIndex(record => record.LineId);
    }

    private static void ConfigureAttachment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingAttachmentRecord>();
        entity.ToTable("SourcingAttachments", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.ObjectKey).HasMaxLength(512).IsRequired();
        entity.Property(record => record.FileName).HasMaxLength(200).IsRequired();
        entity.Property(record => record.ContentType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Sha256).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingCommandRecord>();
        entity.ToTable("SourcingCommands", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.CommandType).HasMaxLength(32).IsRequired();
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        // One command key per actor and command type: a replay resolves to the same effect (NFR-03).
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.ActorUserId,
            record.CommandType,
            record.CommandKey
        }).IsUnique();
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingAuditRecord>();
        entity.ToTable("SourcingAuditRecords", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Action).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ActorJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.CauseJson).HasMaxLength(2_000);
        entity.Property(record => record.ChangedFieldsJson).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.CorrelationReference).HasMaxLength(120).IsRequired();
        entity.Property(record => record.EffectKey).HasMaxLength(200).IsRequired();
        entity.Property(record => record.TargetJson).HasMaxLength(2_000).IsRequired();
        // One effect key produces exactly one audit row even under redelivery (NFR-03).
        entity.HasIndex(record => new { record.OrganizationId, record.Action, record.EffectKey }).IsUnique();
    }

    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingOutboxRecord>();
        entity.ToTable("SourcingOutbox", Schema);
        entity.HasKey(record => record.EventId);
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.OccurredAt });
    }
}
