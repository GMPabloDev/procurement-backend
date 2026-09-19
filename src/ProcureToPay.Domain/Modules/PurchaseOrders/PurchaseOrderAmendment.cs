using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>One line delta of an amendment (<c>amendment-line-delta/v1</c>, REQ-05).</summary>
public sealed record AmendmentLineDelta
{
    public const string ContractVersion = PurchaseOrderCodes.AmendmentLineDeltaContract;

    private AmendmentLineDelta(
        string changeKind,
        PurchaseOrderContentRef lineRef,
        PurchaseOrderLine previous,
        PurchaseOrderLine? replacement)
    {
        ChangeKind = changeKind;
        LineRef = lineRef;
        PreviousLine = previous ?? throw new DomainValidationException("A delta requires its previous line.");
        ReplacementLine = replacement;
        if (changeKind == PurchaseOrderCodes.ChangeCancel && replacement is not null)
        {
            throw new DomainValidationException("A cancelled line carries no replacement document.");
        }

        if (changeKind != PurchaseOrderCodes.ChangeCancel && replacement is null)
        {
            throw new DomainValidationException("A reduced or commercial delta requires its replacement line.");
        }
    }

    public string ChangeKind { get; }
    public PurchaseOrderContentRef LineRef { get; }
    public PurchaseOrderLine PreviousLine { get; }
    public PurchaseOrderLine? ReplacementLine { get; }

    /// <summary>A reduction of the quantity or the amount of one line (REQ-05).</summary>
    public static AmendmentLineDelta Reduce(PurchaseOrderLine previous, PurchaseOrderLine replacement) =>
        new(PurchaseOrderCodes.ChangeReduce, previous.RequestLineRef, previous, replacement);

    /// <summary>A full cancellation of one line (REQ-05).</summary>
    public static AmendmentLineDelta Cancel(PurchaseOrderLine previous) =>
        new(PurchaseOrderCodes.ChangeCancel, previous.RequestLineRef, previous, null);

    /// <summary>
    /// A commercial change that grows the order or alters its terms; REQ-05 requires a successor award
    /// for it, which the service validates against the published award lines.
    /// </summary>
    public static AmendmentLineDelta Commercial(PurchaseOrderLine previous, PurchaseOrderLine replacement) =>
        new(PurchaseOrderCodes.ChangeCommercial, previous.RequestLineRef, previous, replacement);
}

/// <summary>
/// One Acceptance Responsibility replacement of an amendment (<c>responsibility-change/v1</c>, REQ-05).
/// </summary>
public sealed record ResponsibilityChange(
    string Kind,
    PurchaseOrderContentRef LineRef,
    AcceptanceResponsibility Previous,
    AcceptanceResponsibility Replacement)
{
    public const string ContractVersion = PurchaseOrderCodes.ResponsibilityChangeContract;

    public string Kind { get; } = PurchaseOrderCodes.Code(Kind, "Responsibility kind");
}

/// <summary>
/// One Purchase Order amendment version (<c>purchase-order-amendment/v1</c>, REQ-05). Every state
/// transition appends a successor version, so the amendment lineage is append-only exactly like the
/// Purchase Order itself.
/// </summary>
public sealed record PurchaseOrderAmendmentVersion
{
    public const string ContractVersion = PurchaseOrderCodes.AmendmentContract;

    public PurchaseOrderAmendmentVersion(
        Guid amendmentId,
        int version,
        int? predecessorVersion,
        PurchaseOrderContentRef basePoRef,
        int expectedPoVersion,
        AmendmentState state,
        IEnumerable<AmendmentLineDelta> lineDeltas,
        IEnumerable<ResponsibilityChange> responsibilityChanges,
        DeliveryCommitment? replacementDelivery,
        PurchaseOrderContentRef? successorAwardRef,
        IEnumerable<PurchaseOrderContentRef>? evidenceRefs,
        PurchaseOrderContentRef? approvalRef,
        Guid organizationId,
        string reason,
        Guid actorUserId,
        DateTimeOffset occurredAt)
    {
        AmendmentId = amendmentId == Guid.Empty
            ? throw new DomainValidationException("An amendment requires its identity.")
            : amendmentId;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("An amendment version must be positive.");
        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first amendment version has no predecessor.");
        }

