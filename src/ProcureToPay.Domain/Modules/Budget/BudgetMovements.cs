using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>Closed movement vocabulary of the Budget ledger (REQ-03).</summary>
public enum BudgetMovementType
{
    Requested = 1,
    Reserved = 2,
    Committed = 3,
    Consumed = 4,
    Reverse = 5
}

/// <summary>Kind of the immutable operation that groups one all-or-nothing batch (REQ-03).</summary>
public enum BudgetOperationKind
{
    Precheck = 1,
    ApprovalReserve = 2,
    TransferReserve = 3,
    Commit = 4,
    Consume = 5,
    Reverse = 6,
    Release = 7
}

/// <summary>Terminal or intermediate result of one attempt (REQ-07).</summary>
public enum BudgetAttemptState
{
    Pending = 1,
    Processing = 2,
    Reserved = 3,
    Insufficient = 4,
    Signalling = 5,
    Completed = 6,
    Compensating = 7,
    Compensated = 8
}

/// <summary>Contract code of one attempt state; the wire never publishes the CLR enum name (REQ-07).</summary>
public static class BudgetAttemptStateCodes
{
    public static string Of(BudgetAttemptState state) => state switch
    {
        BudgetAttemptState.Pending => "PENDING",
        BudgetAttemptState.Processing => "PROCESSING",
        BudgetAttemptState.Reserved => "RESERVED",
        BudgetAttemptState.Insufficient => "INSUFFICIENT",
        BudgetAttemptState.Signalling => "SIGNALLING",
        BudgetAttemptState.Completed => "COMPLETED",
        BudgetAttemptState.Compensating => "COMPENSATING",
        BudgetAttemptState.Compensated => "COMPENSATED",
        _ => throw new DomainValidationException("The budget attempt state is not recognized.")
    };
}

/// <summary>State of the durable release requested by a Purchase Request cancellation (REQ-08).</summary>
public enum BudgetReleaseAttemptState
{
    Pending = 1,
    Released = 2,
    Completed = 3
}

/// <summary>Result of one prerequisite check (REQ-06) and of the precheck response (REQ-04).</summary>
public enum BudgetCheckResult
{
    Available = 1,
    Insufficient = 2,
    Unfunded = 3
}

/// <summary>Contract code of one release attempt state (REQ-08).</summary>
public static class BudgetReleaseAttemptStateCodes
{
    public static string Of(BudgetReleaseAttemptState state) => state switch
    {
        BudgetReleaseAttemptState.Pending => "PENDING",
        BudgetReleaseAttemptState.Released => "RELEASED",
        BudgetReleaseAttemptState.Completed => "COMPLETED",
        _ => throw new DomainValidationException("The budget release state is not recognized.")
    };
}

/// <summary>Trigger of a durable release (REQ-08).</summary>
public enum BudgetReleaseTrigger
{
    ApprovalResult = 1,
    ApprovalSuperseded = 2,
    PurchaseRequestCancelled = 3
}

/// <summary>The four contractual buckets of one position (REQ-02).</summary>
public sealed record BudgetBuckets
{
    private BudgetBuckets(decimal allocated, decimal reserved, decimal committed, decimal consumed)
    {
        Allocated = allocated;
        Reserved = reserved;
        Committed = committed;
        Consumed = consumed;
    }

    public static readonly BudgetBuckets Empty = new(0m, 0m, 0m, 0m);

    public decimal Allocated { get; }

    public decimal Reserved { get; }

    public decimal Committed { get; }

    public decimal Consumed { get; }

    /// <summary>Builds a projection, rejecting negative or out-of-scale amounts (REQ-02).</summary>
    public static BudgetBuckets Create(decimal allocated, decimal reserved, decimal committed, decimal consumed) =>
        new(
            BudgetCodes.RequireAmount(allocated, "Allocated"),
            BudgetCodes.RequireAmount(reserved, "Reserved"),
            BudgetCodes.RequireAmount(committed, "Committed"),
            BudgetCodes.RequireAmount(consumed, "Consumed"));

