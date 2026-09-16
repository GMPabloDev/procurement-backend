using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// Durable root of one supplier (SPEC 09 REQ-01): stable identity, the approved operational pointer
/// and the single open governed proposal. Historical versions are append-only rows and the pointers
/// only ever move forward to an existing successor.
/// </summary>
public sealed class SupplierRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid FiscalIdentityId { get; set; }
    public int? OperationalVersion { get; set; }
    public Guid? WorkingProposalId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public OrganizationRecord Organization { get; set; } = null!;
    public SupplierFiscalIdentityRecord FiscalIdentity { get; set; } = null!;
    public ICollection<SupplierVersionRecord> Versions { get; } = [];
}

/// <summary>
/// Reserved fiscal identity of one supplier root (REQ-01). The pair (country, normalized tax id)
/// is unique per organization and an approved identity is never reassigned to another root.
/// </summary>
public sealed class SupplierFiscalIdentityRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public string CountryCode { get; set; } = null!;
    public string TaxIdKey { get; set; } = null!;
    public string TaxId { get; set; } = null!;
    public string IdentityKeyDigest { get; set; } = null!;
    public int? FirstApprovedVersion { get; set; }
    public int? LastApprovedVersion { get; set; }

    public SupplierRecord Supplier { get; set; } = null!;
}

