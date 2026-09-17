using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Sourcing of the organization (SPEC 10 REQ-01, REQ-02, REQ-03, REQ-04, REQ-14). A PROCUREMENT_BUYER
/// with an organization assignment opens processes, RFQs, quotations and reviews; PROCUREMENT_APPROVER
/// and AUDITOR only read what their contract allows and ADMIN never gains business capability.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/sourcing")]
public sealed class SourcingController(
    ProcureToPayDbContext dbContext,
    SourcingProcessService processes,
    SourcingQuotationService quotations,
    SourcingEvaluationService evaluations,
    SourcingSelectionService selections,
    SourcingWaiverService waivers,
    SourcingProposalService proposals,
    SourcingAwardService awards)
    : ControllerBase
{
    [HttpPost("processes")]
    public async Task<ActionResult<SourcingProcessResponse>> CreateProcess(
        CreateProcessRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var process = await processes.CreateProcessAsync(
                new CreateSourcingProcessCommand(
                    organizationId,
                    request.RequestId,
                    request.RequestVersion,
                    (request.Lines ?? [])
                        .Select(line => new SourcingLineDraft(
                            line.LineId, line.LineVersion, line.RequestedQuantity, line.UnitCode ?? string.Empty))
                        .ToArray(),
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Created($"/api/v1/sourcing/processes/{process.ProcessId}", ToResponse(process));
    }

    [HttpGet("processes")]
    public async Task<ActionResult<IReadOnlyList<SourcingProcessResponse>>> ListProcesses(
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var listed = await processes.ListProcessesAsync(organizationId, 100, cancellationToken);
        return Ok(listed.Select(ToResponse).ToArray());
    }

    [HttpGet("processes/{processId:guid}")]
    public async Task<ActionResult<SourcingProcessResponse>> GetProcess(
        Guid processId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var process = await processes.GetProcessAsync(organizationId, processId, cancellationToken);
        return Ok(ToResponse(process));
    }

    [HttpPost("processes/{processId:guid}/cancel")]
    public async Task<ActionResult<SourcingProcessResponse>> CancelProcess(
        Guid processId,
        TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var process = await processes.CancelProcessAsync(
                new CancelSourcingProcessCommand(
                    organizationId,
                    processId,
                    request.ExpectedVersion,
                    request.Reason ?? string.Empty,
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(process));
    }

    [HttpPost("processes/{processId:guid}/rfq")]
    public async Task<ActionResult<SourcingRfqResponse>> CreateRfq(
        Guid processId,
        CreateRfqRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await processes.CreateRfqAsync(
                new CreateRfqCommand(
                    organizationId,
                    processId,
                    request.ExpectedProcessVersion,
                    request.Currency ?? string.Empty,
                    ToTerms(request.Terms),
                    ToWeights(request.Weights),
                    request.ResponseDeadline,
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Created($"/api/v1/sourcing/rfqs/{outcome.RfqId}", ToResponse(outcome));
    }

    [HttpGet("rfqs/{rfqId:guid}")]
    public async Task<ActionResult<SourcingRfqViewResponse>> GetRfq(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var rfq = await processes.GetRfqAsync(organizationId, rfqId, cancellationToken);
        return Ok(new SourcingRfqViewResponse(
            rfq.RfqId,
            rfq.ProcessId,
            rfq.RequestId,
            rfq.RequestVersion,
            SourcingStateCodes.RfqStatusCode(rfq.Status),
            rfq.CurrentVersion,
            rfq.ResponseDeadline,
            rfq.OpenedAt,
            rfq.Versions
                .Select(version => new SourcingRfqVersionResponse(
                    version.Version,
                    version.StatusCode,
                    version.PredecessorVersion,
                    version.OpenedAt,
                    version.ResponseDeadline,
                    version.ContentDigest))
                .ToArray(),
            rfq.Extensions
                .Select(extension => new SourcingRfqExtensionResponse(
                    extension.FromVersion,
                    extension.ToVersion,
                    extension.PreviousDeadline,
                    extension.NewDeadline,
                    extension.OccurredAt))
                .ToArray()));
    }

    [HttpPost("rfqs/{rfqId:guid}/open")]
    public async Task<ActionResult<SourcingRfqResponse>> OpenRfq(
        Guid rfqId,
        TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await processes.OpenRfqAsync(
                new TransitionRfqCommand(
                    organizationId, rfqId, request.ExpectedVersion, request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("rfqs/{rfqId:guid}/extend")]
    public async Task<ActionResult<SourcingRfqResponse>> ExtendRfq(
        Guid rfqId,
        TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await processes.ExtendDeadlineAsync(
                new TransitionRfqCommand(
                    organizationId,
                    rfqId,
                    request.ExpectedVersion,
                    request.CommandKey ?? string.Empty,
                    request.Reason,
                    request.NewDeadline),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("rfqs/{rfqId:guid}/close")]
    public async Task<ActionResult<SourcingRfqResponse>> CloseRfq(
        Guid rfqId,
        TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await processes.CloseRfqAsync(
                new TransitionRfqCommand(
                    organizationId, rfqId, request.ExpectedVersion, request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("rfqs/{rfqId:guid}/cancel")]
    public async Task<ActionResult<SourcingRfqResponse>> CancelRfq(
        Guid rfqId,
        TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await processes.CancelRfqAsync(
                new TransitionRfqCommand(
                    organizationId,
                    rfqId,
                    request.ExpectedVersion,
                    request.CommandKey ?? string.Empty,
                    request.Reason),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("rfqs/{rfqId:guid}/attachments")]
    [RequestSizeLimit(21 * 1024 * 1024)]
    public async Task<ActionResult<SourcingAttachmentView>> StageAttachment(
        Guid rfqId,
        [FromForm] SourcingStageAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var file = request.File
            ?? throw new DomainValidationException("A quotation attachment is required.");
        await using var stream = file.OpenReadStream();
        var attachment = await quotations.StageAttachmentAsync(
                organizationId,
                rfqId,
                actor.Id,
                file.FileName,
                file.ContentType,
                file.Length,
                stream,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Created(
            $"/api/v1/sourcing/attachments/{attachment.FileId}/versions/{attachment.Version}",
            attachment);
    }

    [HttpPost("attachments/{attachmentId:guid}/versions/{version:int}/confirm")]
    public async Task<ActionResult<SourcingAttachmentView>> ConfirmAttachment(
        Guid attachmentId,
        int version,
        SourcingConfirmAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var attachment = await quotations.ConfirmAttachmentAsync(
                organizationId,
                attachmentId,
                version,
                request.Sha256 ?? string.Empty,
                actor.Id,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(attachment);
    }

    [HttpGet("attachments/{attachmentId:guid}/versions/{version:int}/download")]
    public async Task<ActionResult<AttachmentDownloadResponse>> DownloadAttachment(
        Guid attachmentId,
        int version,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var download = await quotations.DownloadAttachmentAsync(
                organizationId, attachmentId, version, actor.Id, DateTimeOffset.UtcNow, cancellationToken);
        return Ok(new AttachmentDownloadResponse(download.Url.ToString(), download.ExpiresAt));
    }

    [HttpGet("attachments/{attachmentId:guid}/versions/{version:int}")]
    public async Task<ActionResult<SourcingAttachmentView>> GetAttachment(
        Guid attachmentId,
        int version,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var attachment = await quotations.GetAttachmentAsync(organizationId, attachmentId, version, cancellationToken);
        return Ok(attachment);
    }

    [HttpPost("rfqs/{rfqId:guid}/quotations")]
    public async Task<ActionResult<SourcingQuotationResponse>> RegisterQuotation(
        Guid rfqId,
        RegisterQuotationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await quotations.RegisterQuotationAsync(
                new RegisterQuotationCommand(
                    organizationId,
                    rfqId,
                    request.SupplierId,
                    request.SupplierVersion,
                    request.Currency ?? string.Empty,
                    ToTerms(request.Terms),
                    request.ReceivedAt,
                    (request.Lines ?? [])
                        .Select(line => new QuotationLineDraft(
                            line.LineId,
                            line.Quantity ?? string.Empty,
                            line.UnitPrice ?? string.Empty,
                            line.Subtotal ?? string.Empty,
                            line.Taxes ?? string.Empty,
                            line.AdditionalCharges ?? string.Empty,
                            line.Discounts ?? string.Empty,
                            line.GrossTotal ?? string.Empty,
                            line.TechnicalResponse ?? string.Empty))
                        .ToArray(),
                    (request.Attachments ?? [])
                        .Select(attachment => new SourcingAttachmentRef(
                            attachment.ContentType ?? string.Empty,
                            attachment.FileId,
                            attachment.FileName ?? string.Empty,
                            attachment.Length,
                            attachment.Sha256 ?? string.Empty,
                            attachment.Version))
                        .ToArray(),
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Created($"/api/v1/sourcing/quotations/{outcome.QuotationId}", ToResponse(outcome));
    }

    [HttpPost("quotations/{quotationId:guid}/review")]
    public async Task<ActionResult<SourcingQuotationResponse>> ReviewQuotation(
        Guid quotationId,
        ReviewQuotationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await quotations.ReviewQuotationAsync(
                new ReviewQuotationCommand(
                    organizationId,
                    quotationId,
                    request.ExpectedVersion,
                    request.Status,
                    (request.Codes ?? [])
                        .Select(code => Enum.TryParse<QuotationReviewCode>(code, ignoreCase: true, out var parsed)
                            ? parsed
                            : throw new DomainValidationException($"The review code '{code}' is unknown."))
                        .ToArray(),
                    request.Motive,
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpPost("quotations/{quotationId:guid}/withdraw")]
    public async Task<ActionResult<SourcingQuotationResponse>> WithdrawQuotation(
        Guid quotationId,
        ReviewQuotationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var outcome = await quotations.WithdrawQuotationAsync(
                new ReviewQuotationCommand(
                    organizationId,
                    quotationId,
                    request.ExpectedVersion,
                    QuotationReviewStatus.Withdrawn,
                    [],
                    request.Motive,
                    request.CommandKey ?? string.Empty),
                actor.Id,
                HttpContext.TraceIdentifier,
                DateTimeOffset.UtcNow,
                cancellationToken);
        return Ok(ToResponse(outcome));
    }

    [HttpGet("rfqs/{rfqId:guid}/quotations")]
    public async Task<ActionResult<IReadOnlyList<SourcingQuotationViewResponse>>> ListQuotations(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var listed = await quotations.ListQuotationsAsync(organizationId, rfqId, cancellationToken);
        return Ok(listed.Select(ToResponse).ToArray());
    }

    [HttpGet("quotations/{quotationId:guid}")]
    public async Task<ActionResult<SourcingQuotationViewResponse>> GetQuotation(
        Guid quotationId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var quotation = await quotations.GetQuotationAsync(organizationId, quotationId, cancellationToken);
        return Ok(ToResponse(quotation));
    }

    [HttpGet("rfqs/{rfqId:guid}/counts")]
    public async Task<ActionResult<IReadOnlyList<QuotationCountResponse>>> CountValidQuotations(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var counts = await quotations.CountValidQuotationsAsync(organizationId, rfqId, cancellationToken);
        return Ok(counts
            .OrderBy(count => count.Key)
            .Select(count => new QuotationCountResponse(count.Key, count.Value))
            .ToArray());
    }

    /// <summary>
    /// Evidence references travel with the metadata the caller already received from the staging
    /// endpoint; the server still matches every field against the confirmed attachment it stored.
    /// </summary>
    private static SourcingAttachmentRef ToAttachmentRef(QuotationAttachmentRequest? attachment)
    {
        if (attachment is null)
        {
            throw new DomainValidationException("The command requires its evidence attachment reference.");
        }

        return new SourcingAttachmentRef(
            attachment.ContentType ?? string.Empty,
            attachment.FileId,
            attachment.FileName ?? string.Empty,
            attachment.Length,
            attachment.Sha256 ?? string.Empty,
            attachment.Version);
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);

    [HttpPost("rfqs/{rfqId:guid}/fx-snapshots")]
    public async Task<ActionResult<SourcingFxSnapshotView>> RegisterFxSnapshot(
        Guid rfqId,
        RegisterFxSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var snapshot = await evaluations.RegisterFxSnapshotAsync(
            new RegisterFxSnapshotCommand(
                organizationId,
                rfqId,
                request.SourceCurrency ?? string.Empty,
                request.Rate,
                request.EffectiveAt,
                request.SourceReference ?? string.Empty,
                ToAttachmentRef(request.Attachment),
                request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created($"/api/v1/sourcing/rfqs/{rfqId}/fx-snapshots/{snapshot.Id}", snapshot);
    }

    [HttpPost("rfqs/{rfqId:guid}/manual-scores")]
    public async Task<ActionResult<SourcingManualScoreView>> RecordManualScore(
        Guid rfqId,
        RecordManualScoreRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var score = await evaluations.RecordManualScoreAsync(
            new RecordManualScoreCommand(
                organizationId,
                rfqId,
                request.LineId,
                request.SupplierId,
                request.Criterion,
                request.Score,
                request.Justification ?? string.Empty,
                ToAttachmentRef(request.Evidence),
                request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created($"/api/v1/sourcing/rfqs/{rfqId}/manual-scores/{score.Id}", score);
    }

    [HttpPost("rfqs/{rfqId:guid}/evaluations")]
    public async Task<ActionResult<SourcingEvaluationView>> EvaluateRfq(
        Guid rfqId,
        EvaluateRfqRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var evaluation = await evaluations.EvaluateAsync(
            new EvaluateRfqCommand(organizationId, rfqId, request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created(
            $"/api/v1/sourcing/evaluations/{evaluation.EvaluationId}/versions/{evaluation.Version}",
            evaluation);
    }

    [HttpGet("rfqs/{rfqId:guid}/evaluations/current")]
    public async Task<ActionResult<SourcingEvaluationView>> GetCurrentEvaluation(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await evaluations.GetCurrentEvaluationAsync(organizationId, rfqId, cancellationToken));
    }

    [HttpGet("evaluations/{evaluationId:guid}/versions/{version:int}")]
    public async Task<ActionResult<SourcingEvaluationView>> GetEvaluation(
        Guid evaluationId,
        int version,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await evaluations.GetEvaluationAsync(
            organizationId, evaluationId, version, cancellationToken));
    }

    [HttpPost("rfqs/{rfqId:guid}/selections")]
    public async Task<ActionResult<SourcingSelectionView>> SelectLine(
        Guid rfqId,
        SelectLineRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var selection = await selections.SelectAsync(
            new SelectLineCommand(
                organizationId,
                rfqId,
                request.LineId,
                request.SupplierId,
                request.ExpectedSelectionVersion,
                request.DeviationJustification,
                request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created(
            $"/api/v1/sourcing/rfqs/{rfqId}/selections/{selection.SelectionId}", selection);
    }

    [HttpGet("rfqs/{rfqId:guid}/selections")]
    public async Task<ActionResult<IReadOnlyList<SourcingSelectionView>>> ListSelections(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await selections.ListSelectionsAsync(organizationId, rfqId, cancellationToken));
    }

    [HttpGet("rfqs/{rfqId:guid}/proposal-drafts")]
    public async Task<ActionResult<IReadOnlyList<SourcingProposalDraft>>> ListProposalDrafts(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await selections.PartitionAsync(organizationId, rfqId, cancellationToken));
    }

    [HttpPost("rfqs/{rfqId:guid}/waivers")]
    public async Task<ActionResult<SourcingWaiverView>> RequestWaiver(
        Guid rfqId,
        RequestWaiverRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var waiver = await waivers.RequestReductionAsync(
            new RequestQuotationWaiverCommand(
                organizationId,
                rfqId,
                request.RequirementKey ?? string.Empty,
                request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created($"/api/v1/sourcing/waivers/{waiver.FactsId}", waiver);
    }

    [HttpGet("waivers/{factsId:guid}")]
    public async Task<ActionResult<SourcingWaiverView>> GetWaiver(
        Guid factsId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await waivers.GetFactsAsync(organizationId, factsId, cancellationToken));
    }

    [HttpPost("rfqs/{rfqId:guid}/proposals")]
    public async Task<ActionResult<SourcingProposalView>> BuildProposal(
        Guid rfqId,
        BuildProposalRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var proposal = await proposals.BuildAsync(
            new BuildProposalCommand(
                organizationId, rfqId, request.SupplierId, request.CommandKey ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created(
            $"/api/v1/sourcing/proposals/{proposal.ProposalId}/versions/{proposal.Version}", proposal);
    }

    [HttpGet("rfqs/{rfqId:guid}/proposals")]
    public async Task<ActionResult<IReadOnlyList<SourcingProposalView>>> ListProposals(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await proposals.ListProposalsAsync(organizationId, rfqId, cancellationToken));
    }

    [HttpGet("proposals/{proposalId:guid}/versions/{version:int}")]
    public async Task<ActionResult<SourcingProposalView>> GetProposal(
        Guid proposalId,
        int version,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await proposals.GetProposalAsync(organizationId, proposalId, version, cancellationToken));
    }

    [HttpPost("proposals/{proposalId:guid}/versions/{version:int}/award")]
    public async Task<ActionResult<SourcingAwardView>> PublishAward(
        Guid proposalId,
        int version,
        PublishAwardRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireBuyerAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var award = await awards.PublishAsync(
            new PublishAwardCommand(
                organizationId,
                proposalId,
                version,
                request.ExpectedProcessVersion,
                request.ExpectedAwardVersion,
                request.AwardKey ?? string.Empty,
                request.Reason ?? string.Empty),
            actor.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Created($"/api/v1/sourcing/awards/{award.AwardId}/versions/{award.Version}", award);
    }

    [HttpGet("awards/{awardId:guid}/versions/{version:int}")]
    public async Task<ActionResult<SourcingAwardView>> GetAward(
        Guid awardId,
        int version,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await awards.GetAwardAsync(organizationId, awardId, version, cancellationToken));
    }

    [HttpGet("rfqs/{rfqId:guid}/awards")]
    public async Task<ActionResult<IReadOnlyList<SourcingAwardView>>> ListAwards(
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        await RequireBuyerOrAuditorAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        return Ok(await awards.ListAwardsAsync(organizationId, rfqId, cancellationToken));
    }

    private async Task<UserProfileRecord> RequireBuyerAsync(CancellationToken cancellationToken)
    {
        var actor = await RequireAuthenticatedAsync(cancellationToken);
        var organization = await dbContext.RoleAssignments
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.UserProfileId == actor.Id &&
                              assignment.Role == (int)SystemRole.ProcurementBuyer &&
                              assignment.Status == (int)AssignmentStatus.Active &&
                              assignment.ScopeJson == "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                cancellationToken);
        if (!organization)
        {
            throw new DomainForbiddenException(
                "Sourcing requires an active PROCUREMENT_BUYER with an organization assignment.");
        }

        return actor;
    }

    private async Task<UserProfileRecord> RequireBuyerOrAuditorAsync(CancellationToken cancellationToken)
    {
        var actor = await RequireAuthenticatedAsync(cancellationToken);
        var allowed = await dbContext.RoleAssignments
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.UserProfileId == actor.Id &&
                              assignment.Status == (int)AssignmentStatus.Active &&
                              assignment.ScopeJson == "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]" &&
                              (assignment.Role == (int)SystemRole.ProcurementBuyer ||
                               assignment.Role == (int)SystemRole.Auditor ||
                               assignment.Role == (int)SystemRole.ProcurementApprover),
                cancellationToken);
        if (!allowed)
        {
            throw new DomainForbiddenException("The actor cannot read the sourcing history.");
        }

        return actor;
    }

    private async Task<UserProfileRecord> RequireAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var actor = await HttpContext.RequestServices
            .GetRequiredService<CurrentUserProvisioningService>()
            .EnsureProfileAsync(User, cancellationToken);
        if (actor.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return actor;
    }

    private static CommercialTerms ToTerms(CommercialTermsRequest? terms) => new(
        terms?.DeliveryDays ?? 0,
        terms?.IncotermCode,
        terms?.PaymentTermsCode ?? string.Empty,
        terms?.WarrantyDays ?? 0);

    private static EvaluationWeightSet ToWeights(IReadOnlyList<EvaluationWeightRequest>? weights) =>
        new((weights ?? [])
            .Select(weight => new EvaluationWeight(weight.Criterion, weight.Weight))
            .ToArray());

    private static SourcingProcessResponse ToResponse(SourcingProcessView process) => new(
        process.ProcessId,
        process.RequestId,
        process.RequestVersion,
        process.State,
        process.Version,
        process.TakeoverHeld,
        process.Lines
            .Select(line => new SourcingProcessLineResponse(
                line.LineRef.Id, line.LineRef.Version, line.LineRef.ContentDigest,
                line.RequestedQuantity, line.UnitCode))
            .ToArray(),
        process.UpdatedAt);

    private static SourcingRfqResponse ToResponse(SourcingRfqOutcome outcome) => new(
        outcome.RfqId, outcome.Version, outcome.Status, outcome.ContentDigest, outcome.Replayed);

    private static SourcingQuotationResponse ToResponse(SourcingQuotationOutcome outcome) => new(
        outcome.QuotationId,
        outcome.Version,
        outcome.Timeliness,
        outcome.ReviewStatus,
        outcome.ContentDigest,
        outcome.Replayed);

    private static SourcingQuotationViewResponse ToResponse(QuotationView quotation) => new(
        quotation.QuotationId,
        quotation.RfqId,
        quotation.SupplierId,
        quotation.CurrentVersion,
        quotation.Versions
            .Select(version => new SourcingQuotationVersionResponse(
                version.Version,
                version.PredecessorVersion,
                version.Timeliness,
                version.Content.Review.StatusCode,
                version.Content.ReceivedAt,
                version.Content.RegisteredAt,
                version.ContentDigest))
            .ToArray());
}

public sealed record CreateProcessRequest(
    Guid RequestId,
    int RequestVersion,
    IReadOnlyList<SourcingLineRequest>? Lines,
    string? CommandKey);

public sealed record SourcingLineRequest(Guid LineId, int LineVersion, decimal RequestedQuantity, string? UnitCode);

public sealed record TransitionRequest(
    int ExpectedVersion,
    string? CommandKey,
    string? Reason = null,
    DateTimeOffset? NewDeadline = null);

public sealed record CommercialTermsRequest(int DeliveryDays, string? IncotermCode, string? PaymentTermsCode, int WarrantyDays);

public sealed record EvaluationWeightRequest(SourcingEvaluationCriterion Criterion, int Weight);

public sealed record CreateRfqRequest(
    int ExpectedProcessVersion,
    string? Currency,
    CommercialTermsRequest? Terms,
    IReadOnlyList<EvaluationWeightRequest>? Weights,
    DateTimeOffset ResponseDeadline,
    string? CommandKey);

public sealed record SourcingStageAttachmentRequest(IFormFile? File);

public sealed record SourcingConfirmAttachmentRequest(string? Sha256);

public sealed record QuotationLineRequest(
    Guid LineId,
    string? Quantity,
    string? UnitPrice,
    string? Subtotal,
    string? Taxes,
    string? AdditionalCharges,
    string? Discounts,
    string? GrossTotal,
    string? TechnicalResponse);

public sealed record QuotationAttachmentRequest(
    Guid FileId,
    int Version,
    string? FileName,
    string? ContentType,
    long Length,
    string? Sha256);
public sealed record RegisterQuotationRequest(
    Guid SupplierId,
    int SupplierVersion,
    string? Currency,
    CommercialTermsRequest? Terms,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<QuotationLineRequest>? Lines,
    IReadOnlyList<QuotationAttachmentRequest>? Attachments,
    string? CommandKey);

public sealed record ReviewQuotationRequest(
    int ExpectedVersion,
    QuotationReviewStatus Status,
    IReadOnlyList<string>? Codes,
    string? Motive,
    string? CommandKey);

public sealed record RegisterFxSnapshotRequest(
    string? SourceCurrency,
    decimal Rate,
    DateTimeOffset EffectiveAt,
    string? SourceReference,
    QuotationAttachmentRequest? Attachment,
    string? CommandKey);

public sealed record RecordManualScoreRequest(
    Guid LineId,
    Guid SupplierId,
    SourcingEvaluationCriterion Criterion,
    decimal Score,
    string? Justification,
    QuotationAttachmentRequest? Evidence,
    string? CommandKey);

public sealed record EvaluateRfqRequest(string? CommandKey);

public sealed record RequestWaiverRequest(string? RequirementKey, string? CommandKey);

public sealed record BuildProposalRequest(Guid SupplierId, string? CommandKey);

public sealed record PublishAwardRequest(
    int ExpectedProcessVersion,
    int? ExpectedAwardVersion,
    string? AwardKey,
    string? Reason);

public sealed record SelectLineRequest(
    Guid LineId,
    Guid SupplierId,
    int? ExpectedSelectionVersion,
    string? DeviationJustification,
    string? CommandKey);

public sealed record SourcingProcessLineResponse(
    Guid LineId,
    int LineVersion,
    string ContentDigest,
    decimal RequestedQuantity,
    string UnitCode);

public sealed record SourcingProcessResponse(
    Guid ProcessId,
    Guid RequestId,
    int RequestVersion,
    string State,
    int Version,
    bool TakeoverHeld,
    IReadOnlyList<SourcingProcessLineResponse> Lines,
    DateTimeOffset UpdatedAt);

public sealed record SourcingRfqResponse(
    Guid RfqId,
    int Version,
    string Status,
    string ContentDigest,
    bool Replayed);

public sealed record SourcingRfqVersionResponse(
    int Version,
    string Status,
    int? PredecessorVersion,
    DateTimeOffset? OpenedAt,
    DateTimeOffset ResponseDeadline,
    string ContentDigest);

public sealed record SourcingRfqExtensionResponse(
    int FromVersion,
    int ToVersion,
    DateTimeOffset PreviousDeadline,
    DateTimeOffset NewDeadline,
    DateTimeOffset OccurredAt);

public sealed record SourcingRfqViewResponse(
    Guid RfqId,
    Guid ProcessId,
    Guid RequestId,
    int RequestVersion,
    string Status,
    int CurrentVersion,
    DateTimeOffset ResponseDeadline,
    DateTimeOffset? OpenedAt,
    IReadOnlyList<SourcingRfqVersionResponse> Versions,
    IReadOnlyList<SourcingRfqExtensionResponse> Extensions);

public sealed record SourcingQuotationResponse(
    Guid QuotationId,
    int Version,
    string Timeliness,
    string ReviewStatus,
    string ContentDigest,
    bool Replayed);

public sealed record SourcingQuotationVersionResponse(
    int Version,
    int? PredecessorVersion,
    string Timeliness,
    string ReviewStatus,
    DateTimeOffset ReceivedAt,
    DateTimeOffset RegisteredAt,
    string ContentDigest);

public sealed record SourcingQuotationViewResponse(
    Guid QuotationId,
    Guid RfqId,
    Guid SupplierId,
    int CurrentVersion,
    IReadOnlyList<SourcingQuotationVersionResponse> Versions);

public sealed record QuotationCountResponse(Guid LineId, int ValidQuotations);

public sealed record AttachmentDownloadResponse(string Url, DateTimeOffset ExpiresAt);
