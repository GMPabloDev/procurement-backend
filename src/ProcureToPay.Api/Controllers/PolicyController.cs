using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/policies")]
public sealed class PolicyController(
    PolicyPersistenceService policyService,
    PolicyEvaluationService policyEvaluationService,
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<PolicyVersionResponse>>> GetVersions(
        CancellationToken cancellationToken)
    {
        await EnsurePolicyReadAsync(cancellationToken);
        var organizationId = await GetOrganizationIdAsync(cancellationToken);
        var records = await dbContext.PolicySetVersions
            .AsNoTracking()
            .Where(version => version.OrganizationId == organizationId)
            .OrderByDescending(version => version.Sequence)
            .ToArrayAsync(cancellationToken);
        return Ok(records.Select(ToResponse).ToArray());
    }

    [HttpGet("active")]
    public async Task<ActionResult<PolicyVersionResponse>> GetActive(CancellationToken cancellationToken)
    {
        await EnsurePolicyReadAsync(cancellationToken);
        var organizationId = await GetOrganizationIdAsync(cancellationToken);
        var active = await policyService.FindActiveAsync(organizationId, DateTimeOffset.UtcNow, cancellationToken)
            ?? throw new PolicyConfigurationUnavailableException("No active policy is available.");
        return Ok(ToResponse(active.PolicySetVersion));
    }

    [HttpPost("drafts")]
    public async Task<ActionResult<PolicyVersionResponse>> CreateDraft(
        PolicyDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentJson.Length > 10 * 1024 * 1024)
        {
            throw new BadHttpRequestException("Policy content exceeds the 10 MiB limit.", StatusCodes.Status413PayloadTooLarge);
        }
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var organizationId = actor.OrganizationId;
        var draft = await policyService.AppendDraftAsync(
            new PolicyDraftDocument(organizationId, request.ScopesJson, request.ContentJson, request.ContentDigest),
            DateTimeOffset.UtcNow,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Created($"/api/v1/policies/{draft.Id}", ToResponse(draft));
    }

    [HttpPut("drafts/{draftId:guid}")]
    public async Task<ActionResult<PolicyVersionResponse>> UpdateDraft(
        Guid draftId,
        PolicyDraftUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentJson.Length > 10 * 1024 * 1024)
        {
            throw new BadHttpRequestException("Policy content exceeds the 10 MiB limit.", StatusCodes.Status413PayloadTooLarge);
        }
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var draft = await policyService.UpdateDraftAsync(
            draftId,
            new PolicyDraftDocument(actor.OrganizationId, request.ScopesJson, request.ContentJson, request.ContentDigest),
            request.ExpectedContentDigest,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(draft));
    }

    [HttpPost("{draftId:guid}/publish")]
    public async Task<ActionResult<PolicyVersionResponse>> Publish(
        Guid draftId,
        PolicyActionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        // pi-lens-ignore: CS1061, CS1501
        var publishedAndActivated = await policyService.PublishAndActivateAsync(
            draftId,
            request.EffectiveFrom,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken,
            actor.OrganizationId);
        return Ok(ToResponse(publishedAndActivated.Version));
    }

    [HttpDelete("activations/{activationId:guid}")]
    public async Task<IActionResult> Retire(
        Guid activationId,
        PolicyRetirementRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        // pi-lens-ignore: CS1501
        await policyService.RetireAsync(
            activationId,
            request.EffectiveTo ?? DateTimeOffset.UtcNow.AddSeconds(1),
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken,
            actor.OrganizationId);
        return NoContent();
    }

    [HttpPost("evaluate")]
    public async Task<ActionResult<PolicyEvaluationBundle>> Evaluate(
        PolicyEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var issuer = User.FindFirst("iss")?.Value ?? "internal://procure-to-pay";
        var clientId = User.FindFirst("client_id")?.Value ?? User.FindFirst("azp")?.Value;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new DomainForbiddenException("A workload client identity is required.");
        }
        // pi-lens-ignore: CS1729
        var factRequest = new PolicyFactRequest(
            request.SubjectType,
            request.SubjectId,
            request.SubjectVersion,
            request.Operation,
            request.RequestedAtUtc ?? DateTimeOffset.UtcNow,
            new PolicyWorkloadPrincipal(issuer, clientId),
            HttpContext.TraceIdentifier)
        {
            // pi-lens-ignore: CS0117
            OrganizationId = await GetOrganizationIdAsync(cancellationToken),
            PreviousBundleId = request.PreviousBundleId,
            Cause = request.Cause,
            PreviousResultDigest = request.PreviousResultDigest
        };
        return Ok(await policyEvaluationService.EvaluateEnterprisePurchaseRequestAsync(
            factRequest, request.EvaluationKey, cancellationToken));
    }

    [HttpPost("evaluations/{evaluationId:guid}/quotation-waiver")]
    public async Task<ActionResult<PolicyEvaluationBundle>> ApplyQuotationWaiver(
        Guid evaluationId,
        PolicyQuotationWaiverRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (actor.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }
        if (request.OriginatorId != actor.Id)
        {
            throw new DomainForbiddenException("The waiver originator must be the authenticated user.");
        }
        if (!TryParseExceptionType(request.Type, out var type))
        {
            throw new DomainValidationException("The quotation waiver type is invalid.");
        }
        var waiver = new QuotationWaiverRequest(
            type,
            request.From,
            request.To,
            request.Floor,
            request.PolicyDigest,
            request.EvaluationDigest,
            request.Binding,
            request.Nonce,
            request.EvidenceDigest,
            request.ApproverRole,
            request.AuthorityType,
            request.ApproverId,
            request.WorkloadSubjectId,
            request.OriginatorId)
        {
            TargetRequirementKey = request.TargetRequirementKey
        };
        return Ok(await policyEvaluationService.ApplyQuotationWaiverAsync(
            await LoadEvaluationBundleAsync(evaluationId, actor.OrganizationId, cancellationToken),
            waiver,
            cancellationToken));
    }

    [HttpGet("evaluations/{evaluationId:guid}")]
    public async Task<ActionResult<PolicyEvaluationResponse>> GetEvaluation(
        Guid evaluationId,
        CancellationToken cancellationToken)
    {
        await EnsurePolicyReadAsync(cancellationToken);
        var organizationId = await GetOrganizationIdAsync(cancellationToken);
        var evaluation = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == evaluationId && item.OrganizationId == organizationId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy evaluation is not visible.");
        return Ok(new PolicyEvaluationResponse(
            evaluation.Id, evaluation.EvaluationKey, evaluation.Operation,
            evaluation.SubjectId, evaluation.SubjectVersion, evaluation.Result,
            evaluation.PolicyContentDigest, evaluation.InputDigest, evaluation.ResultDigest,
            evaluation.BundleJson, evaluation.EvaluatedAt));
    }

    [HttpPost("simulate")]
    public async Task<ActionResult<PolicySimulationResponse>> Simulate(
        PolicySimulationRequest request,
        CancellationToken cancellationToken)
    {
        var simulationActor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        if (request.ContentJson.Length > 10 * 1024 * 1024 ||
            (request.InputSnapshot is not null && request.InputSnapshot.Value.GetRawText().Length > 5 * 1024 * 1024))
        {
            throw new BadHttpRequestException("Simulation payload exceeds the configured limit.", StatusCodes.Status413PayloadTooLarge);
        }
        if (string.IsNullOrWhiteSpace(request.ContentJson) ||
            !string.Equals(PolicyCanonicalizer.Hash(request.ContentJson), request.ContentDigest,
                StringComparison.OrdinalIgnoreCase) ||
            request.InputSnapshot is null)
        {
            throw new DomainValidationException("Simulation requires a canonical policy digest and input snapshot.");
        }

        using var policyDocument = JsonDocument.Parse(request.ContentJson);
        var policyId = request.PolicyId ?? Guid.NewGuid();
        if (policyDocument.RootElement.GetProperty("rules").GetArrayLength() > 2000)
        {
            throw new BadHttpRequestException("Policies support at most 2,000 rules.", StatusCodes.Status413PayloadTooLarge);
        }
        var organizationId = policyDocument.RootElement.GetProperty("organization_id").GetGuid();
        if (organizationId != simulationActor.OrganizationId)
        {
            throw new DomainForbiddenException("The simulation policy is outside the actor organization.");
        }
        var policy = PolicyDocumentParser.Parse(
            request.ContentJson,
            policyId,
            organizationId,
            1,
            PolicySetStatus.Published,
            request.ContentDigest);
        if (!string.Equals(PolicyCanonicalizer.ComputePolicyDigest(policy), request.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("Simulation content is not canonical for the typed policy model.");
        }
        var input = ParseSimulationInput(request.InputSnapshot.Value);
        if (input.OrganizationId != organizationId)
        {
            throw new DomainConflictException("Simulation policy and input organizations differ.");
        }
        var evaluatedAt = request.EvaluatedAt ?? DateTimeOffset.UtcNow;
        var bundle = PolicyEvaluator.EvaluateRequest(policy, input, request.EvaluationKey, evaluatedAt);
        dbContext.AdministrativeAuditRecords.Add(new AdministrativeAuditRecord
        {
            Id = Guid.NewGuid(),
            ActorType = "USER",
            ActorUserId = simulationActor.Id,
            OccurredAt = DateTimeOffset.UtcNow,
            Action = "POLICY_SIMULATED",
            TargetType = "POLICY",
            TargetId = policyId,
            ScopeJson = "[\"POLICY\"]",
            AfterJson = JsonSerializer.Serialize(new { request.EvaluationKey, inputHash = PolicyCanonicalizer.Hash(request.InputSnapshot.Value.GetRawText()) }),
            Reason = "Policy simulation",
            CorrelationReference = HttpContext.TraceIdentifier
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new PolicySimulationResponse(
            policyId, request.EvaluationKey, false, bundle.Result, bundle.ResultDigest,
            "Simulation is non-enterprise and does not persist an evaluation bundle."));
    }

    private static PolicyRequestInput ParseSimulationInput(JsonElement root)
    {
        static PolicySubjectReference ParseSubject(JsonElement value) =>
            new(value.GetProperty("id").GetGuid(), value.GetProperty("version").GetInt32());

        static PolicyValue ParseValue(JsonElement value) => PolicyDocumentParser.ParseValue(value);

        static IReadOnlyDictionary<string, PolicyValue> ParseFacts(JsonElement facts) =>
            facts.EnumerateObject().ToDictionary(property => property.Name, property => ParseValue(property.Value), StringComparer.Ordinal);

        var lineElements = root.GetProperty("lines").EnumerateArray().ToArray();
        if (lineElements.Length > 500)
        {
            throw new BadHttpRequestException("Simulation requests support at most 500 lines.", StatusCodes.Status413PayloadTooLarge);
        }
        var lines = lineElements
            .Select(line => new PolicyLineInput(
                ParseSubject(line.GetProperty("subject")),
                ParseFacts(line.GetProperty("facts"))))
            .ToArray();
        return new PolicyRequestInput(
            ParseSubject(root.GetProperty("subject")),
            root.GetProperty("organization_id").GetGuid(),
            root.GetProperty("legal_entity_id").GetGuid(),
            root.GetProperty("base_currency").GetString()!,
            lines,
            root.TryGetProperty("facts", out var facts) ? ParseFacts(facts) : null);
    }

    private async Task<PolicyEvaluationBundle> LoadEvaluationBundleAsync(
        Guid evaluationId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var evaluation = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == evaluationId && item.OrganizationId == organizationId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy evaluation is not visible.");
        return PolicyEvaluationBundleRehydrator.FromJson(evaluation.BundleJson);
    }

    private async Task EnsurePolicyReadAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        var allowed = await dbContext.RoleAssignments.AnyAsync(assignment =>
            assignment.UserProfileId == profile.Id &&
            assignment.Status == (int)AssignmentStatus.Active &&
            assignment.ScopeJson == "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]" &&
            (assignment.Role == (int)SystemRole.Admin || assignment.Role == (int)SystemRole.Auditor),
            cancellationToken);
        if (!allowed)
        {
            throw new DomainForbiddenException("Policy read access requires ADMIN or AUDITOR.");
        }
    }

    private async Task<Guid> GetOrganizationIdAsync(CancellationToken cancellationToken) =>
        (await provisioningService.EnsureProfileAsync(User, cancellationToken)).OrganizationId;

    private static bool TryParseExceptionType(string? value, out PolicyExceptionType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (Enum.TryParse(value, ignoreCase: true, out type))
        {
            return true;
        }
        // Accept the documented uppercase contract code as well as the enum name.
        var pascal = string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        return Enum.TryParse(pascal, ignoreCase: false, out type);
    }

    private static PolicyVersionResponse ToResponse(PolicySetVersionRecord record) => new(
        record.Id, record.OrganizationId, record.Sequence,
        ((PolicySetStatus)record.Status).ToString().ToUpperInvariant(),
        record.ScopesJson, record.ContentDigest, record.CreatedAt);
}

