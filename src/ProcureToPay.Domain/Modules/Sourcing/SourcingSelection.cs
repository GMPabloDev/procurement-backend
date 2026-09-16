using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// One human selection of a line (<c>sourcing-selection/v1</c>, REQ-09). The recommended set is
/// decided by the server from the frozen evaluation; selecting outside it is only possible with a
/// recorded deviation justification, never with a caller-supplied flag.
/// </summary>
public sealed record SourcingSelection
{
    public const string ContractVersion = "sourcing-selection/v1";

    private SourcingSelection(
        SourcingContentRef lineRef,
        SourcingContentRef quotationRef,
        SourcingEntityRef supplierRef,
        bool recommended,
        string? deviationJustification)
    {
        LineRef = lineRef;
        QuotationRef = quotationRef;
        SupplierRef = supplierRef;
        Recommended = recommended;
        DeviationJustification = deviationJustification;
    }

    public SourcingContentRef LineRef { get; }
    public SourcingContentRef QuotationRef { get; }
    public SourcingEntityRef SupplierRef { get; }
    public bool Recommended { get; }
    public string? DeviationJustification { get; }

    /// <summary>
    /// Rehydrates a persisted selection with the same closed rules: a recommended supplier never
    /// carries a justification and a deviation always does (REQ-09).
    /// </summary>
    public static SourcingSelection Stored(
        SourcingContentRef lineRef,
        SourcingContentRef quotationRef,
        SourcingEntityRef supplierRef,
        bool recommended,
        string? deviationJustification)
    {
        ArgumentNullException.ThrowIfNull(lineRef);
        ArgumentNullException.ThrowIfNull(quotationRef);
        ArgumentNullException.ThrowIfNull(supplierRef);
        if (recommended && !string.IsNullOrWhiteSpace(deviationJustification))
        {
            throw new DomainValidationException("A recommended supplier carries no deviation justification.");
        }

        return new SourcingSelection(
            lineRef,
            quotationRef,
            supplierRef,
            recommended,
            recommended ? null : SourcingCodes.Justification(deviationJustification));
    }

    /// <summary>
    /// Builds the selection of one line from the frozen evaluation. The server decides whether the
    /// supplier is recommended and demands the justification exactly when it is not (REQ-09).
    /// </summary>
    public static SourcingSelection Create(
        EvaluationLineResult line,
        SourcingEntityRef supplierRef,
        string? deviationJustification)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(supplierRef);
        var score = line.QuotationScores.SingleOrDefault(candidate =>
            candidate.SupplierRef.Id == supplierRef.Id && candidate.SupplierRef.Version == supplierRef.Version)
            ?? throw new DomainConflictException(
                "The selected supplier has no scored quotation on that line.");
        var recommended = line.RecommendedSuppliers.Any(candidate =>
            candidate.Id == supplierRef.Id && candidate.Version == supplierRef.Version);
        if (recommended)
        {
            if (!string.IsNullOrWhiteSpace(deviationJustification))
            {
                throw new DomainValidationException(
                    "A recommended supplier carries no deviation justification.");
            }

            return new SourcingSelection(line.LineRef, score.QuotationRef, supplierRef, true, null);
        }

        return new SourcingSelection(
            line.LineRef,
            score.QuotationRef,
            supplierRef,
            false,
            SourcingCodes.Justification(deviationJustification));
    }
}

/// <summary>
/// One selection with the commercial facts of its quotation, used to partition the selected lines
/// into proposals (REQ-09, <c>Compatibility de líneas y hechos Supplier</c>).
/// </summary>
public sealed record SelectionCandidate(
    SourcingSelection Selection,
    string Currency,
    CommercialTerms Terms);

/// <summary>One group of selected lines that can live in a single proposal (REQ-09).</summary>
public sealed record SourcingProposalDraft(
    SourcingEntityRef SupplierRef,
    string Currency,
    CommercialTerms Terms,
    IReadOnlyList<SourcingSelection> Selections);

/// <summary>
/// Partition of selected lines (REQ-09): lines selected for the same supplier, currency and
/// compatible terms may share one proposal; any difference splits the set instead of silently
/// forcing an award the contract does not allow.
/// </summary>
public static class SourcingSelectionPartition
{
    public static IReadOnlyList<SourcingProposalDraft> Partition(IEnumerable<SelectionCandidate> candidates)
    {
        var materialized = (candidates ?? []).ToImmutableArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("A selection set requires at least one selected line.");
        }

        if (materialized.Select(candidate => candidate.Selection.LineRef.Id).Distinct().Count() !=
            materialized.Length)
        {
            throw new DomainConflictException("A line can only be selected once per selection set.");
        }

        return materialized
            .GroupBy(candidate => new
            {
                candidate.SupplierRef().Id,
                candidate.SupplierRef().Version,
                candidate.Currency,
                candidate.Terms.DeliveryDays,
                candidate.Terms.IncotermCode,
                candidate.Terms.PaymentTermsCode,
                candidate.Terms.WarrantyDays
            })
            .Select(group => new SourcingProposalDraft(
                group.First().SupplierRef(),
                group.Key.Currency,
                group.First().Terms,
                group.Select(candidate => candidate.Selection)
                    .OrderBy(selection => selection.LineRef.Id)
                    .ToImmutableArray()))
            .OrderBy(draft => draft.SupplierRef.Id, CanonicalUuidComparer.Instance)
            .ThenBy(draft => draft.Currency, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}

internal static class SelectionCandidateExtensions
{
    public static SourcingEntityRef SupplierRef(this SelectionCandidate candidate) =>
        candidate.Selection.SupplierRef;
}
