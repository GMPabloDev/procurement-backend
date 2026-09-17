using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Recalculated valid count of one covered target (<c>quotation-waiver-facts/v1</c>, REQ-05). The
/// count and the quotation versions behind it are computed from SQL by the server; the caller never
/// supplies them.
/// </summary>
public sealed record SourcingWaiverTarget(
    SourcingContentRef LineRef,
    IReadOnlyList<SourcingContentRef> QuotationRefs)
{
    public int ValidQuotations => QuotationRefs.Count;
}

/// <summary>
/// Facts that bind a quotation waiver request to what the RFQ really produced (REQ-05). A waiver is
/// only requestable while the recalculation still shows the shortage it intends to reduce, and any
/// change of request, RFQ or quote invalidates it by construction: the facts carry the exact
/// quotation versions and counts they were computed from.
/// </summary>
public sealed record SourcingWaiverFacts
{
    public const string ContractVersion = SourcingCodes.QuotationWaiverFactsContract;

    public SourcingWaiverFacts(
        Guid organizationId,
        Guid processId,
        SourcingContentRef rfqRef,
        Guid prerequisiteId,
        string requirementKey,
        Guid baseBundleId,
        string baseResultDigest,
        Guid policyVersionId,
        string policyContentDigest,
        string manifestDigest,
        int from,
        int to,
        int floor,
        IEnumerable<SourcingWaiverTarget> targets,
        DateTimeOffset computedAt)
    {
        if (organizationId == Guid.Empty || processId == Guid.Empty || baseBundleId == Guid.Empty ||
            policyVersionId == Guid.Empty || prerequisiteId == Guid.Empty)
        {
            throw new DomainValidationException("A quotation waiver requires its complete identity.");
        }

        OrganizationId = organizationId;
        ProcessId = processId;
        RfqRef = rfqRef ?? throw new DomainValidationException("A quotation waiver requires its RFQ.");
        PrerequisiteId = prerequisiteId;
        RequirementKey = SourcingCodes.Key(requirementKey, "requirement_key");
        BaseBundleId = baseBundleId;
        BaseResultDigest = SourcingCodes.Digest(baseResultDigest, "Base result digest");
        PolicyVersionId = policyVersionId;
        PolicyContentDigest = SourcingCodes.Digest(policyContentDigest, "Policy content digest");
        ManifestDigest = SourcingCodes.Digest(manifestDigest, "Manifest digest");
        var materializedTargets = (targets ?? []).ToImmutableArray();
        if (materializedTargets.Length == 0 ||
            materializedTargets.Select(target => target.LineRef.Id).Distinct().Count() !=
            materializedTargets.Length)
        {
            throw new DomainValidationException("A quotation waiver requires its distinct covered targets.");
        }

        Targets = materializedTargets.OrderBy(target => target.LineRef.Id).ToImmutableArray();
        // REQ-05: a waiver never covers a trace of zero quotations, never reduces below the persisted
        // floor and always reduces something (to < from).
        var lowestCount = Targets.Min(target => target.ValidQuotations);
        if (Targets.Any(target => target.ValidQuotations < 1))
        {
            throw new DomainConflictException(
                "A quotation waiver requires at least one valid quotation on every covered target.");
        }

        From = from;
        To = to;
        Floor = floor;
        if (floor < 1)
        {
            throw new DomainConflictException("A quotation waiver requires a positive floor.");
        }

        if (to != lowestCount)
        {
            throw new DomainConflictException(
                "The requested reduction must equal the lowest recalculated count per target.");
        }

        if (to < floor)
        {
            throw new DomainConflictException("The reduction cannot go below the persisted floor.");
        }

        if (to >= from)
        {
            throw new DomainConflictException("The reduction must be strictly below the published minimum.");
        }

        ComputedAt = computedAt.ToUniversalTime();
        Digest = ComputeDigest();
    }

    public Guid OrganizationId { get; }
    public Guid ProcessId { get; }
    public SourcingContentRef RfqRef { get; }
    public Guid PrerequisiteId { get; }
    public string RequirementKey { get; }
    public Guid BaseBundleId { get; }
    public string BaseResultDigest { get; }
    public Guid PolicyVersionId { get; }
    public string PolicyContentDigest { get; }
    public string ManifestDigest { get; }
    public int From { get; }
    public int To { get; }
    public int Floor { get; }
    public IReadOnlyList<SourcingWaiverTarget> Targets { get; }
    public DateTimeOffset ComputedAt { get; }
    public string Digest { get; }

    /// <summary>
    /// <c>quotation_waiver_facts_digest</c> of the published preimage (REQ-05). It is persisted with
    /// the facts and re-checked by the owner before signalling.
    /// </summary>
    public string ComputeDigest() => PolicyCanonicalizer.Hash(CanonicalDocument());

    /// <summary>
    /// Canonical <c>quotation-waiver-facts/v1</c> document: exactly the digest preimage, so the
    /// stored bytes can be rehashed and any tampering detected (REQ-05).
    /// </summary>
    public string CanonicalDocument()
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_bundle_id"] = BaseBundleId.ToString("D"),
            ["base_result_digest"] = BaseResultDigest,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["computed_at"] = SourcingCodes.FormatUtc(ComputedAt),
            ["contract_version"] = ContractVersion,
            ["floor"] = Floor,
            ["from"] = From,
            ["manifest_digest"] = ManifestDigest,
            ["organization_id"] = OrganizationId.ToString("D"),
            ["policy_content_digest"] = PolicyContentDigest,
            ["policy_version_id"] = PolicyVersionId.ToString("D"),
            ["prerequisite_id"] = PrerequisiteId.ToString("D"),
            ["process_id"] = ProcessId.ToString("D"),
            ["requirement_key"] = RequirementKey,
            ["rfq_ref"] = SourcingCanonicalizer.ContentRef(RfqRef),
            ["targets"] = SourcingCanonicalizer.Set(Targets.Select(target =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["line_ref"] = SourcingCanonicalizer.ContentRef(target.LineRef),
                    ["quotation_refs"] = SourcingCanonicalizer.Set(target.QuotationRefs.Select(reference =>
                        (object?)SourcingCanonicalizer.ContentRef(reference))),
                    ["valid_quotations"] = target.ValidQuotations
                })),
            ["to"] = To
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }

}

/// <summary>Operator projection of one persisted waiver request (REQ-05, REQ-14).</summary>
public sealed record SourcingWaiverView(
    Guid FactsId,
    string RequirementKey,
    int From,
    int To,
    int Floor,
    string FactsDigest,
    Guid? ApprovalCaseId,
    bool CaseCreated,
    IReadOnlyList<SourcingWaiverTarget> Targets,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);
