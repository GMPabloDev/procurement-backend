using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Approved Supplier Catalog (SPEC 09 REQ-06, REQ-07, REQ-11): PROCUREMENT_BUYER drafts entries and
/// stages agreement files, PROCUREMENT_APPROVER decides them through Approval Workflow, any active
/// user reads the effective projections needed to build a Purchase Request, and only Procurement and
/// AUDITOR download the agreement through a temporary URL.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/approved-supplier-catalog")]
public sealed class ApprovedSupplierCatalogController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    ApprovedSupplierCatalogGovernanceService catalog) : ControllerBase
{
    [HttpPost("attachments")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<ActionResult<SupplierAgreementAttachmentView>> StageAttachment(
        [FromForm] StageAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        await RequireProcurementAsync(actor.Id, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var file = request.File
            ?? throw new DomainValidationException("An agreement file is required.");
        await using var stream = file.OpenReadStream();
        var attachment = await catalog.StageAttachmentAsync(
            organizationId,
            request.SupplierId,
            actor.Id,
            file.FileName,
            file.ContentType,
            file.Length,
            stream,
            cancellationToken);
        return Created(
            $"/api/v1/approved-supplier-catalog/attachments/{attachment.Id}/versions/{attachment.Version}",
            attachment);
    }

    [HttpPost("attachments/{attachmentId:guid}/confirm")]
    public async Task<ActionResult<SupplierAgreementAttachmentView>> ConfirmAttachment(
        Guid attachmentId,
        ConfirmAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        await RequireProcurementAsync(actor.Id, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var attachment = await catalog.ConfirmAttachmentAsync(
            organizationId, attachmentId, actor.Id, request.Sha256 ?? string.Empty, cancellationToken);
        return Ok(attachment);
    }

    [HttpPost("entries")]
    public async Task<ActionResult<ApprovedCatalogVersionResponse>> Save(
        SaveCatalogEntryRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        await RequireProcurementAsync(actor.Id, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var supplierRef = new VersionedEntityRef("SUPPLIER", request.SupplierId, request.SupplierVersion);
        var categoryRef = new VersionedCodeRef(
            "SPEND_CATEGORY",
            request.SpendCategoryCode ?? string.Empty,
            request.SpendCategoryVersion,
            request.SpendCategoryDigest ?? string.Empty);
        var productRef = request.ProductId is Guid productId
            ? new VersionedEntityRef("PRODUCT", productId, request.ProductVersion ?? 1)
            : null;
        var outcome = await catalog.SaveAsync(
            organizationId,
            actor.Id,
            request.CatalogEntryId,
            request.ExpectedVersion,
            supplierRef,
            categoryRef,
            productRef,
            request.NegotiatedPrice,
            request.Currency ?? string.Empty,
            request.UnitCode ?? string.Empty,
            request.ExternalContractReference ?? string.Empty,
            request.ValidFrom,
            request.ValidTo,
            ParseStatus(request.Status),
            request.AttachmentId,
            request.AttachmentVersion,
            request.Reason ?? string.Empty,
            request.ChangeKey ?? string.Empty,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(new ApprovedCatalogVersionResponse(
            outcome.Version.CatalogEntryId,
            outcome.Version.Version,
            outcome.RequiresApproval,
            outcome.ProposalId));
    }

    [HttpPost("entries/{catalogEntryId:guid}/versions/{candidateVersion:int}/submit")]
    public async Task<ActionResult<SupplierSubmitResponse>> Submit(
        Guid catalogEntryId,
        int candidateVersion,
        SubmitSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        await RequireProcurementAsync(actor.Id, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await catalog.SubmitAsync(
            organizationId,
            actor.Id,
            catalogEntryId,
            candidateVersion,
            request.SubmissionKey ?? string.Empty,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(new SupplierSubmitResponse(
            outcome.ProposalId, outcome.CandidateVersion, outcome.CaseId, outcome.Replayed));
    }

    [HttpGet("entries")]
    public async Task<ActionResult<IReadOnlyList<ApprovedSupplierReferenceView>>> List(
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await catalog.ListEffectiveAsync(organizationId, DateTimeOffset.UtcNow, cancellationToken));
    }

    [HttpGet("attachments/{attachmentId:guid}/download")]
    public async Task<ActionResult<AgreementDownloadResponse>> Download(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        await RequireProcurementOrAuditorAsync(actor.Id, cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var url = await catalog.CreateDownloadUrlAsync(
            organizationId, attachmentId, actor.Id, HttpContext.TraceIdentifier, cancellationToken);
        return Ok(new AgreementDownloadResponse(url.ToString()));
    }

    private static ApprovedCatalogEntryStatus ParseStatus(string? value) =>
        Enum.TryParse<ApprovedCatalogEntryStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new DomainValidationException("The catalog entry status is invalid.");

    private async Task<UserProfileRecord> RequireActiveUserAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return profile;
    }

    private async Task RequireProcurementAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await dbContext.RoleAssignments
                .AsNoTracking()
                .AnyAsync(
                    assignment => assignment.UserProfileId == userId &&
                                  (assignment.Role == (int)SystemRole.ProcurementBuyer ||
                                   assignment.Role == (int)SystemRole.ProcurementApprover) &&
                                  assignment.Status == (int)AssignmentStatus.Active,
                    cancellationToken))
        {
            return;
        }

        throw new DomainForbiddenException("The actor cannot administer the approved supplier catalog.");
    }

    private async Task RequireProcurementOrAuditorAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await dbContext.RoleAssignments
                .AsNoTracking()
                .AnyAsync(
                    assignment => assignment.UserProfileId == userId &&
                                  (assignment.Role == (int)SystemRole.ProcurementBuyer ||
                                   assignment.Role == (int)SystemRole.ProcurementApprover ||
                                   assignment.Role == (int)SystemRole.Auditor) &&
                                  assignment.Status == (int)AssignmentStatus.Active,
                    cancellationToken))
        {
            return;
        }

        throw new DomainForbiddenException("The actor cannot download the agreement attachment.");
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);
}

public sealed record StageAttachmentRequest(Guid SupplierId, IFormFile? File);

public sealed record ConfirmAttachmentRequest(string? Sha256);

public sealed record SaveCatalogEntryRequest(
    Guid? CatalogEntryId,
    int? ExpectedVersion,
    Guid SupplierId,
    int SupplierVersion,
    string? SpendCategoryCode,
    int SpendCategoryVersion,
    string? SpendCategoryDigest,
    Guid? ProductId,
    int? ProductVersion,
    decimal NegotiatedPrice,
    string? Currency,
    string? UnitCode,
    string? ExternalContractReference,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string? Status,
    Guid AttachmentId,
    int AttachmentVersion,
    string? Reason,
    string? ChangeKey);

public sealed record ApprovedCatalogVersionResponse(
    Guid CatalogEntryId,
    int Version,
    bool RequiresApproval,
    Guid? ProposalId);

public sealed record AgreementDownloadResponse(string Url);
