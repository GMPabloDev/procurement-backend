using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Budget;

/// <summary>
/// SPEC 08 CA-02: the delta table of the Budget ledger, the partial-transition limit of each parent
/// and the all-or-nothing invariants. Marked with the three vectors of the global reference case.
/// </summary>
public sealed class BudgetMovementCalculatorTests
{
    private static readonly Guid RequestedId = Guid.Parse("99999999-9999-9999-9999-999999999998");
    private static readonly Guid ReservedId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid CommittedId = Guid.Parse("99999999-9999-9999-9999-999999999997");
    private static readonly Guid ConsumedId = Guid.Parse("99999999-9999-9999-9999-999999999996");

    private static BudgetTarget Target => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        1,
        new string('1', 64),
        BudgetCodes.PurchaseRequestLineType);

    [Fact]
    public void Requested_records_demand_without_touching_the_buckets()
    {
        var before = BudgetBuckets.Create(1000m, 0m, 0m, 0m);

        var application = BudgetMovementCalculator.Apply(
            before, new BudgetMovementRequest(BudgetMovementType.Requested, 100m, null, Target), null);

        Assert.Equal(BudgetBuckets.Empty.With(allocated: 1000m), application.After);
        Assert.Equal(before, application.After);
        Assert.Equal(1000m, application.After.Available);
    }

    [Fact]
    public void Requested_is_allowed_even_when_the_position_has_no_allocation()
    {
        // A precheck must remain auditable: demand records what was asked, not what is affordable.
        var application = BudgetMovementCalculator.Apply(
            BudgetBuckets.Empty,
            new BudgetMovementRequest(BudgetMovementType.Requested, 250m, null, Target),
            null);

        Assert.Equal(BudgetBuckets.Empty, application.After);
    }

    [Fact]
    public void The_full_chain_produces_the_contractual_deltas()
    {
        var requested = new BudgetPostedMovement(RequestedId, BudgetMovementType.Requested, 100m, null, 0m, false);

        var reserved = BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reserved, 100m, RequestedId, Target),
            requested);
        Assert.Equal(100m, reserved.After.Reserved);
        Assert.Equal(900m, reserved.After.Available);

        var reservedPosted = new BudgetPostedMovement(
            ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 0m, false);
        var committed = BudgetMovementCalculator.Apply(
            reserved.After,
            new BudgetMovementRequest(BudgetMovementType.Committed, 100m, ReservedId, Target),
            reservedPosted);
        Assert.Equal(0m, committed.After.Reserved);
        Assert.Equal(100m, committed.After.Committed);

        var committedPosted = new BudgetPostedMovement(
            CommittedId, BudgetMovementType.Committed, 100m, ReservedId, 0m, false);
        var consumed = BudgetMovementCalculator.Apply(
            committed.After,
            new BudgetMovementRequest(BudgetMovementType.Consumed, 100m, CommittedId, Target),
            committedPosted);
        Assert.Equal(0m, consumed.After.Committed);
        Assert.Equal(100m, consumed.After.Consumed);
        Assert.Equal(900m, consumed.After.Available);
    }

    [Fact]
    public void A_partial_transition_is_limited_by_the_remaining_amount_of_its_parent()
    {
        var reserved = new BudgetPostedMovement(ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 60m, true);
        var before = BudgetBuckets.Create(1000m, 100m, 0m, 0m);

        var application = BudgetMovementCalculator.Apply(
            before,
            new BudgetMovementRequest(BudgetMovementType.Committed, 40m, ReservedId, Target),
            reserved);
        Assert.Equal(40m, application.After.Committed);
        Assert.Equal(60m, application.After.Reserved);

        Assert.Throws<DomainConflictException>(() => BudgetMovementCalculator.Apply(
            before,
            new BudgetMovementRequest(BudgetMovementType.Committed, 41m, ReservedId, Target),
            reserved));
    }

    [Fact]
    public void Reserved_requires_available_budget_and_cannot_overdraw()
    {
        var requested = new BudgetPostedMovement(RequestedId, BudgetMovementType.Requested, 250m, null, 0m, false);

        Assert.Throws<DomainConflictException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(100m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reserved, 250m, RequestedId, Target),
            requested));
    }

    [Fact]
    public void Reserved_requires_a_requested_parent()
    {
        var committedParent = new BudgetPostedMovement(
            CommittedId, BudgetMovementType.Committed, 100m, ReservedId, 0m, false);

        Assert.Throws<DomainValidationException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reserved, 100m, CommittedId, Target),
            committedParent));
        Assert.Throws<DomainValidationException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reserved, 100m, null, Target),
            null));
    }

    [Fact]
    public void Reverse_of_a_requested_movement_only_cancels_the_demand()
    {
        var requested = new BudgetPostedMovement(RequestedId, BudgetMovementType.Requested, 100m, null, 0m, false);

        var application = BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, RequestedId, Target),
            requested);

        Assert.Equal(BudgetBuckets.Create(1000m, 0m, 0m, 0m), application.After);
    }

    [Fact]
    public void Reverse_of_a_reserved_movement_releases_the_hold()
    {
        var reserved = new BudgetPostedMovement(ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 0m, false);

        var application = BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 100m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, ReservedId, Target),
            reserved);

        Assert.Equal(0m, application.After.Reserved);
        Assert.Equal(1000m, application.After.Available);
    }

    [Fact]
    public void Reverse_of_a_committed_movement_returns_the_funds_to_reserved()
    {
        var committed = new BudgetPostedMovement(
            CommittedId, BudgetMovementType.Committed, 100m, ReservedId, 0m, false);

        var application = BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 100m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, CommittedId, Target),
            committed);

        Assert.Equal(100m, application.After.Reserved);
        Assert.Equal(0m, application.After.Committed);
    }

    [Fact]
    public void Reverse_of_a_consumed_movement_returns_the_funds_to_committed()
    {
        var consumed = new BudgetPostedMovement(
            ConsumedId, BudgetMovementType.Consumed, 100m, CommittedId, 0m, false);

        var application = BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 100m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, ConsumedId, Target),
            consumed);

        Assert.Equal(100m, application.After.Committed);
        Assert.Equal(0m, application.After.Consumed);
    }

    [Fact]
    public void A_parent_cannot_be_advanced_by_more_than_its_amount()
    {
        var reserved = new BudgetPostedMovement(ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 100m, true);

        Assert.Throws<DomainConflictException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 1m, ReservedId, Target),
            reserved));
    }

    [Fact]
    public void A_reverse_movement_cannot_be_reversed_again()
    {
        var reverse = new BudgetPostedMovement(
            Guid.NewGuid(), BudgetMovementType.Reverse, 100m, ReservedId, 0m, false);

        Assert.Throws<DomainValidationException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 0m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, reverse.Id, Target),
            reverse));
    }

    [Fact]
    public void A_conflicting_later_state_is_rejected_instead_of_going_negative()
    {
        // The reservation was already committed by another operation: its own reverse cannot pull
        // more from RESERVED than what is still held there.
        var reserved = new BudgetPostedMovement(ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 0m, false);

        Assert.Throws<DomainConflictException>(() => BudgetMovementCalculator.Apply(
            BudgetBuckets.Create(1000m, 0m, 100m, 0m),
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, ReservedId, Target),
            reserved));
    }

    [Fact]
    public void A_projection_never_goes_below_zero_available()
    {
        var before = BudgetBuckets.Create(100m, 100m, 0m, 0m);

        Assert.Equal(0m, before.Available);
        before.EnsureNonNegative();
        Assert.Throws<DomainConflictException>(() => BudgetBuckets.Create(100m, 101m, 0m, 0m).EnsureNonNegative());
    }

    [Fact]
    public void Amounts_must_be_positive_and_scaled()
    {
        Assert.Throws<DomainValidationException>(() => new BudgetMovementRequest(
            BudgetMovementType.Requested, 0m, null, Target));
        Assert.Throws<DomainValidationException>(() => new BudgetMovementRequest(
            BudgetMovementType.Requested, -1m, null, Target));
        Assert.Throws<DomainValidationException>(() => new BudgetMovementRequest(
            BudgetMovementType.Requested, 0.0000000000001m, null, Target));
        Assert.Equal(
            0.000000000001m,
            new BudgetMovementRequest(BudgetMovementType.Requested, 0.000000000001m, null, Target).Amount);
    }

    [Fact]
    public void Buckets_reject_negative_amounts_and_rebuild_from_movements()
    {
        Assert.Throws<DomainValidationException>(() => BudgetBuckets.Create(100m, -1m, 0m, 0m));

        // Reconstruction equals the allocation plus the net delta of every movement of the
        // position, regardless of the allocation version each row was posted under (REQ-02).
        var before = BudgetBuckets.Create(1000m, 0m, 0m, 0m);
        var reserved = BudgetMovementCalculator.Apply(
            before,
            new BudgetMovementRequest(
                BudgetMovementType.Reserved,
                100m,
                RequestedId,
                Target),
            new BudgetPostedMovement(RequestedId, BudgetMovementType.Requested, 100m, null, 0m, false));
        var committed = BudgetMovementCalculator.Apply(
            reserved.After,
            new BudgetMovementRequest(BudgetMovementType.Committed, 100m, ReservedId, Target),
            new BudgetPostedMovement(ReservedId, BudgetMovementType.Reserved, 100m, RequestedId, 0m, false));
        var released = BudgetMovementCalculator.Apply(
            committed.After,
            new BudgetMovementRequest(BudgetMovementType.Reverse, 100m, CommittedId, Target),
            new BudgetPostedMovement(CommittedId, BudgetMovementType.Committed, 100m, ReservedId, 0m, false));

        Assert.Equal(1000m, released.After.Allocated);
        Assert.Equal(100m, released.After.Reserved);
        Assert.Equal(0m, released.After.Committed);
        Assert.Equal(0m, released.After.Consumed);
        Assert.Equal(900m, released.After.Available);
    }
}
