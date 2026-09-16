using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// Trusted in-process adapter that turns one persisted supplier or catalogue proposal into the
/// single approval requirement its governance demands (SPEC 09 REQ-03, REQ-07): exactly one
/// <c>PROCUREMENT_APPROVER</c> task with a SUPPLIER_MASTER authority over the whole organization,
/// excluding the editor and the originator. The material snapshot is derived from the persisted
/// candidate, never accepted from the caller.
/// </summary>
public sealed class SupplierGovernanceApprovalAdapter(
    ProcureToPayDbContext dbContext,
    IConfiguration configuration,
    string subjectType,
    string operation,
    string targetType) : IApprovalSubmissionAdapter
{
    private readonly ApprovalAdapterDescriptor descriptorValue = SupplierApprovalTargets.Descriptor(
        subjectType, operation);

    public ApprovalAdapterDescriptor Descriptor => descriptorValue;

    /// <summary>Minimum SUPPLIER_MASTER rank accepted as approver; lowest configured level by default.</summary>
    public int MinimumAuthorityRank =>
        int.TryParse(configuration["Supplier:Governance:MinimumSupplierMasterRank"], out var rank) && rank >= 1
            ? rank
            : 1;

    /// <summary>
    /// Material snapshot of one candidate, derived exclusively from persisted rows. Both the adapter
    /// and the result consumer recompute it, so a forged decided target cannot move a pointer.
    /// </summary>
    public static async Task<string> ComputeMaterialDigestAsync(
        ProcureToPayDbContext dbContext,
        SupplierChangeProposalRecord proposal,
        string targetType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(proposal);
        var candidateVersion = proposal.CandidateVersion
            ?? throw new ApprovalDependencyUnavailableException("The supplier proposal has no candidate.");
        var requestedStatus = proposal.RequestedStatus is null
            ? "ACTIVE"
            : SupplierStatusCodes.Code((SupplierOperationalStatus)proposal.RequestedStatus.Value);
        var changeKind = targetType == SupplierCodes.CatalogApprovalTargetType
            ? "CATALOG"
            : SupplierApprovalTargets.ChangeKindCode((SupplierChangeKind)proposal.ChangeKind);
        var candidateContentDigest = targetType == SupplierCodes.CatalogApprovalTargetType
            ? await dbContext.ApprovedSupplierCatalogVersions
                .AsNoTracking()
                .Where(row => row.CatalogEntryId == proposal.SupplierId && row.Version == candidateVersion)
                .Select(row => row.ContentDigest)
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SupplierVersions
                .AsNoTracking()
                .Where(row => row.SupplierId == proposal.SupplierId && row.Version == candidateVersion)
                .Select(row => row.ContentDigest)
                .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(candidateContentDigest))
        {
            throw new ApprovalDependencyUnavailableException(
                "The persisted candidate version of the supplier change is missing.");
        }

        return SupplierApprovalTargets.MaterialDigest(
            proposal.OrganizationId,
            proposal.SupplierId,
            candidateVersion,
            candidateContentDigest,
            proposal.BaseVersion,
            changeKind,
            requestedStatus,
            SupplierPersistenceService.ReadSensitiveFields(proposal.SensitiveFieldsJson));
    }

    public async Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SubjectType, subjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Operation, operation, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The supplier governance adapter only serves its declared subject and operation.");
        }

        var proposal = await dbContext.SupplierChangeProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.OrganizationId == request.OrganizationId &&
                       row.SupplierId == request.SubjectId &&
                       row.CandidateVersion == request.SubjectVersion &&
                       (row.State == (int)SupplierProposalState.Draft ||
                        row.State == (int)SupplierProposalState.Pending),
                cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "The persisted supplier proposal is required to submit this change.");
        var editor = proposal.EditorUserId;
        var materialDigest = await ComputeMaterialDigestAsync(dbContext, proposal, targetType, cancellationToken);
        var target = new ApprovalTarget(targetType, request.SubjectId, request.SubjectVersion, materialDigest);
        // The governance requirement covers the whole organization, so the canonical descriptor
        // carries the single explicit ORGANIZATION entry (SPEC 03 REQ-02).
        var scope = DecisionScopeDescriptor.Create(
            request.OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);
        var requirementKey = targetType == SupplierCodes.CatalogApprovalTargetType
            ? ApprovedSupplierCatalogGovernanceService.RequirementKey
            : SupplierGovernanceService.RequirementKey;
        var stage = targetType == SupplierCodes.CatalogApprovalTargetType
            ? ApprovedSupplierCatalogGovernanceService.RequirementStage
            : SupplierGovernanceService.RequirementStage;
        var requirement = SupplierPersistenceService.ApprovalRequirement(
            requirementKey,
            stage,
            scope,
            MinimumAuthorityRank,
            editor,
            request.OriginatorId,
            [target]);
        return new ApprovalSubmission(
            request.SubmissionKey,
            request.OrganizationId,
            subjectType,
            request.SubjectId,
            request.SubjectVersion,
            operation,
            materialDigest,
            editor,
            request.OriginatorId,
            [requirement],
            []);
    }
}
