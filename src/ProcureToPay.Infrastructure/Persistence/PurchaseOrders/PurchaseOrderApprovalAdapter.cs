using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Trusted in-process adapter that turns a Purchase Order or an amendment into one idempotent
/// procurement approval submission (SPEC 11 REQ-03, DEC-03). It resolves the Purchase Request
/// ordering evidence server-side, requires the financial authorities of the completed request case,
/// and publishes a single ordinary <c>PROCUREMENT_APPROVER + PROCUREMENT</c> requirement over the
/// exact ordered lines with the Buyer excluded.
/// </summary>
public sealed class PurchaseOrderApprovalAdapter(
    ProcureToPayDbContext dbContext,
    PurchaseRequestOrderingEvidenceService orderingEvidence) : IApprovalSubmissionAdapter
{
    public const string AdapterId = PurchaseOrderCodes.ApprovalAdapterId;
    public const string ContractVersion = PurchaseOrderCodes.ApprovalAdapterVersion;
    public const string AdapterIdentity = "purchase-order-approval-adapter/v1";
    public const string SnapshotContractVersion = "purchase-order-approval-snapshot/v1";

    private readonly ApprovalAdapterDescriptor descriptorValue = new(
        AdapterId,
        PurchaseOrderCodes.PurchaseOrderSubjectType,
        PurchaseOrderCodes.IssueOperation,
        ContractVersion,
        // REQ-03: the Buyer is the requester and the originator of the order, and the requirement
        // excludes them, so the segregation of duties is enforced by the exclusion, not by the
        // submission shape.
        RequesterRequired: true,
        AllowsRequesterAsOriginator: true,
        SupersessionDeltaSupported: false);

    public ApprovalAdapterDescriptor Descriptor => descriptorValue;

    public async Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var amendment = string.Equals(
            request.SubjectType, PurchaseOrderCodes.AmendmentSubjectType, StringComparison.Ordinal);
        if (!amendment &&
            !string.Equals(request.SubjectType, PurchaseOrderCodes.PurchaseOrderSubjectType, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The purchase order approval adapter only serves PURCHASE_ORDER and "
                + "PURCHASE_ORDER_AMENDMENT submissions.");
        }

        var expectedOperation = amendment ? PurchaseOrderCodes.ApplyAmendmentOperation : PurchaseOrderCodes.IssueOperation;
        if (!string.Equals(request.Operation, expectedOperation, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The purchase order approval adapter does not serve the requested operation.");
        }

        var version = amendment
            ? await AmendmentVersionAsync(request, cancellationToken)
            : await OrderVersionAsync(request, cancellationToken);
        var lines = PurchaseOrderSerialization
            .ReadLines(version.LinesJson)
            .OrderBy(line => line.RequestLineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();
        var requestRef = new PurchaseOrderContentRef(
            version.RequestId, version.RequestVersion, version.RequestContentDigest);
        var coveredTargets = lines
            .Select(line => new OrderingEvidenceTargetRef(
                line.RequestLineRef.Id,
                line.RequestLineRef.Version))
            .ToArray();
        var evidence = await orderingEvidence.ProduceAsync(
            new OrderingEvidenceRequest(
                request.OrganizationId,
                version.RequestId,
                version.RequestVersion,
                coveredTargets,
                DateTimeOffset.UtcNow,
                PurchaseOrderCodes.DomainWorkloadIssuer,
                PurchaseOrderCodes.DomainWorkloadClientId),
            cancellationToken);
        RequireApprovedFinancials(evidence, coveredTargets);
        var snapshotDigest = SnapshotDigest(version, evidence, lines, amendment);
        // REQ-03: the material snapshot digest of every target comes from the completed request case,
        // never from the order itself, so the case approves exactly the resolved projection.
        var targets = evidence.CoveredTargets
            .Select(target => new ApprovalTarget(
                PurchaseOrderCodes.ApprovalTargetType,
                target.TargetId,
                target.TargetVersion,
                target.MaterialSnapshotDigest))
            .ToImmutableArray();
        // REQ-03: an ordinary PROCUREMENT_APPROVER requirement carries the Procurement authority of
        // the ordered amount, at the highest level the completed request case already accredited.
        var financialRank = evidence.Requirements
            .Where(candidate => candidate.AuthorityLevel is not null)
            .Select(candidate => candidate.AuthorityLevel!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var requirement = new ApprovalRequirementDefinition(
            amendment ? "PURCHASE_ORDER_AMENDMENT_PROCUREMENT" : "PURCHASE_ORDER_PROCUREMENT",
            "PROCUREMENT",
            SystemRole.ProcurementApprover,
            AuthorityRequirement.Required(
                ApprovalAuthorityType.Procurement,
                Math.Max(financialRank, 1),
                version.BaseAmount,
                version.BaseCurrency),
            DecisionScopeDescriptor.Create(
                request.OrganizationId,
                [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
            Exclusions(request),
            targets,
            ImmutableArray<ApprovalDependencyRef>.Empty);
        _ = requestRef;
        return new ApprovalSubmission(
            request.SubmissionKey,
            request.OrganizationId,
            request.SubjectType,
            request.SubjectId,
            request.SubjectVersion,
            request.Operation,
            snapshotDigest,
            request.RequesterId,
            request.OriginatorId,
            [requirement],
            []);
    }

    /// <summary>REQ-03: the Buyer and the requester never approve the obligation they created.</summary>
    private static ImmutableHashSet<Guid> Exclusions(ApprovalSubmissionRequest request)
    {
        var builder = ImmutableHashSet.CreateBuilder<Guid>();
        builder.Add(request.OriginatorId);
        if (request.RequesterId is Guid requester && requester != Guid.Empty)
        {
            builder.Add(requester);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// REQ-03: the adapter only submits when the completed request case proves its financial
    /// authorities, and every requirement of that case was approved. Evidence that is valid but
    /// insufficient is <c>422</c>; an absent, ambiguous or stale one is <c>503</c>.
    /// </summary>
    private static void RequireApprovedFinancials(
        PurchaseRequestOrderingEvidence evidence,
        IReadOnlyList<OrderingEvidenceTargetRef> coveredTargets)
    {
        _ = coveredTargets;
        if (!evidence.Requirements.Any(requirement =>
                requirement.Role == "FINANCE_APPROVER" || requirement.AuthorityType is not null))
        {
            throw new PurchaseOrderUnprocessableException(
                "The completed request case carries no financial authority for the ordered amount.");
        }

        foreach (var reference in evidence.ResultRefs)
        {
            if (!string.Equals(reference.Result, "APPROVED", StringComparison.Ordinal))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The ordered request case is not fully approved.");
            }
        }

        var covered = evidence.CoveredTargets
            .Select(target => target.CanonicalIdentity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var authority = evidence.Requirements
            .SelectMany(requirement => requirement.Targets)
            .Select(target => target.CanonicalIdentity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        if (!covered.SequenceEqual(authority, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The financial authority of the request case does not cover exactly the ordered targets.");
        }
    }

    /// <summary>
    /// <c>purchase-order-approval-snapshot/v1</c>: PO or amendment digest, award and policy references,
    /// evidence digest, targets, terms and deltas. It is the binding the approval case approves.
    /// </summary>
    private static string SnapshotDigest(
        PurchaseOrderVersionRecord version,
        PurchaseRequestOrderingEvidence evidence,
        IReadOnlyList<PurchaseOrderLine> lines,
        bool amendment)
    {
        var terms = PurchaseOrderSerialization.ReadTerms(version.TermsJson);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amendment_digest"] = amendment ? version.ContentDigest : null,
            ["award_ref"] = PurchaseOrderCanonicalizer.ContentRef(new PurchaseOrderContentRef(
                version.AwardId, version.AwardVersion, version.AwardContentDigest)),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SnapshotContractVersion,
            ["line_deltas"] = amendment
                ? PurchaseOrderCanonicalizer.Set(PurchaseOrderSerialization
                    .ReadAmendmentDeltaRefs(version.DocumentJson)
                    .Select(delta => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["change_kind"] = delta.ChangeKind,
                        ["line_ref"] = PurchaseOrderCanonicalizer.ContentRef(delta.LineRef)
                    }))
                : null,
            ["ordering_evidence_digest"] = evidence.Digest,
            ["po_content_digest"] = version.ContentDigest,
            ["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(evidence.PolicyBundleRef),
            ["targets"] = PurchaseOrderCanonicalizer.Set(evidence.CoveredTargets.Select(target =>
                (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["material_snapshot_digest"] = target.MaterialSnapshotDigest,
                    ["target_id"] = target.TargetId.ToString("D"),
                    ["target_version"] = target.TargetVersion
                })),
            ["terms_snapshot_digest"] = terms.Digest
        };
        _ = lines;
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    private async Task<PurchaseOrderVersionRecord> OrderVersionAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        var version = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.PoId == request.SubjectId &&
                          record.Version == request.SubjectVersion &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "The purchase order version does not exist.");
        var current = await dbContext.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == request.SubjectId)
            .Select(record => record.CurrentVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (current != request.SubjectVersion)
        {
            throw new ApprovalDependencyUnavailableException(
                "Only the current purchase order version can be presented.");
        }

        return version;
    }

    private async Task<PurchaseOrderVersionRecord> AmendmentVersionAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        var amendment = await dbContext.PurchaseOrderAmendments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == request.SubjectId &&
                          record.Version == request.SubjectVersion &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException("The amendment version does not exist.");
        if ((AmendmentState)amendment.State != AmendmentState.PendingApproval)
        {
            throw new ApprovalDependencyUnavailableException(
                "Only a presented amendment can be submitted to the approval workflow.");
        }

        return await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.PoId == amendment.PoId && record.Version == amendment.BasePoVersion,
                cancellationToken);
    }
}
