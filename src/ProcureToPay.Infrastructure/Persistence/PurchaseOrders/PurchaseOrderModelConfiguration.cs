using Microsoft.EntityFrameworkCore;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// EF Core mapping of the Purchase Orders module (SPEC 11 Datos y contratos). Roots keep a mutable
/// pointer and a rowversion; version, claim, takeover, evidence and audit rows are append-only and
/// their cardinality rules are enforced by constraints and triggers, not only by application code
/// (NFR-01, NFR-03).
/// </summary>
public static class PurchaseOrderModelConfiguration
{
    public const string Schema = "PurchaseOrders";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigurePurchaseOrder(modelBuilder);
        ConfigureNumberSequence(modelBuilder);
        ConfigureVersion(modelBuilder);
        ConfigureLinePointer(modelBuilder);
        ConfigureClaim(modelBuilder);
        ConfigureTakeover(modelBuilder);
        ConfigureLineProjection(modelBuilder);
        ConfigureBudgetAttempt(modelBuilder);
        ConfigureAmendment(modelBuilder);
        ConfigureAmendmentRoot(modelBuilder);
        ConfigureDirectPurchase(modelBuilder);
        ConfigureSupportingDocument(modelBuilder);
        ConfigureProcessorRegistration(modelBuilder);
        ConfigureOwnerAttempt(modelBuilder);
        ConfigureOwnerEvidence(modelBuilder);
        ConfigureOrderingEvidence(modelBuilder);
        ConfigureCommand(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureOutbox(modelBuilder);
    }

