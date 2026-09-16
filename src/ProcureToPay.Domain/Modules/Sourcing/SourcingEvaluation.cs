using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Immutable exchange-rate snapshot of one quotation (<c>sourcing-fx-snapshot/v1</c>, REQ-08).
/// <c>rate</c> means units of the base currency per one unit of the source currency, and a snapshot
/// is never a general FX catalog: it belongs to the evaluation that froze it.
/// </summary>
public sealed record SourcingFxSnapshot
{
    public const string ContractVersion = SourcingCodes.FxSnapshotContract;

    public SourcingFxSnapshot(
        Guid id,
        int version,
        string baseCurrency,
        string sourceCurrency,
        decimal rate,
        DateTimeOffset effectiveAt,
        string sourceReference,
        SourcingAttachmentRef attachment)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("An FX snapshot requires its identity.")
            : id;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("An FX snapshot requires a positive version.");
        BaseCurrency = SourcingCodes.Currency(baseCurrency, "Base currency");
        SourceCurrency = SourcingCodes.Currency(sourceCurrency, "Source currency");
        if (string.Equals(BaseCurrency, SourceCurrency, StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                "Two identical currencies require no FX snapshot and admit no external rate.");
        }

        if (rate <= 0)
        {
            throw new DomainValidationException("An FX rate must be strictly positive.");
        }

        if (decimal.Round(rate, 12) != rate)
        {
            throw new DomainValidationException("An FX rate admits at most twelve decimal places.");
        }

        Rate = rate;
        EffectiveAt = effectiveAt.ToUniversalTime();
        SourceReference = SourcingCodes.SourceReference(sourceReference);
        Attachment = attachment ?? throw new DomainValidationException("An FX snapshot requires its evidence.");
        Digest = ComputeDigest();
    }

    public Guid Id { get; }
    public int Version { get; }
    public string BaseCurrency { get; }
    public string SourceCurrency { get; }
    public decimal Rate { get; }
    public DateTimeOffset EffectiveAt { get; }
    public string SourceReference { get; }
    public SourcingAttachmentRef Attachment { get; }
    public string Digest { get; }

    /// <summary>
    /// <c>sourcing-fx-snapshot/v1</c> digest. Every published property is part of the preimage, so a
    /// tampered rate, instant or reference is detectable without trusting the caller (REQ-08).
    /// </summary>
    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attachment_ref"] = SourcingCanonicalizer.Attachment(Attachment),
            ["base_currency"] = BaseCurrency,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ContractVersion,
            ["effective_at"] = SourcingCodes.FormatUtc(EffectiveAt),
            ["rate"] = SourcingCodes.Decimal(Rate),
            ["source_currency"] = SourceCurrency,
            ["source_reference"] = SourceReference
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary><c>normalized_base_amount = source_amount * rate</c>, rounded to twelve decimals (REQ-08).</summary>
    public decimal Normalize(decimal sourceAmount) => SourcingCodes.Decimal12(sourceAmount * Rate);
}

/// <summary>
/// One manual score of a criterion only a human can judge (<c>manual-criterion-input/v1</c>, REQ-07).
/// Payment terms and technical compliance require a motive and evidence whenever their weight is
/// positive; the server never invents a score.
/// </summary>
public sealed record ManualCriterionInput
{
    public const string ContractVersion = "manual-criterion-input/v1";

    public ManualCriterionInput(
        SourcingEvaluationCriterion criterion,
        SourcingContentRef lineRef,
        SourcingEntityRef supplierRef,
        decimal score,
        string justification,
        SourcingAttachmentRef evidence)
    {
        if (criterion is not (SourcingEvaluationCriterion.PaymentTerms or
            SourcingEvaluationCriterion.TechnicalCompliance))
        {
            throw new DomainValidationException(
                "Only payment terms and technical compliance admit a manual score.");
        }

        Criterion = criterion;
        LineRef = lineRef ?? throw new DomainValidationException("A manual score requires its line.");
        SupplierRef = supplierRef ?? throw new DomainValidationException("A manual score requires its supplier.");
        if (score is < 0 or > 100)
        {
            throw new DomainValidationException("A manual score must be between 0 and 100.");
        }

        Score = SourcingCodes.Score(score);
        Justification = SourcingCodes.Justification(justification);
        Evidence = evidence ?? throw new DomainValidationException("A manual score requires its evidence.");
    }

    public SourcingEvaluationCriterion Criterion { get; }
    public SourcingContentRef LineRef { get; }
    public SourcingEntityRef SupplierRef { get; }
    public decimal Score { get; }
    public string Justification { get; }
    public SourcingAttachmentRef Evidence { get; }
}

/// <summary>One criterion result of one quotation on one line (<c>criterion-score/v1</c>, REQ-07).</summary>
public sealed record CriterionScore
{
    public const string ContractVersion = "criterion-score/v1";

