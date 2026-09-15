using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.BudgetLedger;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Api.Controllers;

/// <summary>
/// Budget positions, balances and movements (SPEC 08 REQ-01, REQ-02, REQ-10). ADMIN manages
/// allocations and reads the operation; AUDITOR reads inside an organization or cost-center
/// assignment; any other user only receives the minimized answer of its own precheck.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/budgets")]
public sealed class BudgetController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    BudgetPersistenceService budgets,
    BudgetPrecheckService prechecks) : ControllerBase
{
    /// <summary>Creates the position and its first allocation revision (REQ-01).</summary>
    [HttpPut("positions/{costCenterId:guid}/{fiscalYear:int}/{spendCategoryCode}")]
    public async Task<ActionResult<BudgetPositionResponse>> SetAllocation(
        Guid costCenterId,
        int fiscalYear,
        string spendCategoryCode,
        SetBudgetAllocationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAdminAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var view = await budgets.SetAllocationAsync(
            organizationId,
            actor.Id,
            costCenterId,
            fiscalYear,
            spendCategoryCode,
            request.Amount,
            request.Currency ?? string.Empty,
            request.ExpectedVersion,
            request.AllocationKey,
            request.Reason,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(ToResponse(view));
    }

    /// <summary>Current projection of every position the caller may read (REQ-10).</summary>
    [HttpGet("positions")]
    public async Task<ActionResult<IReadOnlyCollection<BudgetPositionResponse>>> GetPositions(
        [FromQuery] Guid? costCenterId,
        [FromQuery] int? fiscalYear,
        CancellationToken cancellationToken)
    {
        var organizationId = await RequireBudgetReaderAsync(costCenterId, cancellationToken);
        var views = await budgets.ListPositionsAsync(organizationId, costCenterId, fiscalYear, cancellationToken);
        return Ok(views.Select(ToResponse).ToArray());
    }

    /// <summary>Append-only movement history of one position (REQ-10).</summary>
    [HttpGet("positions/{positionId:guid}/movements")]
    public async Task<ActionResult<IReadOnlyCollection<BudgetMovementResponse>>> GetMovements(
        Guid positionId,
        CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var position = await dbContext.BudgetPositions
            .AsNoTracking()
            .Where(record => record.Id == positionId && record.OrganizationId == organizationId)
            .Select(record => new { record.CostCenterId })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The budget position was not found.");
        await RequireReaderAsync(position.CostCenterId, cancellationToken);
        var movements = await budgets.ReadMovementsAsync(organizationId, positionId, cancellationToken);
        return Ok(movements.Select(movement => new BudgetMovementResponse(
            movement.MovementId,
            movement.OperationId,
            BudgetFingerprints.MovementTypeCode((BudgetMovementType)movement.Type),
            movement.Amount,
            movement.ParentMovementId,
            movement.TargetId,
            movement.TargetVersion,
            movement.OccurredAt)).ToArray());
    }

    /// <summary>
    /// Non-binding precheck of one Purchase Request version (REQ-04): the requester receives only
    /// the aggregate result and the state of its own lines.
    /// </summary>
    [HttpPost("/api/v1/purchase-requests/{requestId:guid}/versions/{requestVersion:int}/budget-precheck")]
    public async Task<ActionResult<BudgetPrecheckResultView>> Precheck(
        Guid requestId,
        int requestVersion,
        BudgetPrecheckRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActiveUserAsync(cancellationToken);
        var organizationId = await OrganizationIdAsync(cancellationToken);
        var owned = await dbContext.PurchaseRequests
            .AsNoTracking()
            .AnyAsync(
                record => record.Id == requestId &&
                          record.OrganizationId == organizationId &&
                          record.RequesterId == actor.Id,
                cancellationToken);
        if (!owned)
        {
            throw new DomainNotFoundException("The purchase request is not visible.");
        }

        if (!string.Equals(
                request.ContractVersion,
                BudgetCodes.PrecheckRequestVersion,
                StringComparison.Ordinal))
        {
            throw new DomainValidationException("The precheck contract version is not recognized.");
        }

        var view = await prechecks.PrecheckAsync(
            organizationId,
            requestId,
            requestVersion,
            request.PrecheckKey,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return Ok(view);
    }

    private async Task<Guid> RequireBudgetReaderAsync(Guid? costCenterId, CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(cancellationToken);
        if (costCenterId is Guid requested)
        {
            await RequireReaderAsync(requested, cancellationToken);
            return organizationId;
        }

        await RequireReaderAsync(null, cancellationToken);
        return organizationId;
    }

    /// <summary>
    /// A budget reader is an active user with an ADMIN or AUDITOR assignment that covers the
    /// organization, or the exact cost center when one is named (REQ-10). Holding the role without
    /// a covering scope never authorizes a read.
    /// </summary>
    private async Task RequireReaderAsync(Guid? costCenterId, CancellationToken cancellationToken)
    {
        var profile = await RequireActiveUserAsync(cancellationToken);
        var scopeJson = await dbContext.CostCenters
            .AsNoTracking()
            .Where(record => record.Id == costCenterId)
            .Select(record => record.Code)
            .SingleOrDefaultAsync(cancellationToken);
        if (costCenterId is not null && scopeJson is null)
        {
            throw new DomainNotFoundException("The budget position was not found.");
        }

        var assignments = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.UserProfileId == profile.Id &&
                                 assignment.Status == (int)AssignmentStatus.Active &&
                                 (assignment.Role == (int)SystemRole.Admin ||
                                  assignment.Role == (int)SystemRole.Auditor))
            .Select(assignment => assignment.ScopeJson)
            .ToArrayAsync(cancellationToken);
        var required = costCenterId is null
            ? AuthorizationScopeSet.Create([AuthorizationScope.Global()])
            : AuthorizationScopeSet.Create([AuthorizationScope.For(ScopeDimension.CostCenter, scopeJson!)]);
        var allowed = assignments.Any(scope =>
        {
            try
            {
                return OrganizationEligibilityService.ParseScope(scope).Covers(required);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        if (!allowed)
        {
            // A position outside the reader scope is indistinguishable from a missing one.
            throw new DomainNotFoundException("The budget position was not found.");
        }
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

    private Task<Guid> OrganizationIdAsync(CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().Select(record => record.Id).SingleAsync(cancellationToken);

    private static BudgetPositionResponse ToResponse(BudgetPositionView view) =>
        new(
            view.PositionId,
            view.CostCenterId,
            view.FiscalYear,
            view.SpendCategoryCode,
            view.AllocationVersion,
            view.BalanceVersion,
            view.Currency,
            new BudgetBucketsResponse(
                view.Buckets.Allocated,
                view.Buckets.Reserved,
                view.Buckets.Committed,
                view.Buckets.Consumed,
                view.Buckets.Available));
}

public sealed record BudgetBucketsResponse(
    decimal Allocated,
    decimal Reserved,
    decimal Committed,
    decimal Consumed,
    decimal Available);

public sealed record BudgetPositionResponse(
    Guid PositionId,
    Guid CostCenterId,
    int FiscalYear,
    string SpendCategoryCode,
    int AllocationVersion,
    int BalanceVersion,
    string Currency,
    BudgetBucketsResponse Balances);

public sealed record BudgetMovementResponse(
    Guid MovementId,
    Guid OperationId,
    string Type,
    decimal Amount,
    Guid? ParentMovementId,
    Guid? TargetId,
    int? TargetVersion,
    DateTimeOffset OccurredAt);

public sealed record SetBudgetAllocationRequest(
    decimal Amount,
    string? Currency,
    int? ExpectedVersion,
    string? AllocationKey,
    string? Reason);

public sealed record BudgetPrecheckRequest(
    [property: JsonPropertyName("contract_version")] string? ContractVersion,
    [property: JsonPropertyName("precheck_key")] string? PrecheckKey);