    private static void ConfigurePurchaseOrder(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderRecord>();
        entity.ToTable("PurchaseOrders", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.PoNumber).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-02: the PO number is unique per organization and never reused.
        entity.HasIndex(record => new { record.OrganizationId, record.PoNumber })
            .IsUnique()
            .HasDatabaseName("IX_PurchaseOrders_Organization_Number");
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.RequestVersion });
        entity.HasOne<OrganizationRecord>()
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNumberSequence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderNumberSequenceRecord>();
        entity.ToTable("PurchaseOrderNumberSequences", Schema);
        entity.HasKey(record => record.OrganizationId);
        entity.HasOne<OrganizationRecord>()
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderVersionRecord>();
        entity.ToTable("PurchaseOrderVersions", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.PoId, record.Version });
        entity.Property(record => record.PoNumber).HasMaxLength(64).IsRequired();
        entity.Property(record => record.RequestContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ProposalContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AwardContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SourceCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.Property(record => record.SourceAmount).HasPrecision(38, 12);
        entity.Property(record => record.BaseAmount).HasPrecision(38, 12);
        entity.Property(record => record.DeliveryJson).HasMaxLength(4_000);
        entity.Property(record => record.TermsJson).IsRequired();
        entity.Property(record => record.LinesJson).IsRequired();
        entity.Property(record => record.BudgetOperationRefsJson).IsRequired();
        entity.Property(record => record.LineParametersJson).IsRequired();
        entity.Property(record => record.ApprovalDigest).HasMaxLength(64);
        entity.Property(record => record.OrderingEvidenceDigest).HasMaxLength(64);
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000);
        // REQ-02: one current version per root; history is append-only.
        entity.HasIndex(record => new { record.PoId, record.Version }).IsUnique();
        entity.HasIndex(record => new { record.OrganizationId, record.State });
        entity.HasIndex(record => new { record.RequestId, record.RequestVersion });
        entity.HasIndex(record => record.ClaimId);
        entity.HasOne(record => record.PurchaseOrder)
            .WithMany()
            .HasForeignKey(record => record.PoId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureLinePointer(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderLinePointerRecord>();
        entity.ToTable("PurchaseOrderLinePointers", Schema);
        entity.HasKey(record => new { record.RequestLineId, record.RequestLineVersion, record.PoId });
        // REQ-02: one award line produces one PO line, and no split is allowed. A cancelled
        // pre-issue PO releases its pointers so the award can be claimed again.
        entity.HasIndex(record => new { record.RequestLineId, record.RequestLineVersion })
            .IsUnique()
            .HasFilter("[State] <> 5")
            .HasDatabaseName("IX_PurchaseOrderLinePointers_Line_Active");
        entity.HasIndex(record => record.PoId);
    }

    private static void ConfigureClaim(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AwardConsumptionClaimRecord>();
        entity.ToTable("AwardConsumptionClaims", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.AwardContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ClaimKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PoNumber).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CoveredLinesJson).IsRequired();
        entity.Property(record => record.AwardSnapshotJson).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(256).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.ReleaseReason).HasMaxLength(4_000);
        // REQ-01: at most one live claim per award, and a key identifies exactly one claim.
        entity.HasIndex(record => new { record.OrganizationId, record.AwardId })
            .IsUnique()
            .HasFilter("[State] < 3")
            .HasDatabaseName("IX_AwardConsumptionClaims_Award_Live");
        entity.HasIndex(record => new { record.OrganizationId, record.ClaimKey })
            .IsUnique()
            .HasDatabaseName("IX_AwardConsumptionClaims_Key");
        entity.HasIndex(record => record.PoId);
    }

    private static void ConfigureTakeover(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestLineTakeoverRecord>();
        entity.ToTable("PurchaseRequestLineTakeovers", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RequestContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.LineContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ConsumerDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ConsumerType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PredecessorDigest).HasMaxLength(64);
        entity.Property(record => record.ReleaseReason).HasMaxLength(4_000);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-10: at most one active takeover per line version, whatever its owner.
        entity.HasIndex(record => new { record.LineId, record.LineVersion })
            .IsUnique()
            .HasFilter("[State] = 1")
            .HasDatabaseName("IX_PurchaseRequestLineTakeovers_Line_Active");
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.RequestVersion });
        entity.HasIndex(record => new { record.ConsumerType, record.ConsumerId });
    }

    private static void ConfigureLineProjection(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestLineProjectionRecord>();
        entity.ToTable("PurchaseRequestLineProjections", Schema);
        entity.HasKey(record => new
        {
            record.RequestId, record.RequestVersion, record.LineId, record.LineVersion
        });
        entity.Property(record => record.ConsumerDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ConsumerType).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId });
    }

    private static void ConfigureBudgetAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderBudgetAttemptRecord>();
        entity.ToTable("PurchaseOrderBudgetAttempts", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.OperationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.ReleaseKey).HasMaxLength(128);
        entity.Property(record => record.ParentsJson).IsRequired();
        entity.Property(record => record.OperationId).HasMaxLength(64);
        entity.Property(record => record.ReleaseOperationId).HasMaxLength(64);
        entity.Property(record => record.MovementRefsJson);
        entity.Property(record => record.ReleaseMovementRefsJson);
        entity.Property(record => record.LeaseOwner).HasMaxLength(256);
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-04: one attempt per PO version and operation, so a retry recovers the same attempt.
        entity.HasIndex(record => new { record.PoId, record.PoVersion, record.Operation })
            .IsUnique()
            .HasDatabaseName("IX_PurchaseOrderBudgetAttempts_Po_Operation");
        entity.HasIndex(record => record.OperationKey).IsUnique();
    }

    private static void ConfigureAmendment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderAmendmentRecord>();
        entity.ToTable("PurchaseOrderAmendments", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.SuccessorAwardDigest).HasMaxLength(64);
        entity.Property(record => record.ApprovalDigest).HasMaxLength(64);
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Reason).HasMaxLength(4_000).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.PoId });
        entity.HasIndex(record => new { record.OrganizationId, record.CommandKey })
            .IsUnique()
            .HasDatabaseName("IX_PurchaseOrderAmendments_CommandKey");
        entity.HasIndex(record => record.AppliedPoVersionNumber);
    }

    private static void ConfigureAmendmentRoot(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderAmendmentRootRecord>();
        entity.ToTable("PurchaseOrderAmendmentRoots", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.RowVersion).IsRowVersion();
        entity.HasIndex(record => record.PoId).IsUnique();
    }

    private static void ConfigureDirectPurchase(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DirectPurchaseAuthorizationRecord>();
        entity.ToTable("DirectPurchaseAuthorizations", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.AuthorizationKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PolicyBundleDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CoveredLinesJson).IsRequired();
        entity.Property(record => record.CoveredTargetsJson).IsRequired();
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CancellationReason).HasMaxLength(4_000);
        // REQ-07: a key identifies exactly one authorization lineage; a cancellation appends a
        // successor version with state CANCELLED instead of rewriting the authorized row.
        entity.HasIndex(record => new { record.OrganizationId, record.AuthorizationKey })
            .IsUnique()
            .HasFilter("[Version] = 1")
            .HasDatabaseName("IX_DirectPurchaseAuthorizations_Key");
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.State });
    }

    private static void ConfigureSupportingDocument(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcurementSupportingDocumentRecord>();
        entity.ToTable("ProcurementSupportingDocuments", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.Id, record.Version });
        entity.Property(record => record.CoveredTargetsJson).IsRequired();
        entity.Property(record => record.FileRefJson).IsRequired();
        entity.Property(record => record.BusinessType).HasMaxLength(16).IsRequired();
        entity.Property(record => record.Sha256).HasMaxLength(64).IsRequired();
        entity.Property(record => record.SupersededReason).HasMaxLength(4_000);
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.RequestVersion });
        entity.HasIndex(record => new { record.OrganizationId, record.Sha256, record.Length });
    }

    private static void ConfigureProcessorRegistration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupportingDocumentProcessorRegistrationRecord>();
        entity.ToTable("SupportingDocumentProcessorRegistrations", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => new { record.AdapterId, record.AdapterVersion });
        entity.Property(record => record.AdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.AdapterVersion).HasMaxLength(32).IsRequired();
        entity.Property(record => record.ProcessorId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(256).IsRequired();
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128).IsRequired();
    }

    private static void ConfigureOwnerAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupportingDocumentOwnerAttemptRecord>();
        entity.ToTable("SupportingDocumentOwnerAttempts", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.SubjectType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.OwnerAdapterId).HasMaxLength(128).IsRequired();
        entity.Property(record => record.OwnerAdapterVersion).HasMaxLength(32).IsRequired();
        entity.Property(record => record.TargetsJson).IsRequired();
        entity.Property(record => record.ParametersDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.AllowedDocumentTypesJson).IsRequired();
        entity.Property(record => record.State).HasMaxLength(32).IsRequired();
        entity.Property(record => record.SignalResult).HasMaxLength(32);
        entity.Property(record => record.CheckKey).HasMaxLength(256).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(256).IsRequired();
        entity.Property(record => record.EvidenceDigest).HasMaxLength(64);
        entity.Property(record => record.LeaseOwner).HasMaxLength(256);
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.Property(record => record.RowVersion).IsRowVersion();
        // REQ-08: one durable attempt per Approval prerequisite.
        entity.HasIndex(record => record.PrerequisiteId).IsUnique();
        entity.HasIndex(record => new { record.State, record.NextAttemptAt });
    }

    private static void ConfigureOwnerEvidence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SupportingDocumentOwnerEvidenceRecord>();
        entity.ToTable("SupportingDocumentOwnerEvidence", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ContractVersion).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Result).HasMaxLength(32).IsRequired();
        entity.Property(record => record.SignalKey).HasMaxLength(256).IsRequired();
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        entity.HasIndex(record => record.PrerequisiteId).IsUnique();
    }

    private static void ConfigureOrderingEvidence(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseRequestOrderingEvidenceRecord>();
        entity.ToTable("PurchaseRequestOrderingEvidence", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.PolicyBundleDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CaseDigest).HasMaxLength(64).IsRequired();
        entity.Property(record => record.CoveredTargetsJson).IsRequired();
        entity.Property(record => record.RequirementsJson).IsRequired();
        entity.Property(record => record.BudgetEvidenceRefsJson).IsRequired();
        entity.Property(record => record.ResultRefsJson).IsRequired();
        entity.Property(record => record.DocumentJson).IsRequired();
        entity.Property(record => record.ContentDigest).HasMaxLength(64).IsRequired();
        // REQ-03: one evidence document per request version and Approval case version.
        entity.HasIndex(record => new { record.OrganizationId, record.RequestId, record.RequestVersion, record.CaseVersion })
            .IsUnique()
            .HasDatabaseName("IX_PurchaseRequestOrderingEvidence_Version");
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderCommandRecord>();
        entity.ToTable("PurchaseOrderCommands", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.CommandKey).HasMaxLength(128).IsRequired();
        entity.Property(record => record.CommandType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Fingerprint).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ResultRef).HasMaxLength(256);
        entity.HasIndex(record => new { record.OrganizationId, record.CommandKey, record.CommandType })
            .IsUnique()
            .HasDatabaseName("IX_PurchaseOrderCommands_Key");
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderAuditRecord>();
        entity.ToTable("PurchaseOrderAudit", Schema, table => table.UseSqlOutputClause(false));
        entity.HasKey(record => record.Id);
        entity.Property(record => record.WorkloadIssuer).HasMaxLength(256);
        entity.Property(record => record.WorkloadClientId).HasMaxLength(128);
        entity.Property(record => record.Action).HasMaxLength(64).IsRequired();
        entity.Property(record => record.Cause).HasMaxLength(64);
        entity.Property(record => record.TargetType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.ChangedFieldsJson).IsRequired();
        entity.Property(record => record.EffectKey).HasMaxLength(256);
        entity.Property(record => record.CorrelationReference).HasMaxLength(256).IsRequired();
        entity.HasIndex(record => new { record.OrganizationId, record.TargetType, record.TargetId });
        entity.HasIndex(record => record.OccurredAt);
    }

    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PurchaseOrderOutboxRecord>();
        entity.ToTable("PurchaseOrderOutbox", Schema);
        entity.HasKey(record => record.Id);
        entity.Property(record => record.EventType).HasMaxLength(64).IsRequired();
        entity.Property(record => record.PayloadJson).IsRequired();
        entity.Property(record => record.IdempotencyKey).HasMaxLength(256).IsRequired();
        entity.Property(record => record.LastErrorCode).HasMaxLength(64);
        entity.HasIndex(record => record.IdempotencyKey).IsUnique();
        entity.HasIndex(record => new { record.DispatchedAt, record.OccurredAt });
    }
}
