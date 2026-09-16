using System.Collections.Immutable;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Reader of the persisted <c>quote-evaluation-version/v1</c> document (SPEC 10 REQ-07, REQ-09). It
/// rehashes the stored bytes before trusting them and rebuilds the typed line result the selection
/// needs, so the recommended set always comes from the frozen evaluation instead of the caller.
/// </summary>
public static class SourcingEvaluationDocument
{
    public static EvaluationLineResult ReadLineResult(string documentJson, Guid lineId)
    {
        var root = Parse(documentJson);
        var lines = root.GetProperty("criteria");
        foreach (var line in lines.EnumerateArray())
        {
            var lineRef = ReadContentRef(line.GetProperty("line_ref"));
            if (lineRef.Id != lineId)
            {
                continue;
            }

            var scores = line.GetProperty("quotation_scores").EnumerateArray()
                .Select(ReadQuotationScore)
                .ToArray();
            var recommended = line.GetProperty("recommended_suppliers").EnumerateArray()
                .Select(ReadEntityRef)
                .ToArray();
            return new EvaluationLineResult(lineRef, scores, recommended);
        }

        throw new DomainNotFoundException("The evaluation does not cover the selected line.");
    }

    /// <summary>Every covered line reference of the document, in canonical order.</summary>
    public static IReadOnlyList<SourcingContentRef> ReadLineRefs(string documentJson)
    {
        var root = Parse(documentJson);
        return root.GetProperty("criteria").EnumerateArray()
            .Select(line => ReadContentRef(line.GetProperty("line_ref")))
            .DistinctBy(reference => reference.Id)
            .ToArray();
    }

    private static QuotationScore ReadQuotationScore(JsonElement element)
    {
        var lineRef = ReadContentRef(element.GetProperty("line_ref"));
        var quotationRef = ReadContentRef(element.GetProperty("quotation_ref"));
        var supplierRef = ReadEntityRef(element.GetProperty("supplier_ref"));
        var normalized = decimal.Parse(
            element.GetProperty("normalized_gross_amount").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var criteria = element.GetProperty("criteria").EnumerateArray()
            .Select(criterion => ReadCriterion(criterion, normalized))
            .ToArray();
        var score = new QuotationScore(quotationRef, supplierRef, lineRef, normalized, criteria);
        var total = decimal.Parse(
            element.GetProperty("total_score").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        if (score.TotalScore != total)
        {
            throw new SourcingDependencyUnavailableException("The stored evaluation is corrupted.");
        }

        return score;
    }

    private static CriterionScore ReadCriterion(JsonElement element, decimal _)
    {
        var criterion = element.GetProperty("criterion").GetString()! switch
        {
            "PRICE" => SourcingEvaluationCriterion.Price,
            "DELIVERY_TIME" => SourcingEvaluationCriterion.DeliveryTime,
            "WARRANTY" => SourcingEvaluationCriterion.Warranty,
            "PAYMENT_TERMS" => SourcingEvaluationCriterion.PaymentTerms,
            "TECHNICAL_COMPLIANCE" => SourcingEvaluationCriterion.TechnicalCompliance,
            "SUPPLIER_PERFORMANCE" => SourcingEvaluationCriterion.SupplierPerformance,
            _ => throw new SourcingDependencyUnavailableException("The stored evaluation is corrupted.")
        };
        var input = element.GetProperty("input_decimal");
        var justification = element.GetProperty("justification");
        return new CriterionScore(
            criterion,
            decimal.Parse(element.GetProperty("score").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture),
            element.GetProperty("weight").GetInt32(),
            element.GetProperty("input_source").GetString()!,
            input.ValueKind == JsonValueKind.Null
                ? null
                : decimal.Parse(input.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            justification.ValueKind == JsonValueKind.Null ? null : justification.GetString());
    }

    private static SourcingContentRef ReadContentRef(JsonElement element) => new(
        element.GetProperty("id").GetGuid(),
        element.GetProperty("version").GetInt32(),
        element.GetProperty("content_digest").GetString()!);

    private static SourcingEntityRef ReadEntityRef(JsonElement element) => new(
        element.GetProperty("id").GetGuid(),
        element.GetProperty("version").GetInt32());

    private static JsonElement Parse(string documentJson)
    {
        try
        {
            return JsonDocument.Parse(documentJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SourcingDependencyUnavailableException("The stored evaluation is corrupted.");
        }
    }
}
