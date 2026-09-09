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
        var records = await dbContext.PolicySetVersions
            .AsNoTracking()
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
            ?? throw new DomainConflictException("No active policy is available.");
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

    [HttpPost("simulate")]
    public async Task<ActionResult<PolicySimulationResponse>> Simulate(
        PolicySimulationRequest request,
        CancellationToken cancellationToken)
    {
        _ = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.ContentJson) ||
            !string.Equals(PolicyCanonicalizer.Hash(request.ContentJson), request.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("Simulation requires a canonical content digest.");
        }

        return Ok(new PolicySimulationResponse(
            request.PolicyId ?? Guid.Empty, request.EvaluationKey, false,
            "Simulation is non-enterprise and does not persist an evaluation bundle."));
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
    DateTimeOffset? EvaluatedAt);

public sealed record PolicyVersionResponse(
    Guid Id,
    Guid OrganizationId,
    long Sequence,
    string Status,
    string ScopesJson,
    string ContentDigest,
    DateTimeOffset CreatedAt);

public sealed record PolicyActivationResponse(Guid Id, Guid PolicySetVersionId, DateTimeOffset EffectiveFrom);

public sealed record PolicySimulationResponse(
    Guid PolicySetVersionId,
    string EvaluationKey,
    bool Persisted,
    string Message);
