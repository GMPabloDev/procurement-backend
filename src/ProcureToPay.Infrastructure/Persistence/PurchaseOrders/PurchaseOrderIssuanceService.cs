using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>Command that presents one draft version of a Purchase Order (REQ-02, REQ-03).</summary>
public sealed record SubmitPurchaseOrderCommand(
    Guid OrganizationId,
    Guid PoId,
    int ExpectedPoVersion,
    string SubmissionKey,
    Guid OriginatorId,
    string CorrelationReference);

/// <summary>Command that cancels a Purchase Order before it is issued (REQ-01, REQ-02).</summary>
public sealed record CancelPurchaseOrderCommand(
    Guid OrganizationId,
    Guid PoId,
    int ExpectedPoVersion,
    string CancelKey,
    string Reason,
    Guid ActorUserId);

/// <summary>Command that issues an approved Purchase Order (REQ-04).</summary>
public sealed record IssuePurchaseOrderCommand(
    Guid OrganizationId,
    Guid PoId,
    int ExpectedPoVersion,
    string IssueKey,
    string CorrelationReference);

/// <summary>
/// Lifecycle of a Purchase Order (SPEC 11 REQ-02, REQ-03, REQ-04): the state machine only advances
/// through append-only successors, the approval case is submitted with the exact ordering evidence of
/// the request, and the emission posts exactly one <c>COMMIT</c> per ordered amount before the order
/// becomes <c>ISSUED</c>.
/// </summary>
public sealed class PurchaseOrderIssuanceService(
    ProcureToPayDbContext dbContext,
    PurchaseOrderBudgetProducer budget,
    PurchaseRequestLineTakeoverService takeovers,
    ApprovalSubmissionService submissions,
    PurchaseRequestOrderingEvidenceService orderingEvidence)
{
    public static readonly ApprovalWorkloadIdentity Workload = new(
        PurchaseOrderCodes.DomainWorkloadIssuer, PurchaseOrderCodes.DomainWorkloadClientId);

    /// <summary>Presents a complete draft: <c>DRAFT→PENDING_APPROVAL</c> (REQ-02, REQ-03).</summary>
    public async Task<PurchaseOrderVersion> SubmitAsync(
        SubmitPurchaseOrderCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var version = await RequireVersionAsync(
            command.OrganizationId, command.PoId, command.ExpectedPoVersion, cancellationToken);
        if ((PurchaseOrderState)version.State != PurchaseOrderState.Draft)
        {
            var existing = await dbContext.PurchaseOrderVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.PoId == command.PoId && record.PredecessorVersion == version.Version,
                    cancellationToken);
            if (existing is not null && existing.State == (int)PurchaseOrderState.PendingApproval)
            {
                return PurchaseOrderSerialization.ReadDocument(existing.DocumentJson, existing.ContentDigest);
            }

            throw new DomainConflictException("Only a draft purchase order can be presented.");
        }

        var current = PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        if (!current.ObligationsComplete)
        {
            throw new PurchaseOrderUnprocessableException(
                "A purchase order cannot be presented without its delivery commitment and "
                + "acceptance responsibilities.");
        }

        var parameters = PurchaseOrderSerialization.ReadLineParameters(version.LineParametersJson);
        foreach (var line in current.Lines)
        {
            var parameter = parameters.Single(entry =>
                string.Equals(entry.LineId, line.RequestLineRef.Id.ToString("D"), StringComparison.Ordinal));
            AcceptanceResponsibilityBuilder.RequireComplete(
                line, parameter.PurchaseType, line.RequestLineRef.CanonicalIdentity);
            if (current.Delivery is not null)
            {
                current.Delivery.RequireFuture(DateOnly.FromDateTime(occurredAt.UtcDateTime));
            }
        }

        // REQ-03: the ordering evidence is produced and bound before the case exists, so the
        // approval approves exactly the evidence the issue will later require.
        var evidence = await orderingEvidence.ProduceAsync(
            new OrderingEvidenceRequest(
                command.OrganizationId,
                version.RequestId,
                version.RequestVersion,
                current.Lines
                    .Select(line => new OrderingEvidenceTargetRef(
                        line.RequestLineRef.Id, line.RequestLineRef.Version))
                    .ToArray(),
                occurredAt,
                PurchaseOrderCodes.DomainWorkloadIssuer,
                PurchaseOrderCodes.DomainWorkloadClientId),
            cancellationToken);
        var evidenceRef = new PurchaseOrderContentRef(
            version.RequestId, version.RequestVersion, evidence.Digest);
        var submission = await submissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                Workload,
                command.OrganizationId,
                PurchaseOrderCodes.PurchaseOrderSubjectType,
                command.PoId,
                command.ExpectedPoVersion,
                PurchaseOrderCodes.IssueOperation,
                PurchaseOrderCodes.ApprovalAdapterVersion,
                command.SubmissionKey,
                command.OriginatorId,
                command.OriginatorId,
                command.CorrelationReference),
            occurredAt,
            cancellationToken);
        var successor = current.With(
            PurchaseOrderState.PendingApproval,
            current.Lines,
            current.Delivery,
            budgetOperationRefs: null,
            approvalRef: null,
            orderingEvidenceRef: evidenceRef,
            amendmentRef: null,
            command.OriginatorId,
            occurredAt,
            issuedAt: null);
        var record = PurchaseOrderVersionWriter.Record(successor, current.Lines, parameters, occurredAt, null);
        record.ApprovalCaseId = submission.CaseId;
        record.ApprovalCaseVersion = submission.Version;
        dbContext.PurchaseOrderVersions.Add(record);
        await AdvanceOrderAsync(command.OrganizationId, command.PoId, version.Version, successor, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return successor;
    }

    /// <summary>Cancels a version before issue and releases its claim and takeovers (REQ-01, REQ-02).</summary>
    public async Task<PurchaseOrderVersion> CancelAsync(
        CancelPurchaseOrderCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var version = await RequireVersionAsync(
            command.OrganizationId, command.PoId, command.ExpectedPoVersion, cancellationToken);
        var state = (PurchaseOrderState)version.State;
        if (state == PurchaseOrderState.Issued)
        {
            throw new DomainConflictException("An issued purchase order is cancelled through an amendment.");
        }

        if (state == PurchaseOrderState.Cancelled)
        {
            return PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        }

        var current = PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        var successor = current.With(
            PurchaseOrderState.Cancelled,
            current.Lines,
            current.Delivery,
            budgetOperationRefs: null,
            approvalRef: null,
            orderingEvidenceRef: null,
            amendmentRef: null,
            command.ActorUserId,
            occurredAt,
            issuedAt: null);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(version.LineParametersJson);
        dbContext.PurchaseOrderVersions.Add(
            PurchaseOrderVersionWriter.Record(successor, current.Lines, parameters, occurredAt, command.Reason));
        await AdvanceOrderAsync(command.OrganizationId, command.PoId, version.Version, successor, cancellationToken);
        var claim = await dbContext.AwardConsumptionClaims
            .SingleOrDefaultAsync(
                record => record.PoId == command.PoId &&
                          record.OrganizationId == command.OrganizationId &&
                          record.State == (int)AwardClaimState.Claimed,
                cancellationToken);
        if (claim is not null)
        {
            // REQ-01: a pre-issue cancellation releases the claim and returns the line takeovers to
            // the award, so the award can be claimed again by a new order. The transfer fences the
            // exact consumer version the order recorded when it claimed the lines.
            var recorded = await takeovers.ResolveSetAsync(
                command.OrganizationId,
                version.RequestId,
                version.RequestVersion,
                PurchaseOrderSerialization.ReadLines(version.LinesJson)
                    .Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                        line.RequestLineRef.Id, line.RequestLineRef.Version, line.RequestLineRef.ContentDigest))
                    .ToArray(),
                cancellationToken);
            var processId = await dbContext.SourcingAwardVersions
                .AsNoTracking()
                .Where(row => row.AwardId == version.AwardId && row.Version == version.AwardVersion)
                .Select(row => row.ProcessId)
                .SingleAsync(cancellationToken);
            var predecessorVersion = recorded[0].ConsumerVersion;
            claim.State = (int)AwardClaimState.Released;
            claim.ReleasedAt = occurredAt;
            claim.ReleaseReason = PurchaseOrderCodes.Reason(command.Reason);
            await takeovers.AcquireAsync(
                command.OrganizationId,
                version.RequestId,
                version.RequestVersion,
                version.RequestContentDigest,
                PurchaseOrderSerialization.ReadLines(version.LinesJson)
                    .Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                        line.RequestLineRef.Id, line.RequestLineRef.Version, line.RequestLineRef.ContentDigest))
                    .ToArray(),
                PurchaseRequestLineOwner.Sourcing,
                processId,
                predecessorVersion,
                version.AwardContentDigest,
                "SOURCING",
                predecessorConsumerId: command.PoId,
                predecessorConsumerVersion: predecessorVersion,
                command.ActorUserId.ToString("D"),
                occurredAt,
                cancellationToken);
            var pointers = await dbContext.PurchaseOrderLinePointers
                .Where(pointer => pointer.PoId == command.PoId)
                .ToArrayAsync(cancellationToken);
            foreach (var pointer in pointers)
            {
                pointer.State = (int)PurchaseOrderState.Cancelled;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return successor;
    }

    /// <summary>
    /// Issues an approved order (REQ-04): exactly one <c>COMMIT</c> of the ordered amount, the open
    /// remainder of every reservation released by <c>REVERSE</c>, and only then an <c>ISSUED</c>
    /// successor with its budget references and projection.
    /// </summary>
    public async Task<PurchaseOrderVersion> IssueAsync(
        IssuePurchaseOrderCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var version = await RequireVersionAsync(
            command.OrganizationId, command.PoId, command.ExpectedPoVersion, cancellationToken);
        if ((PurchaseOrderState)version.State == PurchaseOrderState.Issued)
        {
            return PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        }

        if ((PurchaseOrderState)version.State != PurchaseOrderState.Approved)
        {
            throw new DomainConflictException("Only an approved purchase order can be issued.");
        }

        var current = PurchaseOrderSerialization.ReadDocument(version.DocumentJson, version.ContentDigest);
        if (current.ApprovalRef is null || current.OrderingEvidenceRef is null)
        {
            throw new PurchaseOrderUnprocessableException(
                "An issued purchase order requires its approval and ordering evidence references.");
        }

        var claim = await dbContext.AwardConsumptionClaims
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.PoId == command.PoId &&
                          record.OrganizationId == command.OrganizationId &&
                          record.State == (int)AwardClaimState.Claimed,
                cancellationToken)
            ?? throw new DomainConflictException("The purchase order has no live award claim.");
        var operation = await budget.CommitAsync(
            command.OrganizationId,
            command.PoId,
            version.Version,
            version.ContentDigest,
            current,
            command.IssueKey,
            command.CorrelationReference,
            occurredAt,
            cancellationToken);
        var successor = current.With(
            PurchaseOrderState.Issued,
            current.Lines,
            current.Delivery,
            operation.OperationRefs,
            approvalRef: current.ApprovalRef,
            orderingEvidenceRef: current.OrderingEvidenceRef,
            amendmentRef: null,
            command.IssueKey == string.Empty ? current.ActorUserId : current.ActorUserId,
            occurredAt,
            occurredAt);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(version.LineParametersJson);
        dbContext.PurchaseOrderVersions.Add(
            PurchaseOrderVersionWriter.Record(successor, current.Lines, parameters, occurredAt, null));
        await AdvanceOrderAsync(command.OrganizationId, command.PoId, version.Version, successor, cancellationToken);
        var claimRow = await dbContext.AwardConsumptionClaims
            .SingleAsync(record => record.Id == claim.Id, cancellationToken);
        claimRow.State = (int)AwardClaimState.Issued;
        claimRow.IssuedAt = occurredAt;
        var pointers = await dbContext.PurchaseOrderLinePointers
            .Where(pointer => pointer.PoId == command.PoId)
            .ToArrayAsync(cancellationToken);
        foreach (var pointer in pointers)
        {
            pointer.PoVersion = successor.PurchaseOrderVersionNumber;
            pointer.State = (int)PurchaseOrderState.Issued;
        }

        // REQ-10: the issued lines project ORDERED on the Purchase Request, and a duplicated or late
        // event of another version never moves the projection backwards.
        foreach (var line in current.Lines)
        {
            var projection = await dbContext.PurchaseRequestLineProjections
                .SingleOrDefaultAsync(
                    record => record.RequestId == version.RequestId &&
                              record.RequestVersion == version.RequestVersion &&
                              record.LineId == line.RequestLineRef.Id &&
                              record.LineVersion == line.RequestLineRef.Version,
                    cancellationToken);
            if (projection is null)
            {
                dbContext.PurchaseRequestLineProjections.Add(new PurchaseRequestLineProjectionRecord
                {
                    RequestId = version.RequestId,
                    RequestVersion = version.RequestVersion,
                    LineId = line.RequestLineRef.Id,
                    LineVersion = line.RequestLineRef.Version,
                    OrganizationId = command.OrganizationId,
                    Projection = (int)PurchaseRequestLineProjection.Ordered,
                    ConsumerId = command.PoId,
                    ConsumerVersion = successor.PurchaseOrderVersionNumber,
                    ConsumerDigest = successor.Digest,
                    ConsumerType = PurchaseOrderCodes.TargetPurchaseOrder,
                    ConsumerState = (int)PurchaseOrderState.Issued,
                    OccurredAt = occurredAt
                });
                continue;
            }

            if (projection.ConsumerVersion <= successor.PurchaseOrderVersionNumber)
            {
                projection.Projection = (int)PurchaseRequestLineProjection.Ordered;
                projection.ConsumerVersion = successor.PurchaseOrderVersionNumber;
                projection.ConsumerDigest = successor.Digest;
                projection.ConsumerState = (int)PurchaseOrderState.Issued;
                projection.OccurredAt = occurredAt;
            }
        }

        dbContext.PurchaseOrderOutbox.Add(new PurchaseOrderOutboxRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            EventType = "purchase-order-issued/v1",
            RequestId = version.RequestId,
            RequestVersion = version.RequestVersion,
            PayloadJson = $"{{\"po_id\":\"{command.PoId:D}\",\"po_version\":{successor.PurchaseOrderVersionNumber}," +
                          $"\"po_content_digest\":\"{successor.Digest}\"}}",
            IdempotencyKey = $"purchase-order-issued:{command.PoId:D}:{successor.PurchaseOrderVersionNumber}",
            OccurredAt = occurredAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return successor;
    }

    private async Task<PurchaseOrderVersionRecord> RequireVersionAsync(
        Guid organizationId,
        Guid poId,
        int version,
        CancellationToken cancellationToken) =>
        await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.PoId == poId &&
                          record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The purchase order version is not visible.");

    private async Task AdvanceOrderAsync(
        Guid organizationId,
        Guid poId,
        int previousVersion,
        PurchaseOrderVersion successor,
        CancellationToken cancellationToken)
    {
        var applied = await dbContext.PurchaseOrders
            .Where(record => record.Id == poId && record.CurrentVersion == previousVersion)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(record => record.CurrentVersion, successor.PurchaseOrderVersionNumber),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The purchase order changed concurrently.");
        }

        _ = organizationId;
    }
}
