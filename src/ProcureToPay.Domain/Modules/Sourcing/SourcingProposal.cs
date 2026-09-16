using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>Lifecycle of one proposal version (REQ-10, REQ-11).</summary>
public enum SourcingProposalState
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Rejected = 4,
    ChangesRequested = 5,
    Cancelled = 6
}

/// <summary>
/// One line of the award candidate (<c>award-line/v1</c>, REQ-12). On the RFQ route the quantity,
/// unit, unit price and source gross total are exactly the selected quotation line; on the catalogue
/// route they come from the process line and the negotiated price. Any difference is a contract
/// violation, not a rounding question.
/// </summary>
public sealed record AwardLine
{
    public const string ContractVersion = SourcingCodes.AwardLineContract;

    public AwardLine(
        SourcingContentRef lineRef,
        decimal quantity,
        string unitCode,
        decimal unitPrice,
        string sourceCurrency,
        decimal sourceGrossTotal,
        string baseCurrency,
        decimal baseGrossTotal,
        SourcingContentRef? fxSnapshotRef)
    {
        LineRef = lineRef ?? throw new DomainValidationException("An award line requires its request line.");
        Quantity = SourcingCodes.PositiveDecimal(quantity, "Award quantity");
        UnitCode = SourcingCodes.UnitCode(unitCode, "Unit code");
        UnitPrice = SourcingCodes.PositiveDecimal(unitPrice, "Award unit price");
        SourceCurrency = SourcingCodes.Currency(sourceCurrency, "Source currency");
        SourceGrossTotal = SourcingCodes.PositiveDecimal(sourceGrossTotal, "Source gross total");
        BaseCurrency = SourcingCodes.Currency(baseCurrency, "Base currency");
        BaseGrossTotal = SourcingCodes.PositiveDecimal(baseGrossTotal, "Base gross total");
        FxSnapshotRef = fxSnapshotRef;
        if (string.Equals(SourceCurrency, BaseCurrency, StringComparison.Ordinal) && fxSnapshotRef is not null)
        {
            throw new DomainValidationException("Two identical currencies admit no FX snapshot reference.");
        }

        if (!string.Equals(SourceCurrency, BaseCurrency, StringComparison.Ordinal) && fxSnapshotRef is null)
        {
            throw new DomainValidationException("A foreign award line requires its FX snapshot reference.");
        }

        Digest = ComputeDigest();
    }

    public SourcingContentRef LineRef { get; }
    public decimal Quantity { get; }
    public string UnitCode { get; }
    public decimal UnitPrice { get; }
    public string SourceCurrency { get; }
    public decimal SourceGrossTotal { get; }
    public string BaseCurrency { get; }
    public decimal BaseGrossTotal { get; }
    public SourcingContentRef? FxSnapshotRef { get; }
    public string Digest { get; }

    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_currency"] = BaseCurrency,
            ["base_gross_total"] = SourcingCodes.Decimal(BaseGrossTotal),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ContractVersion,
            ["fx_snapshot_ref"] = FxSnapshotRef is null
                ? null
                : SourcingCanonicalizer.ContentRef(FxSnapshotRef),
            ["line_ref"] = SourcingCanonicalizer.ContentRef(LineRef),
            ["quantity"] = SourcingCodes.Decimal(Quantity),
            ["source_currency"] = SourceCurrency,
            ["source_gross_total"] = SourcingCodes.Decimal(SourceGrossTotal),
            ["unit_code"] = UnitCode,
            ["unit_price"] = SourcingCodes.Decimal(UnitPrice)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}

/// <summary>
/// One supplier candidate of a proposal (<c>award-candidate/v1</c>, REQ-12): its lines, their exact
/// sums and the terms the whole candidate shares.
/// </summary>
public sealed record AwardCandidate
{
    public const string ContractVersion = SourcingCodes.AwardCandidateContract;

