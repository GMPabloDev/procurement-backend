using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Organization;

public sealed record AuthorityRequirement(
    AuthorityRequirementKind Kind,
    ApprovalAuthorityType? Type,
    int? MinimumRank,
    decimal? AmountBase,
    string? BaseCurrency)
{
    public static AuthorityRequirement None { get; } =
        new(AuthorityRequirementKind.None, null, null, null, null);

    public static AuthorityRequirement Required(
        ApprovalAuthorityType type,
        int minimumRank,
        decimal? amountBase,
        string? baseCurrency)
    {
        if (minimumRank < 1 || amountBase is < 0)
        {
            throw new DomainValidationException("Authority requirement rank and amount are invalid.");
        }

        if (amountBase is not null && string.IsNullOrWhiteSpace(baseCurrency))
        {
            throw new DomainValidationException("A monetary authority requirement needs a base currency.");
        }

        return new AuthorityRequirement(
            AuthorityRequirementKind.Required,
            type,
            minimumRank,
            amountBase,
            baseCurrency?.Trim().ToUpperInvariant());
    }
}

public sealed record EligibilityRequest(
    SystemRole RequiredRole,
    AuthorizationScopeSet RequiredScope,
    AuthorityRequirement Authority,
    DateTimeOffset EvaluatedAt,
    IReadOnlySet<Guid> ExcludedUserIds)
{
    public static EligibilityRequest Create(
        SystemRole requiredRole,
        AuthorizationScopeSet requiredScope,
        AuthorityRequirement authority,
        DateTimeOffset evaluatedAt,
        IEnumerable<Guid>? excludedUserIds = null)
    {
        ArgumentNullException.ThrowIfNull(requiredScope);
        ArgumentNullException.ThrowIfNull(authority);

        return new EligibilityRequest(
            requiredRole,
            requiredScope,
            authority,
            evaluatedAt.ToUniversalTime(),
            (excludedUserIds ?? []).ToImmutableHashSet());
    }
}

public sealed record EligibilityEvidence(
    Guid UserId,
    int UserProfileVersion,
    Guid RoleAssignmentId,
    int RoleAssignmentVersion,
    Guid? AuthorityGrantId,
    int? AuthorityGrantVersion,
    Guid? AuthorityLevelId,
    int? AuthorityLevelVersion,
    SystemRole RequiredRole,
    string RequiredScopeSnapshot,
    AuthorityRequirement AuthorityRequirement,
    DateTimeOffset EvaluatedAt,
    string AssignmentScopeSnapshot,
    DateTimeOffset AssignmentAssignedAt,
    DateTimeOffset? AssignmentRevokedAt,
    string? AuthorityGrantScopeSnapshot,
    decimal? AuthorityGrantMaxAmountBase,
    string? AuthorityGrantBaseCurrency,
    DateTimeOffset? AuthorityGrantValidFrom,
    DateTimeOffset? AuthorityGrantValidTo,
    string? AuthorityLevelCode,
    int? AuthorityLevelRank,
    IReadOnlySet<Guid> ExcludedUserIds);

public sealed record EligibleCandidate(
    UserProfile User,
    RoleAssignment RoleAssignment,
    ApprovalAuthorityGrant? AuthorityGrant,
    EligibilityEvidence Evidence);

public static class AuthorizationMatrix
{
    private static readonly IReadOnlyDictionary<SystemRole, IReadOnlySet<ApprovalAuthorityType>> RequiredAuthorityByRole =
        new Dictionary<SystemRole, IReadOnlySet<ApprovalAuthorityType>>
        {
            [SystemRole.DepartmentApprover] = new HashSet<ApprovalAuthorityType>
            {
                ApprovalAuthorityType.BusinessNeed
            },
            [SystemRole.FinanceApprover] = new HashSet<ApprovalAuthorityType>
            {
                ApprovalAuthorityType.Financial,
                ApprovalAuthorityType.MatchException
            },
            [SystemRole.ProcurementApprover] = new HashSet<ApprovalAuthorityType>
            {
                ApprovalAuthorityType.Procurement,
                ApprovalAuthorityType.SupplierMaster,
                ApprovalAuthorityType.MatchException
            },
            [SystemRole.PaymentApprover] = new HashSet<ApprovalAuthorityType>
            {
                ApprovalAuthorityType.Payment
            }
        };

