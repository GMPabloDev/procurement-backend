using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.Api.Controllers;

/// <summary>Body of one award consumption claim (SPEC 11 REQ-01).</summary>
public sealed class PurchaseOrderClaimBody
{
    public Guid AwardId { get; init; }

    public int AwardVersion { get; init; }

    public string AwardContentDigest { get; init; } = string.Empty;

    public IReadOnlyList<PurchaseOrderLineRefBody> CoveredLines { get; init; } = [];

    public string ClaimKey { get; init; } = string.Empty;

    public Guid? PoId { get; init; }
}

/// <summary>Body of one line reference.</summary>
public sealed class PurchaseOrderLineRefBody
{
    public Guid Id { get; init; }

    public int Version { get; init; }

    public string ContentDigest { get; init; } = string.Empty;
}

/// <summary>Body of one draft successor (REQ-02, REQ-06).</summary>
public sealed class PurchaseOrderDraftBody
{
    public int ExpectedPoVersion { get; init; }

    public string DraftKey { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public PurchaseOrderDeliveryBody? Delivery { get; init; }

    public IReadOnlyList<PurchaseOrderAssignmentsBody> Assignments { get; init; } = [];
}

/// <summary>Body of one delivery commitment.</summary>
public sealed class PurchaseOrderDeliveryBody
{
    public string DeliveryDate { get; init; } = string.Empty;

    public string DeliveryLocation { get; init; } = string.Empty;
}

/// <summary>Body of the assignments of one line.</summary>
public sealed class PurchaseOrderAssignmentsBody
{
    public Guid LineId { get; init; }

    public int LineVersion { get; init; }

    public IReadOnlyList<PurchaseOrderAssignmentBody> Assignments { get; init; } = [];
}

/// <summary>Body of one acceptance assignment.</summary>
public sealed class PurchaseOrderAssignmentBody
{
    public string Kind { get; init; } = string.Empty;

    public Guid UserId { get; init; }

