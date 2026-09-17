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
        ConfigureEvaluation(modelBuilder);
        ConfigureEvaluationVersion(modelBuilder);
        ConfigureFxSnapshot(modelBuilder);
        ConfigureManualScore(modelBuilder);
        ConfigureSelection(modelBuilder);
        ConfigureWaiverFacts(modelBuilder);
        ConfigureProposal(modelBuilder);
        ConfigureProposalVersion(modelBuilder);
        ConfigureAward(modelBuilder);
        ConfigureAwardVersion(modelBuilder);
        ConfigureCurrentAwardLine(modelBuilder);
        ConfigureCatalogRoute(modelBuilder);
        ConfigureOwnerAttempt(modelBuilder);
        ConfigureOwnerEvidence(modelBuilder);
        ConfigureProcessorRegistration(modelBuilder);
        ConfigureSelectionVersion(modelBuilder);
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

    private static void ConfigureEvaluation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingEvaluationRecord>();
        entity.ToTable("SourcingEvaluations", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // One evaluation root per RFQ: every material change appends a successor version (REQ-07).
        entity.HasIndex(record => record.RfqId).IsUnique();
        entity.HasIndex(record => record.OrganizationId);
    }

    private static void ConfigureEvaluationVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<QuoteEvaluationVersionRecord>();
        entity.ToTable("QuoteEvaluationVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.EvaluationId, record.Version });
        entity.Property(record => record.RfqContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RecommendationsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.QuotationRefsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.FxSnapshotRefsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasOne(record => record.Evaluation)
            .WithMany()
            .HasForeignKey(record => record.EvaluationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureFxSnapshot(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingFxSnapshotRecord>();
        entity.ToTable("SourcingFxSnapshots", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.SourceCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.Rate).HasPrecision(38, 12);
        entity.Property(record => record.SourceReference).HasMaxLength(300).IsRequired();
        entity.Property(record => record.AttachmentJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId, record.SourceCurrency });
    }

    private static void ConfigureManualScore(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingManualScoreRecord>();
        entity.ToTable("SourcingManualScores", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.Score).HasPrecision(9, 4);
        entity.Property(record => record.Justification).HasMaxLength(4_000).IsRequired();
        entity.Property(record => record.EvidenceJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId, record.LineId, record.SupplierId, record.Criterion });
    }

    private static void ConfigureCatalogRoute(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingCatalogRouteRecord>();
        entity.ToTable("SourcingCatalogRoutes", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.ProcessId);
        entity.Property(record => record.SnapshotsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.SnapshotsDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => record.OrganizationId);
    }

    private static void ConfigureOwnerAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingOwnerAttemptRecord>();
        entity.ToTable("SourcingOwnerAttempts", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SubjectType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.OwnerAdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.OwnerAdapterVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.TargetsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.State).HasMaxLength(16).IsRequired();
        entity.Property(record => record.SignalResult).HasMaxLength(16);
        entity.Property(record => record.CheckKey).HasMaxLength(200).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(200).IsRequired();
        entity.Property(record => record.EvidenceDigest).HasMaxLength(64);
        entity.Property(record => record.LeaseOwner).HasMaxLength(120);
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.PrerequisiteId).IsUnique();
        entity.HasIndex(record => record.State);
        entity.HasIndex(record => record.DueAt);
    }

    private static void ConfigureOwnerEvidence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingOwnerEvidenceRecord>();
        entity.ToTable("SourcingOwnerEvidence", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(16).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(200).IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.PrerequisiteId, record.SignalKey }).IsUnique();
    }

    private static void ConfigureProcessorRegistration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingPrerequisiteProcessorRegistrationRecord>();
        entity.ToTable("SourcingPrerequisiteProcessorRegistrations", Schema);
        entity.HasKey(record => new { record.AdapterId, record.AdapterVersion });
        entity.Property(record => record.AdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.AdapterVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProcessorId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(320).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
    }

    private static void ConfigureAward(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingAwardRecord>();
        entity.ToTable("SourcingAwards", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-12: one award lineage per sourcing process; the supplier of a version may change.
        entity.HasIndex(record => new { record.OrganizationId, record.ProcessId }).IsUnique();
    }

    private static void ConfigureAwardVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingAwardVersionRecord>();
        entity.ToTable("SourcingAwardVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.AwardId, record.Version });
        entity.Property(record => record.EvaluationContentDigest).HasMaxLength(64);
        entity.Property(record => record.LinesJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AwardKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.AwardKey }).IsUnique();
        entity.HasOne(record => record.Award)
            .WithMany()
            .HasForeignKey(record => record.AwardId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCurrentAwardLine(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingCurrentAwardLineRecord>();
        entity.ToTable("SourcingCurrentAwardLines", Schema);
        entity.HasKey(record => record.LineId);
        entity.HasIndex(record => new { record.AwardId, record.AwardVersion });
    }

    private static void ConfigureProposal(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingProposalRecord>();
        entity.ToTable("SourcingProposals", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-10: one proposal root per supplier of the RFQ; its versions are the history.
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId, record.SupplierId }).IsUnique();
    }

    private static void ConfigureProposalVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingProposalVersionRecord>();
        entity.ToTable("SourcingProposalVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.ProposalId, record.Version });
        entity.Property(record => record.RequestRefJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.RequestBundleRefJson).HasMaxLength(2_000).IsRequired();
        entity.Property(record => record.EvaluationContentDigest).HasMaxLength(64);
        entity.Property(record => record.LineIdsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.TermsJson).HasMaxLength(1_000).IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ManifestJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ManifestDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SubmissionKey).HasMaxLength(128);
        entity.Property(record => record.ErrorCode).HasMaxLength(64);
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.CommandKey }).IsUnique();
        entity.HasOne(record => record.Proposal)
            .WithMany()
            .HasForeignKey(record => record.ProposalId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWaiverFacts(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingWaiverFactsRecord>();
        entity.ToTable("SourcingWaiverFacts", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RequirementKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.BaseResultDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PolicyContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ManifestDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.TargetsJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.DocumentJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasIndex(record => new { record.OrganizationId, record.ActorUserId, record.CommandKey }).IsUnique();
    }

    private static void ConfigureSelection(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingSelectionRecord>();
        entity.ToTable("SourcingSelections", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-09: exactly one current selection per line, which is what makes the award unambiguous.
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId, record.LineId }).IsUnique();
    }

    private static void ConfigureSelectionVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SourcingSelectionVersionRecord>();
        entity.ToTable("SourcingSelectionVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.SelectionId, record.Version });
        entity.Property(record => record.LineContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.QuotationContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.EvaluationContentDigest).HasMaxLength(64);
        entity.Property(record => record.DeviationJustification).HasMaxLength(4_000);
        entity.HasIndex(record => new { record.OrganizationId, record.RfqId });
        entity.HasOne(record => record.Selection)
            .WithMany()
            .HasForeignKey(record => record.SelectionId)
            .OnDelete(DeleteBehavior.Restrict);
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
