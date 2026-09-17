using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-09 (CA-05): the recommendation decides, the human deviates only with a recorded
/// justification and the partition of selected lines never forces an award the contract forbids.
/// </summary>
public sealed class SourcingSelectionTests
{
    private static readonly Guid LineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherLineId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Recommended = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void A_recommended_supplier_is_selected_without_a_justification()
    {
        var selection = SourcingSelection.Create(Line(LineId, Recommended), new SourcingEntityRef(Recommended, 1), null);

        Assert.True(selection.Recommended);
        Assert.Null(selection.DeviationJustification);
        Assert.Equal(Recommended, selection.SupplierRef.Id);
        Assert.Equal(LineId, selection.LineRef.Id);
    }

    [Fact]
    public void A_recommended_supplier_cannot_carry_a_justification()
    {
        Assert.Throws<DomainValidationException>(() => SourcingSelection.Create(
            Line(LineId, Recommended), new SourcingEntityRef(Recommended, 1), "because I say so"));
    }

    [Fact]
    public void Selecting_outside_the_recommended_set_requires_the_justification()
    {
        Assert.Throws<DomainValidationException>(() => SourcingSelection.Create(
            Line(LineId, Recommended), new SourcingEntityRef(Other, 1), null));
        Assert.Throws<DomainValidationException>(() => SourcingSelection.Create(
            Line(LineId, Recommended), new SourcingEntityRef(Other, 1), "   "));

        var selection = SourcingSelection.Create(
            Line(LineId, Recommended), new SourcingEntityRef(Other, 1), "Delivery risk of the cheapest offer");
        Assert.False(selection.Recommended);
        Assert.Equal("Delivery risk of the cheapest offer", selection.DeviationJustification);
    }

    [Fact]
    public void A_supplier_without_a_scored_quotation_cannot_be_selected()
    {
        Assert.Throws<DomainConflictException>(() => SourcingSelection.Create(
            Line(LineId, Recommended), new SourcingEntityRef(Guid.NewGuid(), 1), "unknown"));
    }

    [Fact]
    public void A_stored_selection_keeps_the_closed_rules()
    {
        Assert.Throws<DomainValidationException>(() => SourcingSelection.Stored(
            Ref(LineId), Ref(Guid.NewGuid()), new SourcingEntityRef(Other, 1), recommended: false, null));
        var stored = SourcingSelection.Stored(
            Ref(LineId), Ref(Guid.NewGuid()), new SourcingEntityRef(Other, 1), recommended: false,
            "justified deviation");
        Assert.False(stored.Recommended);
    }

    [Fact]
    public void Lines_of_the_same_supplier_and_compatible_terms_share_one_proposal()
    {
        var drafts = SourcingSelectionPartition.Partition(
        [
            Candidate(LineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365)),
            Candidate(OtherLineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365))
        ]);

        var draft = Assert.Single(drafts);
        Assert.Equal(Recommended, draft.SupplierRef.Id);
        Assert.Equal(2, draft.Selections.Count);
        Assert.Equal("PEN", draft.Currency);
    }

    [Fact]
    public void A_different_supplier_currency_or_terms_splits_the_selection_set()
    {
        var bySupplier = SourcingSelectionPartition.Partition(
        [
            Candidate(LineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365)),
            Candidate(OtherLineId, Other, "PEN", Terms(15, "EXW", "NET30", 365))
        ]);
        Assert.Equal(2, bySupplier.Count);

        var byCurrency = SourcingSelectionPartition.Partition(
        [
            Candidate(LineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365)),
            Candidate(OtherLineId, Recommended, "USD", Terms(15, "EXW", "NET30", 365))
        ]);
        Assert.Equal(2, byCurrency.Count);

        var byTerms = SourcingSelectionPartition.Partition(
        [
            Candidate(LineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365)),
            Candidate(OtherLineId, Recommended, "PEN", Terms(30, "EXW", "NET30", 365))
        ]);
        Assert.Equal(2, byTerms.Count);
    }

    [Fact]
    public void The_same_line_cannot_be_selected_twice_in_one_set()
    {
        Assert.Throws<DomainConflictException>(() => SourcingSelectionPartition.Partition(
        [
            Candidate(LineId, Recommended, "PEN", Terms(15, "EXW", "NET30", 365)),
            Candidate(LineId, Other, "PEN", Terms(15, "EXW", "NET30", 365))
        ]));
    }

    private static SelectionCandidate Candidate(Guid lineId, Guid supplierId, string currency, CommercialTerms terms) =>
        new(
            SourcingSelection.Create(
                Line(lineId, Recommended),
                new SourcingEntityRef(supplierId, 1),
                supplierId == Recommended ? null : "justified deviation"),
            currency,
            terms);

    private static EvaluationLineResult Line(Guid lineId, Guid recommendedSupplier) =>
        new(
            Ref(lineId),
            [
                Score(lineId, recommendedSupplier, total: 95m),
                Score(lineId, Other, total: 80m)
            ],
            [new SourcingEntityRef(recommendedSupplier, 1)]);

    private static QuotationScore Score(Guid lineId, Guid supplierId, decimal total)
    {
        var criteria = new[]
        {
            new CriterionScore(SourcingEvaluationCriterion.Price, total, 100m, "SYSTEM", total, null)
        };
        return new QuotationScore(
            Ref(Guid.NewGuid()),
            new SourcingEntityRef(supplierId, 1),
            Ref(lineId),
            100m,
            criteria);
    }

    private static SourcingContentRef Ref(Guid id) => new(id, 1, new string('a', 64));

    private static CommercialTerms Terms(int deliveryDays, string incoterm, string payment, int warranty) =>
        new(deliveryDays, incoterm, payment, warranty);
}
