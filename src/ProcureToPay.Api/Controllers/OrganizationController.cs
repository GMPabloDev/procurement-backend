using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;

namespace ProcureToPay.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class OrganizationController(
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService,
    IOrganizationEligibilityService eligibilityService,
    ReferenceCatalogPersistenceService referenceCatalogs) : ControllerBase
{
    [HttpPost("eligibility")]
    public async Task<ActionResult<IReadOnlyCollection<EligibilityResponse>>> ResolveEligibility(
        ResolveEligibilityRequest request,
        CancellationToken cancellationToken)
    {
        _ = await RequireReadAccessAsync(cancellationToken);
        if (!OrganizationContractCodes.TryRole(request.RequiredRole, out var requiredRole))
        {
            throw new DomainValidationException("Required role is not recognized.");
        }
        var requiredScopeJson = await BuildScopeJsonAsync(request.RequiredScopes, cancellationToken);
        var requiredScope = ParseScopeSet(requiredScopeJson);
        await EnsureEligibilityScopeAsync(requiredScope, cancellationToken);
        var authority = request.Authority is null
            ? AuthorityRequirement.None
            : BuildAuthorityRequirement(request.Authority);
        if (authority.BaseCurrency is not null)
        {
            var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
            if (!string.Equals(authority.BaseCurrency, organization.BaseCurrency, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException("Eligibility authority currency must match the organization base currency.");
            }
        }
        var eligibilityRequest = EligibilityRequest.Create(requiredRole, requiredScope, authority,
            request.EvaluatedAt ?? DateTimeOffset.UtcNow, request.ExcludedUserIds);
        var candidates = await eligibilityService.ResolveAsync(eligibilityRequest, cancellationToken);
        return Ok(candidates.Select(candidate => new EligibilityResponse(
            candidate.User.Id, candidate.RoleAssignment.Id, candidate.AuthorityGrant?.Id,
            ToEvidenceResponse(candidate.Evidence))).ToArray());
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> GetMe(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        var roles = await dbContext.RoleAssignments
            .Where(assignment => assignment.UserProfileId == profile.Id &&
                                assignment.Status == (int)AssignmentStatus.Active)
            .Select(assignment => OrganizationContractCodes.Role((SystemRole)assignment.Role))
            .ToArrayAsync(cancellationToken);

        return Ok(new MeResponse(
            profile.Id,
            profile.Issuer,
            profile.Subject,
            profile.Email,
            profile.DisplayName,
            OrganizationContractCodes.Status((UserProfileStatus)profile.Status),
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
        var access = await RequireGlobalReadAccessAsync(cancellationToken);
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
                OrganizationContractCodes.Status((EntityStatus)entity.Status), entity.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(entities);
    }

    [HttpPatch("legal-entities/{entityId:guid}")]
    public async Task<ActionResult<LegalEntityResponse>> RenameLegalEntity(
        Guid entityId,
        UpdateLegalEntityRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var entity = await dbContext.LegalEntities.SingleOrDefaultAsync(
            item => item.Id == entityId, cancellationToken)
            ?? throw new DomainNotFoundException("Legal Entity was not found.");
        EnsureExpectedVersion(entity.Version, request.ExpectedVersion);
        var beforeJson = JsonSerializer.Serialize(new { entity.Name });
        entity.Name = Required(request.Name, nameof(request.Name));
        entity.Version++;
        AddAudit(actor, "LEGAL_ENTITY_RENAMED", "LegalEntity", entity.Id, request.Reason,
            JsonSerializer.Serialize(new { entity.Name }), entity.Version - 1, entity.Version,
            scopeJson: ScopeJsonFor(ScopeDimension.LegalEntity, entity.Code), beforeJson: beforeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new LegalEntityResponse(entity.Id, entity.Code, entity.Name,
            OrganizationContractCodes.Status((EntityStatus)entity.Status), entity.Version));
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
            Code = RequiredCode(request.Code, nameof(request.Code)).ToUpperInvariant(),
            Name = Required(request.Name, nameof(request.Name)),
            Status = (int)EntityStatus.Active, Version = 1
        };
        dbContext.LegalEntities.Add(entity);
        AddAudit(actor, "LEGAL_ENTITY_CREATED", "LegalEntity", entity.Id, request.Reason,
            JsonSerializer.Serialize(new { entity.Code, entity.Name }), scopeJson: ScopeJsonFor(ScopeDimension.LegalEntity, entity.Code));
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
                OrganizationContractCodes.Status((EntityStatus)department.Status),
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
        var code = RequiredCode(request.Code, nameof(request.Code)).ToUpperInvariant();
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
            JsonSerializer.Serialize(new { code, name }), scopeJson: ScopeJsonFor(ScopeDimension.Department, code));
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var department = await dbContext.Departments.SingleOrDefaultAsync(
            item => item.Id == departmentId, cancellationToken)
            ?? throw new DomainNotFoundException("Department was not found.");
        EnsureExpectedVersion(department.Version, request.ExpectedVersion);
        var beforeJson = JsonSerializer.Serialize(new { department.Name, department.Status });
        if (request.Name is not null)
        {
            department.Name = Required(request.Name, nameof(request.Name));
        }
        if (request.Status is not null)
        {
            if (!Enum.TryParse<EntityStatus>(request.Status, true, out var status) ||
                !Enum.IsDefined(status) || request.Status.Any(char.IsDigit))
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
                var referencedByCostCenter = await dbContext.CostCenterVersions
                    .Where(version => version.DepartmentId == department.Id)
                    .Join(
                        dbContext.CostCenters,
                        version => version.CostCenterId,
                        root => root.Id,
                        (version, root) => new { version, root })
                    .AnyAsync(
                        pair => pair.root.CurrentVersion == pair.version.Version &&
                                pair.version.Status == (int)EntityStatus.Active,
                        cancellationToken);
                if (userIds.Length > 0 || referencedByScope || referencedByCostCenter)
                {
                    throw new DomainConflictException("Department has active references and cannot be deactivated.");
                }
            }
            department.Status = (int)status;
        }
        department.Version++;
        AddAudit(actor, "DEPARTMENT_UPDATED", "Department", department.Id, request.Reason,
            JsonSerializer.Serialize(new { department.Name, status = OrganizationContractCodes.Status((EntityStatus)department.Status) }),
            department.Version - 1, department.Version,
            scopeJson: ScopeJsonFor(ScopeDimension.Department, department.Code), beforeJson: beforeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(ToResponse(department));
    }

    [HttpGet("authority-levels")]
    public async Task<ActionResult<IReadOnlyCollection<AuthorityLevelResponse>>> GetAuthorityLevels(
        CancellationToken cancellationToken)
    {
        _ = await RequireGlobalReadAccessAsync(cancellationToken);
        var levels = await dbContext.AuthorityLevels
            .Where(level => level.IsActive)
            .OrderBy(level => level.Type).ThenBy(level => level.Rank)
            .Select(level => new AuthorityLevelResponse(level.Id, OrganizationContractCodes.Authority((ApprovalAuthorityType)level.Type),
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        if (!OrganizationContractCodes.TryAuthority(request.Type, out var type))
        {
            throw new DomainValidationException("Authority type is not recognized.");
        }
        var code = RequiredCode(request.Code, nameof(request.Code)).ToUpperInvariant();
        var rank = request.Rank > 0 ? request.Rank : throw new DomainValidationException("Rank must be positive.");
        var previousLevel = await dbContext.AuthorityLevels
            .Where(item => item.Type == (int)type && item.Code == code && item.IsActive)
            .OrderByDescending(item => item.LevelVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousLevel is not null && request.ExpectedPreviousVersion != previousLevel.LevelVersion)
        {
            throw new DomainConflictException("The authority level version is stale.");
        }
        if (previousLevel is null && request.ExpectedPreviousVersion != 0)
        {
            throw new DomainConflictException("The authority level version is stale.");
        }

        var previousLevelBefore = previousLevel is null ? (object?)null : new
        {
            type = OrganizationContractCodes.Authority(type), previousLevel.Code,
            previousLevel.Rank, previousLevel.LevelVersion, previousLevel.IsActive
        };
        var previousLevelVersion = previousLevel?.LevelVersion;
        var rankAlreadyActive = await dbContext.AuthorityLevels.AnyAsync(
            item => item.Type == (int)type && item.Rank == rank && item.IsActive,
            cancellationToken);
        if (rankAlreadyActive && previousLevel is null)
        {
            throw new DomainConflictException("The authority rank is already active for this authority type.");
        }
        if (rankAlreadyActive && previousLevel is not null && previousLevel.Rank != rank)
        {
            throw new DomainConflictException("The authority rank is already active for this authority type.");
        }

        var version = (await dbContext.AuthorityLevels
            .Where(level => level.Type == (int)type && level.Code == code)
            .Select(level => (int?)level.LevelVersion)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        if (previousLevel is not null)
        {
            previousLevel.IsActive = false;
        }
        var level = new AuthorityLevelRecord
        {
            Id = Guid.NewGuid(), Type = (int)type, Code = code, Rank = rank,
            LevelVersion = version, IsActive = true
        };
        dbContext.AuthorityLevels.Add(level);
        AddAudit(actor, "AUTHORITY_LEVEL_VERSION_CREATED", "AuthorityLevel", level.Id, request.Reason,
            JsonSerializer.Serialize(new
            {
                type = OrganizationContractCodes.Authority(type), code, rank, version,
                after = new { active = true, type = OrganizationContractCodes.Authority(type), code, rank, version },
                subchanges = previousLevel is null ? [] : new[]
                {
                    new { targetType = "AuthorityLevel", targetId = previousLevel.Id,
                        previousVersion = previousLevel.LevelVersion, newVersion = previousLevel.LevelVersion,
                        before = previousLevelBefore,
                        after = new { previousLevel.Code, previousLevel.Rank,
                            previousLevel.LevelVersion, active = false }, retired = true }
                }
            }), previousVersion: previousLevelVersion, newVersion: level.LevelVersion,
            scopeJson: GlobalScopeJson,
            beforeJson: previousLevelBefore is null ? null : JsonSerializer.Serialize(previousLevelBefore));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Created($"/api/v1/authority-levels/{level.Id}",
            new AuthorityLevelResponse(level.Id, OrganizationContractCodes.Authority(type), level.Code, level.Rank, level.LevelVersion));
    }

    [HttpDelete("authority-levels/{levelId:guid}")]
    public async Task<IActionResult> RetireAuthorityLevel(
        Guid levelId,
        [FromBody] AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        var level = await dbContext.AuthorityLevels.SingleOrDefaultAsync(
            item => item.Id == levelId, cancellationToken)
            ?? throw new DomainNotFoundException("Authority level was not found.");
        EnsureExpectedVersion(level.LevelVersion, request.ExpectedVersion);
        if (!level.IsActive)
        {
            throw new DomainConflictException("Authority level is already retired.");
        }
        level.IsActive = false;
        AddAudit(actor, "AUTHORITY_LEVEL_RETIRED", "AuthorityLevel", level.Id, request.Reason,
            JsonSerializer.Serialize(new { level.Code, level.LevelVersion }),
            level.LevelVersion, level.LevelVersion, beforeJson: JsonSerializer.Serialize(new { IsActive = true }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
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
                access.IsGlobal ? user.Issuer : null,
                access.IsGlobal ? user.Subject : null,
                access.IsGlobal ? user.Email : null,
                access.IsGlobal ? user.DisplayName : null,
                OrganizationContractCodes.Status((UserProfileStatus)user.Status),
                user.DepartmentId,
                user.Department == null ? null : user.Department.Code,
                access.IsGlobal ? user.JobTitle : null,
                user.Version))
            .ToArrayAsync(cancellationToken);
        return Ok(await users);
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
            .Select(record => new AuditResponse(record.Id, record.OccurredAt, record.ActorUserId,
                record.Action, record.TargetType, record.TargetId, record.PreviousVersion,
                record.NewVersion, record.ScopeJson, record.BeforeJson, record.AfterJson ?? "{}",
                record.Reason, record.CorrelationReference))
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
        var roleRecords = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == userId)
            .OrderBy(item => item.Role).ThenBy(item => item.AssignedAt)
            .ToArrayAsync(cancellationToken);
        var roles = roleRecords
            .Where(item => access.IsGlobal || ScopeMatchesReadAccess(item.ScopeJson, access))
            .Select(item => new RoleResponse(item.Id, OrganizationContractCodes.Role((SystemRole)item.Role),
                item.ScopeJson, OrganizationContractCodes.Status((AssignmentStatus)item.Status), item.Version))
            .ToArray();
        return Ok(roles);
    }

    [HttpGet("users/{userId:guid}/grants")]
    public async Task<ActionResult<IReadOnlyCollection<GrantResponse>>> GetGrants(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        await EnsureUserVisibleAsync(userId, access, cancellationToken);
        var grantRecords = await dbContext.AuthorityGrants
            .Where(item => item.UserProfileId == userId)
            .OrderBy(item => item.GrantedAt)
            .ToArrayAsync(cancellationToken);
        var grants = grantRecords
            .Where(item => access.IsGlobal || ScopeMatchesReadAccess(item.ScopeJson, access))
            .Select(item => new GrantResponse(item.Id, item.AuthorityLevelId, item.MaxAmountBase,
                item.BaseCurrency, item.ScopeJson, item.ValidFrom, item.ValidTo,
                OrganizationContractCodes.Status((GrantStatus)item.Status), item.Version))
            .ToArray();
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
            user.Version - 1, user.Version,
            scopeJson: ScopeJsonFor(ScopeDimension.Department, department.Code), beforeJson: beforeJson);
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
            throw new DomainException("User requires an active Department and Job Title before activation.");
        }
        var beforeJson = JsonSerializer.Serialize(new { user.Status, user.DepartmentId, user.JobTitle });
        user.Status = (int)UserProfileStatus.Active;
        user.Version++;
        AddAudit(actor, "USER_ACTIVATED", "UserProfile", user.Id, request.Reason,
            JsonSerializer.Serialize(new { user.Status }), user.Version - 1, user.Version,
            scopeJson: ScopeJsonFor(ScopeDimension.Department, user.Department!.Code), beforeJson: beforeJson);
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
        var beforeUserJson = JsonSerializer.Serialize(new
        {
            status = OrganizationContractCodes.Status((UserProfileStatus)user.Status),
            user.DepartmentId, user.JobTitle
        });
        var assignments = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == user.Id && item.Status == (int)AssignmentStatus.Active)
            .OrderBy(item => item.Id)
            .ToArrayAsync(cancellationToken);
        var grants = await dbContext.AuthorityGrants
            .Where(item => item.UserProfileId == user.Id && item.Status == (int)GrantStatus.Active)
            .OrderBy(item => item.Id)
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
        var subchanges = assignments.Select(item => (object)new
            {
                targetType = "RoleAssignment", targetId = item.Id,
                previousVersion = item.Version - 1, newVersion = item.Version,
                before = new
                {
                    status = OrganizationContractCodes.Status(AssignmentStatus.Active),
                    scope = item.ScopeJson, version = item.Version - 1
                },
                after = new
                {
                    status = OrganizationContractCodes.Status(AssignmentStatus.Revoked),
                    scope = item.ScopeJson, version = item.Version
                }
            })
            .Concat(grants.Select(item => (object)new
            {
                targetType = "AuthorityGrant", targetId = item.Id,
                previousVersion = item.Version - 1, newVersion = item.Version,
                before = new
                {
                    status = OrganizationContractCodes.Status(GrantStatus.Active),
                    scope = item.ScopeJson, maxAmountBase = item.MaxAmountBase,
                    baseCurrency = item.BaseCurrency, version = item.Version - 1
                },
                after = new
                {
                    status = OrganizationContractCodes.Status(GrantStatus.Revoked),
                    scope = item.ScopeJson, maxAmountBase = item.MaxAmountBase,
                    baseCurrency = item.BaseCurrency, version = item.Version
                }
            }))
            .ToArray();
        var affectedScope = MergeAffectedScopes(
            assignments.Select(item => item.ScopeJson)
                .Concat(grants.Select(item => item.ScopeJson))
                .Append(user.Department?.Code is { } departmentCode
                    ? ScopeJsonFor(ScopeDimension.Department, departmentCode)
                    : null));
        AddAudit(actor, "USER_DEACTIVATED", "UserProfile", user.Id, request.Reason,
            JsonSerializer.Serialize(new
            {
                status = OrganizationContractCodes.Status(UserProfileStatus.Inactive),
                assignmentsRevoked = assignments.Length, grantsRevoked = grants.Length, subchanges
            }),
            user.Version - 1, user.Version, scopeJson: affectedScope, beforeJson: beforeUserJson);
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
        AddAudit(actor, "USER_RETURNED_TO_SETUP", "UserProfile", user.Id, request.Reason,
            JsonSerializer.Serialize(new { user.Status }), user.Version - 1, user.Version,
            scopeJson: user.Department?.Code is { } departmentCode
                ? ScopeJsonFor(ScopeDimension.Department, departmentCode)
                : null,
            beforeJson: JsonSerializer.Serialize(new { Status = UserProfileStatus.Inactive }));
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
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
        var validFrom = (request.ValidFrom ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var validTo = request.ValidTo?.ToUniversalTime();
        var grant = new AuthorityGrantRecord
        {
            Id = Guid.NewGuid(), UserProfileId = user.Id, AuthorityLevelId = level.Id,
            MaxAmountBase = request.MaxAmountBase, BaseCurrency = currency,
            ScopeJson = scopeJson, ValidFrom = validFrom,
            ValidTo = validTo, Status = (int)GrantStatus.Active,
            GrantedAt = DateTimeOffset.UtcNow, GrantedBy = actor.Id, Version = 1
        };
        if (grant.ValidTo is not null && grant.ValidTo <= grant.ValidFrom)
        {
            throw new DomainValidationException("ValidTo must be after ValidFrom.");
        }
        dbContext.AuthorityGrants.Add(grant);
        AddAudit(actor, "AUTHORITY_GRANTED", "AuthorityGrant", grant.Id, request.Reason,
            JsonSerializer.Serialize(new
            {
                userId = user.Id, authorityLevelId = level.Id, grant.MaxAmountBase,
                grant.BaseCurrency, grant.ScopeJson, grant.ValidFrom, grant.ValidTo,
                grant.Version
            }), previousVersion: null, newVersion: grant.Version,
            scopeJson: scopeJson, beforeJson: null);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        AddAudit(actor, "AUTHORITY_REVOKED", "AuthorityGrant", grant.Id, request.Reason,
            JsonSerializer.Serialize(new { status = GrantStatus.Revoked, grant.RevokedAt }),
            grant.Version - 1, grant.Version, grant.ScopeJson,
            JsonSerializer.Serialize(new { status = GrantStatus.Active }));
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        if (!OrganizationContractCodes.TryRole(request.Role, out var role))
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
            JsonSerializer.Serialize(new { userId = user.Id, role = OrganizationContractCodes.Role(role) }),
            null, assignment.Version, scopeJson);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("users/{userId:guid}/roles/{assignmentId:guid}")]
    public async Task<IActionResult> RevokeRole(
        Guid userId,
        Guid assignmentId,
        [FromBody] AdministrativeReasonRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await provisioningService.RequireRoleAsync(User, SystemRole.Admin, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var assignment = await dbContext.RoleAssignments.SingleOrDefaultAsync(
            item => item.Id == assignmentId && item.UserProfileId == userId &&
                    item.Status == (int)AssignmentStatus.Active,
            cancellationToken)
            ?? throw new DomainNotFoundException("Active role assignment was not found.");
        EnsureExpectedVersion(assignment.Version, request.ExpectedVersion);
        if (assignment.Role == (int)SystemRole.Admin && await IsLastAdministratorAsync(userId, cancellationToken))
        {
            throw new DomainConflictException("The last active administrator role cannot be revoked.");
        }
        assignment.Status = (int)AssignmentStatus.Revoked;
        assignment.RevokedAt = DateTimeOffset.UtcNow;
        assignment.RevokedBy = actor.Id;
        assignment.Version++;
        AddAudit(actor, "ROLE_REVOKED", "RoleAssignment", assignment.Id, request.Reason,
            JsonSerializer.Serialize(new { status = AssignmentStatus.Revoked, assignment.RevokedAt }),
            assignment.Version - 1, assignment.Version,
            assignment.ScopeJson,
            JsonSerializer.Serialize(new { status = AssignmentStatus.Active }));
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

    private async Task EnsureEligibilityScopeAsync(
        AuthorizationScopeSet requiredScope,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        var assignments = await dbContext.RoleAssignments
            .Where(item => item.UserProfileId == profile.Id && item.Status == (int)AssignmentStatus.Active &&
                          (item.Role == (int)SystemRole.Admin || item.Role == (int)SystemRole.Auditor))
            .Select(item => new { item.Role, item.ScopeJson })
            .ToArrayAsync(cancellationToken);
        if (assignments.Any(item => item.Role == (int)SystemRole.Admin && item.ScopeJson == GlobalScopeJson) ||
            assignments.Where(item => item.Role == (int)SystemRole.Auditor)
                .Any(item => ParseScopeSet(item.ScopeJson).Covers(requiredScope)))
        {
            return;
        }
        throw new DomainForbiddenException("The requested eligibility scope is outside the reader assignment.");
    }

    private async Task<ReadScope> RequireGlobalReadAccessAsync(CancellationToken cancellationToken)
    {
        var access = await RequireReadAccessAsync(cancellationToken);
        if (!access.IsGlobal)
        {
            throw new DomainForbiddenException("Global organization access is required.");
        }
        return access;
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
        var scopeSets = new List<AuthorizationScopeSet>();
        foreach (var assignment in assignments.Where(item => item.Role == (int)SystemRole.Auditor))
        {
            ScopeEntry[] entries;
            try
            {
                entries = JsonSerializer.Deserialize<ScopeEntry[]>(assignment.ScopeJson, ScopeJsonOptions) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }
            var scopeSet = ParseScopeSet(assignment.ScopeJson);
            scopeSets.Add(scopeSet);
            if (entries.Any(entry => string.Equals(entry.Dimension, "Organization", StringComparison.OrdinalIgnoreCase)))
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
        return new ReadScope(false, departments, legalEntities, scopeSets);
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
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim().Length > 160
                ? throw new DomainValidationException($"{field} must contain at most 160 characters.")
                : value.Trim();

    private static string RequiredCode(string? value, string field)
    {
        var normalized = Required(value, field).ToUpperInvariant();
        if (normalized.Length > 80)
        {
            throw new DomainValidationException($"{field} must contain at most 80 characters.");
        }
        return normalized;
    }

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

    private static AuthorityRequirement BuildAuthorityRequirement(AuthorityInput input)
    {
        if (!OrganizationContractCodes.TryAuthority(input.Type, out var type))
        {
            throw new DomainValidationException("Authority type is not recognized.");
        }
        return AuthorityRequirement.Required(type, input.MinimumRank, input.AmountBase, input.BaseCurrency);
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
        var organizationId = await dbContext.Organizations.Select(item => item.Id).SingleAsync(cancellationToken);
        foreach (var input in inputs)
        {
            if (!OrganizationContractCodes.TryScope(input.Dimension, out var dimension))
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
                reference = RequiredCode(reference, nameof(input.Reference));
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
                    ScopeDimension.CostCenter => (await referenceCatalogs.FindActiveCostCenterByCodeAsync(
                        reference, organizationId, cancellationToken))?.Code,
                    _ => null
                } ?? throw new DomainValidationException("The scoped reference is not active.");
                scopes.Add(AuthorizationScope.For(dimension, reference));
            }
            serialized.Add(new { dimension = OrganizationContractCodes.Scope(dimension), reference });
        }

        _ = AuthorizationScopeSet.Create(scopes);
        return JsonSerializer.Serialize(serialized);
    }

    private static string? MergeAffectedScopes(IEnumerable<string?> scopeJsons)
    {
        var scopes = scopeJsons
            .Where(json => json is not null)
            .SelectMany(json => JsonSerializer.Deserialize<ScopeInput[]>(json!, ScopeJsonOptions) ?? [])
            .Select(entry => new ScopeInput(entry.Dimension?.ToUpperInvariant() ?? string.Empty, entry.Reference))
            .Distinct()
            .OrderBy(entry => entry.Dimension)
            .ThenBy(entry => entry.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scopes.Length == 0 ? null : JsonSerializer.Serialize(scopes);
    }

    private static string ScopeJsonFor(ScopeDimension dimension, string reference) =>
        JsonSerializer.Serialize(new[] { new ScopeInput(OrganizationContractCodes.Scope(dimension), reference) });

    private static bool ScopeMatchesReadAccess(string scopeJson, ReadScope access)
    {
        try
        {
            var target = ParseScopeSet(scopeJson);
            return access.ScopeSets.Any(scope => scope.Covers(target));
        }
        catch (DomainValidationException)
        {
            return false;
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
            return (JsonSerializer.Deserialize<ScopeInput[]>(scopeJson, ScopeJsonOptions) ?? [])
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
        var entries = JsonSerializer.Deserialize<ScopeInput[]>(scopeJson, ScopeJsonOptions)
            ?? throw new DomainValidationException("Stored scope is invalid.");
        return AuthorizationScopeSet.Create(entries.Select(entry =>
            OrganizationContractCodes.TryScope(entry.Dimension, out var dimension)
                ? dimension == ScopeDimension.Organization
                    ? AuthorizationScope.Global()
                    : AuthorizationScope.For(dimension, Required(entry.Reference, nameof(entry.Reference)))
                : throw new DomainValidationException("Stored scope dimension is invalid.")));
    }

    private static OrganizationResponse ToResponse(OrganizationRecord organization) =>
        new(organization.Id, organization.Code, organization.Name, organization.BaseCurrency,
            organization.TimeZoneId, organization.FiscalYearStartMonth, organization.Version, 0, 0);

    private static DepartmentResponse ToResponse(DepartmentRecord department) =>
        new(department.Id, department.Code, department.Name, OrganizationContractCodes.Status((EntityStatus)department.Status), department.Version);

    private static UserResponse ToResponse(UserProfileRecord user) =>
        new(user.Id, user.Issuer, user.Subject, user.Email, user.DisplayName,
            OrganizationContractCodes.Status((UserProfileStatus)user.Status), user.DepartmentId, user.Department?.Code,
            user.JobTitle, user.Version);

    private static EligibilityEvidenceResponse ToEvidenceResponse(EligibilityEvidence evidence) => new(
        evidence.UserId, evidence.UserProfileVersion, evidence.RoleAssignmentId,
        evidence.RoleAssignmentVersion, evidence.AuthorityGrantId, evidence.AuthorityGrantVersion,
        evidence.AuthorityLevelId, evidence.AuthorityLevelVersion,
        OrganizationContractCodes.Role(evidence.RequiredRole), evidence.RequiredScopeSnapshot,
        new AuthorityRequirementResponse(
            evidence.AuthorityRequirement.Kind == AuthorityRequirementKind.None ? "NONE" : "REQUIRED",
            evidence.AuthorityRequirement.Type is { } type ? OrganizationContractCodes.Authority(type) : null,
            evidence.AuthorityRequirement.MinimumRank, evidence.AuthorityRequirement.AmountBase,
            evidence.AuthorityRequirement.BaseCurrency), evidence.EvaluatedAt,
        evidence.AssignmentScopeSnapshot, evidence.AssignmentAssignedAt, evidence.AssignmentRevokedAt,
        evidence.AuthorityGrantScopeSnapshot, evidence.AuthorityGrantMaxAmountBase,
        evidence.AuthorityGrantBaseCurrency, evidence.AuthorityGrantValidFrom,
        evidence.AuthorityGrantValidTo, evidence.AuthorityLevelCode, evidence.AuthorityLevelRank,
        evidence.ExcludedUserIds.ToArray());

    private sealed record ScopeEntry(string Dimension, string? Reference);

    private sealed record ReadScope(
        bool IsGlobal,
        IReadOnlySet<string> Departments,
        IReadOnlySet<string> LegalEntities,
        IReadOnlyCollection<AuthorizationScopeSet> ScopeSets)
    {
        public static ReadScope Global { get; } = new(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [AuthorizationScopeSet.Create([AuthorizationScope.Global()])]);
    }

    private static readonly JsonSerializerOptions ScopeJsonOptions = new(JsonSerializerDefaults.Web);

    private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";
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
public sealed record AuditResponse(Guid Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string Action,
    string TargetType, Guid TargetId, int? PreviousVersion, int? NewVersion, string ScopeJson,
    string? BeforeJson, string AfterJson, string Reason, string CorrelationReference);
public sealed record UpdateOrganizationRequest(string? Name, string? TimeZoneId, int ExpectedVersion, string? Reason);
public sealed record LegalEntityResponse(Guid Id, string Code, string Name, string Status, int Version);
public sealed record CreateLegalEntityRequest(string? Code, string? Name, string? Reason, int ExpectedOrganizationVersion = 0);
public sealed record UpdateLegalEntityRequest(string? Name, int ExpectedVersion, string? Reason);
public sealed record ResolveEligibilityRequest(string RequiredRole, IReadOnlyCollection<ScopeInput>? RequiredScopes,
    AuthorityInput? Authority, DateTimeOffset? EvaluatedAt, IReadOnlyCollection<Guid>? ExcludedUserIds);
public sealed record AuthorityInput(string Type, int MinimumRank, decimal? AmountBase, string? BaseCurrency);
public sealed record EligibilityResponse(Guid UserId, Guid RoleAssignmentId, Guid? AuthorityGrantId,
    EligibilityEvidenceResponse Evidence);
public sealed record AuthorityRequirementResponse(string Kind, string? Type, int? MinimumRank,
    decimal? AmountBase, string? BaseCurrency);
public sealed record EligibilityEvidenceResponse(Guid UserId, int UserProfileVersion, Guid RoleAssignmentId,
    int RoleAssignmentVersion, Guid? AuthorityGrantId, int? AuthorityGrantVersion, Guid? AuthorityLevelId,
    int? AuthorityLevelVersion, string RequiredRole, string RequiredScopeSnapshot,
    AuthorityRequirementResponse AuthorityRequirement, DateTimeOffset EvaluatedAt,
    string AssignmentScopeSnapshot, DateTimeOffset AssignmentAssignedAt, DateTimeOffset? AssignmentRevokedAt,
    string? AuthorityGrantScopeSnapshot, decimal? AuthorityGrantMaxAmountBase, string? AuthorityGrantBaseCurrency,
    DateTimeOffset? AuthorityGrantValidFrom, DateTimeOffset? AuthorityGrantValidTo, string? AuthorityLevelCode,
    int? AuthorityLevelRank, IReadOnlyCollection<Guid> ExcludedUserIds);
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
public sealed record CreateAuthorityLevelRequest(
    string? Type, string? Code, int Rank, string? Reason, int ExpectedPreviousVersion = 0);
public sealed record ScopeInput(string? Dimension, string? Reference);
public sealed record GrantAuthorityRequest(Guid AuthorityLevelId, decimal? MaxAmountBase, string? BaseCurrency,
    IReadOnlyCollection<ScopeInput>? Scopes, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, string? Reason,
    int ExpectedUserVersion = 0);
public sealed record AuthorityGrantResponse(Guid Id, Guid UserProfileId, Guid AuthorityLevelId, decimal? MaxAmountBase,
    string BaseCurrency, string ScopeJson, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, int Version);