    public AwardCandidate(
        SourcingEntityRef supplierRef,
        string sourceCurrency,
        string baseCurrency,
        CommercialTerms terms,
        IEnumerable<AwardLine> lines)
    {
        SupplierRef = supplierRef ?? throw new DomainValidationException("A candidate requires its supplier.");
        SourceCurrency = SourcingCodes.Currency(sourceCurrency, "Source currency");
        BaseCurrency = SourcingCodes.Currency(baseCurrency, "Base currency");
        Terms = terms ?? throw new DomainValidationException("A candidate requires its commercial terms.");
        var materialized = (lines ?? []).ToImmutableArray();
        if (materialized.Length == 0 ||
            materialized.Select(line => line.LineRef.Id).Distinct().Count() != materialized.Length)
        {
            throw new DomainValidationException("A candidate requires its distinct lines.");
        }

        Lines = materialized.OrderBy(line => line.LineRef.Id).ToImmutableArray();
        if (Lines.Any(line => !string.Equals(line.SourceCurrency, SourceCurrency, StringComparison.Ordinal) ||
                              !string.Equals(line.BaseCurrency, BaseCurrency, StringComparison.Ordinal)))
        {
            throw new DomainValidationException("Every candidate line must share its declared currencies.");
        }

        SourceAmount = Lines.Aggregate(0m, (total, line) => total + line.SourceGrossTotal);
        BaseAmount = Lines.Aggregate(0m, (total, line) => total + line.BaseGrossTotal);
    }

    public SourcingEntityRef SupplierRef { get; }
    public string SourceCurrency { get; }
    public string BaseCurrency { get; }
    public decimal SourceAmount { get; }
    public decimal BaseAmount { get; }
    public CommercialTerms Terms { get; }
    public IReadOnlyList<AwardLine> Lines { get; }

    public string ComputeDigest()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_lines"] = SourcingCanonicalizer.Set(Lines.Select(line => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["base_currency"] = line.BaseCurrency,
                ["base_gross_total"] = SourcingCodes.Decimal(line.BaseGrossTotal),
                ["fx_snapshot_ref"] = line.FxSnapshotRef is null
                    ? null
                    : SourcingCanonicalizer.ContentRef(line.FxSnapshotRef),
                ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                ["quantity"] = SourcingCodes.Decimal(line.Quantity),
                ["source_currency"] = line.SourceCurrency,
                ["source_gross_total"] = SourcingCodes.Decimal(line.SourceGrossTotal),
                ["unit_code"] = line.UnitCode,
                ["unit_price"] = SourcingCodes.Decimal(line.UnitPrice)
            })),
            ["base_amount"] = SourcingCodes.Decimal(BaseAmount),
            ["base_currency"] = BaseCurrency,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = ContractVersion,
            ["source_amount"] = SourcingCodes.Decimal(SourceAmount),
            ["source_currency"] = SourceCurrency,
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(SupplierRef),
            ["terms"] = SourcingCanonicalizer.Terms(Terms)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}

/// <summary>
/// One frozen catalogue snapshot of a proposal (<c>catalog-snapshot/v1</c>): the exact SPEC 09
/// supplier fact snapshot plus the digest that references it (REQ-06, REQ-10).
/// </summary>
public sealed record SourcingCatalogSnapshot(
    SourcingContentRef SnapshotRef,
    Guid LineId,
    int LineVersion,
    SourcingEntityRef SupplierRef,
    string CatalogContentDigest);

/// <summary>
/// <c>policy-evaluation-ref/v1</c> of one persisted Policy evaluation (REQ-10, REQ-14). The five
/// digests and the sequence are verified against the bundle before they are published.
/// </summary>
public sealed record SourcingPolicyEvaluationRef
{
    public const string ContractVersion = SourcingCodes.PolicyEvaluationRefContract;