    /// <summary>Builds a successor projection keeping the buckets that do not change.</summary>
    public BudgetBuckets With(decimal? allocated = null, decimal? reserved = null, decimal? committed = null, decimal? consumed = null)
    {
        // A projection that would go negative is a conflict with the movements already posted, not
        // a schema violation: the caller tried to advance a chain that a later transition moved on.
        foreach (var (field, value) in new (string Field, decimal Value)[]
                 {
                     ("Reserved", reserved ?? Reserved),
                     ("Committed", committed ?? Committed),
                     ("Consumed", consumed ?? Consumed)
                 })
        {
            if (value < 0m)
            {
                throw new DomainConflictException(
                    $"The position was already advanced by a later transition ({field} would go negative).");
            }
        }

        return Create(
            allocated ?? Allocated,
            reserved ?? Reserved,
            committed ?? Committed,
            consumed ?? Consumed);
    }

    /// <summary><c>AVAILABLE = ALLOCATED - RESERVED - COMMITTED - CONSUMED</c> (REQ-02).</summary>
    public decimal Available => Allocated - Reserved - Committed - Consumed;

    public void EnsureNonNegative()
    {
        if (Available < 0m)
        {
            throw new DomainConflictException("The position has no available budget.");
        }
    }
}

/// <summary>
/// One movement as declared by a command: type, amount, optional parent and optional Approval
/// target (REQ-03).
/// </summary>
public sealed record BudgetMovementRequest(BudgetMovementType Type, decimal Amount, Guid? ParentMovementId, BudgetTarget? Target)
{
    public decimal Amount { get; } = BudgetCodes.RequirePositiveAmount(Amount, "Movement amount");

    public Guid? ParentMovementId { get; } =
        ParentMovementId == Guid.Empty
            ? throw new DomainValidationException("A parent movement id cannot be empty.")
            : ParentMovementId;
}

/// <summary>Already posted movement, as the ledger needs it to validate a child (REQ-03).</summary>
public sealed record BudgetPostedMovement(
    Guid Id,
    BudgetMovementType Type,
    decimal Amount,
    Guid? ParentMovementId,
    decimal ChildrenAmount,
    bool HasChildren)
{
    /// <summary>Remaining amount of this movement that no child has consumed yet.</summary>
    public decimal Remaining => Amount - ChildrenAmount;

    /// <summary>
    /// A reverse may only revert an outward movement; reverting a REVERSE would let a caller
    /// "un-release" budget and bypass the append-only ledger.
    /// </summary>
    public bool IsReversible => Type != BudgetMovementType.Reverse;

    /// <summary>
    /// Demand movements do not advance the chain: they only record intent, so they never become a
    /// parent of COMMITTED or CONSUMED.
    /// </summary>
    public bool AdvancesChain => Type is BudgetMovementType.Reserved
        or BudgetMovementType.Committed
        or BudgetMovementType.Consumed;
}

/// <summary>Result of applying one movement: the new buckets and the posted row (REQ-02, REQ-03).</summary>
public sealed record BudgetMovementApplication(
    BudgetBuckets Before,
    BudgetBuckets After,
    BudgetMovementType Type,
    decimal Amount);

