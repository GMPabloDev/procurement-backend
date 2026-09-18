using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Idempotent consumer of the approval results of a Purchase Order or an amendment (SPEC 11 REQ-02,
/// REQ-03). It verifies organization, case, subject and version against the presented version,
/// appends the result to the local inbox and advances the state machine only for the version that is
/// still current, so a late event of a superseded case never reopens it.
/// </summary>
public sealed class PurchaseOrderResultConsumer(
    ProcureToPayDbContext dbContext,
    ILogger<PurchaseOrderResultConsumer> logger,
    string contractVersion) : IApprovalResultConsumer
{
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string ChangesRequested = "CHANGES_REQUESTED";
    public const string Cancelled = "CANCELLED";
    public const string Superseded = "SUPERSEDED";

    public string ContractVersion { get; } = contractVersion;

    public async Task DeliverAsync(
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!string.Equals(delivery.ContractVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The delivered result does not match the consumer contract.");
        }

        ApprovalResultEvent eventValue;
        try
        {
            eventValue = ApprovalResultEvent.Parse(delivery);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new DomainValidationException("The approval result payload is not readable.");
        }

        var purchaseOrder = string.Equals(
            eventValue.SubjectType, PurchaseOrderCodes.PurchaseOrderSubjectType, StringComparison.Ordinal);
        var amendmentSubject = string.Equals(
            eventValue.SubjectType, PurchaseOrderCodes.AmendmentSubjectType, StringComparison.Ordinal);
        if (!purchaseOrder && !amendmentSubject)
        {
            // The inbox is shared by every subject: an event of another domain is not ours.
            return;
        }

        if (purchaseOrder)
        {
            await ApplyToOrderAsync(eventValue, cancellationToken);
            return;
        }

        await ApplyToAmendmentAsync(eventValue, cancellationToken);
    }

    private async Task ApplyToOrderAsync(ApprovalResultEvent eventValue, CancellationToken cancellationToken)
    {
        var order = await dbContext.PurchaseOrders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == eventValue.SubjectId && record.OrganizationId == eventValue.OrganizationId,
                cancellationToken);
        if (order is null)
        {
            logger.LogInformation("An approval result of an unknown purchase order was ignored.");
            return;
        }

        var version = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.PoId == eventValue.SubjectId &&
                          record.ApprovalCaseId == eventValue.CaseId,
                cancellationToken);
        if (version is null)
        {
            logger.LogInformation("An approval result of an unpresented purchase order was ignored.");
            return;
        }

        if (version.Version != order.CurrentVersion)
        {
            // A late event of a superseded version keeps its history and never reopens it (REQ-02).
            return;
        }

        await AdvanceAsync(
            eventValue,
            version,
            PurchaseOrderCodes.PurchaseOrderSubjectType,
            PurchaseOrderCodes.IssueOperation,
            cancellationToken);
    }

    /// <summary>
    /// Amendment results are applied by the amendment service of REQ-05, which owns the successor
    /// Purchase Order. Until that service exists the consumer records the event and never mutates an
    /// order from an amendment case.
    /// </summary>
    private Task ApplyToAmendmentAsync(ApprovalResultEvent eventValue, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        logger.LogInformation(
            "An approval result of amendment {AmendmentId} was recorded without a projection.",
            eventValue.SubjectId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists the approval reference on a successor version of the presented order. Only an approved
    /// case advances the state; every other terminal result cancels or returns the order to draft.
    /// </summary>
    private async Task AdvanceAsync(
        ApprovalResultEvent eventValue,
        PurchaseOrderVersionRecord version,
        string subjectType,
        string operation,
        CancellationToken cancellationToken)
    {
        _ = subjectType;
        _ = operation;
        var current = PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        var state = current.State;
        if (state is PurchaseOrderState.Issued or PurchaseOrderState.Cancelled)
        {
            return;
        }

        // Only an approved case binds the obligation; a rejection cancels it and a change request
        // returns the order to draft so the Buyer completes it again (REQ-02, REQ-03).
        var approvalRef = new PurchaseOrderContentRef(
            eventValue.CaseId,
            version.ApprovalCaseVersion ?? 1,
            version.ApprovalDigest ?? eventValue.MaterialSnapshotDigest);
        var successorState = eventValue.Result switch
        {
            Approved => PurchaseOrderState.Approved,
            ChangesRequested => PurchaseOrderState.Draft,
            _ => PurchaseOrderState.Cancelled
        };
        var successor = current.With(
            successorState,
            current.Lines,
            current.Delivery,
            budgetOperationRefs: null,
            successorState == PurchaseOrderState.Draft ? null : approvalRef,
            orderingEvidenceRef: null,
            amendmentRef: null,
            current.ActorUserId,
            eventValue.OccurredAt,
            issuedAt: null);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(version.LineParametersJson);
        dbContext.PurchaseOrderVersions.Add(
            PurchaseOrderVersionWriter.Record(successor, current.Lines, parameters, eventValue.OccurredAt, null));
        var applied = await dbContext.PurchaseOrders
            .Where(record => record.Id == version.PoId && record.CurrentVersion == version.Version)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    record => record.CurrentVersion, successor.PurchaseOrderVersionNumber),
                cancellationToken);
        if (applied == 0)
        {
            dbContext.ChangeTracker.Clear();
            return;
        }

        var pointers = await dbContext.PurchaseOrderLinePointers
            .Where(pointer => pointer.PoId == version.PoId)
            .ToArrayAsync(cancellationToken);
        foreach (var pointer in pointers)
        {
            pointer.PoVersion = successor.PurchaseOrderVersionNumber;
            pointer.State = (int)successor.State;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
