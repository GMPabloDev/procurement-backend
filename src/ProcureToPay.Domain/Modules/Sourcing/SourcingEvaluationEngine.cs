using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// One valid quotation participating in the evaluation of one RFQ (REQ-07). It carries only the
/// frozen content the formulas need: the currency, the offered terms and the quoted lines.
/// </summary>
public sealed record EvaluationQuote(
    SourcingContentRef QuotationRef,
    SourcingEntityRef SupplierRef,
    string Currency,
    CommercialTerms Terms,
    IReadOnlyList<QuotationLine> Lines);

/// <summary>
/// Weighted evaluation engine of SPEC 10 REQ-07, REQ-08 and REQ-09. It is a pure function of the
/// frozen snapshots: the same inputs produce the same scores, recommendations and bytes, a permuted
/// input produces the same result and a missing datum of a positively weighted criterion blocks the
/// evaluation instead of degrading to a score.
/// </summary>
public static class SourcingEvaluationEngine
{
    public static QuoteEvaluation Evaluate(
        Guid organizationId,
        SourcingContentRef rfqRef,
        string baseCurrency,
        EvaluationWeightSet weights,
        IEnumerable<RfqLine> rfqLines,
        IEnumerable<EvaluationQuote> quotes,
        IReadOnlyDictionary<Guid, decimal> supplierPerformance,
        IReadOnlyDictionary<string, SourcingFxSnapshot> fxSnapshots,
        IEnumerable<ManualCriterionInput> manualInputs)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(supplierPerformance);
        ArgumentNullException.ThrowIfNull(fxSnapshots);
        var normalizedBase = SourcingCodes.Currency(baseCurrency, "Base currency");
        var lines = (rfqLines ?? []).OrderBy(line => line.LineRef.Id).ToImmutableArray();
        if (lines.Length == 0)
        {
            throw new DomainValidationException("An evaluation requires the lines of its RFQ.");
        }

        var participants = (quotes ?? [])
            .DistinctBy(quote => (quote.QuotationRef.Id, quote.QuotationRef.Version))
            .ToImmutableArray();

        var manual = (manualInputs ?? []).ToImmutableArray();
        var results = ImmutableArray.CreateBuilder<EvaluationLineResult>(lines.Length);
        var usedFx = new List<SourcingFxSnapshot>();
        var usedQuotes = new List<SourcingContentRef>();
        foreach (var line in lines)
        {
            var covered = participants
                .Where(quote => quote.Lines.Any(candidate => candidate.LineRef.Id == line.LineRef.Id))
                .Select(quote => new
                {
                    Quote = quote,
                    Offered = quote.Lines.Single(candidate => candidate.LineRef.Id == line.LineRef.Id)
                })
                .OrderBy(candidate => candidate.Quote.SupplierRef.Id)
                .ToArray();
            if (covered.Length == 0)
            {
                throw new DomainValidationException(
                    "Every evaluated line requires at least one valid quotation (REQ-07).");
            }

            var normalized = covered
                .Select(candidate => new
                {
                    candidate.Quote,
                    candidate.Offered,
                    Amount = Normalize(candidate.Quote.Currency, candidate.Offered.GrossTotalValue, normalizedBase,
                        fxSnapshots, usedFx)
                })
                .ToArray();
            var lowestAmount = normalized.Min(candidate => candidate.Amount);
            var lowestDays = normalized.Min(candidate => candidate.Quote.Terms.DeliveryDays);
            var highestDays = normalized.Max(candidate => candidate.Quote.Terms.DeliveryDays);
            var highestWarranty = normalized.Max(candidate => candidate.Quote.Terms.WarrantyDays);
            foreach (var candidate in normalized)
            {
                usedQuotes.Add(candidate.Quote.QuotationRef);
            }

            var scores = ImmutableArray.CreateBuilder<QuotationScore>(normalized.Length);
            foreach (var candidate in normalized)
            {
                scores.Add(new QuotationScore(
                    candidate.Quote.QuotationRef,
                    candidate.Quote.SupplierRef,
                    line.LineRef,
                    candidate.Amount,
                    Criteria(candidate.Quote, candidate.Amount, line.LineRef, lowestAmount, lowestDays, highestDays,
                        highestWarranty, weights, supplierPerformance, manual)));
            }

            var materialized = scores.ToImmutableArray();
            var best = materialized.Max(score => score.TotalScore);
            var recommended = materialized
                .Where(score => score.TotalScore == best)
                .Select(score => score.SupplierRef)
                .OrderBy(reference => reference.Id, CanonicalUuidComparer.Instance)
                .ToArray();
            results.Add(new EvaluationLineResult(line.LineRef, materialized, recommended));
        }