    private static readonly IReadOnlySet<SystemRole> ScopeOnlyRoles = new HashSet<SystemRole>
    {
        SystemRole.ItReviewer,
        SystemRole.LegalReviewer
    };

    public static bool IsAuthorityRequired(SystemRole role) => RequiredAuthorityByRole.ContainsKey(role);

    public static bool IsScopeOnly(SystemRole role) => ScopeOnlyRoles.Contains(role);

    public static bool Supports(SystemRole role, ApprovalAuthorityType type) =>
        RequiredAuthorityByRole.TryGetValue(role, out var types) && types.Contains(type);

    public static void Validate(SystemRole role, AuthorityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (requirement.Kind == AuthorityRequirementKind.None)
        {
            if (!IsScopeOnly(role))
            {
                throw new DomainValidationException($"Role {role} requires an Approval Authority.");
            }

            return;
        }

        if (requirement.Type is null || requirement.MinimumRank is null ||
            !Supports(role, requirement.Type.Value))
        {
            throw new DomainValidationException($"Authority type is not compatible with role {role}.");
        }
    }
}

public static class EligibilityResolver
{
    public static IReadOnlyList<EligibleCandidate> Resolve(
        IEnumerable<UserProfile> users,
        IEnumerable<RoleAssignment> assignments,
        IEnumerable<ApprovalAuthorityGrant> grants,
        EligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(request);
        AuthorizationMatrix.Validate(request.RequiredRole, request.Authority);

        var usersById = users
            .Where(user => user.Status == UserProfileStatus.Active)
            .ToDictionary(user => user.Id);
        var assignmentsByUser = assignments
            .Where(assignment => assignment.Role == request.RequiredRole &&
                                 assignment.Status == AssignmentStatus.Active)
            .GroupBy(assignment => assignment.UserId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var grantsByUser = grants
            .Where(grant => grant.Status == GrantStatus.Active)
            .GroupBy(grant => grant.UserId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var candidates = new List<EligibleCandidate>();
        foreach (var user in usersById.Values.OrderBy(user => user.Id))
        {
            if (request.ExcludedUserIds.Contains(user.Id) ||
                !assignmentsByUser.TryGetValue(user.Id, out var userAssignments))
            {
                continue;
            }

            foreach (var assignment in userAssignments.OrderBy(assignment => assignment.Id))
            {
                if (assignment.AssignedAt > request.EvaluatedAt ||
                    !assignment.Covers(request.RequiredScope))
                {
                    continue;
                }

                ApprovalAuthorityGrant? grant = null;
                if (request.Authority.Kind == AuthorityRequirementKind.Required)
                {
                    grant = grantsByUser.TryGetValue(user.Id, out var userGrants)
                        ? userGrants
                            .Where(candidate => candidate.Covers(
                                request.Authority.Type!.Value,
                                request.Authority.MinimumRank!.Value,
                                request.Authority.AmountBase,
                                request.Authority.BaseCurrency,
                                request.RequiredScope,
                                request.EvaluatedAt))
                            .OrderBy(candidate => candidate.Id)
                            .FirstOrDefault()
                        : null;
                    if (grant is null)
                    {
                        continue;
                    }
                }

                candidates.Add(new EligibleCandidate(
                    user,
                    assignment,
                    grant,
                    new EligibilityEvidence(
                        user.Id,
                        user.Version,
                        assignment.Id,
                        assignment.Version,
                        grant?.Id,
                        grant?.Version,
                        grant?.Level.Id,
                        grant?.Level.Version,
                        request.RequiredRole,
                        request.RequiredScope.ToString(),
                        request.Authority,
                        request.EvaluatedAt,
                        assignment.Scope.ToString(),
                        assignment.AssignedAt,
                        assignment.RevokedAt,
                        grant?.Scope.ToString(),
                        grant?.MaxAmountBase,
                        grant?.BaseCurrency,
                        grant?.ValidFrom,
                        grant?.ValidTo,
                        grant?.Level.Code,
                        grant?.Level.Rank,
                        request.ExcludedUserIds.ToImmutableHashSet())));
            }
        }

        return candidates;
    }
}
