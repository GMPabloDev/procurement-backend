using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class OrganizationController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> GetMe(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        var roles = await dbContext.RoleAssignments
            .Where(assignment => assignment.UserProfileId == profile.Id &&
                                assignment.Status == (int)AssignmentStatus.Active)
            .Select(assignment => ((SystemRole)assignment.Role).ToString())
            .ToArrayAsync(cancellationToken);

        return Ok(new MeResponse(
            profile.Id,
            profile.Issuer,
            profile.Subject,
            profile.Email,
            profile.DisplayName,
            ((UserProfileStatus)profile.Status).ToString(),
            profile.DepartmentId,
            profile.JobTitle,
            profile.Version,
            roles));
    }

    [HttpPut("organization")]
    public async Task<ActionResult<OrganizationResponse>> UpdateOrganization(
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        EnsureExpectedVersion(organization.Version, request.ExpectedVersion);
        var name = Required(request.Name, nameof(request.Name));
        var timeZoneId = Required(request.TimeZoneId, nameof(request.TimeZoneId));
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new DomainValidationException("TimeZoneId must be an installed IANA time zone.");
        }
        var previousVersion = organization.Version;
        var beforeJson = JsonSerializer.Serialize(new { organization.Name, organization.TimeZoneId });
        organization.Name = name;
        organization.TimeZoneId = timeZoneId;
        organization.Version++;
        AddAudit(actor, "ORGANIZATION_PROFILE_UPDATED", "Organization", organization.Id, request.Reason,
            JsonSerializer.Serialize(new { name, timeZoneId }), previousVersion, organization.Version,
            beforeJson: beforeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(organization));
    }

    [HttpGet("organization")]
    public async Task<ActionResult<OrganizationResponse>> GetOrganization(CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        var departmentCount = access.IsGlobal
            ? await dbContext.Departments.CountAsync(cancellationToken)
            : await dbContext.Departments.CountAsync(item => access.Departments.Contains(item.Code), cancellationToken);
        var userCount = access.IsGlobal
            ? await dbContext.UserProfiles.CountAsync(cancellationToken)
            : await dbContext.UserProfiles.CountAsync(
                item => item.Department != null && access.Departments.Contains(item.Department.Code), cancellationToken);
        return Ok(new OrganizationResponse(
            organization.Id, organization.Code, organization.Name, organization.BaseCurrency,
            organization.TimeZoneId, organization.FiscalYearStartMonth, organization.Version,
            departmentCount, userCount));
    }

    [HttpGet("legal-entities")]
    public async Task<ActionResult<IReadOnlyCollection<LegalEntityResponse>>> GetLegalEntities(
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        var entities = await dbContext.LegalEntities
            .Where(entity => access.IsGlobal || access.LegalEntities.Contains(entity.Code))
            .OrderBy(entity => entity.Code)
            .Select(entity => new LegalEntityResponse(entity.Id, entity.Code, entity.Name,
                ((EntityStatus)entity.Status).ToString(), entity.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(entities);
    }

    [HttpPost("legal-entities")]
    public async Task<ActionResult<LegalEntityResponse>> CreateLegalEntity(
        CreateLegalEntityRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        EnsureExpectedVersion(organization.Version, request.ExpectedOrganizationVersion);
        if (await dbContext.LegalEntities.AnyAsync(cancellationToken))
        {
            throw new DomainConflictException("Only one Legal Entity is supported by this organization foundation.");
        }
        var entity = new LegalEntityRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id,
            Code = Required(request.Code, nameof(request.Code)).ToUpperInvariant(),
            Name = Required(request.Name, nameof(request.Name)),
            Status = (int)EntityStatus.Active, Version = 1
        };
        dbContext.LegalEntities.Add(entity);
        AddAudit(actor, "LEGAL_ENTITY_CREATED", "LegalEntity", entity.Id, request.Reason,
            JsonSerializer.Serialize(new { entity.Code, entity.Name }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Created($"/api/v1/legal-entities/{entity.Id}",
            new LegalEntityResponse(entity.Id, entity.Code, entity.Name, nameof(EntityStatus.Active), entity.Version));
    }

    [HttpGet("departments")]
    public async Task<ActionResult<IReadOnlyCollection<DepartmentResponse>>> GetDepartments(
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        var departments = await dbContext.Departments
            .Where(department => access.IsGlobal || access.Departments.Contains(department.Code))
            .OrderBy(department => department.Code)
            .Select(department => new DepartmentResponse(
                department.Id,
                department.Code,
                department.Name,
                ((EntityStatus)department.Status).ToString(),
                department.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(departments);
    }

    [HttpPost("departments")]
    public async Task<ActionResult<DepartmentResponse>> CreateDepartment(
        CreateDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        EnsureExpectedVersion(organization.Version, request.ExpectedOrganizationVersion);
        var code = Required(request.Code, nameof(request.Code)).ToUpperInvariant();
        var name = Required(request.Name, nameof(request.Name));
        var department = new DepartmentRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Code = code,
            Name = name,
            Status = (int)EntityStatus.Active,
            Version = 1
        };
        dbContext.Departments.Add(department);
        AddAudit(actor, "DEPARTMENT_CREATED", "Department", department.Id, request.Reason,
            JsonSerializer.Serialize(new { code, name }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Created($"/api/v1/departments/{department.Id}", ToResponse(department));
    }

    [HttpPatch("departments/{departmentId:guid}")]
    public async Task<ActionResult<DepartmentResponse>> UpdateDepartment(
        Guid departmentId,
        UpdateDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var department = await dbContext.Departments.SingleOrDefaultAsync(
            item => item.Id == departmentId, cancellationToken)
            ?? throw new DomainNotFoundException("Department was not found.");
        EnsureExpectedVersion(department.Version, request.ExpectedVersion);
        if (request.Name is not null)
        {
            department.Name = Required(request.Name, nameof(request.Name));
        }
        if (request.Status is not null)
        {
            if (!Enum.TryParse<EntityStatus>(request.Status, true, out var status) || !Enum.IsDefined(status))
            {
                throw new DomainValidationException("Department status is not recognized.");
            }
            if (status == EntityStatus.Inactive && department.Status == (int)EntityStatus.Active)
            {
                var userIds = await dbContext.UserProfiles
                    .Where(user => user.DepartmentId == department.Id && user.Status == (int)UserProfileStatus.Active)
                    .Select(user => user.Id)
                    .ToArrayAsync(cancellationToken);
                var assignmentScopes = await dbContext.RoleAssignments
                    .Where(item => item.Status == (int)AssignmentStatus.Active)
                    .Select(item => item.ScopeJson)
                    .ToArrayAsync(cancellationToken);
                var grantScopes = await dbContext.AuthorityGrants
                    .Where(item => item.Status == (int)GrantStatus.Active)
                    .Select(item => item.ScopeJson)
                    .ToArrayAsync(cancellationToken);
                var referencedByScope = assignmentScopes.Concat(grantScopes)
                    .Any(scope => ScopeReferencesDepartment(scope, department.Code));
                if (userIds.Length > 0 || referencedByScope)
                {
                    throw new DomainConflictException("Department has active references and cannot be deactivated.");
                }
            }
            department.Status = (int)status;
        }
        department.Version++;
        AddAudit(actor, "DEPARTMENT_UPDATED", "Department", department.Id, request.Reason,
            JsonSerializer.Serialize(new { department.Name, status = ((EntityStatus)department.Status).ToString() }),
            department.Version - 1, department.Version);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(department));
    }

    [HttpGet("authority-levels")]
    public async Task<ActionResult<IReadOnlyCollection<AuthorityLevelResponse>>> GetAuthorityLevels(
        CancellationToken cancellationToken)
    {
        _ = await RequireReadAccessAsync(cancellationToken);
        var levels = await dbContext.AuthorityLevels
            .Where(level => level.IsActive)
            .OrderBy(level => level.Type).ThenBy(level => level.Rank)
            .Select(level => new AuthorityLevelResponse(level.Id, ((ApprovalAuthorityType)level.Type).ToString(),
                level.Code, level.Rank, level.LevelVersion))
            .ToArrayAsync(cancellationToken);
        return Ok(levels);
    }

    [HttpPost("authority-levels")]
    public async Task<ActionResult<AuthorityLevelResponse>> CreateAuthorityLevel(
        CreateAuthorityLevelRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        if (!Enum.TryParse<ApprovalAuthorityType>(request.Type, true, out var type) || !Enum.IsDefined(type))
        {
            throw new DomainValidationException("Authority type is not recognized.");
        }
        var code = Required(request.Code, nameof(request.Code)).ToUpperInvariant();
        var rank = request.Rank > 0 ? request.Rank : throw new DomainValidationException("Rank must be positive.");
        var version = (await dbContext.AuthorityLevels
            .Where(level => level.Type == (int)type && level.Code == code)
            .Select(level => (int?)level.LevelVersion)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var level = new AuthorityLevelRecord
        {
            Id = Guid.NewGuid(), Type = (int)type, Code = code, Rank = rank,
            LevelVersion = version, IsActive = true
        };
        dbContext.AuthorityLevels.Add(level);
        AddAudit(actor, "AUTHORITY_LEVEL_CREATED", "AuthorityLevel", level.Id, request.Reason,
            JsonSerializer.Serialize(new { type = type.ToString(), code, rank, version }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Created($"/api/v1/authority-levels/{level.Id}",
            new AuthorityLevelResponse(level.Id, type.ToString(), level.Code, level.Rank, level.LevelVersion));
    }

    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyCollection<UserResponse>>> GetUsers(
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        var users = dbContext.UserProfiles
            .Where(user => access.IsGlobal ||
                user.Department != null && access.Departments.Contains(user.Department.Code))
            .Include(user => user.Department)
            .OrderBy(user => user.DisplayName)
            .Select(user => new UserResponse(
                user.Id,
                null,
                null,
                user.Email,
                user.DisplayName,
                ((UserProfileStatus)user.Status).ToString(),
                user.DepartmentId,
                user.Department == null ? null : user.Department.Code,
                user.JobTitle,
                user.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(users);
    }

    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyCollection<AuditResponse>>> GetAudit(
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        var records = await dbContext.AdministrativeAuditRecords
            .OrderByDescending(record => record.OccurredAt)
            .Take(200)
            .ToArrayAsync(cancellationToken);
        var visible = records
            .Where(record => access.IsGlobal ||
                ScopeMatchesReadAccess(record.ScopeJson, access))
            .Select(record => new AuditResponse(record.Id, record.OccurredAt, record.Action,
                record.TargetType, record.TargetId, record.PreviousVersion, record.NewVersion,
                record.ScopeJson, record.Reason, record.CorrelationReference))
            .ToArray();
        return Ok(visible);
    }

    [HttpGet("users/{userId:guid}/roles")]
    public async Task<ActionResult<IReadOnlyCollection<RoleResponse>>> GetRoles(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        await EnsureUserVisibleAsync(userId, access, cancellationToken);
        var roles = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == userId)
            .OrderBy(item => item.Role).ThenBy(item => item.AssignedAt)
            .Select(item => new RoleResponse(item.Id, ((SystemRole)item.Role).ToString(),
                item.ScopeJson, ((AssignmentStatus)item.Status).ToString(), item.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(roles);
    }

    [HttpGet("users/{userId:guid}/grants")]
    public async Task<ActionResult<IReadOnlyCollection<GrantResponse>>> GetGrants(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        await EnsureUserVisibleAsync(userId, access, cancellationToken);
        var grants = await dbContext.AuthorityGrants
            .Where(item => item.UserProfileId == userId)
            .OrderBy(item => item.GrantedAt)
            .Select(item => new GrantResponse(item.Id, item.AuthorityLevelId, item.MaxAmountBase,
                item.BaseCurrency, item.ScopeJson, item.ValidFrom, item.ValidTo,
                ((GrantStatus)item.Status).ToString(), item.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(grants);
    }

    [HttpPatch("users/{userId:guid}/setup")]
    public async Task<ActionResult<UserResponse>> CompleteSetup(
        Guid userId,
        CompleteSetupRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var user = await dbContext.UserProfiles.Include(item => item.Department)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedVersion);
        if (user.Status != (int)UserProfileStatus.PendingSetup)
        {
            throw new DomainConflictException("Only a pending user can complete setup.");
        }
        var department = await dbContext.Departments.SingleOrDefaultAsync(
            item => item.Id == request.DepartmentId && item.Status == (int)EntityStatus.Active,
            cancellationToken)
            ?? throw new DomainValidationException("An active Department is required.");
        var beforeJson = JsonSerializer.Serialize(new { user.DepartmentId, user.JobTitle, user.Status });
        user.DepartmentId = department.Id;
        user.JobTitle = Required(request.JobTitle, nameof(request.JobTitle));
        user.Status = (int)UserProfileStatus.PendingSetup;
        user.Version++;
        AddAudit(actor, "USER_SETUP_COMPLETED", "UserProfile", user.Id, request.Reason,
            JsonSerializer.Serialize(new { departmentId = department.Id, jobTitle = user.JobTitle }),
            user.Version - 1, user.Version, beforeJson: beforeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    [HttpPost("users/{userId:guid}/activate")]
    public async Task<ActionResult<UserResponse>> ActivateUser(
        Guid userId,
        AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var user = await dbContext.UserProfiles.Include(item => item.Department)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedVersion);
        if (user.Status != (int)UserProfileStatus.PendingSetup)
        {
            throw new DomainConflictException("Only a pending user can be activated.");
        }
        if (user.Department is null || user.Department.Status != (int)EntityStatus.Active ||
            string.IsNullOrWhiteSpace(user.JobTitle))
        {
            throw new DomainValidationException("User requires an active Department and Job Title before activation.");
        }
        var beforeJson = JsonSerializer.Serialize(new { user.Status, user.DepartmentId, user.JobTitle });
        user.Status = (int)UserProfileStatus.Active;
        user.Version++;
        AddAudit(actor, "USER_ACTIVATED", "UserProfile", user.Id, request.Reason, "{}",
            user.Version - 1, user.Version, beforeJson: beforeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    [HttpPost("users/{userId:guid}/deactivate")]
    public async Task<ActionResult<UserResponse>> DeactivateUser(
        Guid userId,
        AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var user = await dbContext.UserProfiles.Include(item => item.Department)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedVersion);
        if (user.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainConflictException("Only an active user can be deactivated.");
        }
        if (await IsLastAdministratorAsync(user.Id, cancellationToken))
        {
            throw new DomainConflictException("The last active administrator cannot be deactivated.");
        }

        var now = DateTimeOffset.UtcNow;
        var assignments = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == user.Id && item.Status == (int)AssignmentStatus.Active)
            .ToArrayAsync(cancellationToken);
        var grants = await dbContext.AuthorityGrants
            .Where(item => item.UserProfileId == user.Id && item.Status == (int)GrantStatus.Active)
            .ToArrayAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            assignment.Status = (int)AssignmentStatus.Revoked;
            assignment.RevokedAt = now;
            assignment.RevokedBy = actor.Id;
            assignment.Version++;
        }
        foreach (var grant in grants)
        {
            grant.Status = (int)GrantStatus.Revoked;
            grant.RevokedAt = now;
            grant.RevokedBy = actor.Id;
            grant.Version++;
        }
        user.Status = (int)UserProfileStatus.Inactive;
        user.Version++;
        var subchanges = assignments.Select(item => new
            {
                targetType = "RoleAssignment", targetId = item.Id,
                previousVersion = item.Version - 1, newVersion = item.Version
            })
            .Concat(grants.Select(item => new
            {
                targetType = "AuthorityGrant", targetId = item.Id,
                previousVersion = item.Version - 1, newVersion = item.Version
            }))
            .ToArray();
        AddAudit(actor, "USER_DEACTIVATED", "UserProfile", user.Id, request.Reason,
            JsonSerializer.Serialize(new { assignmentsRevoked = assignments.Length, grantsRevoked = grants.Length, subchanges }),
            user.Version - 1, user.Version);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    [HttpPost("users/{userId:guid}/return-to-setup")]
    public async Task<ActionResult<UserResponse>> ReturnUserToSetup(
        Guid userId,
        AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var user = await dbContext.UserProfiles.Include(item => item.Department)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedVersion);
        if (user.Status != (int)UserProfileStatus.Inactive)
        {
            throw new DomainConflictException("Only an inactive user can return to setup.");
        }
        user.Status = (int)UserProfileStatus.PendingSetup;
        user.Version++;
        AddAudit(actor, "USER_RETURNED_TO_SETUP", "UserProfile", user.Id, request.Reason, "{}",
            user.Version - 1, user.Version);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    [HttpPost("users/{userId:guid}/grants")]
    public async Task<ActionResult<AuthorityGrantResponse>> GrantAuthority(
        Guid userId,
        GrantAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var user = await dbContext.UserProfiles.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedUserVersion);
        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        var level = await dbContext.AuthorityLevels.SingleOrDefaultAsync(
            item => item.Id == request.AuthorityLevelId && item.IsActive, cancellationToken)
            ?? throw new DomainNotFoundException("Active authority level was not found.");
        var scopeJson = await BuildScopeJsonAsync(request.Scopes, cancellationToken);
        var currency = Required(request.BaseCurrency, nameof(request.BaseCurrency)).ToUpperInvariant();
        if (currency != organization.BaseCurrency)
        {
            throw new DomainValidationException("Grant currency must match the organization base currency.");
        }
        if (request.MaxAmountBase is < 0)
        {
            throw new DomainValidationException("Grant amount cannot be negative.");
        }
        var grant = new AuthorityGrantRecord
        {
            Id = Guid.NewGuid(), UserProfileId = user.Id, AuthorityLevelId = level.Id,
            MaxAmountBase = request.MaxAmountBase, BaseCurrency = currency,
            ScopeJson = scopeJson, ValidFrom = request.ValidFrom ?? DateTimeOffset.UtcNow,
            ValidTo = request.ValidTo, Status = (int)GrantStatus.Active,
            GrantedAt = DateTimeOffset.UtcNow, GrantedBy = actor.Id, Version = 1
        };
        if (grant.ValidTo is not null && grant.ValidTo <= grant.ValidFrom)
        {
            throw new DomainValidationException("ValidTo must be after ValidFrom.");
        }
        dbContext.AuthorityGrants.Add(grant);
        AddAudit(actor, "AUTHORITY_GRANTED", "AuthorityGrant", grant.Id, request.Reason,
            JsonSerializer.Serialize(new { userId = user.Id, authorityLevelId = level.Id }),
            null, grant.Version, scopeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Created($"/api/v1/users/{user.Id}/grants/{grant.Id}",
            new AuthorityGrantResponse(grant.Id, grant.UserProfileId, grant.AuthorityLevelId,
                grant.MaxAmountBase, grant.BaseCurrency, grant.ScopeJson, grant.ValidFrom, grant.ValidTo, grant.Version));
    }

    [HttpDelete("users/{userId:guid}/grants/{grantId:guid}")]
    public async Task<IActionResult> RevokeAuthority(
        Guid userId,
        Guid grantId,
        [FromBody] AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var grant = await dbContext.AuthorityGrants.SingleOrDefaultAsync(
            item => item.Id == grantId && item.UserProfileId == userId && item.Status == (int)GrantStatus.Active,
            cancellationToken)
            ?? throw new DomainNotFoundException("Active authority grant was not found.");
        EnsureExpectedVersion(grant.Version, request.ExpectedVersion);
        grant.Status = (int)GrantStatus.Revoked;
        grant.RevokedAt = DateTimeOffset.UtcNow;
        grant.RevokedBy = actor.Id;
        grant.Version++;
        AddAudit(actor, "AUTHORITY_REVOKED", "AuthorityGrant", grant.Id, request.Reason, "{}",
            grant.Version - 1, grant.Version);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("users/{userId:guid}/roles")]
    public async Task<IActionResult> AssignRole(
        Guid userId,
        AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        if (!Enum.TryParse<SystemRole>(request.Role, true, out var role) ||
            !Enum.IsDefined(role))
        {
            throw new DomainValidationException("Role is not recognized.");
        }
        var user = await dbContext.UserProfiles.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new DomainNotFoundException("User profile was not found.");
        EnsureExpectedVersion(user.Version, request.ExpectedUserVersion);
        var exists = await dbContext.RoleAssignments.AnyAsync(
            item => item.UserProfileId == userId && item.Role == (int)role &&
                    item.Status == (int)AssignmentStatus.Active && item.ScopeJson == GlobalScopeJson,
            cancellationToken);
        if (exists)
        {
            throw new DomainConflictException("The role assignment is already active.");
        }
        var scopeJson = await BuildScopeJsonAsync(request.Scopes, cancellationToken);
        if (role == SystemRole.Admin && scopeJson != GlobalScopeJson)
        {
            throw new DomainValidationException("ADMIN assignments require organization scope.");
        }
        var candidateScope = ParseScopeSet(scopeJson);
        var existingAssignments = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == user.Id && item.Role == (int)role &&
                           item.Status == (int)AssignmentStatus.Active)
            .Select(item => item.ScopeJson)
            .ToArrayAsync(cancellationToken);
        if (existingAssignments.Any(existing => ParseScopeSet(existing).Overlaps(candidateScope)))
        {
            throw new DomainConflictException("An active role assignment overlaps the requested scope.");
        }
        var assignment = new RoleAssignmentRecord
        {
            Id = Guid.NewGuid(), UserProfileId = user.Id, Role = (int)role,
            ScopeJson = scopeJson, Status = (int)AssignmentStatus.Active,
            AssignedAt = DateTimeOffset.UtcNow, AssignedBy = actor.Id, Version = 1
        };
        dbContext.RoleAssignments.Add(assignment);
        AddAudit(actor, "ROLE_ASSIGNED", "RoleAssignment", assignment.Id, request.Reason,
            JsonSerializer.Serialize(new { userId = user.Id, role = role.ToString() }),
            null, assignment.Version, scopeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("users/{userId:guid}/roles/{role}")]
    public async Task<IActionResult> RevokeRole(
        Guid userId,
        string role,
        [FromBody] AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        if (!Enum.TryParse<SystemRole>(role, true, out var parsedRole) || !Enum.IsDefined(parsedRole))
        {
            throw new DomainValidationException("Role is not recognized.");
        }
        var assignment = await dbContext.RoleAssignments.SingleOrDefaultAsync(
            item => item.UserProfileId == userId && item.Role == (int)parsedRole &&
                    item.Status == (int)AssignmentStatus.Active && item.ScopeJson == GlobalScopeJson,
            cancellationToken)
            ?? throw new DomainNotFoundException("Active role assignment was not found.");
        EnsureExpectedVersion(assignment.Version, request.ExpectedVersion);
        if (parsedRole == SystemRole.Admin && await IsLastAdministratorAsync(userId, cancellationToken))
        {
            throw new DomainConflictException("The last active administrator role cannot be revoked.");
        }
        assignment.Status = (int)AssignmentStatus.Revoked;
        assignment.RevokedAt = DateTimeOffset.UtcNow;
        assignment.RevokedBy = actor.Id;
        assignment.Version++;
        AddAudit(actor, "ROLE_REVOKED", "RoleAssignment", assignment.Id, request.Reason, "{}",
            assignment.Version - 1, assignment.Version);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> IsLastAdministratorAsync(Guid userId, CancellationToken cancellationToken)
    {
        var isAdministrator = await dbContext.RoleAssignments.AnyAsync(
            item => item.UserProfileId == userId && item.Role == (int)SystemRole.Admin &&
                    item.Status == (int)AssignmentStatus.Active && item.ScopeJson == GlobalScopeJson,
            cancellationToken);
        if (!isAdministrator)
        {
            return false;
        }

        var administratorIds = await dbContext.RoleAssignments
            .Where(item => item.Role == (int)SystemRole.Admin &&
                           item.Status == (int)AssignmentStatus.Active &&
                           item.ScopeJson == GlobalScopeJson)
            .Select(item => item.UserProfileId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var activeAdministrators = await dbContext.UserProfiles.CountAsync(
            item => administratorIds.Contains(item.Id) && item.Status == (int)UserProfileStatus.Active,
            cancellationToken);
        return activeAdministrators <= 1;
    }

    private async Task EnsureUserVisibleAsync(
        Guid userId,
        ReadScope access,
        CancellationToken cancellationToken)
    {
        if (access.IsGlobal)
        {
            return;
        }
        var visible = await dbContext.UserProfiles
            .Where(user => user.Id == userId && user.Department != null &&
                          access.Departments.Contains(user.Department.Code))
            .AnyAsync(cancellationToken);
        if (!visible)
        {
            throw new DomainNotFoundException("User profile was not found.");
        }
    }

    private async Task<ReadScope> RequireReadAccessAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }
        var assignments = await dbContext.RoleAssignments
            .Where(assignment => assignment.UserProfileId == profile.Id &&
                                (assignment.Role == (int)SystemRole.Admin || assignment.Role == (int)SystemRole.Auditor) &&
                                assignment.Status == (int)AssignmentStatus.Active)
            .Select(assignment => new { assignment.Role, assignment.ScopeJson })
            .ToArrayAsync(cancellationToken);
        if (assignments.Any(assignment => assignment.Role == (int)SystemRole.Admin &&
                                          assignment.ScopeJson == GlobalScopeJson))
        {
            return ReadScope.Global;
        }

        var departments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legalEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.Where(item => item.Role == (int)SystemRole.Auditor))
        {
            ScopeEntry[] entries;
            try
            {
                entries = JsonSerializer.Deserialize<ScopeEntry[]>(assignment.ScopeJson) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }
            if (entries.Any(entry => entry.Dimension.Equals("Organization", StringComparison.OrdinalIgnoreCase)))
            {
                return ReadScope.Global;
            }
            foreach (var entry in entries)
            {
                if (entry.Reference is null)
                {
                    continue;
                }
                if (entry.Dimension.Equals("Department", StringComparison.OrdinalIgnoreCase))
                {
                    departments.Add(entry.Reference);
                }
                else if (entry.Dimension.Equals("LegalEntity", StringComparison.OrdinalIgnoreCase))
                {
                    legalEntities.Add(entry.Reference);
                }
            }
        }
        if (departments.Count == 0 && legalEntities.Count == 0)
        {
            throw new DomainForbiddenException("The user does not have administrative read access.");
        }
        return new ReadScope(false, departments, legalEntities);
    }

    private void AddAudit(UserProfileRecord actor, string action, string targetType, Guid targetId,
        string? reason, string afterJson, int? previousVersion = null, int? newVersion = null,
        string? scopeJson = null, string? beforeJson = null)
    {
        var cleanReason = Required(reason, "Reason");
        dbContext.AdministrativeAuditRecords.Add(new AdministrativeAuditRecord
        {
            Id = Guid.NewGuid(), ActorType = "USER", ActorUserId = actor.Id,
            OccurredAt = DateTimeOffset.UtcNow, Action = action, TargetType = targetType,
            TargetId = targetId, PreviousVersion = previousVersion, NewVersion = newVersion ?? 1,
            ScopeJson = scopeJson ?? GlobalScopeJson, BeforeJson = beforeJson,
            AfterJson = afterJson, Reason = cleanReason,
            CorrelationReference = HttpContext.TraceIdentifier
        });
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainValidationException($"{field} is required.") : value.Trim();

    private static void EnsureExpectedVersion(int actualVersion, int expectedVersion)
    {
        if (expectedVersion <= 0)
        {
            throw new DomainValidationException("ExpectedVersion is required.");
        }
        if (actualVersion != expectedVersion)
        {
            throw new DomainConflictException("The resource version is stale.");
        }
    }

    private async Task<string> BuildScopeJsonAsync(
        IReadOnlyCollection<ScopeInput>? inputs,
        CancellationToken cancellationToken)
    {
        if (inputs is null || inputs.Count == 0)
        {
            throw new DomainValidationException("At least one explicit scope is required.");
        }

        var scopes = new List<AuthorizationScope>();
        var serialized = new List<object>();
        foreach (var input in inputs)
        {
            if (!Enum.TryParse<ScopeDimension>(input.Dimension, true, out var dimension) ||
                !Enum.IsDefined(dimension) || dimension == ScopeDimension.CostCenter)
            {
                throw new DomainValidationException("Scope dimension is not supported.");
            }
            var reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim();
            if (dimension == ScopeDimension.Organization)
            {
                if (reference is not null)
                {
                    throw new DomainValidationException("Organization scope cannot contain a reference.");
                }
                scopes.Add(AuthorizationScope.Global());
            }
            else
            {
                reference = Required(reference, nameof(input.Reference));
                reference = dimension switch
                {
                    ScopeDimension.Department => await dbContext.Departments
                        .Where(item => item.Status == (int)EntityStatus.Active &&
                                       item.Code.ToUpper() == reference.ToUpper())
                        .Select(item => item.Code)
                        .SingleOrDefaultAsync(cancellationToken),
                    ScopeDimension.LegalEntity => await dbContext.LegalEntities
                        .Where(item => item.Status == (int)EntityStatus.Active &&
                                       item.Code.ToUpper() == reference.ToUpper())
                        .Select(item => item.Code)
                        .SingleOrDefaultAsync(cancellationToken),
                    _ => null
                } ?? throw new DomainNotFoundException("The scoped reference is not active.");
                scopes.Add(AuthorizationScope.For(dimension, reference));
            }
            serialized.Add(new { dimension = dimension.ToString(), reference });
        }

        _ = AuthorizationScopeSet.Create(scopes);
        return JsonSerializer.Serialize(serialized);
    }

    private static bool ScopeMatchesReadAccess(string scopeJson, ReadScope access)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<ScopeInput[]>(scopeJson) ?? [];
            return entries.Any(entry =>
                (string.Equals(entry.Dimension, "Department", StringComparison.OrdinalIgnoreCase) &&
                 entry.Reference is not null && access.Departments.Contains(entry.Reference)) ||
                (string.Equals(entry.Dimension, "LegalEntity", StringComparison.OrdinalIgnoreCase) &&
                 entry.Reference is not null && access.LegalEntities.Contains(entry.Reference)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ScopeReferencesDepartment(string scopeJson, string departmentCode)
    {
        try
        {
            return (JsonSerializer.Deserialize<ScopeInput[]>(scopeJson) ?? [])
                .Any(scope => string.Equals(scope.Dimension, "Department", StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(scope.Reference, departmentCode, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AuthorizationScopeSet ParseScopeSet(string scopeJson)
    {
        var entries = JsonSerializer.Deserialize<ScopeInput[]>(scopeJson)
            ?? throw new DomainValidationException("Stored scope is invalid.");
        return AuthorizationScopeSet.Create(entries.Select(entry =>
            Enum.TryParse<ScopeDimension>(entry.Dimension, true, out var dimension) &&
            Enum.IsDefined(dimension)
                ? dimension == ScopeDimension.Organization
                    ? AuthorizationScope.Global()
                    : AuthorizationScope.For(dimension, Required(entry.Reference, nameof(entry.Reference)))
                : throw new DomainValidationException("Stored scope dimension is invalid.")));
    }

    private static OrganizationResponse ToResponse(OrganizationRecord organization) =>
        new(organization.Id, organization.Code, organization.Name, organization.BaseCurrency,
            organization.TimeZoneId, organization.FiscalYearStartMonth, organization.Version, 0, 0);

    private static DepartmentResponse ToResponse(DepartmentRecord department) =>
        new(department.Id, department.Code, department.Name, ((EntityStatus)department.Status).ToString(), department.Version);

    private static UserResponse ToResponse(UserProfileRecord user) =>
        new(user.Id, user.Issuer, user.Subject, user.Email, user.DisplayName,
            ((UserProfileStatus)user.Status).ToString(), user.DepartmentId, user.Department?.Code,
            user.JobTitle, user.Version);

    private sealed record ScopeEntry(string Dimension, string? Reference);

    private sealed record ReadScope(
        bool IsGlobal,
        IReadOnlySet<string> Departments,
        IReadOnlySet<string> LegalEntities)
    {
        public static ReadScope Global { get; } = new(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private const string GlobalScopeJson = "[{\"dimension\":\"Organization\",\"reference\":null}]";
}

public sealed record MeResponse(Guid Id, string Issuer, string Subject, string? Email, string? DisplayName,
    string Status, Guid? DepartmentId, string? JobTitle, int Version, IReadOnlyCollection<string> Roles);
public sealed record OrganizationResponse(Guid Id, string Code, string Name, string BaseCurrency,
    string TimeZoneId, int FiscalYearStartMonth, int Version, int DepartmentCount, int UserCount);
public sealed record DepartmentResponse(Guid Id, string Code, string Name, string Status, int Version);
public sealed record UserResponse(Guid Id, string? Issuer, string? Subject, string? Email, string? DisplayName,
    string Status, Guid? DepartmentId, string? DepartmentCode, string? JobTitle, int Version);
public sealed record RoleResponse(Guid Id, string Role, string ScopeJson, string Status, int Version);
public sealed record GrantResponse(Guid Id, Guid AuthorityLevelId, decimal? MaxAmountBase, string BaseCurrency,
    string ScopeJson, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string Status, int Version);
public sealed record AuditResponse(Guid Id, DateTimeOffset OccurredAt, string Action, string TargetType,
    Guid TargetId, int? PreviousVersion, int? NewVersion, string ScopeJson, string Reason,
    string CorrelationReference);
public sealed record UpdateOrganizationRequest(string? Name, string? TimeZoneId, int ExpectedVersion, string? Reason);
public sealed record LegalEntityResponse(Guid Id, string Code, string Name, string Status, int Version);
public sealed record CreateLegalEntityRequest(string? Code, string? Name, string? Reason, int ExpectedOrganizationVersion = 0);
public sealed record UpdateDepartmentRequest(string? Name, string? Status, int ExpectedVersion, string? Reason);
public sealed record CreateDepartmentRequest(string? Code, string? Name, string? Reason, int ExpectedOrganizationVersion = 0);
public sealed record CompleteSetupRequest(Guid DepartmentId, string? JobTitle, string? Reason, int ExpectedVersion = 0);
public sealed record AdministrativeReasonRequest(string? Reason, int ExpectedVersion = 0);
public sealed record AssignRoleRequest(
    string? Role,
    string? Reason,
    IReadOnlyCollection<ScopeInput>? Scopes,
    int ExpectedUserVersion = 0);
public sealed record AuthorityLevelResponse(Guid Id, string Type, string Code, int Rank, int Version);
public sealed record CreateAuthorityLevelRequest(string? Type, string? Code, int Rank, string? Reason);
public sealed record ScopeInput(string? Dimension, string? Reference);
public sealed record GrantAuthorityRequest(Guid AuthorityLevelId, decimal? MaxAmountBase, string? BaseCurrency,
    IReadOnlyCollection<ScopeInput>? Scopes, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, string? Reason,
    int ExpectedUserVersion = 0);
public sealed record AuthorityGrantResponse(Guid Id, Guid UserProfileId, Guid AuthorityLevelId, decimal? MaxAmountBase,
    string BaseCurrency, string ScopeJson, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, int Version);