        var evaluation = new QuoteEvaluation(
            organizationId,
            rfqRef,
            weights,
            results,
            usedFx.DistinctBy(snapshot => (snapshot.Id, snapshot.Version)),
            usedQuotes.DistinctBy(reference => (reference.Id, reference.Version)));
        return evaluation;
    }

    private static IEnumerable<CriterionScore> Criteria(
        EvaluationQuote quote,
        decimal normalizedGross,
        SourcingContentRef lineRef,
        decimal lowestAmount,
        int lowestDays,
        int highestDays,
        int highestWarranty,
        EvaluationWeightSet weights,
        IReadOnlyDictionary<Guid, decimal> supplierPerformance,
        ImmutableArray<ManualCriterionInput> manual)
    {
        foreach (var weight in weights.Weights)
        {
            if (weight.Weight == 0)
            {
                // A criterion with weight zero needs no input and contributes nothing (REQ-07).
                yield return new CriterionScore(weight.Criterion, 0m, 0m, "SYSTEM", null, null);
                continue;
            }

            switch (weight.Criterion)
            {
                case SourcingEvaluationCriterion.Price:
                    yield return new CriterionScore(
                        weight.Criterion,
                        Clamp(lowestAmount / normalizedGross * 100m),
                        weight.Weight,
                        "SYSTEM",
                        normalizedGross,
                        null);
                    break;
                case SourcingEvaluationCriterion.DeliveryTime:
                    var delivery = lowestDays == 0
                        ? quote.Terms.DeliveryDays == 0 ? 100m : 0m
                        : (decimal)lowestDays / quote.Terms.DeliveryDays * 100m;
                    yield return new CriterionScore(
                        weight.Criterion, Clamp(delivery), weight.Weight, "SYSTEM",
                        quote.Terms.DeliveryDays, null);
                    break;
                case SourcingEvaluationCriterion.Warranty:
                    var warranty = highestWarranty == 0
                        ? 100m
                        : (decimal)quote.Terms.WarrantyDays / highestWarranty * 100m;
                    yield return new CriterionScore(
                        weight.Criterion, Clamp(warranty), weight.Weight, "SYSTEM",
                        quote.Terms.WarrantyDays, null);
                    break;
                case SourcingEvaluationCriterion.PaymentTerms:
                    yield return ManualScore(
                        weight, quote, lineRef, manual, SourcingEvaluationCriterion.PaymentTerms);
                    break;
                case SourcingEvaluationCriterion.TechnicalCompliance:
                    yield return ManualScore(
                        weight, quote, lineRef, manual, SourcingEvaluationCriterion.TechnicalCompliance);
                    break;
                case SourcingEvaluationCriterion.SupplierPerformance:
                    if (!supplierPerformance.TryGetValue(quote.SupplierRef.Id, out var performance))
                    {
                        // A positively weighted snapshot that is absent blocks instead of scoring zero.
                        throw new DomainValidationException(
                            "The supplier performance snapshot is absent for a positively weighted criterion.");
                    }

                    yield return new CriterionScore(
                        weight.Criterion, Clamp(performance), weight.Weight, "SUPPLIER_MASTER",
                        SourcingCodes.Score(performance), null);
                    break;
                default:
                    throw new DomainValidationException("The evaluation criterion is invalid.");
            }
        }
    }

    private static CriterionScore ManualScore(
        EvaluationWeight weight,
        EvaluationQuote quote,
        SourcingContentRef lineRef,
        ImmutableArray<ManualCriterionInput> manual,
        SourcingEvaluationCriterion criterion)
    {
        var input = manual.SingleOrDefault(candidate =>
            candidate.Criterion == criterion &&
            candidate.LineRef.Id == lineRef.Id &&
            candidate.SupplierRef.Id == quote.SupplierRef.Id);
        if (input is null)
        {
            throw new DomainValidationException(
                $"The {SourcingStateCodes.Criterion(criterion)} criterion requires a manual input with evidence.");
        }

        if (input.LineRef.Version != lineRef.Version)
        {
            throw new DomainConflictException("A manual input does not belong to the evaluated line version.");
        }

        return new CriterionScore(
            criterion, Clamp(input.Score), weight.Weight, "BUYER", input.Score, input.Justification);
    }

    /// <summary>
    /// REQ-08: a price is compared in the base currency. Identical currencies use rate 1 and admit no
    /// external snapshot; different currencies require the immutable snapshot of that currency.
    /// </summary>
    private static decimal Normalize(
        string sourceCurrency,
        decimal sourceAmount,
        string baseCurrency,
        IReadOnlyDictionary<string, SourcingFxSnapshot> fxSnapshots,
        List<SourcingFxSnapshot> used)
    {
        var normalizedSource = SourcingCodes.Currency(sourceCurrency, "Quotation currency");
        if (string.Equals(normalizedSource, baseCurrency, StringComparison.Ordinal))
        {
            return SourcingCodes.Decimal12(sourceAmount);
        }

        if (!fxSnapshots.TryGetValue(normalizedSource, out var snapshot))
        {
            throw new DomainValidationException(
                $"The evaluation requires an FX snapshot for {normalizedSource} against {baseCurrency}.");
        }

        if (!string.Equals(snapshot.BaseCurrency, baseCurrency, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SourceCurrency, normalizedSource, StringComparison.Ordinal))
        {
            throw new DomainConflictException("The FX snapshot does not match the evaluated currency pair.");
        }

        used.Add(snapshot);
        return snapshot.Normalize(sourceAmount);
    }

    private static decimal Clamp(decimal value) =>
        SourcingCodes.Score(Math.Clamp(value, 0m, 100m));
}
