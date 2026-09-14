using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.Api.Controllers;

public sealed record PurchaseRequestEntityRefBody(string EntityType, Guid Id, int Version);

public sealed record PurchaseRequestCodeRefBody(string Catalog, string Code, int Version, string Digest);

public sealed record PurchaseRequestAnswerBody(
    string QuestionCode,
    int SchemaVersion,
    string Value,
    string ValueKind);

public sealed record PurchaseRequestFxBody(
    Guid AttestationId,
    int AttestationVersion,
    string BaseCurrency,
    string TransactionCurrency,
    decimal EffectiveRate,
    DateOnly RateDate);

public sealed record PurchaseRequestLineContentBody(
    decimal EstimatedGrossAmount,
    string TransactionCurrency,
    decimal BaseAmount,
    string BaseCurrency,
    int FiscalYear,
    string PurchaseType,
    PurchaseRequestCodeRefBody SpendCategoryRef,
    PurchaseRequestEntityRefBody CostCenterRef,
    PurchaseRequestEntityRefBody CostCenterDepartmentRef,
    PurchaseRequestEntityRefBody BeneficiaryDepartmentRef,
    PurchaseRequestEntityRefBody RequestedForUserRef,
    PurchaseRequestEntityRefBody? SupplierRef,
    PurchaseRequestEntityRefBody? PreferredProductRef,
    PurchaseRequestEntityRefBody? RequiredProductRef,
    bool ContractRequired,
    bool NonStandardTerms,
    string AgreementStatus,
    string NeedSummary,
    PurchaseRequestAnswerBody[]? RiskAnswers,
    PurchaseRequestFxBody? FxAttestationRef);

public sealed record PurchaseRequestLineDraftBody(string ClientLineKey, PurchaseRequestLineContentBody Content);

public sealed record PurchaseRequestLineRefBody(Guid Id, int Version, string ContentDigest);

public sealed record PurchaseRequestLineChangeBody(
    Guid Id,
    int ExpectedVersion,
    string ExpectedContentDigest,
    PurchaseRequestLineContentBody Content);

public sealed record PurchaseRequestCreateBody(
    PurchaseRequestEntityRefBody LegalEntityRef,
    string BusinessJustification,
    string Reason,
    string RevisionKey,
    PurchaseRequestLineDraftBody[] Lines);

public sealed record PurchaseRequestRevisionBody(
    int ExpectedRequestVersion,
    string BusinessJustification,
    string Reason,
    string RevisionKey,
    PurchaseRequestLineRefBody[]? Retained,
    PurchaseRequestLineChangeBody[]? Changed,
    PurchaseRequestLineDraftBody[]? Added,
    PurchaseRequestLineRefBody[]? Removed);

public sealed record PurchaseRequestCancellationBody(int ExpectedVersion, string CancelKey, string Reason);

public sealed record PurchaseRequestSubmissionBody(int ExpectedVersion, string SubmissionKey, string Reason);

public sealed record PurchaseRequestSubmissionResponse(
    Guid RequestId,
    int Version,
    PurchaseRequestStatus Status,
    PurchaseRequestSubmissionStatus AttemptStatus,
    Guid? PolicyEvaluationBundleId,
    Guid? ApprovalCaseId,
    bool Replayed);

public sealed record PurchaseRequestCreatedResponse(
    Guid RequestId,
    int Version,
    string ContentDigest,
    PurchaseRequestStatus Status,
    bool Replayed);

public sealed record PurchaseRequestRevisionResponse(
    Guid RequestId,
    int Version,
    string ContentDigest,
    int Added,
    int Changed,
    int Removed,
    bool Replayed);

public sealed record PurchaseRequestLineResponse(
    Guid LineId,
    int LineVersion,
    string ContentDigest,
    PurchaseRequestLineContentBody? Content);

public sealed record PurchaseRequestVersionResponse(
    int Version,
    int? PredecessorVersion,
    Guid LegalEntityId,
    int LegalEntityVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseRequestLineResponse> Lines,
    string? BusinessJustification,
    string? RevisionKey,
    string? Reason);