/// <summary>
/// Pure ledger rules of one Budget position (SPEC 08 REQ-02, REQ-03). The calculator decides the
/// bucket deltas, validates the invariants and returns the projection to persist; persistence and
/// locking stay in the infrastructure layer.
///
/// Rules applied:
/// <list type="bullet">
/// <item><c>REQUESTED</c> records demand, needs no parent and changes no bucket.</item>
/// <item><c>RESERVED</c> takes funds from AVAILABLE and requires a REQUESTED parent with enough
/// remaining amount.</item>
/// <item><c>COMMITTED</c> moves funds from RESERVED to COMMITTED with a RESERVED parent.</item>
/// <item><c>CONSUMED</c> moves funds from COMMITTED to CONSUMED with a COMMITTED parent.</item>
/// <item><c>REVERSE</c> inverts one still-open parent; children of a parent can never exceed its
/// amount, and the buckets never go negative.</item>
/// </list>
/// </summary>
public static class BudgetMovementCalculator
{
    public static BudgetMovementApplication Apply(
        BudgetBuckets before,
        BudgetMovementRequest request,
        BudgetPostedMovement? parent)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(request);
        before.EnsureNonNegative();
        var amount = request.Amount;
        var after = request.Type switch
        {
            BudgetMovementType.Requested => Requested(before, request, parent),
            BudgetMovementType.Reserved => Reserved(before, amount, parent),
            BudgetMovementType.Committed => Committed(before, amount, parent),
            BudgetMovementType.Consumed => Consumed(before, amount, parent),
            BudgetMovementType.Reverse => Reversed(before, amount, parent),
            _ => throw new DomainValidationException("The budget movement type is not recognized.")
        };
        after.EnsureNonNegative();
        return new BudgetMovementApplication(before, after, request.Type, amount);
    }

    /// <summary>
    /// Demand only records how much was requested and which Approval target it covers; it never
    /// depends on availability so a precheck can always be audited (REQ-04).
    /// </summary>
    private static BudgetBuckets Requested(
        BudgetBuckets before,
        BudgetMovementRequest request,
        BudgetPostedMovement? parent)
    {
        if (parent is not null)
        {
            throw new DomainValidationException("A REQUESTED movement cannot have a parent.");
        }

        _ = request;
        return before;
    }

    private static BudgetBuckets Reserved(BudgetBuckets before, decimal amount, BudgetPostedMovement? parent)
    {
        RequireParent(parent, "RESERVED", BudgetMovementType.Requested);
        if (before.Available < amount)
        {
            throw new DomainConflictException("The position does not have enough available budget to reserve.");
        }

        return before.With(reserved: before.Reserved + amount);
    }

    private static BudgetBuckets Committed(BudgetBuckets before, decimal amount, BudgetPostedMovement? parent)
    {
        RequireParent(parent, "COMMITTED", BudgetMovementType.Reserved);
        RequireRemainder(parent!, amount, "reserve");
        if (before.Reserved < amount)
        {
            throw new DomainConflictException("The position does not have enough reserved budget to commit.");
        }

        return before.With(
            reserved: before.Reserved - amount,
            committed: before.Committed + amount);
    }

    private static BudgetBuckets Consumed(BudgetBuckets before, decimal amount, BudgetPostedMovement? parent)
    {
        RequireParent(parent, "CONSUMED", BudgetMovementType.Committed);
        RequireRemainder(parent!, amount, "committed obligation");
        if (before.Committed < amount)
        {
            throw new DomainConflictException("The position does not have enough committed budget to consume.");
        }

        return before.With(
            committed: before.Committed - amount,
            consumed: before.Consumed + amount);
    }

    /// <summary>
    /// REVERSE applies the inverse of its parent delta: a reversed CONSUMED returns to COMMITTED, a
    /// reversed COMMITTED returns to RESERVED, a reversed RESERVED returns to AVAILABLE and a
    /// reversed REQUESTED only cancels demand (REQ-03).
    /// </summary>
    private static BudgetBuckets Reversed(BudgetBuckets before, decimal amount, BudgetPostedMovement? parent)
    {
        if (parent is null)
        {
            throw new DomainValidationException("A REVERSE movement must reference its parent movement.");
        }

        if (!parent.IsReversible)
        {
            throw new DomainValidationException("A REVERSE movement cannot revert another REVERSE movement.");
        }

        RequireRemainder(parent, amount, "reversible");
        var after = parent.Type switch
        {
            BudgetMovementType.Requested => before,
            BudgetMovementType.Reserved => before.With(reserved: before.Reserved - amount),
            BudgetMovementType.Committed => before.With(
                reserved: before.Reserved + amount,
                committed: before.Committed - amount),
            BudgetMovementType.Consumed => before.With(
                committed: before.Committed + amount,
                consumed: before.Consumed - amount),
            _ => throw new DomainValidationException("The parent movement type cannot be reversed.")
        };
        if (after.Reserved < 0m || after.Committed < 0m || after.Consumed < 0m)
        {
            throw new DomainConflictException("The parent movement was already advanced by a later transition.");
        }

        return after;
    }

    private static void RequireParent(
        BudgetPostedMovement? parent,
        string transition,
        BudgetMovementType required)
    {
        if (parent is null)
        {
            throw new DomainValidationException($"A {transition} movement must reference its parent movement.");
        }

        if (parent.Type != required)
        {
            throw new DomainValidationException(
                $"A {transition} movement must reference a {required.ToString().ToUpperInvariant()} movement.");
        }
    }

    private static void RequireRemainder(BudgetPostedMovement parent, decimal amount, string concept)
    {
        if (amount > parent.Remaining)
        {
            throw new DomainConflictException(
                $"The {concept} movement does not have enough remaining amount for this transition.");
        }
    }
}