    public CriterionScore(
        SourcingEvaluationCriterion criterion,
        decimal score,
        decimal weight,
        string inputSource,
        decimal? inputDecimal,
        string? justification)
    {
        if (inputSource is not ("SYSTEM" or "BUYER" or "SUPPLIER_MASTER"))
        {
            throw new DomainValidationException("The criterion input source is invalid.");
        }

        Criterion = criterion;
        Score = SourcingCodes.Score(score);
        Weight = weight;
        WeightedScore = SourcingCodes.Score(weight * score / 100m);
        InputSource = inputSource;
        InputDecimal = inputDecimal;
        Justification = justification;
    }

    public SourcingEvaluationCriterion Criterion { get; }
    public decimal Score { get; }
    public decimal Weight { get; }
    public decimal WeightedScore { get; }
    public string InputSource { get; }
    public decimal? InputDecimal { get; }
    public string? Justification { get; }
}

/// <summary>One scored quotation of one line (<c>quotation-score/v1</c>, REQ-07).</summary>
public sealed record QuotationScore
{
    public const string ContractVersion = "quotation-score/v1";

    public QuotationScore(
        SourcingContentRef quotationRef,
        SourcingEntityRef supplierRef,
        SourcingContentRef lineRef,
        decimal normalizedGrossAmount,
        IEnumerable<CriterionScore> criteria)
    {
        QuotationRef = quotationRef ?? throw new DomainValidationException("A quotation score requires its quote.");
        SupplierRef = supplierRef ?? throw new DomainValidationException("A quotation score requires its supplier.");
        LineRef = lineRef ?? throw new DomainValidationException("A quotation score requires its line.");
        NormalizedGrossAmount = normalizedGrossAmount;
        var materialized = (criteria ?? []).ToImmutableArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("A quotation score requires its criteria.");
        }

        Criteria = materialized.OrderBy(score => score.Criterion).ToImmutableArray();
        TotalScore = SourcingCodes.Score(Criteria.Sum(score => score.WeightedScore));
    }

    public SourcingContentRef QuotationRef { get; }
    public SourcingEntityRef SupplierRef { get; }
    public SourcingContentRef LineRef { get; }
    public decimal NormalizedGrossAmount { get; }
    public IReadOnlyList<CriterionScore> Criteria { get; }
    public decimal TotalScore { get; }
}

/// <summary>
/// Scored quotations of one line and the recommended set of that line
/// (<c>evaluation-line-result/v1</c>, REQ-07, REQ-09). Ties stay tied: the recommendation is a set
/// ordered by canonical UUID, never a silent pick.
/// </summary>
public sealed record EvaluationLineResult
{
    public const string ContractVersion = "evaluation-line-result/v1";

    public EvaluationLineResult(
        SourcingContentRef lineRef,
        IEnumerable<QuotationScore> quotationScores,
        IEnumerable<SourcingEntityRef> recommendedSuppliers)
    {
        LineRef = lineRef ?? throw new DomainValidationException("A line result requires its line.");
        var scores = (quotationScores ?? []).ToImmutableArray();
        if (scores.Length == 0)
        {
            throw new DomainValidationException("A line result requires at least one scored quotation.");
        }

        QuotationScores = scores.ToImmutableArray();
        RecommendedSuppliers = (recommendedSuppliers ?? [])
            .OrderBy(reference => reference.Id, CanonicalUuidComparer.Instance)
            .ToImmutableArray();
        if (RecommendedSuppliers.Count == 0)
        {
            throw new DomainValidationException("A line result requires its recommended suppliers.");
        }

        var highest = QuotationScores.Max(score => score.TotalScore);
        if (RecommendedSuppliers.Any(reference => !QuotationScores.Any(score =>
                score.SupplierRef.Id == reference.Id &&
                score.SupplierRef.Version == reference.Version &&
                score.TotalScore == highest)))
        {
            throw new DomainValidationException(
                "Every recommended supplier must hold the highest total score of its line.");
        }
    }

    public SourcingContentRef LineRef { get; }
    public IReadOnlyList<QuotationScore> QuotationScores { get; }
    public IReadOnlyList<SourcingEntityRef> RecommendedSuppliers { get; }
}

/// <summary>
/// Reproducible weighted evaluation of one RFQ (<c>quote-evaluation-version/v1</c>, REQ-07/REQ-08).
/// The same snapshots produce the same bytes, order and scores; changing a quotation, an FX snapshot,
/// a criterion, a weight or a manual score requires another version instead of a rewrite.
/// </summary>
public sealed record QuoteEvaluation
{
    public const string ContractVersion = SourcingCodes.QuoteEvaluationContract;