    public SourcingPolicyEvaluationRef(
        long evaluationSequence,
        Guid id,
        string factsDigest,
        string inputDigest,
        string manifestDigest,
        string policyContentDigest,
        string resultDigest)
    {
        EvaluationSequence = evaluationSequence >= 1
            ? evaluationSequence
            : throw new DomainValidationException("A policy evaluation reference requires its sequence.");
        Id = id == Guid.Empty
            ? throw new DomainValidationException("A policy evaluation reference requires its identity.")
            : id;
        FactsDigest = SourcingCodes.Digest(factsDigest, "Facts digest");
        InputDigest = SourcingCodes.Digest(inputDigest, "Input digest");
        ManifestDigest = SourcingCodes.Digest(manifestDigest, "Manifest digest");
        PolicyContentDigest = SourcingCodes.Digest(policyContentDigest, "Policy content digest");
        ResultDigest = SourcingCodes.Digest(resultDigest, "Result digest");
    }

    public long EvaluationSequence { get; }
    public Guid Id { get; }
    public string FactsDigest { get; }
    public string InputDigest { get; }
    public string ManifestDigest { get; }
    public string PolicyContentDigest { get; }
    public string ResultDigest { get; }
}

/// <summary>Set digest of the catalogue snapshots of one proposal (<c>catalog-snapshots/v1</c>).</summary>
public static class SourcingCatalogSnapshotSet
{
    public static string ComputeDigest(
        Guid organizationId,
        Guid proposalId,
        int proposalVersion,
        IEnumerable<SourcingCatalogSnapshot> snapshots)
    {
        var materialized = (snapshots ?? []).ToImmutableArray();
        if (materialized.Select(snapshot => snapshot.LineId).Distinct().Count() != materialized.Length)
        {
            throw new DomainConflictException("A catalogue snapshot set cannot cover the same line twice.");
        }

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SourcingCodes.CatalogSnapshotsDigestContract,
            ["organization_id"] = organizationId.ToString("D"),
            ["proposal_id"] = proposalId.ToString("D"),
            ["proposal_version"] = proposalVersion,
            ["snapshots"] = SourcingCanonicalizer.Set(materialized.Select(snapshot =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalog_content_digest"] = snapshot.CatalogContentDigest,
                    ["line_id"] = snapshot.LineId.ToString("D"),
                    ["line_version"] = snapshot.LineVersion,
                    ["snapshot_ref"] = SourcingCanonicalizer.ContentRef(snapshot.SnapshotRef),
                    ["supplier_ref"] = SourcingCanonicalizer.EntityRef(snapshot.SupplierRef)
                }))
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}

/// <summary>
/// One proposal version of a sourcing decision (<c>sourcing-proposal-version/v1</c>, REQ-10). It is
/// the subject <c>SOURCING_PO</c>: the exact selected lines, their supplier, the quotation or
/// catalogue basis, the request bundle it was evaluated against and the award candidate that a
/// publication would create.
/// </summary>
public sealed record SourcingProposalVersion
{
    public const string ContractVersion = SourcingCodes.ProposalVersionContract;

