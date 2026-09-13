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
    public string ActionsJson { get; set; } = string.Empty;
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
    // Real delegation applied to this assignment, if any (SPEC 04 REQ-02, CA-02).
    public Guid? DelegationId { get; set; }
    public int? DelegationVersion { get; set; }
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
    // Closed actor union: HUMAN keeps its user; the derived carry-forward uses SYSTEM (SPEC 04 REQ-05).
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset DecidedAt { get; set; }
    // Null for a derived decision: it is not a replayable human command (SPEC 04 REQ-05).
    public string? DecisionKey { get; set; }
    public string? Fingerprint { get; set; }
    public string DecisionDigest { get; set; } = string.Empty;
    public string AuthorityEvidenceDigest { get; set; } = string.Empty;
    public string EligibilityEvidenceJson { get; set; } = string.Empty;
    // Shared authority evidence row: human root or referenced by its carry-forward decisions (REQ-06).
    public Guid EvidenceId { get; set; }
    public int EvidenceVersion { get; set; } = 1;
    public Guid? SourceDecisionId { get; set; }
    public Guid? RootHumanDecisionId { get; set; }
    // Preimage versions of the decision fingerprint. They are stored so a replay reproduces the
    // original digest instead of recomputing it from state the decision already advanced (REQ-06).
    public int? RequirementVersion { get; set; }
    public int? TaskVersion { get; set; }
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
    // Delegation cause of a DELEGATION_CHANGE or DELEGATION_EXPIRY run (SPEC 04 REQ-03).
    public Guid? DelegationId { get; set; }
    public int? DelegationVersion { get; set; }
    public string? DelegationTransition { get; set; }
    public DateTimeOffset? DelegationScheduledAt { get; set; }
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

/// <summary>Bounded delegation of routing with its interval, scope and lifecycle (SPEC 04 REQ-01).</summary>
public sealed class ApprovalDelegationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid DelegatorUserId { get; set; }
    public Guid DelegateeUserId { get; set; }
    public int Role { get; set; }
    public string DecisionScopeJson { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string DelegationCommandKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>Root audit of the create/revoke command that scheduled this delegation (REQ-03).</summary>
    public Guid RootAuditId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Durable ACTIVATE/EXPIRE job of one delegation version (SPEC 04 REQ-03).</summary>
public sealed class ApprovalDelegationTransitionJobRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid DelegationId { get; set; }
    public int DelegationVersion { get; set; }
    public string Transition { get; set; } = string.Empty;
    public DateTimeOffset ScheduledAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    // Lease and fencing of the transition worker (SPEC 04 REQ-03).
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public long FencingToken { get; set; }
    public int Attempts { get; set; }
    public int Version { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Append-only full-case supersession record (SPEC 04 REQ-04).</summary>
public sealed class CaseSupersessionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PreviousCaseId { get; set; }
    public int PreviousCaseVersion { get; set; }
    public Guid NewCaseId { get; set; }
    public string SupersessionKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string WorkloadIssuer { get; set; } = string.Empty;
    public string WorkloadClientId { get; set; } = string.Empty;
    public string TargetMappingJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stable identity of the authority evidence of a human root decision (SPEC 04 REQ-06).</summary>
public sealed class DecisionAuthorityEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RootHumanDecisionId { get; set; }
    public string Digest { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Provenance of one carry-forward decision (SPEC 04 REQ-05).</summary>
public sealed class DecisionCarryForwardEntryRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SourceDecisionId { get; set; }
    public Guid RootHumanDecisionId { get; set; }
    public Guid NewDecisionId { get; set; }
    public Guid EvidenceId { get; set; }
    public int EvidenceVersion { get; set; }
    public Guid NewCaseId { get; set; }
    public Guid NewRequirementId { get; set; }
    public string NewRequirementKey { get; set; } = string.Empty;
    public string SourceRequirementContractDigest { get; set; } = string.Empty;
    public string NewRequirementContractDigest { get; set; } = string.Empty;
    public string ProofDigest { get; set; } = string.Empty;
    public string SourceTargetJson { get; set; } = string.Empty;
    public string NewTargetJson { get; set; } = string.Empty;
    public string TargetMappingJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only, irreversible evidence revocation (SPEC 04 REQ-06).</summary>
public sealed class DecisionEvidenceRevocationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public Guid EvidenceId { get; set; }
    /// <summary>Evidence version confirmed by the revocation command (SPEC 04 REQ-06).</summary>
    public int EvidenceVersion { get; set; }
    public Guid DecisionId { get; set; }
    public string RevocationKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string? ActorWorkloadIssuer { get; set; }
    public string? ActorWorkloadClientId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