    public QuoteEvaluation(
        Guid organizationId,
        SourcingContentRef rfqRef,
        EvaluationWeightSet weights,
        IEnumerable<EvaluationLineResult> lines,
        IEnumerable<SourcingFxSnapshot> fxSnapshots,
        IEnumerable<SourcingContentRef> quotationRefs)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("An evaluation requires its organization.");
        }

        OrganizationId = organizationId;
        RfqRef = rfqRef ?? throw new DomainValidationException("An evaluation requires its RFQ.");
        Weights = weights ?? throw new DomainValidationException("An evaluation requires its weights.");
        var materializedLines = (lines ?? []).ToImmutableArray();
        if (materializedLines.Length == 0 ||
            materializedLines.Select(line => line.LineRef.Id).Distinct().Count() != materializedLines.Length)
        {
            throw new DomainValidationException("An evaluation requires one result per line.");
        }

        Lines = materializedLines.OrderBy(line => line.LineRef.Id).ToImmutableArray();
        FxSnapshots = (fxSnapshots ?? [])
            .OrderBy(snapshot => snapshot.Id)
            .ToImmutableArray();
        QuotationRefs = (quotationRefs ?? [])
            .OrderBy(reference => reference.Id)
            .ToImmutableArray();
        Recommendations = Lines
            .SelectMany(line => line.RecommendedSuppliers)
            .DistinctBy(reference => (reference.Id, reference.Version))
            .ToImmutableArray();
    }

    public Guid OrganizationId { get; }
    public SourcingContentRef RfqRef { get; }
    public EvaluationWeightSet Weights { get; }
    public IReadOnlyList<EvaluationLineResult> Lines { get; }
    public IReadOnlyList<SourcingFxSnapshot> FxSnapshots { get; }
    public IReadOnlyList<SourcingContentRef> QuotationRefs { get; }
    public IReadOnlyList<SourcingEntityRef> Recommendations { get; }

    /// <summary><c>evaluation_content_digest</c> of the published preimage (REQ-07).</summary>
    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ContractVersion,
            ["criteria"] = SourcingCanonicalizer.Set(Lines.Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                ["quotation_scores"] = SourcingCanonicalizer.Set(line.QuotationScores.Select(score =>
                    (object?)CriterionTable(score))),
                ["recommended_suppliers"] = SourcingCanonicalizer.Set(line.RecommendedSuppliers.Select(reference =>
                    (object?)SourcingCanonicalizer.EntityRef(reference)))
            })),
            ["fx_snapshots"] = SourcingCanonicalizer.Set(FxSnapshots.Select(snapshot =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["base_currency"] = snapshot.BaseCurrency,
                    ["digest"] = snapshot.Digest,
                    ["id"] = snapshot.Id.ToString("D"),
                    ["rate"] = SourcingCodes.Decimal(snapshot.Rate),
                    ["source_currency"] = snapshot.SourceCurrency,
                    ["version"] = snapshot.Version
                })),
            ["organization_id"] = OrganizationId.ToString("D"),
            ["quotation_refs"] = SourcingCanonicalizer.Set(QuotationRefs.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["recommendations"] = SourcingCanonicalizer.Set(Recommendations.Select(reference =>
                (object?)SourcingCanonicalizer.EntityRef(reference))),
            ["rfq_ref"] = SourcingCanonicalizer.ContentRef(RfqRef),
            ["scores"] = SourcingCanonicalizer.Set(Lines.Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                ["quotations"] = SourcingCanonicalizer.Set(line.QuotationScores.Select(score =>
                    (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["normalized_gross_amount"] = SourcingCodes.Decimal(score.NormalizedGrossAmount),
                        ["quotation_ref"] = SourcingCanonicalizer.ContentRef(score.QuotationRef),
                        ["supplier_ref"] = SourcingCanonicalizer.EntityRef(score.SupplierRef),
                        ["total_score"] = SourcingCodes.Decimal(score.TotalScore)
                    }))
            })),
            ["version"] = 1,
            ["weights"] = SourcingCanonicalizer.Set(Weights.Weights.Select(weight =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["criterion"] = SourcingStateCodes.Criterion(weight.Criterion),
                    ["weight"] = weight.Weight
                }))
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    private static SortedDictionary<string, object?> CriterionTable(QuotationScore score) =>
        new(StringComparer.Ordinal)
        {
            ["criteria"] = SourcingCanonicalizer.Set(score.Criteria.Select(criterion =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["criterion"] = SourcingStateCodes.Criterion(criterion.Criterion),
                    ["input_decimal"] = criterion.InputDecimal is null
                        ? null
                        : SourcingCodes.Decimal(criterion.InputDecimal.Value),
                    ["input_source"] = criterion.InputSource,
                    ["justification"] = criterion.Justification,
                    ["score"] = SourcingCodes.Decimal(criterion.Score),
                    ["weight"] = criterion.Weight,
                    ["weighted_score"] = SourcingCodes.Decimal(criterion.WeightedScore)
                })),
            ["line_ref"] = SourcingCanonicalizer.ContentRef(score.LineRef),
            ["normalized_gross_amount"] = SourcingCodes.Decimal(score.NormalizedGrossAmount),
            ["quotation_ref"] = SourcingCanonicalizer.ContentRef(score.QuotationRef),
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(score.SupplierRef),
            ["total_score"] = SourcingCodes.Decimal(score.TotalScore)
        };
}

/// <summary>Canonical UUID order: the 128-bit big-endian value of the identifier (REQ-09).</summary>
public sealed class CanonicalUuidComparer : IComparer<Guid>
{
    public static readonly CanonicalUuidComparer Instance = new();

    public int Compare(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        _ = left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        _ = right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }
}
