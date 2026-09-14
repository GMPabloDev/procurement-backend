using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Reference catalogs of Cost Centers and Spend Categories (SPEC 07 REQ-08): ADMIN mutates with
/// reason and audit, any active user reads the minimal current projections needed to build a
/// Purchase Request, and AUDITOR reads history inside an organization or cost-center assignment.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/reference-catalogs")]
public sealed class ReferenceCatalogController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    ReferenceCatalogPersistenceService catalogs) : ControllerBase
{
    [HttpPost("cost-centers")]
    public async Task<ActionResult<CostCenterReferenceResponse>> CreateCostCenter(
        CreateCostCenterRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAdminAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var reference = await catalogs.CreateCostCenterAsync(
            organizationId,
            actor.Id,
            request.Code,
            request.Name,
            request.DepartmentId,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Created(
            $"/api/v1/reference-catalogs/cost-centers/{reference.Id}",
            ToResponse(reference));
    }

    [HttpPatch("cost-centers/{costCenterId:guid}")]
    public async Task<ActionResult<CostCenterReferenceResponse>> UpdateCostCenter(
        Guid costCenterId,
        UpdateCostCenterRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAdminAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var status = ParseStatus(request.Status);
        var reference = await catalogs.UpdateCostCenterAsync(
            organizationId,
            actor.Id,
            costCenterId,
            request.ExpectedVersion,
            request.Name,
            request.DepartmentId,
            status,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(reference));
    }

    /// <summary>Minimal current projections any active user needs to build a Purchase Request.</summary>
    [HttpGet("cost-centers")]
    public async Task<ActionResult<IReadOnlyCollection<CostCenterReferenceResponse>>> GetCostCenters(
        CancellationToken cancellationToken)
    {
        _ = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var references = await catalogs.ListActiveCostCentersAsync(organizationId, cancellationToken);
        return Ok(references.Select(ToResponse).ToArray());
    }

    [HttpGet("cost-centers/{costCenterId:guid}/history")]
    public async Task<ActionResult<IReadOnlyCollection<CostCenterVersionResponse>>> GetCostCenterHistory(
        Guid costCenterId,
        CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var code = await dbContext.CostCenters
            .AsNoTracking()
            .Where(record => record.Id == costCenterId && record.OrganizationId == organizationId)
            .Select(record => record.Code)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The cost center was not found.");
        await RequireHistoryReaderAsync(
            AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.CostCenter, code)]),
            cancellationToken);
        var history = await catalogs.ReadCostCenterHistoryAsync(organizationId, costCenterId, cancellationToken);
        return Ok(history.Select(version => new CostCenterVersionResponse(
            version.Version,
            version.Name,
            version.DepartmentId,
            version.Status,
            version.PredecessorVersion,
            version.OccurredAt,
            version.ActorUserId,
            version.Reason)).ToArray());
    }

    [HttpPost("spend-categories")]
    public async Task<ActionResult<SpendCategoryReferenceResponse>> CreateSpendCategory(
        CreateSpendCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAdminAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var reference = await catalogs.CreateSpendCategoryAsync(
            organizationId,
            actor.Id,
            request.Code,
            request.Name,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Created(
            $"/api/v1/reference-catalogs/spend-categories/{reference.Code}",
            ToResponse(reference));
    }

    [HttpPatch("spend-categories/{code}")]
    public async Task<ActionResult<SpendCategoryReferenceResponse>> UpdateSpendCategory(
        string code,
        UpdateSpendCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAdminAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var status = ParseStatus(request.Status);
        var reference = await catalogs.UpdateSpendCategoryAsync(
            organizationId,
            actor.Id,
            code,
            request.ExpectedVersion,
            request.Name,
            status,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(reference));
    }

    [HttpGet("spend-categories")]
    public async Task<ActionResult<IReadOnlyCollection<SpendCategoryReferenceResponse>>> GetSpendCategories(
        CancellationToken cancellationToken)
    {
        _ = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var references = await catalogs.ListActiveSpendCategoriesAsync(organizationId, cancellationToken);
        return Ok(references.Select(ToResponse).ToArray());
    }

    [HttpGet("spend-categories/{code}/history")]
    public async Task<ActionResult<IReadOnlyCollection<SpendCategoryVersionResponse>>> GetSpendCategoryHistory(
        string code,
        CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(cancellationToken);
        // A spend category is organization configuration: only an organization-scoped reader sees it.
        await RequireHistoryReaderAsync(
            AuthorizationScopeSet.Create([AuthorizationScope.Global()]),
            cancellationToken);
        var history = await catalogs.ReadSpendCategoryHistoryAsync(organizationId, code, cancellationToken);
        return Ok(history.Select(version => new SpendCategoryVersionResponse(
            version.Version,
            version.Code,
            version.Name,
            version.Digest,
            version.Status,
            version.PredecessorVersion,
            version.OccurredAt,
            version.ActorUserId,
            version.Reason)).ToArray());
    }

    private async Task<UserProfileRecord> RequireAdminAsync(CancellationToken cancellationToken) =>
        await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);

    private async Task<UserProfileRecord> RequireActiveUserAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return profile;
    }

    private async Task RequireHistoryReaderAsync(
        AuthorizationScopeSet requiredScope,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveUserAsync(cancellationToken);
        var assignments = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.UserProfileId == profile.Id &&
                                 assignment.Status == (int)AssignmentStatus.Active &&
                                 (assignment.Role == (int)SystemRole.Admin ||
                                  assignment.Role == (int)SystemRole.Auditor))
            .Select(assignment => new { assignment.Role, assignment.ScopeJson })
            .ToArrayAsync(cancellationToken);
        var allowed = assignments.Any(assignment =>
        {
            try
            {
                return OrganizationEligibilityService.ParseScope(assignment.ScopeJson).Covers(requiredScope);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        if (!allowed)
        {
            throw new DomainForbiddenException("The requested reference catalog is outside the reader scope.");
        }
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);

    private static EntityStatus? ParseStatus(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!Enum.TryParse<EntityStatus>(value, true, out var status) ||
            !Enum.IsDefined(status) ||
            value.Any(char.IsDigit))
        {
            throw new DomainValidationException("Reference catalog status is not recognized.");
        }

        return status;
    }

    private static CostCenterReferenceResponse ToResponse(CostCenterReference reference) =>
        new(
            reference.Id,
            reference.Code,
            reference.Name,
            reference.Version,
            new CostCenterDepartmentRefResponse(reference.DepartmentId, reference.DepartmentVersion));

    private static SpendCategoryReferenceResponse ToResponse(SpendCategoryReference reference) =>
        new(
            ReferenceCatalogCodes.SpendCategoryCatalog,
            reference.Code,
            reference.Digest,
            reference.Name,
            reference.Version);
}

