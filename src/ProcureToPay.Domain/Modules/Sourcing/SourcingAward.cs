using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Published approval evidence of one award (<c>policy-approval-refs</c>, REQ-12): the Policy
/// evaluation, the completed case that carried it and the case digest, never a full payload.
/// </summary>
public sealed record SourcingPolicyApprovalRef
{
    public SourcingPolicyApprovalRef(
        SourcingPolicyEvaluationRef policy,
        Guid caseId,
        int caseVersion,
        string caseDigest)
    {
        Policy = policy
            ?? throw new DomainValidationException("An approval reference requires its policy evaluation.");
        if (caseId == Guid.Empty)
        {
            throw new DomainValidationException("An approval reference requires its case.");
        }

        if (caseVersion < 1)
        {
            throw new DomainValidationException("An approval reference requires its case version.");
        }

        CaseId = caseId;
        CaseVersion = caseVersion;
        CaseDigest = SourcingCodes.Digest(caseDigest, "Case digest");
    }

    public SourcingPolicyEvaluationRef Policy { get; }
    public Guid CaseId { get; }
    public int CaseVersion { get; }
    public string CaseDigest { get; }
}

/// <summary>
/// One published award version (<c>award-version/v1</c>, REQ-12). It is append-only, references the
/// exact contractual process version that was awarded and keeps the proposal, the evaluations, the
/// quotations and the catalogue snapshots it consumed.
/// </summary>
public sealed record SourcingAwardVersion
{
    public const string ContractVersion = SourcingCodes.AwardVersionContract;

    public SourcingAwardVersion(
        int version,
        int? predecessorVersion,
        Guid organizationId,
        Guid actorUserId,
        DateTimeOffset publishedAt,
        SourcingEntityRef processRef,
        SourcingContentRef proposalRef,
        SourcingContentRef requestRef,
        SourcingSelectionBasis selectionBasis,
        SourcingEntityRef supplierRef,
        SourcingContentRef? evaluationRef,
        IReadOnlyList<SourcingContentRef> quotationRefs,
        IReadOnlyList<SourcingCatalogSnapshot> catalogSnapshots,
        Guid? waiverRef,
        IReadOnlyList<SourcingPolicyApprovalRef> policyApprovalRefs,
        CommercialTerms terms,
        AwardCandidate awardCandidate,
        string reason)
    {
        if (organizationId == Guid.Empty || actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("An award requires its complete actor identity.");
        }

        Version = version >= 1
            ? version
            : throw new DomainValidationException("An award requires a positive version.");
        PredecessorVersion = predecessorVersion;
        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first award version has no predecessor.");
        }

        OrganizationId = organizationId;
        ActorUserId = actorUserId;
        PublishedAt = publishedAt.ToUniversalTime();
        ProcessRef = processRef ?? throw new DomainValidationException("An award requires its process.");
        ProposalRef = proposalRef ?? throw new DomainValidationException("An award requires its proposal.");
        RequestRef = requestRef ?? throw new DomainValidationException("An award requires its request.");
        SelectionBasis = selectionBasis;
        SupplierRef = supplierRef ?? throw new DomainValidationException("An award requires its supplier.");
        EvaluationRef = evaluationRef;
        QuotationRefs = (quotationRefs ?? []).OrderBy(reference => reference.Id).ToImmutableArray();
        CatalogSnapshots = (catalogSnapshots ?? []).OrderBy(snapshot => snapshot.LineId).ToImmutableArray();
        WaiverRef = waiverRef;
        var approvals = (policyApprovalRefs ?? []).ToImmutableArray();
        if (approvals.Length == 0)
        {
            throw new DomainValidationException("An award requires its policy approval evidence.");
        }

        PolicyApprovalRefs = approvals;
        Terms = terms ?? throw new DomainValidationException("An award requires its terms.");
        AwardCandidate = awardCandidate ?? throw new DomainValidationException("An award requires its lines.");
        Reason = SourcingCodes.Reason(reason);

        if (AwardCandidate.SupplierRef.Id != SupplierRef.Id ||
            AwardCandidate.SupplierRef.Version != SupplierRef.Version)
        {
            throw new DomainConflictException("The award lines belong to another supplier.");
        }

