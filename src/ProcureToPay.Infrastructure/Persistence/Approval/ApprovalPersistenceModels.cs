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
    // Owner workload identity resolved exactly once at ingestion (REQ-03, DEC-11).
    public string OwnerWorkloadIssuer { get; set; } = string.Empty;
    public string OwnerWorkloadClientId { get; set; } = string.Empty;
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
    // Artifacts of the decision at its own instant: a replay must return them, not the current state.
    public int RequirementStatusAfter { get; set; }
    public int TaskStatusAfter { get; set; }
    public int CaseStatusAfter { get; set; }
    public int CaseVersionAfter { get; set; }
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
    // Identity of the persisted entity that really transitioned (REQ-08). Nullable in storage so a
    // clean-baseline upgrade never fabricates values for pre-existing rows; new events always set it.
    public string? ResultSourceType { get; set; }
    public Guid? ResultSourceId { get; set; }
    public string? ResultSourceKey { get; set; }
    public Guid? SourceCommandId { get; set; }
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

public sealed class ApprovalReconciliationRunRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string? ReconciliationKey { get; set; }
    public Guid? TriggerAuditId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CursorCaseId { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public long FencingToken { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid RootAuditId { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ApprovalAuditEntryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? RequirementId { get; set; }
    // Closed actor union: exactly one variant is populated and an empty UUID is never an identity.
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string? ActorWorkloadIssuer { get; set; }
    public string? ActorWorkloadClientId { get; set; }
    public string? ActorSystemId { get; set; }
    // Causal link of an automatic effect or of an organization-triggered root audit.
    public string? CausedByAuditStream { get; set; }
    public Guid? CausedByAuditId { get; set; }
    public string? AutomaticEffectKey { get; set; }
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
