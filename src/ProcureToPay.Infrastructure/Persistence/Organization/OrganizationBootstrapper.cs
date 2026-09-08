using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
// pi-lens-ignore: lsp:CS0234
using OrganizationEntity = ProcureToPay.Domain.Modules.Organization.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Organization;

public sealed record OrganizationBootstrapOptions(
    string OrganizationCode,
    string OrganizationName,
    string BaseCurrency,
    string TimeZoneId,
    int FiscalYearStartMonth,
    string LegalEntityCode,
    string LegalEntityName,
    string InitialDepartmentCode,
    string InitialDepartmentName,
    string AdminIssuer,
    string AdminSubject,
    string Reason);

public sealed class OrganizationBootstrapper(
    ProcureToPayDbContext dbContext,
    ILogger<OrganizationBootstrapper> logger)
{
    private static readonly Guid SystemActorId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    public async Task<bool> InitializeAsync(
        OrganizationBootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var fingerprint = ComputeFingerprint(options);
        var state = await dbContext.BootstrapStates.SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
        {
            if (!string.Equals(state.ConfigurationFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Organization bootstrap configuration conflicts with the completed bootstrap.");
            }

            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (await dbContext.Organizations.AnyAsync(cancellationToken) ||
            await dbContext.LegalEntities.AnyAsync(cancellationToken) ||
            await dbContext.Departments.AnyAsync(cancellationToken) ||
            await dbContext.UserProfiles.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Organization data exists without a bootstrap marker.");
        }

        var now = DateTimeOffset.UtcNow;
        var organization = new OrganizationEntity(
            Guid.NewGuid(),
            options.OrganizationCode,
            options.OrganizationName,
            options.BaseCurrency,
            options.TimeZoneId,
            options.FiscalYearStartMonth);
        var legalEntity = new LegalEntity(
            Guid.NewGuid(),
            organization.Id,
            options.LegalEntityCode,
            options.LegalEntityName);
        var department = new Department(
            Guid.NewGuid(),
            options.InitialDepartmentCode,
            options.InitialDepartmentName);
        var admin = new UserProfile(
            Guid.NewGuid(),
            options.AdminIssuer,
            options.AdminSubject,
            null,
            null);
        admin.CompleteSetup(department.Id, "System Administrator");
        admin.Activate(true);

        dbContext.Organizations.Add(ToRecord(organization));
        dbContext.LegalEntities.Add(ToRecord(legalEntity));
        dbContext.Departments.Add(ToRecord(department, organization.Id));
        dbContext.UserProfiles.Add(ToRecord(admin, organization.Id));
        var adminAssignmentId = Guid.NewGuid();
        dbContext.RoleAssignments.Add(new RoleAssignmentRecord
        {
            Id = adminAssignmentId,
            UserProfileId = admin.Id,
            Role = (int)SystemRole.Admin,
            ScopeJson = GlobalScopeJson,
            Status = (int)AssignmentStatus.Active,
            AssignedAt = now,
            AssignedBy = SystemActorId,
            Version = 1
        });
        dbContext.AdministrativeAuditRecords.Add(CreateAudit(
            now,
            "BOOTSTRAP_COMPLETED",
            "Organization",
            organization.Id,
            options.Reason,
            GlobalScopeJson,
            $"{{\"subchanges\":[{{\"targetType\":\"Organization\",\"targetId\":\"{organization.Id}\",\"newVersion\":1}},{{\"targetType\":\"LegalEntity\",\"targetId\":\"{legalEntity.Id}\",\"newVersion\":1}},{{\"targetType\":\"Department\",\"targetId\":\"{department.Id}\",\"newVersion\":1}},{{\"targetType\":\"UserProfile\",\"targetId\":\"{admin.Id}\",\"newVersion\":1}},{{\"targetType\":\"RoleAssignment\",\"targetId\":\"{adminAssignmentId}\",\"newVersion\":1}}]}}"));
        dbContext.BootstrapStates.Add(new BootstrapStateRecord
        {
            ConfigurationFingerprint = fingerprint,
            CompletedAt = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Organization bootstrap completed with correlation {CorrelationReference}.", organization.Id);
        return true;
    }

    public async Task<Guid> RecoverAdministratorAsync(
        OrganizationBootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (await dbContext.BootstrapStates.SingleOrDefaultAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("Administrator recovery requires a completed bootstrap.");
        }

        var organization = await dbContext.Organizations.SingleAsync(cancellationToken);
        var department = await dbContext.Departments
            .Where(department => department.Status == (int)EntityStatus.Active)
            .OrderBy(department => department.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Administrator recovery requires an active Department.");
        var existing = await dbContext.UserProfiles.SingleOrDefaultAsync(
            user => user.Issuer == options.AdminIssuer && user.Subject == options.AdminSubject,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var previousAdminVersion = existing?.Version;
        var beforeAdminJson = existing is null ? null :
            $"{{\"status\":\"{((UserProfileStatus)existing.Status)}\",\"departmentId\":\"{existing.DepartmentId}\",\"jobTitle\":\"{existing.JobTitle}\"}}";

        UserProfileRecord admin;
        if (existing is null)
        {
            admin = new UserProfileRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                Issuer = options.AdminIssuer,
                Subject = options.AdminSubject,
                DepartmentId = department.Id,
                JobTitle = "System Administrator",
                Status = (int)UserProfileStatus.Active,
                Version = 1
            };
            dbContext.UserProfiles.Add(admin);
        }
        else
        {
            admin = existing;
            admin.Status = (int)UserProfileStatus.Active;
            admin.DepartmentId = department.Id;
            admin.JobTitle = string.IsNullOrWhiteSpace(admin.JobTitle)
                ? "System Administrator"
                : admin.JobTitle;
            admin.Version++;
        }

        var adminAssignment = await dbContext.RoleAssignments.SingleOrDefaultAsync(
            assignment => assignment.UserProfileId == admin.Id &&
                          assignment.Role == (int)SystemRole.Admin &&
                          assignment.Status == (int)AssignmentStatus.Active,
            cancellationToken);
        var adminAssignmentCreated = adminAssignment is null;
        if (adminAssignmentCreated)
        {
            adminAssignment = new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = admin.Id,
                Role = (int)SystemRole.Admin,
                ScopeJson = GlobalScopeJson,
                Status = (int)AssignmentStatus.Active,
                AssignedAt = now,
                AssignedBy = SystemActorId,
                Version = 1
            };
            dbContext.RoleAssignments.Add(adminAssignment);
        }

        var assignmentForAudit = adminAssignment!;
        dbContext.AdministrativeAuditRecords.Add(CreateAudit(
            now,
            "ADMINISTRATOR_RECOVERED",
            "UserProfile",
            admin.Id,
            options.Reason,
            GlobalScopeJson,
            $"{{\"subchanges\":[{{\"targetType\":\"UserProfile\",\"targetId\":\"{admin.Id}\",\"previousVersion\":{previousAdminVersion?.ToString() ?? "null"},\"newVersion\":{admin.Version}}},{{\"targetType\":\"RoleAssignment\",\"targetId\":\"{assignmentForAudit.Id}\",\"previousVersion\":{(adminAssignmentCreated ? "null" : (assignmentForAudit.Version - 1).ToString())},\"newVersion\":{assignmentForAudit.Version},\"created\":{adminAssignmentCreated.ToString().ToLowerInvariant()}}}]}}",
            previousAdminVersion, admin.Version, beforeAdminJson));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning("Administrator recovery completed for an explicitly configured identity.");
        return admin.Id;
    }

    private static OrganizationRecord ToRecord(OrganizationEntity organization) => new()
    {
        Id = organization.Id,
        Code = organization.Code,
        Name = organization.Name,
        BaseCurrency = organization.BaseCurrency,
        TimeZoneId = organization.TimeZoneId,
        FiscalYearStartMonth = organization.FiscalYearStartMonth,
        SingletonKey = 1,
        Version = organization.Version
    };

    private static LegalEntityRecord ToRecord(LegalEntity legalEntity) => new()
    {
        Id = legalEntity.Id,
        OrganizationId = legalEntity.OrganizationId,
        Code = legalEntity.Code,
        Name = legalEntity.Name,
        Status = (int)legalEntity.Status,
        Version = legalEntity.Version
    };

    private static DepartmentRecord ToRecord(Department department, Guid organizationId) => new()
    {
        Id = department.Id,
        OrganizationId = organizationId,
        Code = department.Code,
        Name = department.Name,
        Status = (int)department.Status,
        Version = department.Version
    };

    private static UserProfileRecord ToRecord(UserProfile user, Guid organizationId) => new()
    {
        Id = user.Id,
        OrganizationId = organizationId,
        Issuer = user.Issuer,
        Subject = user.Subject,
        Email = user.Email,
        DisplayName = user.DisplayName,
        DepartmentId = user.DepartmentId,
        JobTitle = user.JobTitle,
        Status = (int)user.Status,
        Version = user.Version
    };

    private static AdministrativeAuditRecord CreateAudit(
        DateTimeOffset occurredAt,
        string action,
        string targetType,
        Guid targetId,
        string reason,
        string scopeJson,
        string afterJson,
        int? previousVersion = null,
        int? newVersion = 1,
        string? beforeJson = null) => new()
        {
            Id = Guid.NewGuid(),
            ActorType = "SYSTEM",
            ActorUserId = null,
            OccurredAt = occurredAt,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            PreviousVersion = previousVersion,
            NewVersion = newVersion,
            ScopeJson = scopeJson,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            CorrelationReference = targetId.ToString("N")
        };

    private static void ValidateOptions(OrganizationBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.AdminIssuer) ||
            string.IsNullOrWhiteSpace(options.AdminSubject) ||
            string.IsNullOrWhiteSpace(options.Reason))
        {
            throw new ArgumentException("Bootstrap identity and reason are required.", nameof(options));
        }
    }

    private static string ComputeFingerprint(OrganizationBootstrapOptions options)
    {
        var payload = JsonSerializer.Serialize(options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private const string GlobalScopeJson = "[{\"dimension\":\"Organization\",\"reference\":null}]";
}
