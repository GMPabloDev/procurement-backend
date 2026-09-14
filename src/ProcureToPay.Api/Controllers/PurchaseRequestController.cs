using System.Globalization;
using System.Text.Json.Serialization;
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestEntityRefBody(
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("version")] int Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestCodeRefBody(
    [property: JsonPropertyName("catalog")] string Catalog,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("digest")] string Digest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestAnswerBody(
    [property: JsonPropertyName("question_code")] string QuestionCode,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("value_kind")] string ValueKind);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestFxBody(
    [property: JsonPropertyName("attestation_id")] Guid AttestationId,
    [property: JsonPropertyName("attestation_version")] int AttestationVersion,
    [property: JsonPropertyName("base_currency")] string BaseCurrency,
    [property: JsonPropertyName("transaction_currency")] string TransactionCurrency,
    [property: JsonPropertyName("effective_rate")] string EffectiveRate,
    [property: JsonPropertyName("rate_date")] string RateDate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestLineContentBody(
    [property: JsonPropertyName("base_amount")] string BaseAmount,
    [property: JsonPropertyName("base_currency")] string BaseCurrency,
    [property: JsonPropertyName("beneficiary_department_ref")] PurchaseRequestEntityRefBody BeneficiaryDepartmentRef,
    [property: JsonPropertyName("contract_required")] bool ContractRequired,
    [property: JsonPropertyName("cost_center_department_ref")] PurchaseRequestEntityRefBody CostCenterDepartmentRef,
    [property: JsonPropertyName("cost_center_ref")] PurchaseRequestEntityRefBody CostCenterRef,
    [property: JsonPropertyName("estimated_gross_amount")] string EstimatedGrossAmount,
    [property: JsonPropertyName("fiscal_year")] int FiscalYear,
    [property: JsonPropertyName("fx_attestation_ref")] PurchaseRequestFxBody? FxAttestationRef,
    [property: JsonPropertyName("need_summary")] string NeedSummary,
    [property: JsonPropertyName("non_standard_terms")] bool NonStandardTerms,
    [property: JsonPropertyName("preferred_product_ref")] PurchaseRequestEntityRefBody? PreferredProductRef,
    [property: JsonPropertyName("purchase_type")] string PurchaseType,
    [property: JsonPropertyName("requested_for_user_ref")] PurchaseRequestEntityRefBody RequestedForUserRef,
    [property: JsonPropertyName("required_product_ref")] PurchaseRequestEntityRefBody? RequiredProductRef,
    [property: JsonPropertyName("risk_answers")] PurchaseRequestAnswerBody[]? RiskAnswers,
    [property: JsonPropertyName("spend_category_ref")] PurchaseRequestCodeRefBody SpendCategoryRef,
    [property: JsonPropertyName("supplier_ref")] PurchaseRequestEntityRefBody? SupplierRef,
    [property: JsonPropertyName("transaction_currency")] string TransactionCurrency);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestLineDraftBody(
    [property: JsonPropertyName("client_line_key")] string ClientLineKey,
    [property: JsonPropertyName("content")] PurchaseRequestLineContentBody Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestLineRefBody(
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("version")] int Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestLineChangeBody(
    [property: JsonPropertyName("content")] PurchaseRequestLineContentBody Content,
    [property: JsonPropertyName("expected_content_digest")] string ExpectedContentDigest,
    [property: JsonPropertyName("expected_version")] int ExpectedVersion,
    [property: JsonPropertyName("id")] Guid Id);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestCreateBody(
    [property: JsonPropertyName("business_justification")] string BusinessJustification,
    [property: JsonPropertyName("command_version")] string CommandVersion,
    [property: JsonPropertyName("legal_entity_ref")] PurchaseRequestEntityRefBody LegalEntityRef,
    [property: JsonPropertyName("line_drafts")] PurchaseRequestLineDraftBody[]? LineDrafts,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("revision_key")] string RevisionKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestRevisionBody(
    [property: JsonPropertyName("added")] PurchaseRequestLineDraftBody[]? Added,
    [property: JsonPropertyName("business_justification")] string BusinessJustification,
    [property: JsonPropertyName("changed")] PurchaseRequestLineChangeBody[]? Changed,
    [property: JsonPropertyName("command_version")] string CommandVersion,
    [property: JsonPropertyName("expected_request_version")] int ExpectedRequestVersion,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("removed")] PurchaseRequestLineRefBody[]? Removed,
    [property: JsonPropertyName("retained")] PurchaseRequestLineRefBody[]? Retained,
    [property: JsonPropertyName("revision_key")] string RevisionKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestCancellationBody(
    [property: JsonPropertyName("cancel_key")] string CancelKey,
    [property: JsonPropertyName("expected_version")] int ExpectedVersion,
    [property: JsonPropertyName("reason")] string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequestSubmissionBody(
    [property: JsonPropertyName("expected_version")] int ExpectedVersion,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("submission_key")] string SubmissionKey);

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

public sealed record PurchaseRequestSubmissionResponse(
    Guid RequestId,
    int Version,
    PurchaseRequestStatus Status,
    PurchaseRequestSubmissionStatus AttemptStatus,
    Guid? PolicyEvaluationBundleId,
    Guid? ApprovalCaseId,
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

/// <summary>Minimized owner reference of one persisted assertion (REQ-10).</summary>
public sealed record PurchaseRequestAssertionRefResponse(
    string? Code,
    string? Digest,
    Guid? Id,
    string Kind,
    string Type,
    int? Version);

public sealed record PurchaseRequestAssertionResponse(
    string AssertionType,
    string OwnerId,
    string OwnerContractVersion,
    PurchaseRequestAssertionRefResponse SourceRef,
    PurchaseRequestAssertionRefResponse? TargetRef,
    string Status);

public sealed record PurchaseRequestAttestationResponse(
    int Version,
    DateTimeOffset AttestedAt,
    IReadOnlyList<PurchaseRequestAssertionResponse> Assertions);

public sealed record PurchaseRequestResponse(
    Guid RequestId,
    Guid OrganizationId,
    Guid RequesterId,
    PurchaseRequestStatus Status,
    int CurrentVersion,
    IReadOnlyList<PurchaseRequestVersionResponse> Versions,
    IReadOnlyList<PurchaseRequestAttestationResponse> Attestations);

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
        RequireCommandVersion(request.CommandVersion, PurchaseRequestCodes.CreateCommandVersion);
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var creation = await service.CreateAsync(
            new PurchaseRequestCreateCommand(
                profile.OrganizationId,
                profile.Id,
                ToEntityRef(request.LegalEntityRef),
                request.BusinessJustification,
                request.RevisionKey,
                request.Reason,
                (request.LineDrafts ?? []).Select(line => new PurchaseRequestLineDraft(
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
        RequireCommandVersion(request.CommandVersion, PurchaseRequestCodes.RevisionCommandVersion);
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
        var cancellation = await submissionService.CancelAsync(
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

        // The requester reads the full snapshot; an organizational AUDITOR reads the same history
        // without content or summaries plus the owner attestations and their minimized references
        // (REQ-10). Neither reads another organization's request.
        var attestations = await service.ReadAttestationsAsync(
            requestId, profile.OrganizationId, cancellationToken);
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
                isRequester ? version.Reason : null)).ToArray(),
            attestations.Select(ToAttestation).ToArray()));
    }

    /// <summary>The payload declares its exact contract version (REQ-11, Datos y contratos).</summary>
    private static void RequireCommandVersion(string? declared, string expected)
    {
        if (!string.Equals(declared, expected, StringComparison.Ordinal))
        {
            throw new DomainValidationException($"The payload must declare command_version '{expected}'.");
        }
    }

    private static string FormatAmount(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static PurchaseRequestAttestationResponse ToAttestation(PurchaseRequestAttestationView view) =>
        new(
            view.Version,
            view.AttestedAt,
            view.Assertions.Select(assertion => new PurchaseRequestAssertionResponse(
                PurchaseRequestCodes.Code(assertion.AssertionType),
                assertion.OwnerId,
                assertion.OwnerContractVersion,
                ToAssertionRef(assertion.SourceRef),
                assertion.TargetRef is null ? null : ToAssertionRef(assertion.TargetRef),
                assertion.Status)).ToArray());

    private static PurchaseRequestAssertionRefResponse ToAssertionRef(PurchaseRequestAttestedRef reference) =>
        new(
            reference.Code,
            reference.Digest,
            reference.Id,
            reference.KindCode,
            PurchaseRequestCodes.Code(reference.Type),
            reference.Version);

    private static decimal Amount(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            throw new DomainValidationException($"'{field}' must be a decimal string.");
        }

        return amount;
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
            Amount(body.EstimatedGrossAmount, "estimated_gross_amount"),
            body.TransactionCurrency,
            Amount(body.BaseAmount, "base_amount"),
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
            // purchase-request-line-content/v1 carries no agreement status; the closed catalog
            // projects its non-informative value instead of accepting caller input.
            "NONE",
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
                    Amount(body.FxAttestationRef.EffectiveRate, "effective_rate"),
                    DateOnly.Parse(body.FxAttestationRef.RateDate, CultureInfo.InvariantCulture)));

    private static PurchaseRequestLineContentBody ToBody(PurchaseRequestLineContent content) =>
        new(
            FormatAmount(content.BaseAmount),
            content.BaseCurrency,
            ToEntityRefBody(content.BeneficiaryDepartmentRef),
            content.ContractRequired,
            ToEntityRefBody(content.CostCenterDepartmentRef),
            ToEntityRefBody(content.CostCenterRef),
            FormatAmount(content.EstimatedGrossAmount),
            content.FiscalYear,
            content.FxAttestationRef is null
                ? null
                : new PurchaseRequestFxBody(
                    content.FxAttestationRef.AttestationId,
                    content.FxAttestationRef.AttestationVersion,
                    content.FxAttestationRef.BaseCurrency,
                    content.FxAttestationRef.TransactionCurrency,
                    FormatAmount(content.FxAttestationRef.EffectiveRate),
                    content.FxAttestationRef.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            content.NeedSummary,
            content.NonStandardTerms,
            content.PreferredProductRef is null ? null : ToEntityRefBody(content.PreferredProductRef),
            content.PurchaseType,
            ToEntityRefBody(content.RequestedForUserRef),
            content.RequiredProductRef is null ? null : ToEntityRefBody(content.RequiredProductRef),
            content.RiskAnswers
                .Select(answer => new PurchaseRequestAnswerBody(
                    answer.QuestionCode,
                    answer.SchemaVersion,
                    answer.Value,
                    answer.ValueKind == TypedAnswerValueKind.Boolean ? "BOOLEAN" : "ENUM_CODE"))
                .ToArray(),
            new PurchaseRequestCodeRefBody(
                content.SpendCategoryRef.Catalog,
                content.SpendCategoryRef.Code,
                content.SpendCategoryRef.Version,
                content.SpendCategoryRef.Digest),
            content.SupplierRef is null ? null : ToEntityRefBody(content.SupplierRef),
            content.TransactionCurrency);

    private static PurchaseRequestEntityRefBody ToEntityRefBody(VersionedEntityRef reference) =>
        new(reference.EntityType, reference.Id, reference.Version);

}