    public SourcingProposalVersion(
        int version,
        int? predecessorVersion,
        Guid organizationId,
        SourcingContentRef requestRef,
        SourcingPolicyEvaluationRef requestBundleRef,
        SourcingSelectionBasis selectionBasis,
        SourcingEntityRef supplierRef,
        IReadOnlyList<SourcingContentRef> selectedLines,
        SourcingContentRef? evaluationRef,
        IReadOnlyList<SourcingContentRef> quotationRefs,
        IReadOnlyList<SourcingCatalogSnapshot> catalogSnapshots,
        Guid? waiverRef,
        CommercialTerms terms,
        AwardCandidate awardCandidate)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A proposal requires its organization.");
        }

        Version = version >= 1
            ? version
            : throw new DomainValidationException("A proposal requires a positive version.");
        PredecessorVersion = predecessorVersion;
        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first proposal version has no predecessor.");
        }

        OrganizationId = organizationId;
        RequestRef = requestRef ?? throw new DomainValidationException("A proposal requires its request.");
        RequestBundleRef = requestBundleRef
            ?? throw new DomainValidationException("A proposal requires its request policy bundle.");
        SelectionBasis = selectionBasis;
        SupplierRef = supplierRef ?? throw new DomainValidationException("A proposal requires its supplier.");
        var lines = (selectedLines ?? []).ToImmutableArray();
        if (lines.Length == 0 || lines.Select(line => line.Id).Distinct().Count() != lines.Length)
        {
            throw new DomainValidationException("A proposal requires its distinct selected lines.");
        }

        SelectedLines = lines.OrderBy(line => line.Id).ToImmutableArray();
        EvaluationRef = evaluationRef;
        QuotationRefs = (quotationRefs ?? [])
            .OrderBy(reference => reference.Id)
            .ToImmutableArray();
        CatalogSnapshots = (catalogSnapshots ?? [])
            .OrderBy(snapshot => snapshot.LineId)
            .ToImmutableArray();
        WaiverRef = waiverRef;
        Terms = terms ?? throw new DomainValidationException("A proposal requires its terms.");
        AwardCandidate = awardCandidate ?? throw new DomainValidationException("A proposal requires its award candidate.");

        if (AwardCandidate.SupplierRef.Id != SupplierRef.Id ||
            AwardCandidate.SupplierRef.Version != SupplierRef.Version)
        {
            throw new DomainConflictException("The award candidate belongs to another supplier.");
        }

        if (AwardCandidate.Lines.Count != SelectedLines.Count ||
            AwardCandidate.Lines.Any(line => !SelectedLines.Any(selected => selected.Id == line.LineRef.Id)))
        {
            throw new DomainConflictException("The award candidate must cover exactly the selected lines.");
        }

        // REQ-10: the basis decides the closed nullability of every optional reference.
        switch (selectionBasis)
        {
            case SourcingSelectionBasis.Rfq:
                if (evaluationRef is null || QuotationRefs.Count == 0)
                {
                    throw new DomainValidationException(
                        "An RFQ proposal requires its evaluation and its quotation references.");
                }

                break;
            case SourcingSelectionBasis.ApprovedCatalog:
                if (evaluationRef is not null || QuotationRefs.Count != 0 || WaiverRef is not null)
                {
                    throw new DomainValidationException(
                        "A catalogue proposal carries neither evaluation, quotations nor waiver.");
                }

                if (CatalogSnapshots.Count != SelectedLines.Count)
                {
                    throw new DomainValidationException(
                        "A catalogue proposal requires one active snapshot per selected line.");
                }

                break;
            default:
                throw new DomainValidationException("The selection basis is invalid.");
        }
    }

    public int Version { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public SourcingContentRef RequestRef { get; }
    public SourcingPolicyEvaluationRef RequestBundleRef { get; }
    public SourcingSelectionBasis SelectionBasis { get; }
    public SourcingEntityRef SupplierRef { get; }
    public IReadOnlyList<SourcingContentRef> SelectedLines { get; }
    public SourcingContentRef? EvaluationRef { get; }
    public IReadOnlyList<SourcingContentRef> QuotationRefs { get; }
    public IReadOnlyList<SourcingCatalogSnapshot> CatalogSnapshots { get; }
    public Guid? WaiverRef { get; }
    public CommercialTerms Terms { get; }
    public AwardCandidate AwardCandidate { get; }

    /// <summary><c>proposal_content_digest</c> of the published preimage (REQ-10).</summary>
    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_candidate"] = AwardCandidateDocument(AwardCandidate),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["catalog_snapshots"] = SourcingCanonicalizer.Set(CatalogSnapshots.Select(snapshot =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalog_content_digest"] = snapshot.CatalogContentDigest,
                    ["line_id"] = snapshot.LineId.ToString("D"),
                    ["line_version"] = snapshot.LineVersion,
                    ["snapshot_ref"] = SourcingCanonicalizer.ContentRef(snapshot.SnapshotRef),
                    ["supplier_ref"] = SourcingCanonicalizer.EntityRef(snapshot.SupplierRef)
                })),
            ["contract_version"] = ContractVersion,
            ["evaluation_ref"] = EvaluationRef is null ? null : SourcingCanonicalizer.ContentRef(EvaluationRef),
            ["organization_id"] = OrganizationId.ToString("D"),
            ["predecessor_version"] = PredecessorVersion,
            ["quotation_refs"] = SourcingCanonicalizer.Set(QuotationRefs.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["request_bundle_ref"] = EvaluationRefDocument(RequestBundleRef),
            ["request_ref"] = SourcingCanonicalizer.ContentRef(RequestRef),
            ["selected_lines"] = SourcingCanonicalizer.Set(SelectedLines.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["selection_basis"] = SourcingStateCodes.SelectionBasis(SelectionBasis),
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(SupplierRef),
            ["terms"] = SourcingCanonicalizer.Terms(Terms),
            ["version"] = Version,
            ["waiver_ref"] = WaiverRef?.ToString("D")
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }

    public static SortedDictionary<string, object?> AwardCandidateDocument(AwardCandidate candidate) =>
        new(StringComparer.Ordinal)
        {
            ["award_lines"] = SourcingCanonicalizer.Set(candidate.Lines.Select(line =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["base_currency"] = line.BaseCurrency,
                    ["base_gross_total"] = SourcingCodes.Decimal(line.BaseGrossTotal),
                    ["fx_snapshot_ref"] = line.FxSnapshotRef is null
                        ? null
                        : SourcingCanonicalizer.ContentRef(line.FxSnapshotRef),
                    ["line_ref"] = SourcingCanonicalizer.ContentRef(line.LineRef),
                    ["quantity"] = SourcingCodes.Decimal(line.Quantity),
                    ["source_currency"] = line.SourceCurrency,
                    ["source_gross_total"] = SourcingCodes.Decimal(line.SourceGrossTotal),
                    ["unit_code"] = line.UnitCode,
                    ["unit_price"] = SourcingCodes.Decimal(line.UnitPrice)
                })),
            ["base_amount"] = SourcingCodes.Decimal(candidate.BaseAmount),
            ["base_currency"] = candidate.BaseCurrency,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = AwardCandidate.ContractVersion,
            ["source_amount"] = SourcingCodes.Decimal(candidate.SourceAmount),
            ["source_currency"] = candidate.SourceCurrency,
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(candidate.SupplierRef),
            ["terms"] = SourcingCanonicalizer.Terms(candidate.Terms)
        };

    public static SortedDictionary<string, object?> EvaluationRefDocument(SourcingPolicyEvaluationRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["evaluation_sequence"] = reference.EvaluationSequence,
            ["facts_digest"] = reference.FactsDigest,
            ["id"] = reference.Id.ToString("D"),
            ["input_digest"] = reference.InputDigest,
            ["manifest_digest"] = reference.ManifestDigest,
            ["policy_content_digest"] = reference.PolicyContentDigest,
            ["result_digest"] = reference.ResultDigest
        };
}