    public int UserVersion { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Body of a keyed transition (submit, issue, cancel, amendment, direct purchase).</summary>
public sealed class PurchaseOrderCommandBody
{
    public int ExpectedVersion { get; init; }

    public string Key { get; init; } = string.Empty;

    public string? Reason { get; init; }

    public string? AmendmentJson { get; init; }
}

/// <summary>Body of one award recovery (REQ-01).</summary>
public sealed class AwardRecoveryBody
{
    public string AwardContentDigest { get; init; } = string.Empty;

    public int ExpectedProcessVersion { get; init; }

    public int ExpectedAwardVersion { get; init; }

    public string Action { get; init; } = string.Empty;

    public string RecoveryKey { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

/// <summary>Body of one amendment creation (REQ-05).</summary>
public sealed class PurchaseOrderAmendmentBody
{
    public int ExpectedPoVersion { get; init; }

    public string AmendmentKey { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Guid? SuccessorAwardId { get; init; }

    public int? SuccessorAwardVersion { get; init; }

    public string? SuccessorAwardDigest { get; init; }

    public IReadOnlyList<PurchaseOrderAmendmentDeltaBody> LineDeltas { get; init; } = [];

    public IReadOnlyList<PurchaseOrderResponsibilityChangeBody> ResponsibilityChanges { get; init; } = [];

    public PurchaseOrderDeliveryBody? ReplacementDelivery { get; init; }
}

/// <summary>Body of one amendment line delta.</summary>
public sealed class PurchaseOrderAmendmentDeltaBody
{
    public string ChangeKind { get; init; } = string.Empty;

    public Guid LineId { get; init; }

    public int LineVersion { get; init; }

    /// <summary>The complete replacement line; absent only for a cancellation (REQ-05).</summary>
    public PurchaseOrderLineBody? ReplacementLine { get; init; }
}

/// <summary>Body of one complete <c>purchase-order-line/v1</c> replacement.</summary>
public sealed class PurchaseOrderLineBody
{
    public decimal Quantity { get; init; }

    public string UnitCode { get; init; } = string.Empty;

    public decimal UnitPrice { get; init; }

    public string SourceCurrency { get; init; } = string.Empty;

    public decimal GrossTotal { get; init; }

    public string BaseCurrency { get; init; } = string.Empty;

    public decimal BaseGrossTotal { get; init; }

    public decimal Subtotal { get; init; }

    public decimal Taxes { get; init; }

    public decimal AdditionalCharges { get; init; }

    public decimal Discounts { get; init; }

    public Guid? AwardLineId { get; init; }

    public int? AwardLineVersion { get; init; }

    public string? AwardLineDigest { get; init; }
}

/// <summary>Body of one responsibility change.</summary>
public sealed class PurchaseOrderResponsibilityChangeBody
{
    public string Kind { get; init; } = string.Empty;

    public Guid LineId { get; init; }

    public int LineVersion { get; init; }

    public PurchaseOrderAssignmentBody? Replacement { get; init; }
}

/// <summary>Body of one Direct Purchase authorization (REQ-07).</summary>
public sealed class DirectPurchaseBody
{
    public Guid RequestId { get; init; }

    public int ExpectedRequestVersion { get; init; }

    public string AuthorizationKey { get; init; } = string.Empty;

    public IReadOnlyList<PurchaseOrderLineRefBody> CoveredTargets { get; init; } = [];

    public IReadOnlyList<DirectPurchaseAssignmentBody> AcceptanceResponsibilities { get; init; } = [];
}

/// <summary>Body of one Direct Purchase assignment.</summary>
public sealed class DirectPurchaseAssignmentBody
{
    public Guid LineId { get; init; }

    public int LineVersion { get; init; }

    public string Kind { get; init; } = string.Empty;

    public Guid UserId { get; init; }

    public int UserVersion { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Body of one supporting document confirmation (REQ-08).</summary>
public sealed class SupportingDocumentConfirmBody
{
    public int ExpectedVersion { get; init; }
}

/// <summary>Published projection of one Purchase Order version (REQ-11).</summary>
public sealed record PurchaseOrderVersionResponse(
    Guid PoId,
    int Version,
    int? PredecessorVersion,
    string PoNumber,
    string State,
    Guid SupplierId,
    int SupplierVersion,
    decimal SourceAmount,
    string SourceCurrency,
    decimal BaseAmount,
    string BaseCurrency,
    int LineCount,
    string ContentDigest,
    DateTimeOffset OccurredAt,
    DateTimeOffset? IssuedAt);

/// <summary>Published projection of one amendment version (REQ-11).</summary>
public sealed record PurchaseOrderAmendmentResponse(
    Guid AmendmentId,
    int Version,
    int? PredecessorVersion,
    int ExpectedPoVersion,
    string State,
    string Reason,
    int LineDeltaCount,
    string ContentDigest,
    DateTimeOffset OccurredAt);

/// <summary>Published projection of one Direct Purchase authorization (REQ-11).</summary>
public sealed record DirectPurchaseResponse(
    Guid AuthorizationId,
    int Version,
    string State,
    Guid RequestId,
    int RequestVersion,
    Guid SupplierId,
    int SupplierVersion,
    decimal MaximumSourceAmount,
    string SourceCurrency,
    decimal MaximumBaseAmount,
    int CoveredLineCount,
    string ContentDigest,
    DateTimeOffset AuthorizedAt);

/// <summary>Published projection of one supporting document (REQ-11): metadata only, never a location.</summary>
public sealed record SupportingDocumentResponse(
    Guid DocumentId,
    int Version,
    string State,
    string BusinessType,
    Guid RequestId,
    int RequestVersion,
    string FileName,
    string ContentType,
    long Length,
    string Sha256,
    string ContentDigest,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ConfirmedAt);

/// <summary>
/// Purchase Orders HTTP surface (SPEC 11 REQ-11, CA-09). Every command derives its organization and
/// actor from the authenticated profile, a resource outside the caller scope answers <c>404</c>, and
/// no response, log or Problem Details exposes a location, a workbook, an amount of another tenant or
/// a full digest.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class PurchaseOrderController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    PurchaseOrderClaimService claims,
    PurchaseOrderDraftService drafts,
    PurchaseOrderIssuanceService issuance,
    PurchaseOrderAmendmentService amendments,
    DirectPurchaseService directPurchases,
    SupportingDocumentService documents,
    PurchaseRequestOrderingQuery ordering) : ControllerBase
{
    /// <summary>Maximum multipart body of one supporting document upload (REQ-11).</summary>
    private const long MaxUploadBytes = PurchaseOrderCodes.MaxDocumentBytes + 1024 * 1024;

    /// <summary>Maximum JSON body of one command whose content is a set of lines (REQ-11).</summary>
    private const long MaxCommandBytes = 512 * 1024;

    /// <summary>
    /// Ordering aggregate of one Purchase Request version (REQ-10, REQ-11): the requester of the
    /// request, a buyer or an auditor reads it; anything else answers <c>404</c>.
    /// </summary>
    [HttpGet("purchase-orders/requests/{requestId:guid}/versions/{requestVersion:int}/ordering")]
    public async Task<ActionResult<object>> GetOrderingAsync(
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var actor = await RequireDocumentReaderAsync(requestId, cancellationToken);
        var view = await ordering.SummarizeAsync(
            actor.OrganizationId, requestId, requestVersion, cancellationToken);
        return Ok(new
        {
            view.RequestId,
            view.RequestVersion,
            view.LineCount,
            view.OrderedLines,
            view.DirectPurchaseLines,
            view.Summary
        });
    }

    [HttpPost("purchase-orders")]
    [RequestSizeLimit(MaxCommandBytes)]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> ClaimAsync(
        PurchaseOrderClaimBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var request = new AwardConsumptionClaimRequest(
            actor.OrganizationId,
            body.PoId ?? Guid.NewGuid(),
            new PurchaseOrderContentRef(body.AwardId, body.AwardVersion, body.AwardContentDigest),
            body.CoveredLines.Select(ToRef).ToArray(),
            body.ClaimKey,
            DateTimeOffset.UtcNow,
            PurchaseOrderCodes.DomainWorkloadIssuer,
            PurchaseOrderCodes.DomainWorkloadClientId);
        var response = await claims.ClaimAsync(request, actor.Id, cancellationToken);
        var version = await RequireVersionAsync(
            actor.OrganizationId, response.PoRef.Id, response.PoRef.Version, cancellationToken);
        return Created($"/api/v1/purchase-orders/{response.PoRef.Id:D}", ToResponse(version));
    }

    [HttpGet("purchase-orders/{poId:guid}")]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> GetOrderAsync(
        Guid poId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireOrderReaderAsync(poId, cancellationToken);
        var current = await dbContext.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId && record.OrganizationId == actor.OrganizationId)
            .Select(record => record.CurrentVersion)
            .SingleAsync(cancellationToken);
        return Ok(ToResponse(await RequireVersionAsync(actor.OrganizationId, poId, current, cancellationToken)));
    }

    [HttpGet("purchase-orders/{poId:guid}/versions")]
    public async Task<ActionResult<IReadOnlyCollection<PurchaseOrderVersionResponse>>> GetVersionsAsync(
        Guid poId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireOrderReaderAsync(poId, cancellationToken);
        var versions = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .Where(record => record.PoId == poId && record.OrganizationId == actor.OrganizationId)
            .OrderBy(record => record.Version)
            .ToArrayAsync(cancellationToken);
        return Ok(versions
            .Select(record => ToResponse(
                PurchaseOrderSerialization.ReadDocument(record.DocumentJson, record.ContentDigest)))
            .ToArray());
    }

    [HttpGet("purchase-orders/{poId:guid}/audit")]
    public async Task<ActionResult<IReadOnlyCollection<object>>> GetAuditAsync(
        Guid poId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAuditorAsync(cancellationToken);
        var visible = await dbContext.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(
                record => record.Id == poId && record.OrganizationId == actor.OrganizationId,
                cancellationToken);
        if (!visible)
        {
            throw new DomainNotFoundException("The purchase order is not visible.");
        }

        var records = await dbContext.PurchaseOrderAuditRecords
            .AsNoTracking()
            .Where(record => record.TargetId == poId && record.OrganizationId == actor.OrganizationId)
            .OrderBy(record => record.OccurredAt)
            .Select(record => new
            {
                record.Action,
                record.TargetType,
                record.TargetVersion,
                record.OccurredAt
            })
            .ToArrayAsync(cancellationToken);
        return Ok(records.Cast<object>().ToArray());
    }

    [HttpPut("purchase-orders/{poId:guid}/draft")]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> UpdateDraftAsync(
        Guid poId,
        PurchaseOrderDraftBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await drafts.UpdateDraftAsync(
            new UpdatePurchaseOrderDraftCommand(
                actor.OrganizationId,
                poId,
                body.ExpectedPoVersion,
                body.Delivery is null ? null : ToDelivery(body.Delivery),
                body.Assignments
                    .Select(assignment => new PurchaseOrderLineAssignments(
                        assignment.LineId,
                        assignment.LineVersion,
                        assignment.Assignments
                            .Select(entry => new AcceptanceAssignmentRequest(
                                entry.Kind, entry.UserId, entry.UserVersion, entry.Reason))
                            .ToArray()))
                    .ToArray(),
                body.DraftKey,
                body.Reason),
            actor.Id,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/{poId:guid}/submit")]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> SubmitAsync(
        Guid poId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await issuance.SubmitAsync(
            new SubmitPurchaseOrderCommand(
                actor.OrganizationId, poId, body.ExpectedVersion, body.Key, actor.Id, Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/{poId:guid}/issue")]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> IssueAsync(
        Guid poId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        _ = actor;
        var version = await issuance.IssueAsync(
            new IssuePurchaseOrderCommand(
                actor.OrganizationId, poId, body.ExpectedVersion, body.Key, Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/{poId:guid}/cancel")]
    public async Task<ActionResult<PurchaseOrderVersionResponse>> CancelAsync(
        Guid poId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await issuance.CancelAsync(
            new CancelPurchaseOrderCommand(
                actor.OrganizationId,
                poId,
                body.ExpectedVersion,
                body.Key,
                body.Reason ?? "Cancelled by the buyer",
                actor.Id),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/awards/{awardId:guid}/recovery")]
    public async Task<ActionResult<object>> RecoverAwardAsync(
        Guid awardId,
        AwardRecoveryBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var response = await claims.RecoverAsync(
            new AwardRecoveryCommand(
                actor.OrganizationId,
                new PurchaseOrderContentRef(
                    awardId, body.ExpectedAwardVersion, body.AwardContentDigest),
                body.ExpectedProcessVersion,
                body.ExpectedAwardVersion,
                body.Action,
                body.RecoveryKey,
                body.Reason),
            actor.Id,
            cancellationToken);
        return Ok(new
        {
            awardId = response.AwardRef.Id,
            action = response.Action,
            processVersion = response.ProcessVersion,
            processState = response.ProcessState,
            releasedTakeovers = response.ReleasedTakeovers.Count,
            response.Replayed
        });
    }

    [HttpPost("purchase-orders/{poId:guid}/amendments")]
    [RequestSizeLimit(MaxCommandBytes)]
    public async Task<ActionResult<PurchaseOrderAmendmentResponse>> CreateAmendmentAsync(
        Guid poId,
        PurchaseOrderAmendmentBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var current = await dbContext.PurchaseOrders
            .AsNoTracking()
            .Where(record => record.Id == poId && record.OrganizationId == actor.OrganizationId)
            .Select(record => record.CurrentVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (current == 0)
        {
            throw new DomainNotFoundException("The purchase order is not visible.");
        }

        var record = await RequireVersionAsync(actor.OrganizationId, poId, current, cancellationToken);
        var version = PurchaseOrderSerialization.ReadDocument(record.DocumentJson, record.ContentDigest);
        var lines = version.Lines
            .ToDictionary(line => line.RequestLineRef.Id, line => line);
        var deltas = body.LineDeltas
            .Select(delta => ToDelta(delta, lines))
            .ToArray();
        var changes = body.ResponsibilityChanges
            .Select(change => ToResponsibilityChange(change, lines, actor.Id))
            .ToArray();
        var amendment = await amendments.CreateAsync(
            new CreatePurchaseOrderAmendmentCommand(
                actor.OrganizationId,
                poId,
                body.ExpectedPoVersion,
                body.AmendmentKey,
                body.SuccessorAwardId is Guid successorId && body.SuccessorAwardVersion is int successorVersion
                    ? new PurchaseOrderContentRef(
                        successorId, successorVersion,
                        body.SuccessorAwardDigest ?? throw new DomainValidationException(
                            "A successor award reference requires its digest."))
                    : null,
                deltas,
                changes,
                body.ReplacementDelivery is null ? null : ToDelivery(body.ReplacementDelivery),
                body.Reason,
                actor.Id,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created(
            $"/api/v1/purchase-orders/amendments/{amendment.AmendmentId:D}", ToResponse(amendment));
    }

    [HttpGet("purchase-orders/amendments/{amendmentId:guid}")]
    public async Task<ActionResult<PurchaseOrderAmendmentResponse>> GetAmendmentAsync(
        Guid amendmentId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerOrAuditorAsync(cancellationToken);
        var current = await amendments.CurrentAmendmentVersionAsync(
            actor.OrganizationId, amendmentId, cancellationToken)
            ?? throw new DomainNotFoundException("The amendment is not visible.");
        return Ok(ToResponse(await amendments.ReadAmendmentAsync(
            actor.OrganizationId, amendmentId, current, cancellationToken)));
    }

    [HttpPost("purchase-orders/amendments/{amendmentId:guid}/submit")]
    public async Task<ActionResult<PurchaseOrderAmendmentResponse>> SubmitAmendmentAsync(
        Guid amendmentId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await amendments.SubmitAsync(
            new SubmitPurchaseOrderAmendmentCommand(
                actor.OrganizationId,
                amendmentId,
                body.ExpectedVersion,
                body.Key,
                actor.Id,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/amendments/{amendmentId:guid}/apply")]
    public async Task<ActionResult<PurchaseOrderAmendmentResponse>> ApplyAmendmentAsync(
        Guid amendmentId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await amendments.ApplyAsync(
            new ApplyPurchaseOrderAmendmentCommand(
                actor.OrganizationId,
                amendmentId,
                body.ExpectedVersion,
                body.Key,
                actor.Id,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("purchase-orders/amendments/{amendmentId:guid}/cancel")]
    public async Task<ActionResult<PurchaseOrderAmendmentResponse>> CancelAmendmentAsync(
        Guid amendmentId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var version = await amendments.CancelAsync(
            new CancelPurchaseOrderAmendmentCommand(
                actor.OrganizationId,
                amendmentId,
                body.ExpectedVersion,
                body.Key,
                body.Reason ?? "Cancelled by the buyer",
                actor.Id),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("direct-purchases")]
    public async Task<ActionResult<DirectPurchaseResponse>> AuthorizeDirectPurchaseAsync(
        DirectPurchaseBody body,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRequesterOrBuyerAsync(body.RequestId, cancellationToken);
        var authorization = await directPurchases.AuthorizeAsync(
            new CreateDirectPurchaseAuthorizationCommand(
                actor.OrganizationId,
                body.RequestId,
                body.ExpectedRequestVersion,
                body.AuthorizationKey,
                actor.Id,
                body.CoveredTargets
                    .Select(target => new OrderingEvidenceTargetRef(target.Id, target.Version))
                    .ToArray(),
                body.AcceptanceResponsibilities
                    .Select(assignment => new DirectPurchaseAssignmentRequest(
                        assignment.LineId,
                        assignment.LineVersion,
                        assignment.Kind,
                        assignment.UserId,
                        assignment.UserVersion,
                        assignment.Reason))
                    .ToArray()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created(
            $"/api/v1/direct-purchases/{authorization.AuthorizationId:D}", ToResponse(authorization));
    }

    [HttpGet("direct-purchases/{authorizationId:guid}")]
    public async Task<ActionResult<DirectPurchaseResponse>> GetDirectPurchaseAsync(
        Guid authorizationId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireDirectPurchaseReaderAsync(authorizationId, cancellationToken);
        var current = await dbContext.DirectPurchaseAuthorizations
            .AsNoTracking()
            .Where(record => record.Id == authorizationId && record.OrganizationId == actor.OrganizationId)
            .OrderByDescending(record => record.Version)
            .Select(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (current == 0)
        {
            throw new DomainNotFoundException("The Direct Purchase is not visible.");
        }

        return Ok(ToResponse(await directPurchases.ReadAuthorizationAsync(
            actor.OrganizationId, authorizationId, current, cancellationToken)));
    }

    /// <summary>
    /// REQ-11: the requester that authorized it, a procurement buyer or an auditor reads the Direct
    /// Purchase; anything else answers <c>404</c>.
    /// </summary>
    private async Task<UserProfileRecord> RequireDirectPurchaseReaderAsync(
        Guid authorizationId,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var row = await dbContext.DirectPurchaseAuthorizations
            .AsNoTracking()
            .Where(record => record.Id == authorizationId && record.OrganizationId == profile.OrganizationId)
            .OrderByDescending(record => record.Version)
            .Select(record => new { record.ActorUserId, record.RequestId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            throw new DomainNotFoundException("The Direct Purchase is not visible.");
        }

        if (await HasRoleAsync(profile, SystemRole.ProcurementBuyer, cancellationToken) ||
            await HasRoleAsync(profile, SystemRole.Auditor, cancellationToken) ||
            row.ActorUserId == profile.Id)
        {
            return profile;
        }

        var requester = await dbContext.PurchaseRequests
            .AsNoTracking()
            .Where(record => record.Id == row.RequestId)
            .Select(record => record.RequesterId)
            .SingleAsync(cancellationToken);
        if (requester != profile.Id)
        {
            throw new DomainNotFoundException("The Direct Purchase is not visible.");
        }

        return profile;
    }

    [HttpPost("direct-purchases/{authorizationId:guid}/cancel")]
    public async Task<ActionResult<DirectPurchaseResponse>> CancelDirectPurchaseAsync(
        Guid authorizationId,
        PurchaseOrderCommandBody body,
        CancellationToken cancellationToken)
    {
        var (actor, current) = await RequireOwnedDirectPurchaseAsync(authorizationId, cancellationToken);
        var version = await directPurchases.CancelAsync(
            new CancelDirectPurchaseAuthorizationCommand(
                actor.OrganizationId,
                authorizationId,
                current,
                body.Key,
                body.Reason ?? "Cancelled by the requester",
                actor.Id),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(ToResponse(version));
    }

    [HttpPost("supporting-documents")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<SupportingDocumentResponse>> StageDocumentAsync(
        [FromForm] Guid requestId,
        [FromForm] int requestVersion,
        [FromForm] Guid fileId,
        [FromForm] int fileVersion,
        [FromForm] string businessType,
        [FromForm] string coveredTargets,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var actor = await RequireRequesterOrBuyerAsync(requestId, cancellationToken);
        if (file.Length <= 0 ||
            Request.ContentLength is > MaxUploadBytes ||
            file.Length > PurchaseOrderCodes.MaxDocumentBytes)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A supporting document admits at most {PurchaseOrderCodes.MaxDocumentBytes} bytes.");
        }

        var targets = ParseTargets(coveredTargets);
        await using var content = file.OpenReadStream();
        var document = await documents.StageAsync(
            new StageSupportingDocumentCommand(
                actor.OrganizationId,
                requestId,
                requestVersion,
                fileId,
                fileVersion,
                file.ContentType ?? "application/pdf",
                file.FileName,
                file.Length,
                null,
                businessType,
                targets,
                actor.Id),
            content,
            cancellationToken);
        return Created(
            $"/api/v1/supporting-documents/{document.DocumentId:D}", ToResponse(document));
    }

    [HttpGet("supporting-documents/{documentId:guid}")]
    public async Task<ActionResult<SupportingDocumentResponse>> GetDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(record => record.Id == documentId)
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document is not visible.");
        var actor = await RequireDocumentReaderAsync(row.RequestId, cancellationToken);
        if (row.OrganizationId != actor.OrganizationId)
        {
            throw new DomainNotFoundException("The supporting document is not visible.");
        }

        return Ok(ToResponse(PurchaseOrderSerialization.ReadSupportingDocument(
            row.DocumentJson, row.Id, row.ActorUserId, row.OccurredAt, row.ConfirmedAt, row.ContentDigest)));
    }

    [HttpPost("supporting-documents/{documentId:guid}/confirm")]
    public async Task<ActionResult<SupportingDocumentResponse>> ConfirmDocumentAsync(
        Guid documentId,
        SupportingDocumentConfirmBody body,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(record => record.Id == documentId)
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document is not visible.");
        var actor = await RequireRequesterOrBuyerAsync(row.RequestId, cancellationToken);
        var document = await documents.ConfirmAsync(
            new ConfirmSupportingDocumentCommand(
                actor.OrganizationId, documentId, body.ExpectedVersion, actor.Id),
            cancellationToken);
        return Ok(ToResponse(document));
    }

    /// <summary>Temporary download URL of one confirmed document (REQ-11, ≤15 minutes and audited).</summary>
    [HttpGet("supporting-documents/{documentId:guid}/download")]
    public async Task<ActionResult<object>> DownloadDocumentAsync(
        Guid documentId,
        [FromQuery] int? version,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(record => record.Id == documentId)
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document is not visible.");
        var actor = await RequireRequesterOrBuyerAsync(row.RequestId, cancellationToken);
        var selected = version ?? row.Version;
        var url = await documents.GenerateDownloadUrlAsync(
            actor.OrganizationId, documentId, selected, actor.Id, cancellationToken);
        return Ok(new
        {
            documentId,
            version = selected,
            url = url.ToString(),
            expiresInSeconds = (int)SupportingDocumentService.MaximumDownloadLifetime.TotalSeconds
        });
    }

    private async Task<UserProfileRecord> RequireBuyerAsync(CancellationToken cancellationToken) =>
        await provisioningService.RequireRoleAsync(User, SystemRole.ProcurementBuyer, cancellationToken);

    private async Task<UserProfileRecord> RequireAuditorAsync(CancellationToken cancellationToken) =>
        await provisioningService.RequireRoleAsync(User, SystemRole.Auditor, cancellationToken);

    private async Task<UserProfileRecord> RequireBuyerOrAuditorAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var allowed = await HasRoleAsync(profile, SystemRole.ProcurementBuyer, cancellationToken) ||
                      await HasRoleAsync(profile, SystemRole.Auditor, cancellationToken);
        if (!allowed)
        {
            throw new DomainForbiddenException("The user is not a procurement buyer or auditor.");
        }

        return profile;
    }

    /// <summary>REQ-11: only the Buyer reads an order; anything else is invisible (404).</summary>
    private async Task<UserProfileRecord> RequireOrderReaderAsync(Guid poId, CancellationToken cancellationToken)
    {
        var profile = await RequireBuyerOrAuditorAsync(cancellationToken);
        var visible = await dbContext.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(
                record => record.Id == poId && record.OrganizationId == profile.OrganizationId,
                cancellationToken);
        if (!visible)
        {
            throw new DomainNotFoundException("The purchase order is not visible.");
        }

        return profile;
    }

    /// <summary>
    /// REQ-11: the requester of the request or a procurement buyer manages its Direct Purchase and its
    /// documents; anything else answers <c>404</c>.
    /// </summary>
    private async Task<UserProfileRecord> RequireRequesterOrBuyerAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var buyer = await HasRoleAsync(profile, SystemRole.ProcurementBuyer, cancellationToken);
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == requestId && record.OrganizationId == profile.OrganizationId,
                cancellationToken);
        if (request is null || (!buyer && request.RequesterId != profile.Id))
        {
            throw new DomainNotFoundException("The purchase request is not visible.");
        }

        return profile;
    }

    /// <summary>REQ-11: the requester of the request, a buyer or an auditor reads its documents.</summary>
    private async Task<UserProfileRecord> RequireDocumentReaderAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        if (await HasRoleAsync(profile, SystemRole.Auditor, cancellationToken))
        {
            return profile;
        }

        var buyer = await HasRoleAsync(profile, SystemRole.ProcurementBuyer, cancellationToken);
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == requestId && record.OrganizationId == profile.OrganizationId,
                cancellationToken);
        if (request is null || (!buyer && request.RequesterId != profile.Id))
        {
            throw new DomainNotFoundException("The purchase request is not visible.");
        }

        return profile;
    }

    /// <summary>
    /// REQ-11: only the requester that authorized the Direct Purchase or a procurement buyer cancels
    /// it; anything else answers <c>404</c>.
    /// </summary>
    private async Task<(UserProfileRecord Profile, int Version)> RequireOwnedDirectPurchaseAsync(
        Guid authorizationId,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var row = await dbContext.DirectPurchaseAuthorizations
            .AsNoTracking()
            .Where(record => record.Id == authorizationId && record.OrganizationId == profile.OrganizationId)
            .OrderByDescending(record => record.Version)
            .Select(record => new { record.Version, record.ActorUserId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The Direct Purchase is not visible.");
        if (row.ActorUserId != profile.Id &&
            !await HasRoleAsync(profile, SystemRole.ProcurementBuyer, cancellationToken))
        {
            throw new DomainNotFoundException("The Direct Purchase is not visible.");
        }

        _ = dbContext;
        return (profile, row.Version);
    }

    private Task<bool> HasRoleAsync(
        UserProfileRecord profile,
        SystemRole role,
        CancellationToken cancellationToken) =>
        dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Role == (int)role &&
                          assignment.Status == (int)AssignmentStatus.Active,
            cancellationToken);

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

    private string Correlation() => HttpContext.TraceIdentifier;

    private static IReadOnlyList<OrderingEvidenceTargetRef> ParseTargets(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var targets = new List<OrderingEvidenceTargetRef>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            targets.Add(new OrderingEvidenceTargetRef(
                element.GetProperty("id").GetGuid(),
                element.GetProperty("version").GetInt32()));
        }

        return targets;
    }

    private static AmendmentLineDelta ToDelta(
        PurchaseOrderAmendmentDeltaBody body,
        IReadOnlyDictionary<Guid, PurchaseOrderLine> lines)
    {
        if (!lines.TryGetValue(body.LineId, out var line))
        {
            throw new DomainNotFoundException("The amended line is not visible.");
        }

        if (body.ChangeKind == PurchaseOrderCodes.ChangeCancel)
        {
            return AmendmentLineDelta.Cancel(line);
        }

        if (body.ReplacementLine is null)
        {
            throw new DomainValidationException("A reduced or commercial delta requires its replacement line.");
        }

        var replacement = ToLine(body.ReplacementLine, line);
        return body.ChangeKind switch
        {
            PurchaseOrderCodes.ChangeReduce => AmendmentLineDelta.Reduce(line, replacement),
            PurchaseOrderCodes.ChangeCommercial => AmendmentLineDelta.Commercial(line, replacement),
            _ => throw new DomainValidationException("The amendment change kind is not recognized.")
        };
    }

    /// <summary>
    /// Rebuilds one complete <c>purchase-order-line/v1</c> replacement: the caller supplies its
    /// commercial content and the server keeps the line/request identity and the responsibility set
    /// of the line it replaces (REQ-02, REQ-05).
    /// </summary>
    private static PurchaseOrderLine ToLine(PurchaseOrderLineBody body, PurchaseOrderLine previous) =>
        new(
            previous.LineId,
            body.AwardLineId is Guid awardLineId && body.AwardLineVersion is int awardLineVersion
                ? new PurchaseOrderContentRef(
                    awardLineId, awardLineVersion,
                    body.AwardLineDigest ?? throw new DomainValidationException(
                        "A replacement line reference requires its digest."))
                : previous.AwardLineRef,
            previous.RequestLineRef,
            body.Quantity,
            body.UnitCode,
            body.UnitPrice,
            body.SourceCurrency,
            body.GrossTotal,
            body.BaseCurrency,
            body.BaseGrossTotal,
            body.Subtotal,
            body.Taxes,
            body.AdditionalCharges,
            body.Discounts,
            previous.FxSnapshotRef,
            previous.AcceptanceResponsibilities);

    private static ResponsibilityChange ToResponsibilityChange(
        PurchaseOrderResponsibilityChangeBody body,
        IReadOnlyDictionary<Guid, PurchaseOrderLine> lines,
        Guid actorUserId)
    {
        var line = lines[body.LineId];
        var previous = line.AcceptanceResponsibilities.FirstOrDefault(assignment => assignment.Kind == body.Kind)
            ?? throw new DomainNotFoundException("The responsibility is not visible.");
        var replacement = body.Replacement is null
            ? previous
            : new AcceptanceResponsibility(
                body.Kind,
                previous.LineRef,
                new PurchaseOrderEntityRef(body.Replacement.UserId, body.Replacement.UserVersion),
                previous.CandidateUserRef,
                actorUserId,
                string.IsNullOrWhiteSpace(body.Replacement.Reason)
                    ? AcceptanceResponsibilityBuilder.ReasonRequestedFor
                    : PurchaseOrderCodes.Reason(body.Replacement.Reason),
                DateTimeOffset.UtcNow,
                previous.Version + 1);
        return new ResponsibilityChange(body.Kind, previous.LineRef, previous, replacement);
    }

    private static DeliveryCommitment ToDelivery(PurchaseOrderDeliveryBody body) =>
        new(
            DateOnly.ParseExact(
                body.DeliveryDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            body.DeliveryLocation);

    private static PurchaseOrderContentRef ToRef(PurchaseOrderLineRefBody body) =>
        new(body.Id, body.Version, body.ContentDigest);

    private static PurchaseOrderVersionResponse ToResponse(PurchaseOrderVersionRecord record) =>
        ToResponse(PurchaseOrderSerialization.ReadDocument(record.DocumentJson, record.ContentDigest));

    private static PurchaseOrderVersionResponse ToResponse(PurchaseOrderVersion version) =>
        new(
            version.PoId,
            version.PurchaseOrderVersionNumber,
            version.PredecessorVersion,
            version.PoNumber,
            PurchaseOrderStateCodes.Of(version.State),
            version.SupplierRef.Id,
            version.SupplierRef.Version,
            version.SourceAmount,
            version.SourceCurrency,
            version.BaseAmount,
            version.BaseCurrency,
            version.Lines.Count,
            version.Digest,
            version.OccurredAt,
            version.IssuedAt);

    private static PurchaseOrderAmendmentResponse ToResponse(PurchaseOrderAmendmentVersion version) =>
        new(
            version.AmendmentId,
            version.Version,
            version.PredecessorVersion,
            version.ExpectedPoVersion,
            AmendmentStateCodes.Of(version.State),
            version.Reason,
            version.LineDeltas.Count,
            version.Digest,
            version.OccurredAt);

    private static DirectPurchaseResponse ToResponse(DirectPurchaseAuthorization authorization) =>
        new(
            authorization.AuthorizationId,
            authorization.Version,
            DirectPurchaseStateCodes.Of(authorization.State),
            authorization.RequestRef.Id,
            authorization.RequestRef.Version,
            authorization.SupplierRef.Id,
            authorization.SupplierRef.Version,
            authorization.MaximumSourceAmount,
            authorization.SourceCurrency,
            authorization.MaximumBaseAmount,
            authorization.CoveredLines.Count,
            authorization.Digest,
            authorization.AuthorizedAt);

    private static SupportingDocumentResponse ToResponse(ProcurementSupportingDocumentVersion document) =>
        new(
            document.DocumentId,
            document.Version,
            SupportingDocumentStateCodes.Of(document.State),
            document.BusinessType,
            document.RequestRef.Id,
            document.RequestRef.Version,
            document.FileRef.FileName,
            document.FileRef.ContentType,
            document.FileRef.Length,
            document.FileRef.Sha256,
            document.Digest,
            document.OccurredAt,
            document.ConfirmedAt);
}
