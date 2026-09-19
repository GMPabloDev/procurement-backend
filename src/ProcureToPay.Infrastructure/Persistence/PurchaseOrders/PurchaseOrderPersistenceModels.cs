namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Durable root of one Purchase Order (SPEC 11 REQ-02): a stable, immutable and non-reusable PO
/// number, a current version pointer that only advances and the frozen commercial identity. The
/// technical rowversion never leaves the persistence boundary.
/// </summary>
public sealed class PurchaseOrderRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string PoNumber { get; set; } = null!;
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public Guid LegalEntityId { get; set; }
    public int LegalEntityVersion { get; set; }
    public int CurrentVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Monotonic PO number allocator of one organization. A number is never reused: the allocator only
/// advances inside the claiming transaction, so a rolled back claim consumes no number.
/// </summary>
public sealed class PurchaseOrderNumberSequenceRecord
{
    public Guid OrganizationId { get; set; }
    public long LastValue { get; set; }
}

/// <summary>
/// Append-only Purchase Order version (<c>purchase-order-version/v1</c>, REQ-02). The published
/// document is stored verbatim so its digest is reproducible at read time, and the indexed columns
/// exist only for uniqueness and lookup.
/// </summary>
public sealed class PurchaseOrderVersionRecord
{
    public Guid PoId { get; set; }
    public int Version { get; set; }
    public int? PredecessorVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public string PoNumber { get; set; } = null!;
    public int State { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string RequestContentDigest { get; set; } = null!;
    public Guid ProposalId { get; set; }
    public int ProposalVersion { get; set; }
    public string ProposalContentDigest { get; set; } = null!;
    public Guid AwardId { get; set; }
    public int AwardVersion { get; set; }
    public string AwardContentDigest { get; set; } = null!;
    public Guid ClaimId { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public Guid LegalEntityId { get; set; }
    public int LegalEntityVersion { get; set; }
    public decimal SourceAmount { get; set; }
    public string SourceCurrency { get; set; } = null!;
    public decimal BaseAmount { get; set; }
    public string BaseCurrency { get; set; } = null!;
    public string? DeliveryJson { get; set; }
    public string TermsJson { get; set; } = null!;
    public string LinesJson { get; set; } = null!;
    public string BudgetOperationRefsJson { get; set; } = "[]";

    /// <summary>
    /// Server-owned completion parameters: one entry per line with the attested purchase type and the
    /// Requested For candidate. They stay outside the published document so its exact property set
    /// never changes (REQ-06).
    /// </summary>
    public string LineParametersJson { get; set; } = "[]";
    public Guid? ApprovalCaseId { get; set; }
    public int? ApprovalCaseVersion { get; set; }
    public string? ApprovalDigest { get; set; }
    public string? OrderingEvidenceDigest { get; set; }
    public Guid? AmendmentId { get; set; }
    public int? AmendmentVersion { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }

    public PurchaseOrderRecord PurchaseOrder { get; set; } = null!;
}

/// <summary>
/// Current published version of one Purchase Order line. The unique index makes two current lines of
/// the same request line impossible, so an award line is never ordered twice (REQ-01, REQ-02).
/// </summary>
public sealed class PurchaseOrderLinePointerRecord
{
    public Guid RequestLineId { get; set; }
    public int RequestLineVersion { get; set; }
    public Guid PoId { get; set; }
    public int PoVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public int State { get; set; }
}

/// <summary>
/// Durable award consumption claim (REQ-01): one active claim per award, with the fingerprint of its
/// preimage so a replay returns the recorded outcome and a different preimage is a conflict.
/// </summary>
public sealed class AwardConsumptionClaimRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AwardId { get; set; }
    public int AwardVersion { get; set; }
    public string AwardContentDigest { get; set; } = null!;
    public string ClaimKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public Guid PoId { get; set; }
    public string PoNumber { get; set; } = null!;
    public string CoveredLinesJson { get; set; } = null!;

    /// <summary>
    /// Exact <c>award-consumption/v1</c> snapshot verified at claim time. A replay returns these
    /// bytes instead of consuming the award twice (REQ-01).
    /// </summary>
    public string AwardSnapshotJson { get; set; } = null!;
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public int State { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>
/// One line takeover (<c>purchase-request-line-takeover/v1</c>, REQ-10). The unique filtered index on
/// active rows makes two live owners of the same line version impossible even for a direct SQL writer.
/// </summary>
public sealed class PurchaseRequestLineTakeoverRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string RequestContentDigest { get; set; } = null!;
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string LineContentDigest { get; set; } = null!;
    public int Owner { get; set; }
    public int State { get; set; }
    public int Version { get; set; }
    public Guid ConsumerId { get; set; }
    public int ConsumerVersion { get; set; }
    public string ConsumerDigest { get; set; } = null!;
    public string ConsumerType { get; set; } = null!;
    public Guid? PredecessorId { get; set; }
    public int? PredecessorVersion { get; set; }
    public string? PredecessorDigest { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Projection of one Purchase Request line published by this module (REQ-10). Events never move the
/// projection backwards: a late or duplicated event of an older version is ignored.
/// </summary>
public sealed class PurchaseRequestLineProjectionRecord
{
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public int Projection { get; set; }
    public Guid ConsumerId { get; set; }
    public int ConsumerVersion { get; set; }
    public string ConsumerDigest { get; set; } = null!;
    public string ConsumerType { get; set; } = null!;
    public int ConsumerState { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Durable attempt of the COMMIT/REVERSE boundary between a Purchase Order and Budget (REQ-04,
/// REQ-05). The attempt is recovered by key; a confirmed effect is never posted twice.
/// </summary>
public sealed class PurchaseOrderBudgetAttemptRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PoId { get; set; }
    public int PoVersion { get; set; }
    public Guid? AmendmentId { get; set; }
    public int Operation { get; set; }
    public int State { get; set; }
    public string OperationKey { get; set; } = null!;
    public string? ReleaseKey { get; set; }
    public string ParentsJson { get; set; } = null!;
    public string? OperationId { get; set; }
    public string? ReleaseOperationId { get; set; }
    public string? MovementRefsJson { get; set; }
    public string? ReleaseMovementRefsJson { get; set; }
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public long FencingToken { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only Purchase Order amendment version (<c>purchase-order-amendment/v1</c>, REQ-05).</summary>
public sealed class PurchaseOrderAmendmentRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public int? PredecessorVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PoId { get; set; }
    public int BasePoVersion { get; set; }
    public int State { get; set; }
    public Guid? SuccessorAwardId { get; set; }
    public int? SuccessorAwardVersion { get; set; }
    public string? SuccessorAwardDigest { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public int? ApprovalCaseVersion { get; set; }
    public string? ApprovalDigest { get; set; }
    public Guid? AppliedPoVersion { get; set; }
    public int? AppliedPoVersionNumber { get; set; }
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public string CommandKey { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;
}

/// <summary>Durable root of one amendment lineage: the pointer to its current version (REQ-05).</summary>
public sealed class PurchaseOrderAmendmentRootRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PoId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Append-only Direct Purchase authorization version (<c>direct-purchase-authorization/v1</c>,
/// REQ-07). It authorizes a maximum and never posts a COMMIT.
/// </summary>
public sealed class DirectPurchaseAuthorizationRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string AuthorizationKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public Guid PolicyBundleId { get; set; }
    public int PolicyBundleVersion { get; set; }
    public string PolicyBundleDigest { get; set; } = null!;
    public string CoveredLinesJson { get; set; } = null!;
    public string CoveredTargetsJson { get; set; } = null!;
    public int State { get; set; }
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
}

/// <summary>
/// Append-only supporting document version (<c>procurement-supporting-document/v1</c>, REQ-08).
/// Confirmed bytes are never replaced: a correction creates another root.
/// </summary>
public sealed class ProcurementSupportingDocumentRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string CoveredTargetsJson { get; set; } = null!;
    public string FileRefJson { get; set; } = null!;
    public string BusinessType { get; set; } = null!;
    public int State { get; set; }
    public Guid? SuccessorOfId { get; set; }
    public string? SupersededReason { get; set; }
    public string Sha256 { get; set; } = null!;
    public long Length { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
}

/// <summary>
/// Explicit exactly-one processor registration of the <c>supporting-document-owner/v1</c> adapter
/// (REQ-08): adapter id/version, stable processor id and the persisted owner workload identity.
/// </summary>
public sealed class SupportingDocumentProcessorRegistrationRecord
{
    public string AdapterId { get; set; } = null!;
    public string AdapterVersion { get; set; } = null!;
    public string ProcessorId { get; set; } = null!;
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
}

/// <summary>Durable attempt of one supporting document prerequisite (REQ-08).</summary>
public sealed class SupportingDocumentOwnerAttemptRecord
{
    public Guid Id { get; set; }
    public Guid PrerequisiteId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public string SubjectType { get; set; } = null!;
    public string OwnerAdapterId { get; set; } = null!;
    public string OwnerAdapterVersion { get; set; } = null!;
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string TargetsJson { get; set; } = null!;
    public string ParametersDigest { get; set; } = null!;
    public string AllowedDocumentTypesJson { get; set; } = null!;
    public int MinimumCount { get; set; }
    public string State { get; set; } = null!;
    public string? SignalResult { get; set; }
    public string CheckKey { get; set; } = null!;
    public string SignalKey { get; set; } = null!;
    public string? EvidenceDigest { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public long FencingToken { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? AbandonedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only evidence of one satisfied supporting document prerequisite (REQ-08).</summary>
public sealed class SupportingDocumentOwnerEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PrerequisiteId { get; set; }
    public Guid AttemptId { get; set; }
    public string ContractVersion { get; set; } = null!;
    public string Result { get; set; } = null!;
    public string SignalKey { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Server-produced ordering evidence of one Purchase Request version
/// (<c>purchase-request-ordering-evidence/v1</c>, REQ-03). It is the proof that the Approval case of
/// the request completed with its financial authorities, and the adapter never accepts it from a
/// caller.
/// </summary>
public sealed class PurchaseRequestOrderingEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid PolicyBundleId { get; set; }
    public int PolicyBundleVersion { get; set; }
    public string PolicyBundleDigest { get; set; } = null!;
    public Guid CaseId { get; set; }
    public int CaseVersion { get; set; }
    public string CaseDigest { get; set; } = null!;
    public string CoveredTargetsJson { get; set; } = null!;
    public string RequirementsJson { get; set; } = null!;
    public string BudgetEvidenceRefsJson { get; set; } = null!;
    public string ResultRefsJson { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>Idempotency record of one Purchase Orders command (REQ-12).</summary>
public sealed class PurchaseOrderCommandRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string CommandKey { get; set; } = null!;
    public string CommandType { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public string? ResultRef { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Append-only audit record of one Purchase Orders mutation (Datos y contratos). It is written in the
/// same transaction as the local mutation and never carries a payload.
/// </summary>
public sealed class PurchaseOrderAuditRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? WorkloadIssuer { get; set; }
    public string? WorkloadClientId { get; set; }
    public string Action { get; set; } = null!;
    public string? Cause { get; set; }
    public string TargetType { get; set; } = null!;
    public Guid TargetId { get; set; }
    public int TargetVersion { get; set; }
    public string ChangedFieldsJson { get; set; } = "[]";
    public string? EffectKey { get; set; }
    public string CorrelationReference { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Durable outbox event of the module (REQ-10): the Purchase Request projection consumes it without
/// moving backwards, and the dispatcher owns its delivery.
/// </summary>
public sealed class PurchaseOrderOutboxRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string PayloadJson { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int DispatchAttempts { get; set; }
    public string? LastErrorCode { get; set; }
}
