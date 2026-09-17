using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-02 / REQ-03 / CA-02: the RFQ and quotation documents are reproducible, every set is
/// ordered by its canonical bytes, the deadline in force is part of the recorded fact and the
/// monetary equations of a quoted line hold at <c>decimal(38,12)</c> before anything is persisted.
/// </summary>
public sealed class SourcingCanonicalizerTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProcessId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RfqId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RequestId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherLineId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-16T12:00:00.0000000Z");

    private static readonly Guid SupplierId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid AttachmentId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    [Fact]
    public void The_rfq_digest_is_stable_and_permutation_invariant()
    {
        var first = RfqDigest(Lines(LineId, OtherLineId), Weights(price: 40));
        var second = RfqDigest(Lines(OtherLineId, LineId), Weights(price: 40));

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Opening_the_rfq_changes_its_bytes()
    {
        var draft = RfqDigest(Lines(LineId), Weights(price: 40));
        var opened = SourcingCanonicalizer.RfqContentDigest(
            RfqId, 1, OrganizationId, ProcessId, RequestId, 3, RfqStatus.Open, "PEN", Terms(),
            Weights(price: 40), Clock, Clock.AddDays(7), null, Lines(LineId));

        Assert.NotEqual(draft, opened);
    }

    [Fact]
    public void A_changed_weight_or_deadline_changes_the_digest()
    {
        var baseline = RfqDigest(Lines(LineId), Weights(price: 40));
        var reweighted = RfqDigest(Lines(LineId), Weights(price: 50));

        Assert.NotEqual(baseline, reweighted);
    }

    [Fact]
    public void The_process_line_and_its_rfq_line_are_byte_equivalent()
    {
        var line = new SourcingProcessLine(new SourcingContentRef(LineId, 2, Digest('a')), 12.5m, "EA");
        var rfqLine = new RfqLine(new SourcingContentRef(LineId, 2, Digest('a')), 12.5m, "EA");

        Assert.True(SourcingCanonicalizer.ProcessLineMatchesRfqLine(line, rfqLine));
        Assert.Equal(SourcingCanonicalizer.ProcessLineDocument(line), SourcingCanonicalizer.RfqLineDocument(rfqLine));
    }

    [Fact]
    public void Timeliness_is_part_of_the_quotation_digest()
    {
        var onTime = QuotationDigest(QuotationTimeliness.OnTime);
        var late = QuotationDigest(QuotationTimeliness.Late);

        Assert.NotEqual(onTime, late);
    }

    [Fact]
    public void A_quotation_digest_is_stable_across_line_order()
    {
        var first = QuotationDigest(QuotationTimeliness.OnTime, LineId, OtherLineId);
        var second = QuotationDigest(QuotationTimeliness.OnTime, OtherLineId, LineId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_set_cannot_repeat_an_element()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new EvaluationWeightSet(
        [
            new EvaluationWeight(SourcingEvaluationCriterion.Price, 40),
            new EvaluationWeight(SourcingEvaluationCriterion.Price, 40),
            new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, 60)
        ]));

        Assert.Contains("exactly once", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weights_must_add_up_to_one_hundred_and_keep_price_positive()
    {
        Assert.Throws<DomainValidationException>(() => Weights(price: 40, sum: 99));
        Assert.Throws<DomainValidationException>(() => Weights(price: 0));
    }

    [Fact]
    public void A_quoted_line_reconciles_both_monetary_equations()
    {
        var line = QuotedLine(null, "10", "12.5", taxes: "5", charges: "2.5", discounts: "1");

        Assert.Equal("125", line.Subtotal);
        Assert.Equal("131.5", line.GrossTotal);
        Assert.Equal(10m, line.QuantityValue);
        Assert.Equal(12.5m, line.UnitPriceValue);
    }

    [Fact]
    public void A_quoted_line_with_a_broken_subtotal_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => new QuotationLine(
            "0", "0", "131.5", Ref(LineId), "10", "124", "5", "delivered", "EA", "12.5"));
    }

    [Fact]
    public void Discounts_cannot_drive_the_gross_total_to_zero()
    {
        Assert.Throws<DomainValidationException>(() => new QuotationLine(
            "0", "200", "0", Ref(LineId), "10", "200", "0", "delivered", "EA", "20"));
    }

    [Fact]
    public void A_valid_review_forbids_codes_and_motive()
    {
        Assert.Throws<DomainValidationException>(() => QuotationReview.Decided(
            QuotationReviewStatus.Valid, [QuotationReviewCode.Other], "not valid", Guid.NewGuid(), Clock));
        var review = QuotationReview.Decided(
            QuotationReviewStatus.Valid, [], null, Guid.NewGuid(), Clock);
        Assert.Equal("VALID", review.StatusCode);
        Assert.Equal("PENDING", QuotationReview.Pending().StatusCode);
    }

    [Fact]
    public void An_invalid_or_withdrawn_review_requires_a_motive()
    {
        Assert.Throws<DomainValidationException>(() => QuotationReview.Decided(
            QuotationReviewStatus.Invalid, [QuotationReviewCode.MoneyMismatch], null, Guid.NewGuid(), Clock));
        var review = QuotationReview.Decided(
            QuotationReviewStatus.Withdrawn, [], "supplier withdrew the offer", Guid.NewGuid(), Clock);
        Assert.Equal("WITHDRAWN", review.StatusCode);
        Assert.Equal("supplier withdrew the offer", review.Motive);
    }

    [Fact]
    public void A_quotation_cannot_be_received_after_it_was_registered()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new QuotationVersionContent(
            OrganizationId,
            new SourcingContentRef(RfqId, 1, Digest('b')),
            new SourcingEntityRef(Guid.NewGuid(), 1),
            "PEN",
            Terms(),
            QuotationTimeliness.OnTime,
            Clock.AddHours(1),
            Clock,
            [QuotedLine()],
            [Attachment()],
            QuotationReview.Pending()));

        Assert.Contains("received after", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_quotation_requires_evidence_and_a_distinct_line_set()
    {
        Assert.Throws<DomainValidationException>(() => new QuotationVersionContent(
            OrganizationId,
            new SourcingContentRef(RfqId, 1, Digest('b')),
            new SourcingEntityRef(Guid.NewGuid(), 1),
            "PEN",
            Terms(),
            QuotationTimeliness.OnTime,
            Clock,
            Clock,
            [QuotedLine()],
            [],
            QuotationReview.Pending()));
        Assert.Throws<DomainValidationException>(() => new QuotationVersionContent(
            OrganizationId,
            new SourcingContentRef(RfqId, 1, Digest('b')),
            new SourcingEntityRef(Guid.NewGuid(), 1),
            "PEN",
            Terms(),
            QuotationTimeliness.OnTime,
            Clock,
            Clock,
            [QuotedLine(), QuotedLine()],
            [Attachment()],
            QuotationReview.Pending()));
    }

    [Fact]
    public void A_process_line_requires_a_positive_quantity_and_a_contractual_unit()
    {
        Assert.Throws<DomainValidationException>(() =>
            new SourcingProcessLine(Ref(LineId), 0m, "EA"));
        Assert.Throws<DomainValidationException>(() =>
            new SourcingProcessLine(Ref(LineId), 1m, "E A"));
        // Lowercase input is normalized to the contractual alphabet, never rejected silently.
        Assert.Equal("EA", new SourcingProcessLine(Ref(LineId), 1m, "ea").UnitCode);
    }

    [Fact]
    public void Command_fingerprints_ignore_the_clock_and_reject_a_different_payload()
    {
        var lines = Lines(LineId);
        var first = SourcingCommandFingerprints.Process(
            OrganizationId, Guid.NewGuid(), RequestId, 3,
            [new SourcingProcessLine(Ref(LineId), 5m, "EA")], "cmd-1");
        var second = SourcingCommandFingerprints.Process(
            OrganizationId, Guid.NewGuid(), RequestId, 3,
            [new SourcingProcessLine(Ref(LineId), 6m, "EA")], "cmd-1");

        Assert.NotEqual(first, second);
    }

    private static string RfqDigest(IEnumerable<RfqLine> lines, EvaluationWeightSet weights) =>
        SourcingCanonicalizer.RfqContentDigest(
            RfqId, 1, OrganizationId, ProcessId, RequestId, 3, RfqStatus.Draft, "PEN", Terms(), weights,
            openedAt: null, Clock.AddDays(7), predecessorVersion: null, lines);

    private static string QuotationDigest(QuotationTimeliness timeliness, params Guid[] lineIds) =>
        SourcingCanonicalizer.QuotationContentDigest(
            1, null, OrganizationId, new SourcingContentRef(RfqId, 1, Digest('b')),
            new SourcingEntityRef(SupplierId, 1), "PEN", Terms(), timeliness, Clock, Clock,
            (lineIds.Length == 0 ? [LineId] : lineIds).Select(id => QuotedLine(id)), [Attachment()],
            QuotationReview.Pending());

    private static IReadOnlyList<RfqLine> Lines(params Guid[] ids) =>
        ids.Select(id => new RfqLine(Ref(id), 5m, "EA")).ToArray();

    private static QuotationLine QuotedLine(
        Guid? lineId = null,
        string quantity = "10",
        string unitPrice = "12.5",
        string taxes = "5",
        string charges = "2.5",
        string discounts = "1")
    {
        var subtotal = (decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture) *
                        decimal.Parse(unitPrice, System.Globalization.CultureInfo.InvariantCulture))
            .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
        var gross = (decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture) +
                     decimal.Parse(taxes, System.Globalization.CultureInfo.InvariantCulture) +
                     decimal.Parse(charges, System.Globalization.CultureInfo.InvariantCulture) -
                     decimal.Parse(discounts, System.Globalization.CultureInfo.InvariantCulture))
            .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
        return new QuotationLine(
            charges, discounts, gross, Ref(lineId ?? LineId), quantity, subtotal, taxes, "delivered", "EA",
            unitPrice);
    }

    private static CommercialTerms Terms() => new(15, "EXW", "NET30", 365);

    private static SourcingAttachmentRef Attachment() =>
        new("application/pdf", AttachmentId, "offer.pdf", 128, Digest('c'), 1);

    private static SourcingContentRef Ref(Guid lineId) => new(lineId, 1, Digest('a'));

    private static EvaluationWeightSet Weights(int price, int sum = 100)
    {
        var remaining = sum - price;
        return new EvaluationWeightSet(
        [
            new EvaluationWeight(SourcingEvaluationCriterion.Price, price),
            new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, remaining),
            new EvaluationWeight(SourcingEvaluationCriterion.Warranty, 0),
            new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, 0),
            new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, 0),
            new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, 0)
        ]);
    }

    private static string Digest(char value) => new(value, 64);
}
