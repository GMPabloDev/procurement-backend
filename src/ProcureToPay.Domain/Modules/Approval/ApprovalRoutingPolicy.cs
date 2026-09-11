using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Deterministic candidate selection (REQ-04, DEC-02): among the direct candidates returned by
/// the SPEC 01 resolver, choose the lowest number of current PENDING tasks in the organization
/// and break every tie by canonical ascending UUID. The policy is pure so the ordering is
/// reproducible with the same eligibility snapshot and clock (NFR-01).
/// </summary>
public static class ApprovalRoutingPolicy
{
    /// <summary>Returns the selected candidate, or <c>null</c> when nobody is eligible (no fallback).</summary>
    public static EligibleCandidate? SelectLowestLoad(
        IEnumerable<EligibleCandidate> candidates,
        IReadOnlyDictionary<Guid, int> loads)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(loads);

        return candidates
            .GroupBy(candidate => candidate.User.Id)
            .Select(group => group.OrderBy(candidate => candidate.RoleAssignment.Id).First())
            .OrderBy(candidate => LoadOf(loads, candidate.User.Id))
            .ThenBy(candidate => CanonicalUserId(candidate.User.Id), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The observed load of a user; an unseen user holds no PENDING task.</summary>
    public static int LoadOf(IReadOnlyDictionary<Guid, int> loads, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(loads);
        return loads.TryGetValue(userId, out var load) ? load : 0;
    }

    /// <summary>
    /// Canonical UUID ordering. The lowercase "D" representation is compared ordinally rather
    /// than by signed component, so the tie-break is a stable byte order (REQ-04).
    /// </summary>
    public static string CanonicalUserId(Guid userId) => userId.ToString("D").ToLowerInvariant();
}
