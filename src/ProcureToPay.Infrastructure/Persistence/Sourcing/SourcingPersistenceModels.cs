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