public sealed record PurchaseRequestResponse(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterId,
    PurchaseRequestStatus Status,
    int CurrentVersion,
    IReadOnlyList<PurchaseRequestVersionResponse> Versions);

/// <summary>
/// Immutable Purchase Requests surface (REQ-11): create, revise, cancel and read. The caller never
/// supplies facts, manifests, results or state; organization and requester come from the token.
/// </summary>
[ApiController]
[Authorize]
[Route("v1/purchase-requests")]
public sealed class PurchaseRequestController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    PurchaseRequestPersistenceService service,
    PurchaseRequestSubmissionService submissionService) : ControllerBase
{
    private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";

    [HttpPost]
    public async Task<ActionResult<PurchaseRequestCreatedResponse>> Create(
        PurchaseRequestCreateBody request,
        CancellationToken cancellationToken)
    {
        RequireSnapshotSize();
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var creation = await service.CreateAsync(
            new PurchaseRequestCreateCommand(
                profile.OrganizationId,
                profile.Id,
                ToEntityRef(request.LegalEntityRef),
                request.BusinessJustification,
                request.RevisionKey,
                request.Reason,
                (request.Lines ?? []).Select(line => new PurchaseRequestLineDraft(
                    line.ClientLineKey, ToContent(line.Content)))),
            profile.Id,
            Correlation(),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return StatusCode(
            StatusCodes.Status201Created,
            new PurchaseRequestCreatedResponse(
                creation.RequestId,
                creation.Version,
                creation.ContentDigest,
                creation.Replayed ? PurchaseRequestStatus.Draft : PurchaseRequestStatus.Draft,
                creation.Replayed));
    }

    [HttpPost("{requestId:guid}/revisions")]
    public async Task<ActionResult<PurchaseRequestRevisionResponse>> Revise(
        Guid requestId,
        PurchaseRequestRevisionBody request,
        CancellationToken cancellationToken)
    {
        RequireSnapshotSize();
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var request_ = await RequireOwnedRequestAsync(requestId, profile, cancellationToken);
        var revision = await service.ReviseAsync(
            new PurchaseRequestRevisionCommand(
                requestId,
                request.ExpectedRequestVersion,
                request.BusinessJustification,
                request.RevisionKey,
                request.Reason,
                (request.Retained ?? []).Select(ToLineRef),
                (request.Changed ?? []).Select(change => new PurchaseRequestLineChange(
                    change.Id,
                    change.ExpectedVersion,
                    change.ExpectedContentDigest,
                    ToContent(change.Content))),
                (request.Added ?? []).Select(line => new PurchaseRequestLineDraft(
                    line.ClientLineKey, ToContent(line.Content))),
                (request.Removed ?? []).Select(ToLineRef)),
            request_.OrganizationId,
            profile.Id,
            Correlation(),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new PurchaseRequestRevisionResponse(
            revision.RequestId,
            revision.Version,
            revision.ContentDigest,
            revision.AddedLines,
            revision.ChangedLines,
            revision.RemovedLines,
            revision.Replayed));
    }

    [HttpPost("{requestId:guid}/cancellation")]
    public async Task<ActionResult<object>> Cancel(
        Guid requestId,
        PurchaseRequestCancellationBody request,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var request_ = await RequireOwnedRequestAsync(requestId, profile, cancellationToken);
        var cancellation = await service.CancelAsync(
            requestId,
            request.ExpectedVersion,
            request.CancelKey,
            request.Reason,
            request_.OrganizationId,
            profile.Id,
            Correlation(),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new
        {
            cancellation.RequestId,
            cancellation.Version,
            Status = PurchaseRequestStatus.Cancelled,
            cancellation.Replayed
        });
    }

    [HttpPost("{requestId:guid}/submission")]
    public async Task<ActionResult<PurchaseRequestSubmissionResponse>> Submit(
        Guid requestId,
        PurchaseRequestSubmissionBody request,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var request_ = await RequireOwnedRequestAsync(requestId, profile, cancellationToken);
        var outcome = await submissionService.SubmitAsync(
            new PurchaseRequestSubmissionCommand(
                requestId,
                request.ExpectedVersion,
                request.SubmissionKey,
                request.Reason,
                request_.OrganizationId,
                profile.Id,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new PurchaseRequestSubmissionResponse(
            outcome.RequestId,
            outcome.Version,
            outcome.Status,
            outcome.AttemptStatus,
            outcome.PolicyEvaluationBundleId,
            outcome.ApprovalCaseId,
            outcome.Replayed));
    }

    [HttpGet("{requestId:guid}")]
    public async Task<ActionResult<PurchaseRequestResponse>> Get(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var view = await service.ReadAsync(requestId, profile.OrganizationId, cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        var isRequester = view.RequesterId == profile.Id;
        if (!isRequester && !await HasOrganizationalAuditAsync(profile, cancellationToken))
        {
            throw new DomainNotFoundException("The purchase request is not visible.");
        }

        // An AUDITOR reads a minimized projection: no summaries, no personal references (REQ-10).
        return Ok(new PurchaseRequestResponse(
            view.RequestId,
            view.OrganizationId,
            view.RequesterId,
            view.Status,
            view.CurrentVersion,
            view.Versions.Select(version => new PurchaseRequestVersionResponse(
                version.Version,
                version.PredecessorVersion,
                version.LegalEntityId,
                version.LegalEntityVersion,
                version.CreatedAt,
                version.Lines.Select(line => new PurchaseRequestLineResponse(
                    line.LineId,
                    line.LineVersion,
                    line.ContentDigest,
                    isRequester ? ToBody(line.Content) : null)).ToArray(),
                isRequester ? version.BusinessJustification : null,
                isRequester ? version.RevisionKey : null,
                isRequester ? version.Reason : null)).ToArray()));
    }

    /// <summary>The incoming snapshot cannot exceed 5 MiB (REQ-11).</summary>
    private void RequireSnapshotSize()
    {
        if (Request.ContentLength is > PurchaseRequestLimits.MaxSnapshotBytes)
        {
            throw new PurchaseRequestPayloadTooLargeException(
                $"The purchase request snapshot exceeds {PurchaseRequestLimits.MaxSnapshotBytes} bytes.");
        }
    }

    private async Task<UserProfileRecord> RequireActiveProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return profile;
    }

    /// <summary>Only the requester of the current version may revise or cancel (REQ-02, REQ-10).</summary>
    private async Task<PurchaseRequestRecord> RequireOwnedRequestAsync(
        Guid requestId,
        UserProfileRecord profile,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == requestId && candidate.OrganizationId == profile.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (record.RequesterId != profile.Id)
        {
            throw new DomainForbiddenException("Only the requester may change this purchase request.");
        }

        return record;
    }

    private Task<bool> HasOrganizationalAuditAsync(UserProfileRecord profile, CancellationToken cancellationToken) =>
        dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson &&
                          assignment.Role == (int)SystemRole.Auditor,
            cancellationToken);

    private string Correlation() => HttpContext.TraceIdentifier;

    private static VersionedEntityRef ToEntityRef(PurchaseRequestEntityRefBody body) =>
        new(body.EntityType, body.Id, body.Version);

    private static VersionedEntityRef? ToOptionalEntityRef(PurchaseRequestEntityRefBody? body) =>
        body is null ? null : ToEntityRef(body);

    private static PurchaseRequestLineRef ToLineRef(PurchaseRequestLineRefBody body) =>
        new(body.Id, body.Version, body.ContentDigest);

    private static PurchaseRequestLineContent ToContent(PurchaseRequestLineContentBody body) =>
        new(
            body.EstimatedGrossAmount,
            body.TransactionCurrency,
            body.BaseAmount,
            body.BaseCurrency,
            body.FiscalYear,
            body.PurchaseType,
            new VersionedCodeRef(
                body.SpendCategoryRef.Catalog,
                body.SpendCategoryRef.Code,
                body.SpendCategoryRef.Version,
                body.SpendCategoryRef.Digest),
            ToEntityRef(body.CostCenterRef),
            ToEntityRef(body.CostCenterDepartmentRef),
            ToEntityRef(body.BeneficiaryDepartmentRef),
            ToEntityRef(body.RequestedForUserRef),
            ToOptionalEntityRef(body.SupplierRef),
            ToOptionalEntityRef(body.PreferredProductRef),
            ToOptionalEntityRef(body.RequiredProductRef),
            body.ContractRequired,
            body.NonStandardTerms,
            body.AgreementStatus,
            body.NeedSummary,
            (body.RiskAnswers ?? []).Select(answer => new TypedAnswerRef(
                answer.QuestionCode,
                answer.SchemaVersion,
                answer.ValueKind switch
                {
                    "BOOLEAN" => TypedAnswerValueKind.Boolean,
                    "ENUM_CODE" => TypedAnswerValueKind.EnumCode,
                    _ => throw new DomainValidationException("The risk answer kind is invalid.")
                },
                answer.Value)),
            body.FxAttestationRef is null
                ? null
                : PurchaseRequestFxRef.Create(
                    body.FxAttestationRef.AttestationId,
                    body.FxAttestationRef.AttestationVersion,
                    body.FxAttestationRef.BaseCurrency,
                    body.FxAttestationRef.TransactionCurrency,
                    body.FxAttestationRef.EffectiveRate,
                    body.FxAttestationRef.RateDate));

    private static PurchaseRequestLineContentBody ToBody(PurchaseRequestLineContent content) =>
        new(
            content.EstimatedGrossAmount,
            content.TransactionCurrency,
            content.BaseAmount,
            content.BaseCurrency,
            content.FiscalYear,
            content.PurchaseType,
            new PurchaseRequestCodeRefBody(
                content.SpendCategoryRef.Catalog,
                content.SpendCategoryRef.Code,
                content.SpendCategoryRef.Version,
                content.SpendCategoryRef.Digest),
            new PurchaseRequestEntityRefBody(
                content.CostCenterRef.EntityType, content.CostCenterRef.Id, content.CostCenterRef.Version),
            new PurchaseRequestEntityRefBody(
                content.CostCenterDepartmentRef.EntityType,
                content.CostCenterDepartmentRef.Id,
                content.CostCenterDepartmentRef.Version),
            new PurchaseRequestEntityRefBody(
                content.BeneficiaryDepartmentRef.EntityType,
                content.BeneficiaryDepartmentRef.Id,
                content.BeneficiaryDepartmentRef.Version),
            new PurchaseRequestEntityRefBody(
                content.RequestedForUserRef.EntityType,
                content.RequestedForUserRef.Id,
                content.RequestedForUserRef.Version),
            content.SupplierRef is null
                ? null
                : new PurchaseRequestEntityRefBody(
                    content.SupplierRef.EntityType, content.SupplierRef.Id, content.SupplierRef.Version),
            content.PreferredProductRef is null
                ? null
                : new PurchaseRequestEntityRefBody(
                    content.PreferredProductRef.EntityType,
                    content.PreferredProductRef.Id,
                    content.PreferredProductRef.Version),
            content.RequiredProductRef is null
                ? null
                : new PurchaseRequestEntityRefBody(
                    content.RequiredProductRef.EntityType,
                    content.RequiredProductRef.Id,
                    content.RequiredProductRef.Version),
            content.ContractRequired,
            content.NonStandardTerms,
            content.AgreementStatus,
            content.NeedSummary,
            content.RiskAnswers
                .Select(answer => new PurchaseRequestAnswerBody(
                    answer.QuestionCode,
                    answer.SchemaVersion,
                    answer.Value,
                    answer.ValueKind == TypedAnswerValueKind.Boolean ? "BOOLEAN" : "ENUM_CODE"))
                .ToArray(),
            content.FxAttestationRef is null
                ? null
                : new PurchaseRequestFxBody(
                    content.FxAttestationRef.AttestationId,
                    content.FxAttestationRef.AttestationVersion,
                    content.FxAttestationRef.BaseCurrency,
                    content.FxAttestationRef.TransactionCurrency,
                    content.FxAttestationRef.EffectiveRate,
                    content.FxAttestationRef.RateDate));
}
