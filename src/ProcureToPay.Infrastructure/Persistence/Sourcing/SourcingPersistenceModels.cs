using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Durable root of one sourcing process (SPEC 10 REQ-01): the contractual version is a monotonic
/// integer advanced by confirmed lifecycle transitions; the technical rowversion never leaves the
/// persistence boundary.
/// </summary>
public sealed class SourcingProcessRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public int State { get; set; }
    public int Version { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CancellationReason { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public ICollection<SourcingProcessLineRecord> Lines { get; } = [];
}

/// <summary>
/// Append-only line set of one process (<c>sourcing-process-line/v1</c>). The Buyer declares the
/// quantity and unit; the Purchase Request line is never modified (REQ-01).
/// </summary>
public sealed class SourcingProcessLineRecord
{
    public Guid ProcessId { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string LineContentDigest { get; set; } = null!;
    public decimal RequestedQuantity { get; set; }
    public string UnitCode { get; set; } = null!;

    public SourcingProcessRecord Process { get; set; } = null!;
}

/// <summary>
/// Takeover of one Purchase Request version (REQ-01). A row exists while the process is active or
/// awarded; cancelling the pre-award process releases it so another process can take over.
/// </summary>
public sealed class SourcingTakeoverRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid ProcessId { get; set; }
    public int ProcessVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>Root of one RFQ of a process (REQ-02); the current version row carries the status.</summary>
public sealed class RfqRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only RFQ version (<c>rfq-version/v1</c>, REQ-02).</summary>
public sealed class RfqVersionRecord
{
    public Guid RfqId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public int Status { get; set; }
    public string Currency { get; set; } = null!;
    public string TermsJson { get; set; } = null!;
    public string WeightsJson { get; set; } = null!;
    public string LinesJson { get; set; } = null!;
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset ResponseDeadline { get; set; }
    public int? PredecessorVersion { get; set; }
    /// <summary>
    /// Server-side quotation parameters frozen at opening: the minimum valid quotations of every
    /// covered target and the persisted floor. They stay outside the published document so its exact
    /// property set never changes (REQ-02, REQ-05).
    /// </summary>
    public string ParametersJson { get; set; } = "[]";
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }

    public RfqRecord Rfq { get; set; } = null!;
}

/// <summary>Recorded extension of one RFQ deadline (REQ-02, DEC-02).</summary>
public sealed class RfqDeadlineExtensionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
    public DateTimeOffset PreviousDeadline { get; set; }
    public DateTimeOffset NewDeadline { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;
}

/// <summary>Root of one quotation of an RFQ and supplier (REQ-03).</summary>
public sealed class QuotationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public Guid SupplierId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only quotation version (<c>quotation-version/v1</c>, REQ-03).</summary>
public sealed class QuotationVersionRecord
{
    public Guid QuotationId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    /// <summary>Exact RFQ version the answer responds to; frozen with its digest (REQ-03).</summary>
    public int RfqVersion { get; set; }
    public string RfqContentDigest { get; set; } = null!;
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public string Currency { get; set; } = null!;
    public string TermsJson { get; set; } = null!;
    public string LinesJson { get; set; } = null!;
    public string AttachmentsJson { get; set; } = null!;
    public string ReviewJson { get; set; } = null!;
    /// <summary>
    /// Denormalized review status and timeliness of the version. They are part of the canonical
    /// document and are copied here so validity counting is exact in SQL without reading JSON
    /// (REQ-04).
    /// </summary>
    public int ReviewStatus { get; set; }
    public int Timeliness { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public int? PredecessorVersion { get; set; }
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }

    public QuotationRecord Quotation { get; set; } = null!;
}

/// <summary>
/// Line scope of one quotation version (REQ-04): one row per quoted line, so the valid count per
/// target is an exact SQL aggregation and never hides an uncovered line.
/// </summary>
public sealed class QuotationLineScopeRecord
{
    public Guid QuotationId { get; set; }
    public int Version { get; set; }
    public Guid LineId { get; set; }
}

/// <summary>
/// Staged or confirmed quotation evidence (REQ-03). Only a confirmed attachment is immutable and
/// publishable; the object key stays opaque and out of every contract.
/// </summary>
public sealed class SourcingAttachmentRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public string ObjectKey { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long Length { get; set; }
    public string Sha256 { get; set; } = null!;
    public int State { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>
/// Durable attempt of one sourcing command (REQ-01, REQ-02): the key and the fingerprint are
/// persisted before the effect, so a replay returns the same version and a different payload is
/// refused instead of producing a second artefact.
/// </summary>
public sealed class SourcingCommandRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorUserId { get; set; }
    public string CommandType { get; set; } = null!;
    public string CommandKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public Guid? ResultId { get; set; }
    public int? ResultVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only sourcing audit with causal identity and no copied commercial values (REQ-14).</summary>
public sealed class SourcingAuditRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Action { get; set; } = null!;
    public string ActorJson { get; set; } = null!;
    public string? CauseJson { get; set; }
    public string ChangedFieldsJson { get; set; } = "[]";
    public string CorrelationReference { get; set; } = null!;
    public string EffectKey { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public string TargetJson { get; set; } = null!;
}

/// <summary>Outbox event committed atomically with one sourcing effect (NFR-02).</summary>
public sealed class SourcingOutboxRecord
{
    public Guid EventId { get; set; }
    public Guid OrganizationId { get; set; }
    public string ContractVersion { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
}

/// <summary>
/// Root of the weighted evaluation of one RFQ (REQ-07, REQ-08). Evaluation versions are append-only
/// and only a current version that no proposal consumed may be replaced by a successor.
/// </summary>
public sealed class SourcingEvaluationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public int CurrentVersion { get; set; }
    public int? ConsumedVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Append-only evaluation version (<c>quote-evaluation-version/v1</c>, REQ-07). The stored document
/// is exactly the digest preimage, so rehydration recomputes the digest and detects tampering.
/// </summary>
public sealed class QuoteEvaluationVersionRecord
{
    public Guid EvaluationId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public int RfqVersion { get; set; }
    public string RfqContentDigest { get; set; } = null!;
    public string BaseCurrency { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public string RecommendationsJson { get; set; } = null!;
    public string QuotationRefsJson { get; set; } = null!;
    public string FxSnapshotRefsJson { get; set; } = null!;
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public SourcingEvaluationRecord Evaluation { get; set; } = null!;
}

/// <summary>
/// Immutable FX snapshot of one currency pair used by an evaluation (REQ-08). It is not a general FX
/// catalog: it exists only for the evaluation that froze it and never mutates.
/// </summary>
public sealed class SourcingFxSnapshotRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public string BaseCurrency { get; set; } = null!;
    public string SourceCurrency { get; set; } = null!;
    public decimal Rate { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public string SourceReference { get; set; } = null!;
    public string AttachmentJson { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Append-only manual score of one criterion only a human judges (REQ-07): payment terms or
/// technical compliance, always with a motive and confirmed evidence.
/// </summary>
public sealed class SourcingManualScoreRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RfqId { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public int Criterion { get; set; }
    public decimal Score { get; set; }
    public string Justification { get; set; } = null!;
    public string EvidenceJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Root of the human selection of one line (REQ-09). Exactly one current selection exists per line,
/// and every change appends a successor instead of rewriting the previous decision.
/// </summary>
public sealed class SourcingSelectionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid LineId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only selection version (<c>sourcing-selection/v1</c>, REQ-09).</summary>
public sealed class SourcingSelectionVersionRecord
{
    public Guid SelectionId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string LineContentDigest { get; set; } = null!;
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public Guid QuotationId { get; set; }
    public int QuotationVersion { get; set; }
    public string QuotationContentDigest { get; set; } = null!;
    public Guid? EvaluationId { get; set; }
    public int? EvaluationVersion { get; set; }
    public string? EvaluationContentDigest { get; set; }
    public bool Recommended { get; set; }
    public string? DeviationJustification { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public SourcingSelectionRecord Selection { get; set; } = null!;
}

/// <summary>
/// Persisted facts of one quotation waiver request (REQ-05). They are written before the approval
/// case exists, so the recalculated counts are auditable even when the submission fails closed.
/// </summary>
public sealed class SourcingWaiverFactsRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public int RfqVersion { get; set; }
    public Guid PrerequisiteId { get; set; }
    public string RequirementKey { get; set; } = null!;
    public Guid BaseBundleId { get; set; }
    public string BaseResultDigest { get; set; } = null!;
    public Guid PolicyVersionId { get; set; }
    public string PolicyContentDigest { get; set; } = null!;
    public string ManifestDigest { get; set; } = null!;
    public int From { get; set; }
    public int To { get; set; }
    public int Floor { get; set; }
    public string TargetsJson { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public string CommandKey { get; set; } = null!;
    public Guid? ApprovalCaseId { get; set; }
    public Guid? ApprovalRequirementId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Root of the proposal of one supplier of an RFQ (REQ-10). A supplier has at most one proposal root
/// per RFQ, and every material change appends a successor version instead of rewriting the decision.
/// </summary>
public sealed class SourcingProposalRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid SupplierId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Append-only proposal version (<c>sourcing-proposal-version/v1</c>, REQ-10). Only the approval
/// bookkeeping of the case may change after the write; the content and its digest never do.
/// </summary>
public sealed class SourcingProposalVersionRecord
{
    public Guid ProposalId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string RequestRefJson { get; set; } = null!;
    public string RequestBundleRefJson { get; set; } = null!;
    public int SelectionBasis { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public Guid? EvaluationId { get; set; }
    public int? EvaluationVersion { get; set; }
    public string? EvaluationContentDigest { get; set; }
    public Guid? WaiverFactsId { get; set; }
    public string LineIdsJson { get; set; } = null!;
    public string TermsJson { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public string ManifestJson { get; set; } = null!;
    public string ManifestDigest { get; set; } = null!;
    public int State { get; set; }
    public string? SubmissionKey { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public string? ErrorCode { get; set; }
    public string CommandKey { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public SourcingProposalRecord Proposal { get; set; } = null!;
}

/// <summary>
/// Root of one published award (REQ-12). Every publication or correction appends a successor version;
/// the root keeps the current pointer and never rewrites history.
/// </summary>
public sealed class SourcingAwardRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid SupplierId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only award version (<c>award-version/v1</c>, REQ-12).</summary>
public sealed class SourcingAwardVersionRecord
{
    public Guid AwardId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProcessId { get; set; }
    public Guid RfqId { get; set; }
    public Guid ProposalId { get; set; }
    public int ProposalVersion { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public int SelectionBasis { get; set; }
    public Guid? EvaluationId { get; set; }
    public int? EvaluationVersion { get; set; }
    public string? EvaluationContentDigest { get; set; }
    public Guid? WaiverFactsId { get; set; }
    public string LinesJson { get; set; } = null!;
    public string DocumentJson { get; set; } = null!;
    public string ContentDigest { get; set; } = null!;
    public string AwardKey { get; set; } = null!;
    public int? PredecessorVersion { get; set; }
    public bool Superseded { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string Reason { get; set; } = null!;

    public SourcingAwardRecord Award { get; set; } = null!;
}

/// <summary>
/// Current award of one Purchase Request line (REQ-12): the unique index makes a double award
/// impossible even for a direct SQL writer, so a line is never split between suppliers.
/// </summary>
public sealed class SourcingCurrentAwardLineRecord
{
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public Guid AwardId { get; set; }
    public int AwardVersion { get; set; }
    public Guid OrganizationId { get; set; }
}

/// <summary>
/// Durable owner attempt of one sourcing prerequisite (REQ-13). The state machine is
/// PENDING→PROCESSING→CHECKED→SIGNALLING→COMPLETED, a pre-signal cancellation takes any non-terminal
/// state to ABANDONED and a technical error returns to PENDING with a retry instant.
/// </summary>
public sealed class SourcingOwnerAttemptRecord
{
    public Guid Id { get; set; }
    public Guid PrerequisiteId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public string SubjectType { get; set; } = null!;
    public string OwnerAdapterId { get; set; } = null!;
    public string OwnerAdapterVersion { get; set; } = null!;
    public Guid? ProcessId { get; set; }
    public Guid? RfqId { get; set; }
    public string TargetsJson { get; set; } = null!;
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

/// <summary>Append-only evidence of one satisfied sourcing prerequisite (REQ-13).</summary>
public sealed class SourcingOwnerEvidenceRecord
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
/// Explicit exactly-one processor registration of one sourcing owner (REQ-13): adapter id/version,
/// stable processor id and the workload identity Approval persisted for that owner.
/// </summary>
public sealed class SourcingPrerequisiteProcessorRegistrationRecord
{
    public string AdapterId { get; set; } = null!;
    public string AdapterVersion { get; set; } = null!;
    public string ProcessorId { get; set; } = null!;
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
}

/// <summary>
/// Confirmed governed catalogue route of one process (REQ-06): the frozen approved catalogue
/// snapshots that let the RFQ be omitted while the Policy evaluation generated no quotation control.
/// </summary>
public sealed class SourcingCatalogRouteRecord
{
    public Guid ProcessId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public string SnapshotsJson { get; set; } = null!;
    public string SnapshotsDigest { get; set; } = null!;
    /// <summary>Contractual process version whose snapshot set produced the digest (REQ-06).</summary>
    public int SnapshotsVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
}