/// <summary>Append-only version of one supplier (REQ-02, REQ-04).</summary>
public sealed class SupplierVersionRecord
{
    public Guid SupplierId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public int? PredecessorVersion { get; set; }
    public string LegalName { get; set; } = null!;
    public string? TradeName { get; set; }
    public string CountryCode { get; set; } = null!;
    public string TaxId { get; set; } = null!;
    public string AddressesJson { get; set; } = null!;
    public string ContactsJson { get; set; } = null!;
    public string PaymentTermsJson { get; set; } = null!;
    public string SupportedCurrenciesJson { get; set; } = null!;
    public string CategoriesSuppliedJson { get; set; } = null!;
    public string PerformanceJson { get; set; } = null!;
    public string BankingRefsJson { get; set; } = null!;
    public int Status { get; set; }
    public int RiskStatus { get; set; }
    public string ContentDigest { get; set; } = null!;
    public Guid? ProposalId { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public string? ChangeKey { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;

    public SupplierRecord Supplier { get; set; } = null!;
}

/// <summary>Stable root of one bank account of a supplier (REQ-05).</summary>
public sealed class SupplierBankingDetailRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<SupplierBankingVersionRecord> Versions { get; } = [];
}

/// <summary>
/// Append-only, AEAD-encrypted version of one bank account (REQ-05). Only the masked suffix and the
/// non-secret metadata are readable without the key provider; the account number lives exclusively
/// inside <c>Ciphertext</c> authenticated by <c>Tag</c>.
/// </summary>
public sealed class SupplierBankingVersionRecord
{
    public Guid BankingDetailId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    // R11 (revisión independiente): el titular forma parte del plaintext bancario cifrado y no se
    // persiste en claro; solo metadata no secreta acompaña al sobre.
    public string BankName { get; set; } = null!;
    public string BankCountryCode { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public int AccountType { get; set; }
    public string MaskedSuffix { get; set; } = null!;
    public bool IsDefault { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public string KeyVersion { get; set; } = null!;
    public int? PredecessorVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;
    public string ChangeKey { get; set; } = null!;

    public SupplierBankingDetailRecord Detail { get; set; } = null!;
}

/// <summary>
/// Durable attempt of one banking command (REQ-05): the key and the referenced version are
/// persisted before the envelope, so a retry replays the same version or refuses a different payload.
/// </summary>
public sealed class SupplierBankingCommandRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public string ChangeKey { get; set; } = null!;
    public Guid BankingDetailId { get; set; }
    public int Version { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Current default account of one supplier and currency; at most one row per pair (REQ-05).</summary>
public sealed class SupplierBankingDefaultRecord
{
    public Guid SupplierId { get; set; }
    public string Currency { get; set; } = null!;
    public Guid BankingDetailId { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// One governed change proposal (REQ-02, REQ-03). The candidate version is immutable once submitted;
/// the case is created or recovered from the same row so a retry never duplicates it.
/// </summary>
public sealed class SupplierChangeProposalRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public int? BaseVersion { get; set; }
    public int? CandidateVersion { get; set; }
    public int ChangeKind { get; set; }
    public string SensitiveFieldsJson { get; set; } = "[]";
    public int? RequestedStatus { get; set; }
    public Guid EditorUserId { get; set; }
    public int State { get; set; }
    public string ChangeKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    /// <summary>Submission key of the durable attempt; a retry reuses it to recover the same case (REQ-03).</summary>
    public string? SubmissionKey { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public string? RequirementKey { get; set; }
    public string? CaseContractVersion { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int AttemptCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Durable root of one approved catalog entry, identified by its exact selector (REQ-06).</summary>
public sealed class ApprovedSupplierCatalogEntryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public string SpendCategoryCode { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public int CurrentVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only version of one approved catalog entry (REQ-06, REQ-07).</summary>
public sealed class ApprovedSupplierCatalogVersionRecord
{
    public Guid CatalogEntryId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public string SpendCategoryJson { get; set; } = null!;
    public string? ProductJson { get; set; }
    public decimal NegotiatedPrice { get; set; }
    public string Currency { get; set; } = null!;
    public string UnitCode { get; set; } = null!;
    public string ExternalContractReference { get; set; } = null!;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    public int Status { get; set; }
    public Guid AttachmentId { get; set; }
    public int AttachmentVersion { get; set; }
    public string ContentDigest { get; set; } = null!;
    public int? PredecessorVersion { get; set; }
    public Guid? ProposalId { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = null!;

    public ApprovedSupplierCatalogEntryRecord Entry { get; set; } = null!;
}

/// <summary>
/// Confirmed or staged agreement attachment (REQ-07). Only a confirmed attachment can be published;
/// the SHA-256 is computed server-side and the object key stays opaque and out of the contract.
/// </summary>
public sealed class SupplierAgreementAttachmentRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
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

/// <summary>Frozen catalogue lookup of one Purchase Request line (REQ-08, REQ-09).</summary>
public sealed class SupplierPolicyFactSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string LineContentDigest { get; set; } = null!;
    public Guid? SupplierId { get; set; }
    public int? SupplierVersion { get; set; }
    public string SpendCategoryJson { get; set; } = null!;
    public string? ProductJson { get; set; }
    public Guid? CatalogEntryId { get; set; }
    public int? CatalogEntryVersion { get; set; }
    public string? CatalogContentDigest { get; set; }
    public bool PreferredSupplier { get; set; }
    public string AgreementStatus { get; set; } = null!;
    public DateTimeOffset EvaluatedAt { get; set; }
    public string SnapshotDigest { get; set; } = null!;
}

/// <summary>Durable attempt of one <c>active-supplier-owner/v1</c> prerequisite (REQ-10).</summary>
public sealed class SupplierPrerequisiteAttemptRecord
{
    public Guid Id { get; set; }
    public Guid PrerequisiteId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Explicit exactly-one processor registration for <c>active-supplier-owner/v1</c> (REQ-10).</summary>
public sealed class SupplierPrerequisiteProcessorRegistrationRecord
{
    public string AdapterId { get; set; } = null!;
    public string AdapterVersion { get; set; } = null!;
    public string ProcessorId { get; set; } = null!;
}

/// <summary>Append-only supplier audit with causal identity and no copied sensitive values (REQ-11).</summary>
public sealed class SupplierAuditRecord
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

/// <summary>Idempotent inbox row of one approval result or lifecycle event (REQ-03).</summary>
public sealed class SupplierApprovalResultRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EventId { get; set; }
    public string ContractVersion { get; set; } = null!;
    public Guid CaseId { get; set; }
    public Guid TargetId { get; set; }
    public int TargetVersion { get; set; }
    public string Result { get; set; } = null!;
    public Guid? ProposalId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Outbox event published atomically with one approved status change (REQ-10).</summary>
public sealed class SupplierStatusOutboxRecord
{
    public Guid EventId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public int SupplierVersion { get; set; }
    public int? PreviousStatus { get; set; }
    public int Status { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string PayloadJson { get; set; } = null!;
    public DateTimeOffset? DispatchedAt { get; set; }
}
