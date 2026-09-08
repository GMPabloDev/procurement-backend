using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;

namespace ProcureToPay.Infrastructure.Persistence.Organization;

public sealed class OrganizationEligibilityService(ProcureToPayDbContext dbContext)
{
    public async Task<IReadOnlyList<EligibleCandidate>> ResolveAsync(
        EligibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        var users = (await dbContext.UserProfiles
            .Where(user => user.Status == (int)UserProfileStatus.Active)
            .ToArrayAsync(cancellationToken)).Select(ToDomain).ToArray();
        var assignments = (await dbContext.RoleAssignments
            .Where(assignment => assignment.Status == (int)AssignmentStatus.Active)
            .ToArrayAsync(cancellationToken)).Select(ToDomain).ToArray();
        var grants = (await dbContext.AuthorityGrants
            .Include(grant => grant.AuthorityLevel)
            .Where(grant => grant.Status == (int)GrantStatus.Active)
            .ToArrayAsync(cancellationToken)).Select(ToDomain).ToArray();
        return EligibilityResolver.Resolve(users, assignments, grants, request);
    }

    private static UserProfile ToDomain(UserProfileRecord record)
    {
        var user = new UserProfile(record.Id, record.Issuer, record.Subject, record.Email, record.DisplayName);
        user.CompleteSetup(record.DepartmentId ?? Guid.NewGuid(), record.JobTitle ?? "Configured user");
        user.Activate(true);
        return user;
    }

    private static RoleAssignment ToDomain(RoleAssignmentRecord record) => RoleAssignment.Create(
        record.Id, record.UserProfileId, (SystemRole)record.Role, ParseScope(record.ScopeJson),
        record.AssignedAt, record.AssignedBy);

    private static ApprovalAuthorityGrant ToDomain(AuthorityGrantRecord record)
    {
        var level = new AuthorityLevelVersion(
            record.AuthorityLevel.Id,
            (ApprovalAuthorityType)record.AuthorityLevel.Type,
            record.AuthorityLevel.Code,
            record.AuthorityLevel.Rank,
            record.AuthorityLevel.LevelVersion,
            record.AuthorityLevel.IsActive);
        return ApprovalAuthorityGrant.Create(
            record.Id, record.UserProfileId, level, record.MaxAmountBase, record.BaseCurrency,
            ParseScope(record.ScopeJson), record.ValidFrom, record.ValidTo, record.GrantedAt, record.GrantedBy);
    }

    private static AuthorizationScopeSet ParseScope(string json)
    {
        var entries = JsonSerializer.Deserialize<ScopeEntry[]>(json)
            ?? throw new InvalidOperationException("Stored authorization scope is invalid.");
        return AuthorizationScopeSet.Create(entries.Select(entry =>
        {
            if (!Enum.TryParse<ScopeDimension>(entry.Dimension, true, out var dimension) ||
                !Enum.IsDefined(dimension))
            {
                throw new InvalidOperationException("Stored authorization scope dimension is invalid.");
            }
            return dimension == ScopeDimension.Organization
                ? AuthorizationScope.Global()
                : AuthorizationScope.For(dimension, entry.Reference
                    ?? throw new InvalidOperationException("Stored authorization scope reference is missing."));
        }));
    }

    private sealed record ScopeEntry(string? Dimension, string? Reference);
}
