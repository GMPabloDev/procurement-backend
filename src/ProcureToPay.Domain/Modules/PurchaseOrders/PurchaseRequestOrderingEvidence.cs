using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>One covered target of an ordering evidence document (REQ-03).</summary>
public sealed record OrderingEvidenceTarget(Guid TargetId, int TargetVersion, string MaterialSnapshotDigest)
{
    public string MaterialSnapshotDigest { get; } =
        PurchaseOrderCodes.Digest(MaterialSnapshotDigest, "Material snapshot digest");

    public string CanonicalIdentity => $"{TargetId:D}:{TargetVersion}";
}

/// <summary>
/// One approval requirement of the completed Purchase Request case as the ordering evidence publishes
/// it: the role, the authority and the scope that admitted the decision, plus its result reference.
/// </summary>
public sealed record OrderingEvidenceRequirement(
    string Key,
    string Role,
    string Scope,
    IReadOnlyList<OrderingEvidenceTarget> Targets,
    string? AuthorityType,
    int? AuthorityLevel,
    decimal? AuthorityAmountBase,
    string? AuthorityCurrency)
{
    public string Key { get; } = PurchaseOrderCodes.Code(Key, "Requirement key");

    public string Role { get; } = PurchaseOrderCodes.Code(Role, "Requirement role");

    public IReadOnlyList<OrderingEvidenceTarget> Targets { get; } = (Targets ?? [])
        .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
        .ToImmutableArray();
}

/// <summary>One recorded decision of the completed case (REQ-03).</summary>
public sealed record OrderingEvidenceResultRef(
    Guid RequirementId,
    string Result,
    Guid? DecisionId,
    string? DecisionDigest,
    string? DecisionKey,
    IReadOnlyList<OrderingEvidenceTarget> Targets)
{
    public string Result { get; } = PurchaseOrderCodes.Code(Result, "Decision result");

    public IReadOnlyList<OrderingEvidenceTarget> Targets { get; } =
        (Targets ?? []).OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal).ToImmutableArray();
}

/// <summary>One satisfied budget prerequisite of the completed case (REQ-03).</summary>
public sealed record OrderingEvidenceBudgetRef(
    Guid PrerequisiteId,
    string Key,
    string? EvidenceReference,
    string? EvidenceDigest);

/// <summary>
/// Identity of one covered target of the request (REQ-03): the caller names the line it orders and
/// the server resolves the material snapshot digest the completed case published for it.
/// </summary>
public sealed record OrderingEvidenceTargetRef(Guid TargetId, int TargetVersion)
{
    public string CanonicalIdentity => $"{TargetId:D}:{TargetVersion}";
}

/// <summary>Request of the exact <c>purchase-request-ordering-evidence/v1</c> contract (REQ-03).</summary>
public sealed record OrderingEvidenceRequest(
    Guid OrganizationId,
    Guid RequestId,
    int RequestVersion,
    IReadOnlyList<OrderingEvidenceTargetRef> CoveredTargets,
    DateTimeOffset RequestedAt,
    string WorkloadIssuer,
    string WorkloadClientId)
{
    public IReadOnlyList<OrderingEvidenceTargetRef> CoveredTargets { get; } = (CoveredTargets ?? [])
        .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
        .ToImmutableArray();
}

/// <summary>
/// Server-produced proof that the approval case of a Purchase Request version completed with its
/// financial authorities (REQ-03, <c>purchase-request-ordering-evidence/v1</c>). The Purchase Order
/// adapter consumes it server-side and never accepts it from a caller.
/// </summary>
public sealed record PurchaseRequestOrderingEvidence
{
    public const string ContractVersion = PurchaseOrderCodes.OrderingEvidenceContract;

