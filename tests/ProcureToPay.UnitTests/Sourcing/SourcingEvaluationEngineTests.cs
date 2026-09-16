using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Sourcing;

/// <summary>
/// SPEC 10 REQ-07 / REQ-08 / REQ-09 (CA-05): the evaluation is reproducible, normalizes every price
/// in the base currency at twelve decimals, rounds criterion and total scores to four decimals half
/// to even, refuses a missing datum of a positively weighted criterion and keeps exact ties tied.
/// </summary>
public sealed class SourcingEvaluationEngineTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RfqId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SupplierA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SupplierB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-16T12:00:00.0000000Z");

    [Fact]
    public void The_price_criterion_is_the_lowest_over_the_current_normalized_amount()
    {
        var evaluation = Evaluate(
            [Quote(SupplierA, "100"), Quote(SupplierB, "80")],
            SupplierPerformance: null,
            ManualFor: null);

        var scores = Line(evaluation).QuotationScores;
        var cheapest = scores.Single(score => score.SupplierRef.Id == SupplierB);
        var other = scores.Single(score => score.SupplierRef.Id == SupplierA);
        Assert.Equal(100m, cheapest.TotalScore);
        Assert.Equal(80m, other.TotalScore);
        Assert.Equal(80m, cheapest.NormalizedGrossAmount);
        Assert.Equal(100m, other.NormalizedGrossAmount);
    }

    [Fact]
    public void An_external_rate_normalizes_the_foreign_amount_into_the_base_currency()
    {
        var fx = new SourcingFxSnapshot(
            Guid.NewGuid(), 1, "PEN", "USD", 3.75m, Clock, "SBS published rate", Attachment());
        var evaluation = Evaluate(
            [Quote(SupplierA, "100", currency: "PEN"), Quote(SupplierB, "30", currency: "USD")],
            SupplierPerformance: null,
            ManualFor: null,
            fx: new Dictionary<string, SourcingFxSnapshot>(StringComparer.Ordinal) { ["USD"] = fx });

        var scores = Line(evaluation).QuotationScores;
        Assert.Equal(100m, scores.Single(score => score.SupplierRef.Id == SupplierA).NormalizedGrossAmount);
        Assert.Equal(112.5m, scores.Single(score => score.SupplierRef.Id == SupplierB).NormalizedGrossAmount);
        Assert.Equal(100m, scores.Single(score => score.SupplierRef.Id == SupplierA).TotalScore);
        // 100 / 112.5 * 100 = 88.888… → 88.8889 half to even at four decimals.
        Assert.Equal(88.8889m, scores.Single(score => score.SupplierRef.Id == SupplierB).TotalScore);
        Assert.Single(evaluation.FxSnapshots);
        Assert.Equal(fx.Digest, evaluation.FxSnapshots[0].Digest);
    }

    [Fact]
    public void A_missing_rate_or_an_identical_currency_snapshot_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => Evaluate(
            [Quote(SupplierA, "100"), Quote(SupplierB, "30", currency: "USD")],
            SupplierPerformance: null,
            ManualFor: null));

        Assert.Throws<DomainValidationException>(() => new SourcingFxSnapshot(
            Guid.NewGuid(), 1, "PEN", "PEN", 1m, Clock, "same currency", Attachment()));
    }

    [Fact]
    public void Delivery_time_uses_the_lowest_ratio_and_handles_zero_days()
    {
        var evaluation = Evaluate(
            [Quote(SupplierA, "100", days: 10), Quote(SupplierB, "100", days: 0)],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 1, delivery: 99));

        var scores = Line(evaluation).QuotationScores;
        var zeroDays = Criterion(scores.Single(score => score.SupplierRef.Id == SupplierB),
            SourcingEvaluationCriterion.DeliveryTime);
        var tenDays = Criterion(scores.Single(score => score.SupplierRef.Id == SupplierA),
            SourcingEvaluationCriterion.DeliveryTime);
        Assert.Equal(100m, zeroDays.Score);
        Assert.Equal(0m, tenDays.Score);
    }

    [Fact]
    public void Warranty_gives_every_offer_one_hundred_when_nobody_offers_days()
    {
        var evaluation = Evaluate(
            [Quote(SupplierA, "100", warranty: 0), Quote(SupplierB, "100", warranty: 0)],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 1, warranty: 99));

        Assert.All(Line(evaluation).QuotationScores, score => Assert.Equal(
            100m, Criterion(score, SourcingEvaluationCriterion.Warranty).Score));
    }

    [Fact]
    public void Warranty_and_delivery_are_clamped_to_the_zero_one_hundred_range()
    {
        var evaluation = Evaluate(
            [Quote(SupplierA, "100", days: 5, warranty: 365), Quote(SupplierB, "100", days: 30, warranty: 730)],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 1, delivery: 1, warranty: 98));

        var scores = Line(evaluation).QuotationScores;
        var better = scores.Single(score => score.SupplierRef.Id == SupplierA);
        var worse = scores.Single(score => score.SupplierRef.Id == SupplierB);
        Assert.Equal(100m, Criterion(better, SourcingEvaluationCriterion.DeliveryTime).Score);
        Assert.Equal(50m, Criterion(better, SourcingEvaluationCriterion.Warranty).Score);
        Assert.Equal(16.6667m, Criterion(worse, SourcingEvaluationCriterion.DeliveryTime).Score);
        Assert.Equal(100m, Criterion(worse, SourcingEvaluationCriterion.Warranty).Score);
    }

    [Fact]
    public void Scores_round_to_four_decimals_half_to_even()
    {
        // lowest/current = 3/7 → 42.857142857142857142... → 42.8571 at four decimals.
        var evaluation = Evaluate([Quote(SupplierA, "3"), Quote(SupplierB, "7")], null, null);
        var scores = Line(evaluation).QuotationScores;

        Assert.Equal(100m, scores.Single(score => score.SupplierRef.Id == SupplierA).TotalScore);
        Assert.Equal(42.8571m, scores.Single(score => score.SupplierRef.Id == SupplierB).TotalScore);
    }

    [Fact]
    public void A_positive_weight_without_its_datum_blocks_the_evaluation()
    {
        var exception = Assert.Throws<DomainValidationException>(() => Evaluate(
            [Quote(SupplierA, "100")],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 50, performance: 50)));
        Assert.Contains("supplier performance", exception.Message, StringComparison.OrdinalIgnoreCase);

        // A positively weighted manual criterion without its input is refused instead of scored.
        Assert.Throws<DomainValidationException>(() => Evaluate(
            [Quote(SupplierA, "100")],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 50, paymentTerms: 50),
            provideManualInputs: false));
    }

    [Fact]
    public void A_manual_score_requires_its_evidence_and_only_covers_its_own_line()
    {
        Assert.Throws<DomainValidationException>(() => new ManualCriterionInput(
            SourcingEvaluationCriterion.PaymentTerms, Ref(LineId), new SourcingEntityRef(SupplierA, 1), 80m,
            "Net 60 offered", null!));

        var evaluation = Evaluate(
            [Quote(SupplierA, "100")],
            SupplierPerformance: null,
            ManualFor: null,
            weights: WeightSet(price: 50, paymentTerms: 50));

        var scores = Line(evaluation).QuotationScores.Single();
        var manual = scores.Criteria.Single(criterion =>
            criterion.Criterion == SourcingEvaluationCriterion.PaymentTerms);
        Assert.Equal("BUYER", manual.InputSource);
        Assert.Equal(45m, manual.WeightedScore);
        Assert.Equal("Net 60 with valid evidence", manual.Justification);
        Assert.Equal(95m, scores.TotalScore);
    }

    [Fact]
    public void Supplier_performance_comes_from_the_frozen_snapshot_and_never_from_the_quotation()
    {
        var evaluation = Evaluate(
            [Quote(SupplierA, "100")],
            SupplierPerformance: new Dictionary<Guid, decimal> { [SupplierA] = 87.5m },
            ManualFor: null,
            weights: WeightSet(price: 50, performance: 50));

        var criterion = Line(evaluation).QuotationScores.Single().Criteria.Single(score =>
            score.Criterion == SourcingEvaluationCriterion.SupplierPerformance);
        Assert.Equal("SUPPLIER_MASTER", criterion.InputSource);
        Assert.Equal(87.5m, criterion.Score);
        Assert.Equal(43.75m, criterion.WeightedScore);
    }

    [Fact]
    public void Exact_ties_stay_tied_and_are_ordered_by_canonical_uuid()
    {
        var evaluation = Evaluate([Quote(SupplierB, "100"), Quote(SupplierA, "100")], null, null);

        var recommended = Line(evaluation).RecommendedSuppliers;
        Assert.Equal(2, recommended.Count);
        Assert.Equal(
            recommended.OrderBy(reference => reference.Id, CanonicalUuidComparer.Instance).Select(r => r.Id),
            recommended.Select(reference => reference.Id));
    }

    [Fact]
    public void The_evaluation_is_reproducible_and_permutation_invariant()
    {
        var first = Evaluate([Quote(SupplierA, "100"), Quote(SupplierB, "80")], null, null);
        var second = Evaluate([Quote(SupplierB, "80"), Quote(SupplierA, "100")], null, null);

        Assert.Equal(first.ComputeDigest(), second.ComputeDigest());

        var reweighted = Evaluate([Quote(SupplierA, "100"), Quote(SupplierB, "80")], null, null,
            weights: WeightSet(price: 100, delivery: 0));
        var other = Evaluate([Quote(SupplierA, "100"), Quote(SupplierB, "80")], null, null,
            weights: WeightSet(price: 90, delivery: 10));
        Assert.NotEqual(reweighted.ComputeDigest(), other.ComputeDigest());
    }

    [Fact]
    public void A_line_without_any_valid_quotation_blocks_evaluation()
    {
        Assert.Throws<DomainValidationException>(() => SourcingEvaluationEngine.Evaluate(
            OrganizationId,
            new SourcingContentRef(RfqId, 2, Digest('a')),
            "PEN",
            WeightSet(price: 100),
            [new RfqLine(Ref(LineId), 1m, "EA")],
            [],
            new Dictionary<Guid, decimal>(),
            new Dictionary<string, SourcingFxSnapshot>(StringComparer.Ordinal),
            []));
    }

    private static QuoteEvaluation Evaluate(
        IReadOnlyList<EvaluationQuote> quotes,
        IReadOnlyDictionary<Guid, decimal>? SupplierPerformance,
        object? ManualFor,
        EvaluationWeightSet? weights = null,
        IReadOnlyDictionary<string, SourcingFxSnapshot>? fx = null,
        bool provideManualInputs = true) =>
        SourcingEvaluationEngine.Evaluate(
            OrganizationId,
            new SourcingContentRef(RfqId, 2, Digest('a')),
            "PEN",
            weights ?? WeightSet(price: 100),
            [new RfqLine(Ref(LineId), 1m, "EA")],
            quotes,
            SupplierPerformance ?? new Dictionary<Guid, decimal>(),
            fx ?? new Dictionary<string, SourcingFxSnapshot>(StringComparer.Ordinal),
            provideManualInputs ? ManualInputs(quotes, weights) : []);

    /// <summary>
    /// Manual inputs are only materialized when the weights actually demand them, so a test that
    /// isolates another formula never injects a score the contract would not require.
    /// </summary>
    private static IReadOnlyList<ManualCriterionInput> ManualInputs(
        IReadOnlyList<EvaluationQuote> quotes,
        EvaluationWeightSet? weights)
    {
        if (weights is null ||
            weights.Weights.All(weight => weight.Weight == 0 ||
                weight.Criterion is not (SourcingEvaluationCriterion.PaymentTerms or
                    SourcingEvaluationCriterion.TechnicalCompliance)))
        {
            return [];
        }

        return quotes.SelectMany(quote => new[]
        {
            new ManualCriterionInput(
                SourcingEvaluationCriterion.PaymentTerms, Ref(LineId), quote.SupplierRef, 90m,
                "Net 60 with valid evidence", Attachment()),
            new ManualCriterionInput(
                SourcingEvaluationCriterion.TechnicalCompliance, Ref(LineId), quote.SupplierRef, 80m,
                "Technically compliant with evidence", Attachment())
        }).ToArray();
    }

    private static CriterionScore Criterion(QuotationScore score, SourcingEvaluationCriterion criterion) =>
        score.Criteria.Single(candidate => candidate.Criterion == criterion);

    private static EvaluationLineResult Line(QuoteEvaluation evaluation) => evaluation.Lines.Single();

    private static EvaluationQuote Quote(
        Guid supplierId,
        string grossTotal,
        string currency = "PEN",
        int days = 15,
        int warranty = 365) =>
        new(
            new SourcingContentRef(QuotationIdFor(supplierId), 1, Digest('b')),
            new SourcingEntityRef(supplierId, 1),
            currency,
            new CommercialTerms(days, "EXW", "NET30", warranty),
            [
                new QuotationLine(
                    "0", "0", grossTotal, Ref(LineId), "1", grossTotal, "0", "compliant", "EA",
                    grossTotal)
            ]);

    private static SourcingAttachmentRef Attachment() =>
        new("application/pdf", Guid.Parse("99999999-9999-9999-9999-999999999999"), "evidence.pdf", 10, Digest('c'), 1);

    /// <summary>Deterministic quotation identity per supplier so permutation tests compare digests.</summary>
    private static Guid QuotationIdFor(Guid supplierId)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = supplierId.TryWriteBytes(bytes, bigEndian: true, out _);
        bytes[0] ^= 0xFF;
        return new Guid(bytes, bigEndian: true);
    }

    private static SourcingContentRef Ref(Guid lineId) => new(lineId, 1, Digest('d'));

    /// <summary>Weights that leave the remaining criteria at zero so one formula is isolated.</summary>
    private static EvaluationWeightSet WeightSet(
        int price = 0,
        int delivery = 0,
        int warranty = 0,
        int paymentTerms = 0,
        int technical = 0,
        int performance = 0) =>
        new(
        [
            new EvaluationWeight(SourcingEvaluationCriterion.Price, price),
            new EvaluationWeight(SourcingEvaluationCriterion.DeliveryTime, delivery),
            new EvaluationWeight(SourcingEvaluationCriterion.Warranty, warranty),
            new EvaluationWeight(SourcingEvaluationCriterion.PaymentTerms, paymentTerms),
            new EvaluationWeight(SourcingEvaluationCriterion.TechnicalCompliance, technical),
            new EvaluationWeight(SourcingEvaluationCriterion.SupplierPerformance, performance)
        ]);

    private static string Digest(char value) => new(value, 64);
}