/// <summary>
/// Completeness manifest of one sourcing proposal (<c>sourcing-completeness-manifest/v1</c>, REQ-10).
/// It is the preimage of <c>sourcing_manifest_digest</c> and the evidence Policy and Approval verify
/// before the proposal becomes a subject.
/// </summary>
public sealed record SourcingCompletenessManifest
{
    public const string ContractVersion = SourcingCodes.CompletenessManifestContract;

    public SourcingCompletenessManifest(
        string providerId,
        string providerContractVersion,
        Guid requestId,
        int requestVersion,
        string requestManifestDigest,
        string requestResultDigest,
        Guid requestBundleId,
        Guid sourcingProposalId,
        int sourcingProposalVersion,
        IReadOnlyList<SourcingContentRef> coveredLines,
        string? evaluationDigest,
        string quotationSetDigest,
        string catalogSnapshotsDigest,
        string awardCandidateDigest,
        string termsDigest)
    {
        ProviderId = SourcingCodes.Key(providerId, "provider_id");
        ProviderContractVersion = SourcingCodes.ContractId(providerContractVersion, "provider_contract_version");
        if (requestId == Guid.Empty || requestBundleId == Guid.Empty || sourcingProposalId == Guid.Empty)
        {
            throw new DomainValidationException("A completeness manifest requires its identities.");
        }

        RequestId = requestId;
        RequestVersion = requestVersion >= 1
            ? requestVersion
            : throw new DomainValidationException("A completeness manifest requires its request version.");
        RequestManifestDigest = SourcingCodes.Digest(requestManifestDigest, "Request manifest digest");
        RequestResultDigest = SourcingCodes.Digest(requestResultDigest, "Request result digest");
        RequestBundleId = requestBundleId;
        SourcingProposalId = sourcingProposalId;
        SourcingProposalVersion = sourcingProposalVersion >= 1
            ? sourcingProposalVersion
            : throw new DomainValidationException("A completeness manifest requires its proposal version.");
        var lines = (coveredLines ?? []).ToImmutableArray();
        if (lines.Length == 0 || lines.Select(line => line.Id).Distinct().Count() != lines.Length)
        {
            throw new DomainValidationException("A completeness manifest requires its distinct covered lines.");
        }

        CoveredLines = lines.OrderBy(line => line.Id).ToImmutableArray();
        EvaluationDigest = evaluationDigest is null
            ? null
            : SourcingCodes.Digest(evaluationDigest, "Evaluation digest");
        QuotationSetDigest = SourcingCodes.Digest(quotationSetDigest, "Quotation set digest");
        CatalogSnapshotsDigest = SourcingCodes.Digest(catalogSnapshotsDigest, "Catalogue snapshots digest");
        AwardCandidateDigest = SourcingCodes.Digest(awardCandidateDigest, "Award candidate digest");
        TermsDigest = SourcingCodes.Digest(termsDigest, "Terms digest");
    }