    public PurchaseRequestOrderingEvidence(
        Guid organizationId,
        PurchaseOrderContentRef requestRef,
        SourcingPolicyEvaluationRef policyBundleRef,
        OrderingEvidenceCaseRef caseRef,
        IReadOnlyList<OrderingEvidenceTarget> coveredTargets,
        IReadOnlyList<OrderingEvidenceRequirement> requirements,
        IReadOnlyList<OrderingEvidenceResultRef> resultRefs,
        IReadOnlyList<OrderingEvidenceBudgetRef> budgetEvidenceRefs,
        DateTimeOffset checkedAt)
    {
        OrganizationId = organizationId;
        RequestRef = requestRef ?? throw new DomainValidationException("Ordering evidence requires its request.");
        PolicyBundleRef = policyBundleRef ??
            throw new DomainValidationException("Ordering evidence requires its policy bundle.");
        CaseRef = caseRef ?? throw new DomainValidationException("Ordering evidence requires its case.");
        CoveredTargets = (coveredTargets ?? [])
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        Requirements = (requirements ?? [])
            .OrderBy(requirement => requirement.Key, StringComparer.Ordinal)
            .ToImmutableArray();
        ResultRefs = (resultRefs ?? [])
            .OrderBy(reference => reference.RequirementId)
            .ToImmutableArray();
        BudgetEvidenceRefs = (budgetEvidenceRefs ?? [])
            .OrderBy(reference => reference.PrerequisiteId)
            .ToImmutableArray();
        CheckedAt = checkedAt.ToUniversalTime();
        if (CoveredTargets.Count == 0)
        {
            throw new DomainValidationException("Ordering evidence requires at least one covered target.");
        }

        if (Requirements.Count == 0)
        {
            throw new PurchaseOrderUnprocessableException(
                "A completed ordering case requires at least one approval requirement.");
        }

        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid OrganizationId { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public SourcingPolicyEvaluationRef PolicyBundleRef { get; }
    public OrderingEvidenceCaseRef CaseRef { get; }
    public IReadOnlyList<OrderingEvidenceTarget> CoveredTargets { get; }
    public IReadOnlyList<OrderingEvidenceRequirement> Requirements { get; }
    public IReadOnlyList<OrderingEvidenceResultRef> ResultRefs { get; }
    public IReadOnlyList<OrderingEvidenceBudgetRef> BudgetEvidenceRefs { get; }
    public DateTimeOffset CheckedAt { get; }

    /// <summary><c>ordering_evidence_digest</c> of the published preimage (REQ-03).</summary>
    public string Digest { get; }

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.OrderingEvidenceDocument(this);

    /// <summary>
    /// REQ-03: the evidence must cover exactly the ordered targets, so a Purchase Order can never
    /// borrow the financial authority of another line.
    /// </summary>
    public void RequireExactCoverage(IEnumerable<PurchaseOrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var expected = lines
            .Select(line => line.RequestLineRef.CanonicalIdentity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var covered = CoveredTargets
            .Select(target => $"{target.TargetId:D}:{target.TargetVersion}")
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(covered, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The ordering evidence does not cover exactly the ordered targets.");
        }
    }
}

/// <summary><c>case_ref={case_id,case_version,content_digest,operation,subject_id,subject_type,subject_version}</c>.</summary>
public sealed record OrderingEvidenceCaseRef(
    Guid CaseId,
    int CaseVersion,
    string ContentDigest,
    string Operation,
    Guid SubjectId,
    string SubjectType,
    int SubjectVersion)
{
    public string ContentDigest { get; } = PurchaseOrderCodes.Digest(ContentDigest, "Case content digest");

    public string Operation { get; } = PurchaseOrderCodes.Code(Operation, "Case operation");

    public string SubjectType { get; } = PurchaseOrderCodes.Code(SubjectType, "Case subject type");

    /// <summary>
    /// Reproducible digest of one approval case: its identity, operation, subject version and source
    /// snapshot. Two cases of the same version never share it.
    /// </summary>
    public static string ComputeDigest(
        Guid caseId,
        int caseVersion,
        string operation,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string sourceSnapshotDigest,
        string submissionFingerprint) =>
        PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["case_id"] = caseId.ToString("D"),
                ["case_version"] = caseVersion,
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["contract_version"] = "purchase-request-approval-case/v1",
                ["operation"] = operation,
                ["source_snapshot_digest"] = sourceSnapshotDigest,
                ["subject_id"] = subjectId.ToString("D"),
                ["subject_type"] = subjectType,
                ["subject_version"] = subjectVersion,
                ["submission_fingerprint"] = submissionFingerprint
            }));
}
