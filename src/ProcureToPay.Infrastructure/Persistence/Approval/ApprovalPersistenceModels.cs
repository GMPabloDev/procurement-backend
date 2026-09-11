namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed class ApprovalCaseRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string SubjectType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public int SubjectVersion { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string SourceSnapshotDigest { get; set; } = string.Empty;
    public string WorkloadIssuer { get; set; } = string.Empty;
    public string WorkloadClientId { get; set; } = string.Empty;
    public string SubmissionKey { get; set; } = string.Empty;
    public string SubmissionFingerprint { get; set; } = string.Empty;
    public Guid OriginatorId { get; set; }
    public Guid? RequesterId { get; set; }
    public int Status { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CorrelationReference { get; set; } = string.Empty;
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalRequirementRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public string SourceRequirementKey { get; set; } = string.Empty;
    public string WorkflowRequirementKey { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public int Role { get; set; }
    public string AuthorityJson { get; set; } = string.Empty;
    public string DecisionScopeJson { get; set; } = string.Empty;
    public string ExcludedUserIdsJson { get; set; } = string.Empty;
    public string TargetsJson { get; set; } = string.Empty;
    public string DependenciesJson { get; set; } = string.Empty;
    public int Status { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalPrerequisiteRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string OwnerAdapterId { get; set; } = string.Empty;
    public string OwnerAdapterVersion { get; set; } = string.Empty;
    public string SourceControlType { get; set; } = string.Empty;
    public string SourceControlDigest { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = string.Empty;
    public string TargetsJson { get; set; } = string.Empty;
    public int Status { get; set; }
    public int Version { get; set; }
    public string? SignalKey { get; set; }
    public string? SignalFingerprint { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalPrerequisiteSignalRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public Guid PrerequisiteId { get; set; }
    public string SignalKey { get; set; } = string.Empty;
    public string SignalFingerprint { get; set; } = string.Empty;
    public bool Satisfied { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public string? EvidenceReference { get; set; }
    public string? EvidenceDigest { get; set; }
    public string CorrelationReference { get; set; } = string.Empty;
}

public sealed class ApprovalTaskRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequirementId { get; set; }
    public int Status { get; set; }
    public Guid? CurrentAssigneeUserId { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalAssignmentRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TaskId { get; set; }
    public Guid AssigneeUserId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public int Load { get; set; }
    public string Cause { get; set; } = string.Empty;
    public string EligibilityEvidenceJson { get; set; } = string.Empty;
}

public sealed class ApprovalDecisionRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequirementId { get; set; }
    public Guid? TaskId { get; set; }
    public int Action { get; set; }
    public int Origin { get; set; }
    public Guid ActorUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset DecidedAt { get; set; }
    public string DecisionKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string DecisionDigest { get; set; } = string.Empty;
    public string AuthorityEvidenceDigest { get; set; } = string.Empty;
    public string EligibilityEvidenceJson { get; set; } = string.Empty;
    // Preimage versions of the decision fingerprint. They are stored so a replay reproduces the
    // original digest instead of recomputing it from state the decision already advanced (REQ-06).
    public int RequirementVersion { get; set; }
    public int TaskVersion { get; set; }
    public string CorrelationReference { get; set; } = string.Empty;
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalDecisionTargetRecord
{
    public Guid Id { get; set; }
    public Guid DecisionId { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequirementId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public int TargetVersion { get; set; }
    public string MaterialSnapshotDigest { get; set; } = string.Empty;
}

public sealed class ApprovalOutboxEventRecord
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? RequirementId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public int TargetVersion { get; set; }
    public string MaterialSnapshotDigest { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string ContractVersion { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
    public string CorrelationReference { get; set; } = string.Empty;
    public string? LockOwner { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalAuditEntryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? RequirementId { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public string ScopeJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string CorrelationReference { get; set; } = string.Empty;
}

public sealed class ApprovalSubmissionReservationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string WorkloadIssuer { get; set; } = string.Empty;
    public string WorkloadClientId { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string SubmissionKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public Guid CaseId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalWorkflowStateRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset LastReconciliationCompletedAt { get; set; }
    public string? LastReconciliationOwner { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
