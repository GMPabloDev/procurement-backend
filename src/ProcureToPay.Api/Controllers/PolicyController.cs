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
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var organizationId = await GetOrganizationIdAsync(cancellationToken);
        var draft = await policyService.AppendDraftAsync(
            new PolicyDraftDocument(organizationId, request.ScopesJson, request.ContentJson, request.ContentDigest),
            DateTimeOffset.UtcNow,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Created($"/api/v1/policies/{draft.Id}", ToResponse(draft));
    }

    [HttpPost("{draftId:guid}/publish")]
    public async Task<ActionResult<PolicyVersionResponse>> Publish(
        Guid draftId,
        PolicyActionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        // pi-lens-ignore: CS1061
        var publishedAndActivated = await policyService.PublishAndActivateAsync(
            draftId,
            request.EffectiveFrom,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(publishedAndActivated.Version));
    }

    [HttpDelete("activations/{activationId:guid}")]
    public async Task<IActionResult> Retire(
        Guid activationId,
        PolicyRetirementRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        await policyService.RetireAsync(
            activationId,
            request.EffectiveTo ?? DateTimeOffset.UtcNow,
            new PolicyActor("USER", actor.Id),
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return NoContent();
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
        _ = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ContentJson) ||
            !string.Equals(PolicyCanonicalizer.Hash(request.ContentJson), request.ContentDigest,
                StringComparison.OrdinalIgnoreCase) ||
            request.InputSnapshot is null)
        {
            throw new DomainValidationException("Simulation requires a canonical policy digest and input snapshot.");
        }

        using var policyDocument = JsonDocument.Parse(request.ContentJson);
        var policyId = request.PolicyId ?? Guid.NewGuid();
        var organizationId = policyDocument.RootElement.GetProperty("organization_id").GetGuid();
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
        return Ok(new PolicySimulationResponse(
            policyId, request.EvaluationKey, false, bundle.Result, bundle.ResultDigest,
            "Simulation is non-enterprise and does not persist an evaluation bundle."));
    }

    private static PolicyRequestInput ParseSimulationInput(JsonElement root)
    {
        static PolicySubjectReference ParseSubject(JsonElement value) =>
            new(value.GetProperty("id").GetGuid(), value.GetProperty("version").GetInt32());

        static PolicyValue ParseValue(JsonElement value)
        {
            var kind = value.GetProperty("kind").GetString()!;
            var enumName = string.Concat(kind.Split('_').Select(part =>
                char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
            var parsedKind = Enum.Parse<PolicyValueKind>(enumName, true);
            return parsedKind switch
            {
                PolicyValueKind.Money => PolicyValue.Money(decimal.Parse(value.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                PolicyValueKind.Boolean => PolicyValue.Boolean(bool.Parse(value.GetProperty("value").GetString()!)),
                PolicyValueKind.Code => PolicyValue.Code(value.GetProperty("value").GetString()!),
                PolicyValueKind.Reference => PolicyValue.Reference(
                    value.GetProperty("reference_type").GetString()!,
                    Guid.Parse(value.GetProperty("value").GetString()!.Split(':')[0]),
                    int.Parse(value.GetProperty("value").GetString()!.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)),
                PolicyValueKind.Set => PolicyValue.Set(value.GetProperty("members").EnumerateArray().Select(ParseValue).ToArray()),
                PolicyValueKind.MoneyRange => PolicyValue.MoneyRange(
                    decimal.Parse(value.GetProperty("lower_bound").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    decimal.Parse(value.GetProperty("upper_bound").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    value.GetProperty("lower_inclusive").GetBoolean(),
                    value.GetProperty("upper_inclusive").GetBoolean()),
                _ => throw new DomainValidationException("Unsupported simulation policy value.")
            };
        }

        static IReadOnlyDictionary<string, PolicyValue> ParseFacts(JsonElement facts) =>
            facts.EnumerateObject().ToDictionary(property => property.Name, property => ParseValue(property.Value), StringComparer.Ordinal);

        var lines = root.GetProperty("lines").EnumerateArray()
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
        await dbContext.Organizations.Select(organization => organization.Id).SingleAsync(cancellationToken);

    private static PolicyVersionResponse ToResponse(PolicySetVersionRecord record) => new(
        record.Id, record.OrganizationId, record.Sequence,
        ((PolicySetStatus)record.Status).ToString().ToUpperInvariant(),
        record.ScopesJson, record.ContentDigest, record.CreatedAt);
}

public sealed record PolicyDraftRequest(
    string ScopesJson,
    string ContentJson,
    string ContentDigest,
    string Reason);

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
