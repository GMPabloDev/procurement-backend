using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>
/// Request of the exact <c>award-consumption-claim/v1</c> contract (REQ-01). The caller only
/// supplies identities: the award reference, the claim key, the covered lines, the new PO id and its
/// own workload. Supplier, prices, quantities, currencies, terms, snapshots and Policy/Approval
/// references are never accepted from the caller.
/// </summary>
public sealed record AwardConsumptionClaimRequest(
    Guid OrganizationId,
    Guid PoId,
    PurchaseOrderContentRef AwardRef,
    IReadOnlyList<PurchaseOrderContentRef> CoveredLines,
    string ClaimKey,
    DateTimeOffset RequestedAt,
    string WorkloadIssuer,
    string WorkloadClientId)
{
    public string ClaimKey { get; } = PurchaseOrderCodes.Key(ClaimKey, "claim_key");

    public IReadOnlyList<PurchaseOrderContentRef> CoveredLines { get; } = CanonicalCoveredLines(CoveredLines);

    /// <summary><c>award_claim_fingerprint</c> of the released preimage (REQ-01).</summary>
    public string Fingerprint => PurchaseOrderFingerprints.AwardClaimFingerprint(this);

    private static IReadOnlyList<PurchaseOrderContentRef> CanonicalCoveredLines(
        IReadOnlyList<PurchaseOrderContentRef>? lines)
    {
        var covered = (lines ?? []).ToImmutableArray();
        if (covered.Length == 0)
        {
            throw new DomainValidationException("A claim requires at least one covered line.");
        }

        if (covered.Select(line => line.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() != covered.Length)
        {
            throw new DomainConflictException("A claim cannot repeat a covered line version.");
        }

        return covered.OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal).ToImmutableArray();
    }
}

/// <summary>
/// Response of the exact <c>award-consumption-claim/v1</c> contract (REQ-01): the verified award
/// snapshot, the claim reference, the created PO reference and the takeover set that covers exactly
/// the claimed lines. A replay of the same preimage returns the recorded outcome.
/// </summary>
public sealed record AwardConsumptionClaimResponse(
    string ContractVersion,
    PurchaseOrderContentRef ClaimRef,
    AwardConsumptionResponse AwardSnapshot,
    PurchaseOrderContentRef PoRef,
    IReadOnlyList<PurchaseOrderContentRef> TakeoverRefs,
    DateTimeOffset ClaimedAt,
    bool Replayed);

/// <summary>
/// Request of the exact <c>award-recovery/v1</c> contract (REQ-01): the Buyer recovers an unclaimed
/// award that stopped being consumable. Recovery never skips a new Policy/Approval and never acts on
/// an existing claim.
/// </summary>
public sealed record AwardRecoveryCommand(
    Guid OrganizationId,
    PurchaseOrderContentRef AwardRef,
    int ExpectedProcessVersion,
    int ExpectedAwardVersion,
    string Action,
    string RecoveryKey,
    string Reason)
{
    public const string ActionReopen = "REOPEN";
    public const string ActionCancel = "CANCEL";

    public string Action { get; } = Action is ActionReopen or ActionCancel
        ? Action
        : throw new DomainValidationException("The award recovery action must be REOPEN or CANCEL.");

    public string RecoveryKey { get; } = PurchaseOrderCodes.Key(RecoveryKey, "recovery_key");

    public string Reason { get; } = PurchaseOrderCodes.Reason(Reason);
}

/// <summary>Response of one award recovery.</summary>
public sealed record AwardRecoveryResponse(
    PurchaseOrderContentRef AwardRef,
    string Action,
    int ProcessVersion,
    string ProcessState,
    IReadOnlyList<PurchaseOrderContentRef> ReleasedTakeovers,
    bool Replayed);

/// <summary>
/// One line takeover (<c>purchase-request-line-takeover/v1</c>, REQ-10). It replaces the request-wide
/// lock of SPEC 10 for new operations: at most one active takeover exists per request line version,
/// and the consumer is identified by its own reference.
/// </summary>
public sealed record PurchaseRequestLineTakeover
{
    public const string ContractVersion = PurchaseOrderCodes.TakeoverContract;

    public PurchaseRequestLineTakeover(
        Guid organizationId,
        PurchaseOrderContentRef requestRef,
        PurchaseOrderContentRef lineRef,
        PurchaseRequestLineOwner owner,
        PurchaseOrderContentRef consumerRef,
        PurchaseOrderContentRef? predecessorRef,
        TakeoverState state,
        int version,
        DateTimeOffset occurredAt,
        Guid actorUserId)
    {
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("A takeover requires its organization.")
            : organizationId;
        RequestRef = requestRef ?? throw new DomainValidationException("A takeover requires its request.");
        LineRef = lineRef ?? throw new DomainValidationException("A takeover requires its line.");
        Owner = owner;
        ConsumerRef = consumerRef ?? throw new DomainValidationException("A takeover requires its consumer.");
        PredecessorRef = predecessorRef;
        State = state;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A takeover version must be positive.");
        OccurredAt = occurredAt.ToUniversalTime();
        ActorUserId = actorUserId == Guid.Empty
            ? throw new DomainValidationException("A takeover requires its actor.")
            : actorUserId;
        if (state == TakeoverState.Active && predecessorRef is null && version != 1)
        {
            throw new DomainValidationException("Only the first takeover of a line has no predecessor.");
        }
    }

    public Guid OrganizationId { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public PurchaseOrderContentRef LineRef { get; }
    public PurchaseRequestLineOwner Owner { get; }
    public PurchaseOrderContentRef ConsumerRef { get; }
    public PurchaseOrderContentRef? PredecessorRef { get; }
    public TakeoverState State { get; }
    public int Version { get; }
    public DateTimeOffset OccurredAt { get; }
    public Guid ActorUserId { get; }

    public string OwnerCode => PurchaseOrderCodes.OwnerOf(Owner);

    /// <summary>Identity of one takeover: the line version plus its owner.</summary>
    public string CanonicalIdentity => $"{LineRef.CanonicalIdentity}:{OwnerCode}";

    public string Digest => PurchaseOrderCanonicalizer.Hash(PurchaseOrderCanonicalizer.TakeoverDocument(this));
}

/// <summary>
/// Projection published by this module on the Purchase Request line (REQ-10): <c>ORDERED</c> for an
/// issued Purchase Order line and <c>DIRECT_PURCHASE_AUTHORIZED</c> for a direct purchase.
/// </summary>
public sealed record PurchaseRequestLineProjectionRecord(
    PurchaseRequestLineProjection Projection,
    PurchaseOrderContentRef ConsumerRef,
    DateTimeOffset OccurredAt)
{
    public string ProjectionCode => Projection switch
    {
        PurchaseRequestLineProjection.Ordered => "ORDERED",
        PurchaseRequestLineProjection.DirectPurchaseAuthorized => "DIRECT_PURCHASE_AUTHORIZED",
        _ => throw new DomainValidationException("The line projection is not recognized.")
    };
}
