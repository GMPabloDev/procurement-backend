using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;

namespace ProcureToPay.Infrastructure.Persistence.Organization;

public sealed class CurrentUserProvisioningService(ProcureToPayDbContext dbContext)
{
    public async Task<UserProfileRecord> EnsureProfileAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var issuer = FindClaim(principal, "iss");
        var subject = FindClaim(principal, "sub");
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            throw new DomainValidationException("The authenticated token must contain issuer and subject.");
        }

        var organization = await dbContext.Organizations.SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainValidationException("Organization bootstrap has not completed.");
        var profile = await dbContext.UserProfiles.SingleOrDefaultAsync(
            user => user.Issuer == issuer && user.Subject == subject,
            cancellationToken);

        var email = FindClaim(principal, ClaimTypes.Email) ?? FindClaim(principal, "email");
        var displayName = FindClaim(principal, ClaimTypes.Name) ?? FindClaim(principal, "name");
        if (profile is null)
        {
            profile = new UserProfileRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                Issuer = issuer.Trim(),
                Subject = subject.Trim(),
                Email = Normalize(email),
                DisplayName = Normalize(displayName),
                Status = (int)UserProfileStatus.PendingSetup,
                Version = 1
            };
            dbContext.UserProfiles.Add(profile);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(profile).State = EntityState.Detached;
                profile = await dbContext.UserProfiles.SingleAsync(
                    user => user.Issuer == issuer && user.Subject == subject,
                    cancellationToken);
            }

            return profile;
        }

        if (!string.Equals(profile.Email, Normalize(email), StringComparison.Ordinal) ||
            !string.Equals(profile.DisplayName, Normalize(displayName), StringComparison.Ordinal))
        {
            profile.Email = Normalize(email);
            profile.DisplayName = Normalize(displayName);
            profile.Version++;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return profile;
    }

    public async Task<UserProfileRecord> RequireRoleAsync(
        ClaimsPrincipal principal,
        SystemRole role,
        CancellationToken cancellationToken = default)
    {
        var profile = await EnsureProfileAsync(principal, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var hasRole = await dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Role == (int)role &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson,
            cancellationToken);
        if (!hasRole)
        {
            throw new DomainForbiddenException("The user does not have the required active role.");
        }

        return profile;
    }

    private static string? FindClaim(ClaimsPrincipal principal, string type) =>
        principal.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string GlobalScopeJson = "[{\"dimension\":\"Organization\",\"reference\":null}]";
}