        PredecessorVersion = predecessorVersion;
        BasePoRef = basePoRef ?? throw new DomainValidationException("An amendment requires its base order.");
        ExpectedPoVersion = expectedPoVersion >= 1
            ? expectedPoVersion
            : throw new DomainValidationException("An amendment requires the expected order version.");
        State = state;
        LineDeltas = (lineDeltas ?? [])
            .OrderBy(delta => delta.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        ResponsibilityChanges = (responsibilityChanges ?? [])
            .OrderBy(change => change.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ToImmutableArray();
        ReplacementDelivery = replacementDelivery;
        SuccessorAwardRef = successorAwardRef;
        EvidenceRefs = (evidenceRefs ?? [])
            .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        ApprovalRef = approvalRef;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("An amendment requires its organization.")
            : organizationId;
        Reason = PurchaseOrderCodes.Reason(reason);
        ActorUserId = actorUserId == Guid.Empty
            ? throw new DomainValidationException("An amendment requires its actor.")
            : actorUserId;
        OccurredAt = occurredAt.ToUniversalTime();

        if (LineDeltas.Select(delta => delta.LineRef.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() !=
            LineDeltas.Count)
        {
            throw new DomainConflictException("An amendment cannot carry two deltas of the same line.");
        }

        var commercial = LineDeltas.Any(delta =>
            string.Equals(delta.ChangeKind, PurchaseOrderCodes.ChangeCommercial, StringComparison.Ordinal));
        if (commercial && successorAwardRef is null)
        {
            throw new PurchaseOrderUnprocessableException(
                "A commercial change requires a successor award of SPEC 10.");
        }

        if (!commercial && successorAwardRef is not null && LineDeltas.Count != 0)
        {
            throw new DomainValidationException(
                "Only a commercial change carries a successor award reference.");
        }

        if (LineDeltas.Count == 0 && ResponsibilityChanges.Count == 0 && ReplacementDelivery is null &&
            successorAwardRef is null)
        {
            throw new DomainValidationException("An amendment must change something.");
        }

        if (!Enum.IsDefined(State))
        {
            throw new DomainValidationException("An amendment state is not recognized.");
        }

        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid AmendmentId { get; }
    public int Version { get; }
    public int? PredecessorVersion { get; }
    public PurchaseOrderContentRef BasePoRef { get; }
    public int ExpectedPoVersion { get; }
    public AmendmentState State { get; }
    public IReadOnlyList<AmendmentLineDelta> LineDeltas { get; }
    public IReadOnlyList<ResponsibilityChange> ResponsibilityChanges { get; }
    public DeliveryCommitment? ReplacementDelivery { get; }
    public PurchaseOrderContentRef? SuccessorAwardRef { get; }
    public IReadOnlyList<PurchaseOrderContentRef> EvidenceRefs { get; }
    public PurchaseOrderContentRef? ApprovalRef { get; }
    public Guid OrganizationId { get; }
    public string Reason { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset OccurredAt { get; }

    /// <summary><c>purchase_order_amendment_digest</c> of the published preimage (REQ-05).</summary>
    public string Digest { get; }

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.AmendmentDocument(this);

    /// <summary>A successor of this amendment under another state, keeping every published reference.</summary>
    public PurchaseOrderAmendmentVersion With(
        AmendmentState state,
        PurchaseOrderContentRef? approvalRef,
        Guid actorUserId,
        DateTimeOffset occurredAt) =>
        new(
            AmendmentId,
            Version + 1,
            Version,
            BasePoRef,
            ExpectedPoVersion,
            state,
            LineDeltas,
            ResponsibilityChanges,
            ReplacementDelivery,
            SuccessorAwardRef,
            EvidenceRefs,
            approvalRef ?? ApprovalRef,
            OrganizationId,
            Reason,
            actorUserId,
            occurredAt);

    /// <summary>
    /// REQ-05: the amendment never changes the legal entity, the supplier or the currency of the order,
    /// and it derives its source/base deltas by subtracting the previous and replacement documents.
    /// </summary>
    public (decimal SourceDelta, decimal BaseDelta) ComputeDeltas(
        IReadOnlyDictionary<string, PurchaseOrderLine> previousLines)
    {
        ArgumentNullException.ThrowIfNull(previousLines);
        var sourceDelta = 0m;
        var baseDelta = 0m;
        foreach (var delta in LineDeltas)
        {
            var previous = previousLines.TryGetValue(delta.LineRef.CanonicalIdentity, out var found)
                ? found
                : throw new DomainConflictException("A delta references a line the order does not carry.");
            var replacement = delta.ReplacementLine;
            sourceDelta += PurchaseOrderCodes.Decimal12((replacement?.GrossTotal ?? 0m) - previous.GrossTotal);
            baseDelta += PurchaseOrderCodes.Decimal12((replacement?.BaseGrossTotal ?? 0m) - previous.BaseGrossTotal);
        }

        return (sourceDelta, baseDelta);
    }
}
