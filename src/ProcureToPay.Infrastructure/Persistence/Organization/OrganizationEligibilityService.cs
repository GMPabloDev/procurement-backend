using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Infrastructure.Persistence;

namespace ProcureToPay.Infrastructure.Persistence.Organization;

public sealed class OrganizationEligibilityService(ProcureToPayDbContext dbContext) : IOrganizationEligibilityService
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
        return UserProfile.Restore(record.Id, record.Issuer, record.Subject, record.Email, record.DisplayName,
            record.DepartmentId, record.JobTitle, (UserProfileStatus)record.Status, record.Version);
    }

    private static RoleAssignment ToDomain(RoleAssignmentRecord record) => RoleAssignment.Restore(
        record.Id, record.UserProfileId, (SystemRole)record.Role, ParseScope(record.ScopeJson),
        record.AssignedAt, record.AssignedBy, (AssignmentStatus)record.Status, record.Version,
        record.RevokedAt, record.RevokedBy);

    private static ApprovalAuthorityGrant ToDomain(AuthorityGrantRecord record)
    {
        var level = new AuthorityLevelVersion(
            record.AuthorityLevel.Id,
            (ApprovalAuthorityType)record.AuthorityLevel.Type,
            record.AuthorityLevel.Code,
            record.AuthorityLevel.Rank,
            record.AuthorityLevel.LevelVersion,
            true);
        var grant = ApprovalAuthorityGrant.Restore(
            record.Id, record.UserProfileId, level, record.MaxAmountBase, record.BaseCurrency,
            ParseScope(record.ScopeJson), record.ValidFrom, record.ValidTo, record.GrantedAt, record.GrantedBy,
            (GrantStatus)record.Status, record.Version, record.RevokedAt, record.RevokedBy);
        if (!record.AuthorityLevel.IsActive)
        {
            level.Retire();
        }
        return grant;
    }

    private static AuthorizationScopeSet ParseScope(string json)
    {
        var entries = JsonSerializer.Deserialize<ScopeEntry[]>(json, ScopeJsonOptions)
            ?? throw new InvalidOperationException("Stored authorization scope is invalid.");
        return AuthorizationScopeSet.Create(entries.Select(entry =>
        {
            if (!OrganizationContractCodes.TryScope(entry.Dimension, out var dimension))
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

    private static readonly JsonSerializerOptions ScopeJsonOptions = new(JsonSerializerDefaults.Web);
}