public sealed record CostCenterDepartmentRefResponse(Guid Id, int Version);

public sealed record CostCenterReferenceResponse(
    Guid Id,
    string Code,
    string Name,
    int Version,
    [property: JsonPropertyName("department_ref")] CostCenterDepartmentRefResponse DepartmentRef);

public sealed record SpendCategoryReferenceResponse(
    string Catalog,
    string Code,
    string Digest,
    string Name,
    int Version);

public sealed record CostCenterVersionResponse(
    int Version,
    string Name,
    Guid DepartmentId,
    string Status,
    int? PredecessorVersion,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Reason);

public sealed record SpendCategoryVersionResponse(
    int Version,
    string Code,
    string Name,
    string Digest,
    string Status,
    int? PredecessorVersion,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Reason);

public sealed record CreateCostCenterRequest(
    string? Code,
    string? Name,
    Guid DepartmentId,
    string? Reason);

public sealed record UpdateCostCenterRequest(
    string? Name,
    Guid? DepartmentId,
    string? Status,
    int ExpectedVersion,
    string? Reason);

public sealed record CreateSpendCategoryRequest(string? Code, string? Name, string? Reason);

public sealed record UpdateSpendCategoryRequest(
    string? Name,
    string? Status,
    int ExpectedVersion,
    string? Reason);
