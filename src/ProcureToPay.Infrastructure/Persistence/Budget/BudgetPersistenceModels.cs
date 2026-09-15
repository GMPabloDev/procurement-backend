namespace ProcureToPay.Infrastructure.Persistence.BudgetLedger;

/// <summary>
/// Durable root of one Budget position (SPEC 08 REQ-01): the stable identity
/// <c>Cost Center + Fiscal Year + Spend Category</c> and the two current pointers. Historical
/// allocation versions and movements never move these pointers backwards.
/// </summary>
public sealed class BudgetPositionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CostCenterId { get; set; }
    public int FiscalYear { get; set; }
    public string SpendCategoryCode { get; set; } = null!;
    public string PositionKeyDigest { get; set; } = null!;
    public int CurrentAllocationVersion { get; set; }
    public int CurrentBalanceVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Append-only allocation revision (REQ-01). The revision freezes the Cost Center and Spend
/// Category references that were attested when it was accepted; it is not a movement.
/// </summary>
public sealed class BudgetAllocationVersionRecord
{
    public Guid PositionId { get; set; }
    public int Version { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CostCenterId { get; set; }
    public int CostCenterVersion { get; set; }
    public string SpendCategoryCode { get; set; } = null!;
    public int SpendCategoryVersion { get; set; }
    public string SpendCategoryDigest { get; set; } = null!;
    public decimal AllocatedAmount { get; set; }
    public string Currency { get; set; } = null!;
    public int? PredecessorVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string AllocationKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public string Reason { get; set; } = null!;

    public BudgetPositionRecord Position { get; set; } = null!;
}

/// <summary>
/// Mutable projection of the four buckets of one position (REQ-02). The invariant is that this row
/// always equals the current allocation plus the net delta of every movement of the position; the
/// health check rebuilds it and degrades on divergence.
/// </summary>
public sealed class BudgetBalanceRecord
{
    public Guid PositionId { get; set; }
    public int BalanceVersion { get; set; }
    public int AllocationVersion { get; set; }
    public decimal Allocated { get; set; }
    public decimal Reserved { get; set; }
    public decimal Committed { get; set; }
    public decimal Consumed { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Immutable, all-or-nothing root of one Budget batch (REQ-03).</summary>
public sealed class BudgetOperationRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; } = 1;
    public Guid OrganizationId { get; set; }
    public int Kind { get; set; }
    public string OperationKey { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public string SourceType { get; set; } = null!;
    public Guid SourceId { get; set; }
    public int SourceVersion { get; set; }
    public string SourceDigest { get; set; } = null!;
    public string ActorJson { get; set; } = null!;
    public string? CauseStream { get; set; }
    public Guid? CauseAuditId { get; set; }
    public string ReasonCode { get; set; } = null!;
    public string Result { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationReference { get; set; } = null!;
}

/// <summary>
/// Append-only movement row (REQ-03). Every amount of every allocation version feeds the same
/// position balance; the row records the balance projection before and after the delta so a
/// rebuild can reconstruct each bucket exactly.
/// </summary>
public sealed class BudgetMovementRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; } = 1;
    public Guid OperationId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PositionId { get; set; }
    public int AllocationVersionAtPosting { get; set; }
    public int Type { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public Guid? ParentMovementId { get; set; }
    public Guid? TargetId { get; set; }
    public int? TargetVersion { get; set; }
    public string? TargetMaterialSnapshotDigest { get; set; }
    public string? TargetType { get; set; }
    public int BalanceVersionAfter { get; set; }
    public decimal AllocatedBefore { get; set; }
    public decimal ReservedBefore { get; set; }
    public decimal CommittedBefore { get; set; }
    public decimal ConsumedBefore { get; set; }
    public decimal AllocatedAfter { get; set; }
    public decimal ReservedAfter { get; set; }
    public decimal CommittedAfter { get; set; }
    public decimal ConsumedAfter { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public BudgetOperationRecord Operation { get; set; } = null!;
    public BudgetPositionRecord Position { get; set; } = null!;
}

/// <summary>
/// Durable attempt of the real <c>budget-check-owner/v1</c> processor (REQ-07). It carries the
/// four deterministic keys and every artifact id so a retry never posts a second reservation.
/// </summary>
public sealed class BudgetPrerequisiteAttemptRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CaseId { get; set; }
    public Guid PrerequisiteId { get; set; }
    public string PrerequisiteKey { get; set; } = null!;
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public string ParametersJson { get; set; } = null!;
    public string ParametersDigest { get; set; } = null!;
    public string SourceControlDigest { get; set; } = null!;
    public string RequestKey { get; set; } = null!;
    public string ReserveKey { get; set; } = null!;
    public string SignalKey { get; set; } = null!;
    public string CompensateKey { get; set; } = null!;
    public Guid? RequestOperationId { get; set; }
    public Guid? ReserveOperationId { get; set; }
    public Guid? ReverseOperationId { get; set; }
    public string State { get; set; } = null!;
    public string? SignalResult { get; set; }
    public string? EvidenceDigest { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int FencingToken { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Durable release requested before a Purchase Request cancellation is confirmed (REQ-08): the
/// command spans two local transactions, so the attempt survives a crash between them.
/// </summary>
public sealed class PurchaseRequestBudgetReleaseAttemptRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public int RequestVersion { get; set; }
    public Guid CaseId { get; set; }
    public string ReleaseKey { get; set; } = null!;
    public Guid? ReleaseOperationId { get; set; }
    public string State { get; set; } = null!;
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Registered workload allowed to post financial transitions (REQ-09). Productive registrations of
/// COMMIT, CONSUME and REVERSE only appear with the specs that own Purchase Orders and invoices.
/// </summary>
public sealed class BudgetMovementProducerRegistrationRecord
{
    public Guid Id { get; set; }
    public string Operation { get; set; } = null!;
    public string ContractVersion { get; set; } = null!;
    public string SourceType { get; set; } = null!;
    public string ProducerId { get; set; } = null!;
    public string WorkloadIssuer { get; set; } = null!;
    public string WorkloadClientId { get; set; } = null!;
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Registration of the real budget prerequisite processor (REQ-05, REQ-09): exactly one for
/// <c>budget-check-owner/v1</c>, and its workload must match the identity Approval persisted.
/// </summary>
public sealed class BudgetPrerequisiteProcessorRegistrationRecord
{
    public Guid Id { get; set; }
    public string AdapterId { get; set; } = null!;
    public string AdapterVersion { get; set; } = null!;
    public string ProcessorId { get; set; } = null!;
    public bool IsEnabled { get; set; }
}

/// <summary>Append-only audit row of the Budget module, atomic with its local mutation (REQ-10).</summary>
public sealed class BudgetAuditRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string ActorJson { get; set; } = null!;
    public string? CauseStream { get; set; }
    public Guid? CauseAuditId { get; set; }
    public string Action { get; set; } = null!;
    public string TargetType { get; set; } = null!;
    public Guid TargetId { get; set; }
    public int? PreviousVersion { get; set; }
    public int NewVersion { get; set; }
    public string DeltasJson { get; set; } = null!;
    public string CorrelationReference { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
}