public sealed record PolicyEvaluationRequest(
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    string EvaluationKey,
    DateTimeOffset? RequestedAtUtc = null,
    Guid? PreviousBundleId = null,
    string? Cause = null,
    string? PreviousResultDigest = null);

public sealed record PolicyDraftRequest(
    string ScopesJson,
    string ContentJson,
    string ContentDigest,
    string Reason);

public sealed record PolicyDraftUpdateRequest(
    string ScopesJson,
    string ContentJson,
    string ContentDigest,
    string ExpectedContentDigest,
    string Reason);

public sealed record PolicyQuotationWaiverRequest(
    string Type,
    int From,
    int To,
    int Floor,
    string PolicyDigest,
    string EvaluationDigest,
    string Binding,
    string Nonce,
    string EvidenceDigest,
    string ApproverRole,
    string AuthorityType,
    Guid ApproverId,
    Guid WorkloadSubjectId,
    Guid OriginatorId,
    string TargetRequirementKey);

public sealed record PolicyActionRequest(string Reason, DateTimeOffset EffectiveFrom);


public sealed record PolicyRetirementRequest(DateTimeOffset? EffectiveTo, string Reason);

public sealed record PolicySimulationRequest(
    string EvaluationKey,
    string ContentJson,
    string ContentDigest,
    Guid? PolicyId,
    DateTimeOffset? EvaluatedAt,
    JsonElement? InputSnapshot = null);

public sealed record PolicyVersionResponse(
    Guid Id,
    Guid OrganizationId,
    long Sequence,
    string Status,
    string ScopesJson,
    string ContentDigest,
    DateTimeOffset CreatedAt);

public sealed record PolicyActivationResponse(Guid Id, Guid PolicySetVersionId, DateTimeOffset EffectiveFrom);

public sealed record PolicyEvaluationResponse(
    Guid Id,
    string EvaluationKey,
    string Operation,
    Guid SubjectId,
    int SubjectVersion,
    string Result,
    string PolicyContentDigest,
    string InputDigest,
    string ResultDigest,
    string BundleJson,
    DateTimeOffset EvaluatedAt);

public sealed record PolicySimulationResponse(
    Guid PolicySetVersionId,
    string EvaluationKey,
    bool Persisted,
    PolicyResult Result,
    string ResultDigest,
    string Message);