    public string ProviderId { get; }
    public string ProviderContractVersion { get; }
    public Guid RequestId { get; }
    public int RequestVersion { get; }
    public string RequestManifestDigest { get; }
    public string RequestResultDigest { get; }
    public Guid RequestBundleId { get; }
    public Guid SourcingProposalId { get; }
    public int SourcingProposalVersion { get; }
    public IReadOnlyList<SourcingContentRef> CoveredLines { get; }
    public string? EvaluationDigest { get; }
    public string QuotationSetDigest { get; }
    public string CatalogSnapshotsDigest { get; }
    public string AwardCandidateDigest { get; }
    public string TermsDigest { get; }

    /// <summary><c>sourcing_manifest_digest</c>: the canonical document is its own preimage (REQ-10).</summary>
    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_candidate_digest"] = AwardCandidateDigest,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["catalog_snapshots_digest"] = CatalogSnapshotsDigest,
            ["contract_version"] = ContractVersion,
            ["covered_lines"] = SourcingCanonicalizer.Set(CoveredLines.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["evaluation_digest"] = EvaluationDigest,
            ["provider_contract_version"] = ProviderContractVersion,
            ["provider_id"] = ProviderId,
            ["quotation_set_digest"] = QuotationSetDigest,
            ["request_bundle_id"] = RequestBundleId.ToString("D"),
            ["request_id"] = RequestId.ToString("D"),
            ["request_manifest_digest"] = RequestManifestDigest,
            ["request_result_digest"] = RequestResultDigest,
            ["request_version"] = RequestVersion,
            ["sourcing_proposal_id"] = SourcingProposalId.ToString("D"),
            ["sourcing_proposal_version"] = SourcingProposalVersion,
            ["terms_digest"] = TermsDigest
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }
}