        // REQ-12: the basis keeps the same closed nullability the proposal declared.
        switch (selectionBasis)
        {
            case SourcingSelectionBasis.Rfq:
                if (evaluationRef is null || QuotationRefs.Count == 0)
                {
                    throw new DomainValidationException(
                        "An RFQ award requires its evaluation and quotation references.");
                }

                break;
            case SourcingSelectionBasis.ApprovedCatalog:
                if (evaluationRef is not null || QuotationRefs.Count != 0 || WaiverRef is not null)
                {
                    throw new DomainValidationException(
                        "A catalogue award carries no evaluation, quotations or waiver.");
                }

                if (CatalogSnapshots.Count != AwardCandidate.Lines.Count)
                {
                    throw new DomainValidationException(
                        "A catalogue award requires one active snapshot per awarded line.");
                }

                break;
            default:
                throw new DomainValidationException("The selection basis is invalid.");
        }
    }

    public int Version { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset PublishedAt { get; }
    public SourcingEntityRef ProcessRef { get; }
    public SourcingContentRef ProposalRef { get; }
    public SourcingContentRef RequestRef { get; }
    public SourcingSelectionBasis SelectionBasis { get; }
    public SourcingEntityRef SupplierRef { get; }
    public SourcingContentRef? EvaluationRef { get; }
    public IReadOnlyList<SourcingContentRef> QuotationRefs { get; }
    public IReadOnlyList<SourcingCatalogSnapshot> CatalogSnapshots { get; }
    public Guid? WaiverRef { get; }
    public IReadOnlyList<SourcingPolicyApprovalRef> PolicyApprovalRefs { get; }
    public CommercialTerms Terms { get; }
    public AwardCandidate AwardCandidate { get; }
    public string Reason { get; }

    public IReadOnlyList<SourcedRef> Lines => AwardCandidate.Lines
        .Select(line => new SourcedRef(line.LineRef.Id, line.LineRef.Version))
        .ToImmutableArray();

    /// <summary><c>award_content_digest</c> of the published preimage (REQ-12).</summary>
    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = ActorUserId.ToString("D"),
            ["award_lines"] = SourcingCanonicalizer.Set(AwardCandidate.Lines.Select(line =>
                (object?)SourcingProposalVersion.AwardLineDocument(line))),
            ["base_amount"] = SourcingCodes.Decimal(AwardCandidate.BaseAmount),
            ["base_currency"] = AwardCandidate.BaseCurrency,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["catalog_snapshots"] = SourcingCanonicalizer.Set(CatalogSnapshots.Select(snapshot =>
                (object?)SourcingCatalogSnapshotDocument(snapshot))),
            ["contract_version"] = ContractVersion,
            ["evaluation_ref"] = EvaluationRef is null ? null : SourcingCanonicalizer.ContentRef(EvaluationRef),
            ["organization_id"] = OrganizationId.ToString("D"),
            ["policy_approval_refs"] = SourcingCanonicalizer.Set(PolicyApprovalRefs.Select(reference =>
                (object?)PolicyApprovalRefDocument(reference))),
            ["predecessor_version"] = PredecessorVersion,
            ["process_ref"] = SourcingCanonicalizer.EntityRef(ProcessRef),
            ["proposal_ref"] = SourcingCanonicalizer.ContentRef(ProposalRef),
            ["published_at"] = SourcingCodes.FormatUtc(PublishedAt),
            ["quotation_refs"] = SourcingCanonicalizer.Set(QuotationRefs.Select(reference =>
                (object?)SourcingCanonicalizer.ContentRef(reference))),
            ["reason"] = Reason,
            ["request_ref"] = SourcingCanonicalizer.ContentRef(RequestRef),
            ["source_amount"] = SourcingCodes.Decimal(AwardCandidate.SourceAmount),
            ["source_currency"] = AwardCandidate.SourceCurrency,
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(SupplierRef),
            ["terms"] = SourcingCanonicalizer.Terms(Terms),
            ["version"] = Version,
            ["waiver_ref"] = WaiverRef?.ToString("D")
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }

    internal static SortedDictionary<string, object?> SourcingCatalogSnapshotDocument(
        SourcingCatalogSnapshot snapshot) =>
        new(StringComparer.Ordinal)
        {
            ["catalog_content_digest"] = snapshot.CatalogContentDigest,
            ["line_id"] = snapshot.LineId.ToString("D"),
            ["line_version"] = snapshot.LineVersion,
            ["snapshot_ref"] = SourcingCanonicalizer.ContentRef(snapshot.SnapshotRef),
            ["supplier_ref"] = SourcingCanonicalizer.EntityRef(snapshot.SupplierRef)
        };

    internal static SortedDictionary<string, object?> PolicyApprovalRefDocument(SourcingPolicyApprovalRef reference) =>
        new(StringComparer.Ordinal)
        {
            ["case_digest"] = reference.CaseDigest,
            ["case_id"] = reference.CaseId.ToString("D"),
            ["case_version"] = reference.CaseVersion,
            ["policy_ref"] = SourcingProposalVersion.EvaluationRefDocument(reference.Policy)
        };
}

/// <summary>Minimal identity of one awarded line, used by the uniqueness rules of REQ-12.</summary>
public sealed record SourcedRef(Guid Id, int Version);

/// <summary>Request of the exact <c>award-consumption/v1</c> contract (REQ-14).</summary>
public sealed record AwardConsumptionRequest(
    Guid OrganizationId,
    Guid AwardId,
    int AwardVersion,
    string AwardContentDigest,
    Guid RequestId,
    int RequestVersion,
    IReadOnlyList<SourcedRef> CoveredLines,
    DateTimeOffset RequestedAt,
    string WorkloadIssuer,
    string WorkloadClientId);

/// <summary>Response of the exact <c>award-consumption/v1</c> contract (REQ-14).</summary>
public sealed record AwardConsumptionResponse(
    SourcingContentRef AwardRef,
    SourcingContentRef ProposalRef,
    SourcingContentRef RequestRef,
    SourcingEntityRef SupplierRef,
    IReadOnlyList<AwardLine> AwardLines,
    IReadOnlyList<SourcedRef> CoveredLines,
    string SourceCurrency,
    decimal SourceAmount,
    string BaseCurrency,
    decimal BaseAmount,
    IReadOnlyList<SourcingCatalogSnapshot> CatalogSnapshots,
    string CatalogSnapshotsDigest,
    IReadOnlyList<SourcingPolicyEvaluationRef> FxSnapshots,
    SourcingPolicyEvaluationRef PolicyBundleRef,
    CommercialTerms Terms,
    DateTimeOffset EligibleAt);

/// <summary>Illegal or ineligible award consumption: it maps to <c>422</c> (REQ-14).</summary>
public sealed class AwardNotEligibleException(string message) : DomainException(message);
