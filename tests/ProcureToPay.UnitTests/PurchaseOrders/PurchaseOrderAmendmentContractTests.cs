using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseOrders;

/// <summary>
/// Contract tests of the amendment domain (SPEC 11 CA-06): the delta reuses complete
/// <c>purchase-order-line/v1</c> documents, subtracts source/base amounts deterministically, a
/// commercial change requires a successor award, and the published document is reproducible and
/// permutation stable.
/// </summary>
public sealed class PurchaseOrderAmendmentContractTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid PoId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid AmendmentId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid AwardId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid LineId = Guid.Parse("33333333-9999-9999-9999-999999999999");
    private static readonly Guid SecondLineId = Guid.Parse("44444444-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ActorId = Guid.Parse("55555555-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_reduction_subtracts_complete_documents_at_twelve_decimals()
    {
        var previous = Line(LineId, quantity: 3m, unitPrice: 100m);
        var replacement = Line(LineId, quantity: 1m, unitPrice: 100m);
        var amendment = Amendment(
            [AmendmentLineDelta.Reduce(previous, replacement)],
            successorAwardRef: null);
        var lines = new Dictionary<string, PurchaseOrderLine>(StringComparer.Ordinal)
        {
            [previous.CanonicalIdentity] = previous
        };

        var (sourceDelta, baseDelta) = amendment.ComputeDeltas(lines);

        Assert.Equal(-200m, sourceDelta);
        Assert.Equal(-200m, baseDelta);
    }

    [Fact]
    public void A_cancellation_of_one_line_subtracts_its_whole_amount()
    {
        var previous = Line(LineId, quantity: 2m, unitPrice: 10m);
        var amendment = Amendment([AmendmentLineDelta.Cancel(previous)], successorAwardRef: null);

        var (sourceDelta, baseDelta) = amendment.ComputeDeltas(
            new Dictionary<string, PurchaseOrderLine>(StringComparer.Ordinal)
            {
                [previous.CanonicalIdentity] = previous
            });

        Assert.Equal(-20m, sourceDelta);
        Assert.Equal(-20m, baseDelta);
    }

    [Fact]
    public void A_commercial_change_without_a_successor_award_is_rejected()
    {
        var previous = Line(LineId, quantity: 2m, unitPrice: 10m);
        var replacement = Line(LineId, quantity: 4m, unitPrice: 10m);

        var exception = Assert.Throws<PurchaseOrderUnprocessableException>(() => Amendment(
            [AmendmentLineDelta.Commercial(previous, replacement)],
            successorAwardRef: null));

        Assert.Contains("successor award", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cancelled_delta_carries_no_replacement_document()
    {
        var previous = Line(LineId, quantity: 2m, unitPrice: 10m);

        var delta = AmendmentLineDelta.Cancel(previous);

        Assert.Equal(PurchaseOrderCodes.ChangeCancel, delta.ChangeKind);
        Assert.Null(delta.ReplacementLine);
        Assert.Same(previous, delta.PreviousLine);
    }

    [Fact]
    public void An_amendment_cannot_carry_two_deltas_of_the_same_line()
    {
        var previous = Line(LineId, quantity: 2m, unitPrice: 10m);
        var replacement = Line(LineId, quantity: 1m, unitPrice: 10m);

        Assert.Throws<DomainConflictException>(() => Amendment(
            [
                AmendmentLineDelta.Reduce(previous, replacement),
                AmendmentLineDelta.Reduce(previous, replacement)
            ],
            successorAwardRef: null));
    }

    [Fact]
    public void An_amendment_without_changes_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => Amendment([], successorAwardRef: null));
    }

    [Fact]
    public void The_amendment_document_is_reproducible_and_permutation_stable()
    {
        var first = Line(LineId, quantity: 3m, unitPrice: 10m);
        var second = Line(SecondLineId, quantity: 3m, unitPrice: 10m);
        var firstReplacement = Line(LineId, quantity: 1m, unitPrice: 10m);
        var secondReplacement = Line(SecondLineId, quantity: 2m, unitPrice: 10m);

        var ordered = Amendment(
            [
                AmendmentLineDelta.Reduce(first, firstReplacement),
                AmendmentLineDelta.Reduce(second, secondReplacement)
            ],
            successorAwardRef: null);
        var permuted = Amendment(
            [
                AmendmentLineDelta.Reduce(second, secondReplacement),
                AmendmentLineDelta.Reduce(first, firstReplacement)
            ],
            successorAwardRef: null);

        Assert.Equal(ordered.Digest, permuted.Digest);
        Assert.Equal(
            ordered.Digest,
            PurchaseOrderCanonicalizer.Hash(ordered.CanonicalDocument()));
        using var document = System.Text.Json.JsonDocument.Parse(ordered.CanonicalDocument());
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            [
                "amendment_id", "approval_ref", "base_po_ref", "canonicalization_version", "contract_version",
                "evidence_refs", "expected_po_version", "line_deltas", "predecessor_version", "reason",
                "replacement_delivery", "responsibility_changes", "state", "successor_award_ref", "version"
            ],
            properties);
    }

    [Fact]
    public void A_successor_version_keeps_its_references_and_advances_the_lineage()
    {
        var previous = Line(LineId, quantity: 3m, unitPrice: 10m);
        var amendment = Amendment(
            [AmendmentLineDelta.Reduce(previous, Line(LineId, quantity: 1m, unitPrice: 10m))],
            successorAwardRef: null);
        var approval = new PurchaseOrderContentRef(Guid.NewGuid(), 1, Digest('e'));

        var approved = amendment.With(AmendmentState.Approved, approval, ActorId, OccurredAt.AddMinutes(5));

        Assert.Equal(2, approved.Version);
        Assert.Equal(1, approved.PredecessorVersion);
        Assert.Equal(AmendmentState.Approved, approved.State);
        Assert.Equal(approval.Id, approved.ApprovalRef!.Id);
        Assert.Single(approved.LineDeltas);
        Assert.Equal(amendment.BasePoRef.Id, approved.BasePoRef.Id);
        Assert.NotEqual(amendment.Digest, approved.Digest);
    }

    [Fact]
    public void Only_the_first_version_has_no_predecessor()
    {
        var previous = Line(LineId, quantity: 3m, unitPrice: 10m);

        Assert.Throws<DomainValidationException>(() => new PurchaseOrderAmendmentVersion(
            AmendmentId,
            2,
            null,
            Ref(PoId, 1, Digest('1')),
            1,
            AmendmentState.Draft,
            [AmendmentLineDelta.Reduce(previous, Line(LineId, quantity: 1m, unitPrice: 10m))],
            [],
            null,
            null,
            null,
            null,
            OrganizationId,
            "Reduce the line",
            ActorId,
            OccurredAt));
    }

    [Fact]
    public void A_responsibility_change_validates_its_kind()
    {
        var lineRef = new PurchaseOrderContentRef(LineId, 1, Digest('b'));
        var assignment = new AcceptanceResponsibility(
            PurchaseOrderCodes.ResponsibilityGoodsReceipt,
            lineRef,
            new PurchaseOrderEntityRef(ActorId, 1),
            new PurchaseOrderEntityRef(ActorId, 1),
            ActorId,
            AcceptanceResponsibilityBuilder.ReasonRequestedFor,
            OccurredAt,
            1);

        var change = new ResponsibilityChange("DELIVERY", lineRef, assignment, assignment);

        Assert.Equal("DELIVERY", change.Kind);
        Assert.Throws<DomainValidationException>(() =>
            new ResponsibilityChange("not a kind", lineRef, assignment, assignment));
    }

    private static PurchaseOrderAmendmentVersion Amendment(
        IReadOnlyList<AmendmentLineDelta> deltas,
        PurchaseOrderContentRef? successorAwardRef) =>
        new(
            AmendmentId,
            1,
            null,
            Ref(PoId, 1, Digest('1')),
            1,
            AmendmentState.Draft,
            deltas,
            [],
            null,
            successorAwardRef,
            null,
            null,
            OrganizationId,
            "Amend the order",
            ActorId,
            OccurredAt);

    private static PurchaseOrderLine Line(Guid lineId, decimal quantity, decimal unitPrice)
    {
        var gross = quantity * unitPrice;
        return new PurchaseOrderLine(
            lineId,
            Ref(lineId, 1, Digest('a')),
            Ref(lineId, 1, Digest('b')),
            quantity,
            "UNIT",
            unitPrice,
            "PEN",
            gross,
            "PEN",
            gross,
            gross,
            0m,
            0m,
            0m,
            null,
            []);
    }

    private static PurchaseOrderContentRef Ref(Guid id, int version, string digest) => new(id, version, digest);

    private static string Digest(char value) => new(value, 64);
}
