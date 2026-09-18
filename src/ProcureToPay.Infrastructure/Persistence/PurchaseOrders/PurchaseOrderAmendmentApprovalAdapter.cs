using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.PurchaseOrders;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Trusted in-process adapter that presents one Purchase Order amendment to the approval workflow
/// (SPEC 11 REQ-03, REQ-05). It is the same exactly-one Procurement adapter as the order, bound to
/// the amendment subject type and operation so the adapter registry resolves it without ambiguity.
/// </summary>
public sealed class PurchaseOrderAmendmentApprovalAdapter(
    ProcureToPayDbContext dbContext,
    PurchaseRequestOrderingEvidenceService orderingEvidence) : IApprovalSubmissionAdapter
{
    private readonly PurchaseOrderApprovalAdapter inner = new(dbContext, orderingEvidence);

    private readonly ApprovalAdapterDescriptor descriptorValue = new(
        PurchaseOrderCodes.ApprovalAdapterId,
        PurchaseOrderCodes.AmendmentSubjectType,
        PurchaseOrderCodes.ApplyAmendmentOperation,
        PurchaseOrderCodes.ApprovalAdapterVersion,
        RequesterRequired: true,
        AllowsRequesterAsOriginator: true,
        SupersessionDeltaSupported: false);

    public ApprovalAdapterDescriptor Descriptor => descriptorValue;

    public Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default) =>
        inner.BuildAsync(request, cancellationToken);
}
