namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>Stable identity of one Purchase Request; its state is a projection of history (REQ-01).</summary>
public sealed class PurchaseRequestRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid LegalEntityId { get; set; }
    public int LegalEntityVersion { get; set; }
    public int CurrentVersion { get; set; }
    public int Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only request version snapshot (REQ-01, REQ-02).</summary>
public sealed class PurchaseRequestVersionRecord
{
    public Guid RequestId { get; set; }
    public int Version { get; set; }
    public int? PredecessorVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid LegalEntityId { get; set; }
    public int LegalEntityVersion { get; set; }
    public string BusinessJustification { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string? RevisionKey { get; set; }
    public string? Reason { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only line version; a line always belongs to the same request (REQ-01).</summary>
public sealed class PurchaseRequestLineVersionRecord
{
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public Guid RequestId { get; set; }
    public Guid OrganizationId { get; set; }
    public decimal EstimatedGrossAmount { get; set; }
    public string TransactionCurrency { get; set; } = string.Empty;
    public decimal BaseAmount { get; set; }
    public string BaseCurrency { get; set; } = string.Empty;
    public int FiscalYear { get; set; }
    public string PurchaseType { get; set; } = string.Empty;
    public string SpendCategoryJson { get; set; } = string.Empty;
    public string CostCenterJson { get; set; } = string.Empty;
    public string CostCenterDepartmentJson { get; set; } = string.Empty;
    public string BeneficiaryDepartmentJson { get; set; } = string.Empty;
    public string RequestedForUserJson { get; set; } = string.Empty;
    public string? SupplierJson { get; set; }
    public string? PreferredProductJson { get; set; }
    public string? RequiredProductJson { get; set; }
    public bool ContractRequired { get; set; }
    public bool NonStandardTerms { get; set; }
    public string AgreementStatus { get; set; } = string.Empty;
    public string NeedSummary { get; set; } = string.Empty;
    public string RiskAnswersJson { get; set; } = string.Empty;
    public string? FxAttestationJson { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Exact line set of one request version (the manifest for domain readers).</summary>
public sealed class PurchaseRequestVersionLineRecord
{
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string ContentDigest { get; set; } = string.Empty;
}

/// <summary>Append-only revision delta covering both manifests exactly (REQ-02).</summary>
public sealed class PurchaseRequestRevisionDeltaRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int FromRequestVersion { get; set; }
    public int ToRequestVersion { get; set; }
    public string DeltaJson { get; set; } = string.Empty;
    public string RevisionKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Immutable owner answers of one presented version (REQ-04).</summary>
public sealed class PurchaseRequestReferenceAttestationRecord
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset AttestedAt { get; set; }
    public string AssertionsJson { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Attested completeness manifest; written before the provider can serve the version.</summary>
public sealed class PurchaseRequestCompletenessManifestRecord
{
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid OrganizationId { get; set; }
    public string RequestContentDigest { get; set; } = string.Empty;
    public string ReferenceAttestationDigest { get; set; } = string.Empty;
    public string PolicyManifestDigest { get; set; } = string.Empty;
    public string DomainAttestationDigest { get; set; } = string.Empty;
    public string LinesJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Durable submission attempt: retries advance the same row (REQ-06).</summary>
public sealed class PurchaseRequestSubmissionAttemptRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string SubmissionKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public int Status { get; set; }
    public Guid? PolicySetVersionId { get; set; }
    public Guid? PolicyEvaluationBundleId { get; set; }
    public string? PolicyResultDigest { get; set; }
    public Guid? ApprovalCaseId { get; set; }
    public Guid? PreviousCaseId { get; set; }
    public string ApprovalContractVersion { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int AttemptCount { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only, deduplicated approval result inbox row (REQ-09).</summary>
public sealed class PurchaseRequestApprovalResultRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid EventId { get; set; }
    public string ContractVersion { get; set; } = string.Empty;
    public Guid CaseId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public string? SourceKey { get; set; }
    public Guid LineId { get; set; }
    public int LineVersion { get; set; }
    public string MaterialSnapshotDigest { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public Guid? DecisionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only lifecycle evidence backing the aggregate status (NFR-01).</summary>
public sealed class PurchaseRequestLifecycleEventRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string Action { get; set; } = string.Empty;
    public int? BeforeStatus { get; set; }
    public int AfterStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? WorkloadIssuer { get; set; }
    public string? WorkloadClientId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string CorrelationReference { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Idempotency ledger of create, revision, cancel and submit commands (REQ-06, REQ-11).</summary>
public sealed class PurchaseRequestCommandRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorUserId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string CommandKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
