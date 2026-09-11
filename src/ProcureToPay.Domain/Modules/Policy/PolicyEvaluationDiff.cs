using System.Collections.Immutable;

namespace ProcureToPay.Domain.Modules.Policy;

/// <summary>
/// Stable reevaluation diff between two evaluations of the same subject (REQ-13).
/// Controls are matched by requirement key, type and phase, and then by covered
/// subjects; a control is equivalent only when every effective parameter matches.
/// Any increase of authority, amount, quotations, documents, scope or restrictions
/// across matched controls is reported as <c>HARDENED</c>.
/// </summary>
public static class PolicyEvaluationDiff
{
    public const string Added = "ADDED";
    public const string Hardened = "HARDENED";
    public const string Removed = "REMOVED";
    public const string Unchanged = "UNCHANGED";

    /// <summary>
    /// Computes the ordered, duplicate-free diff of the combined controls of a previous
    /// evaluation against the combined controls of its reevaluation.
    /// </summary>
    public static IReadOnlyList<PolicyEvaluationDiffEntry> Compute(
        PolicyEvaluationBundle previous,
        PolicyEvaluationBundle current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousGroups = Group(previous.Controls);
        var currentGroups = Group(current.Controls);
        var entries = new List<PolicyEvaluationDiffEntry>();

        foreach (var key in previousGroups.Keys
                     .Union(currentGroups.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var unmatchedPrevious = previousGroups.TryGetValue(key, out var previousControls)
                ? new List<PolicyGeneratedControl>(previousControls)
                : [];
            var currentControls = currentGroups.TryGetValue(key, out var currentList) ? currentList : [];

            foreach (var currentControl
                     in currentControls.OrderBy(control => control.SubjectIds.Count)
                         .ThenBy(control => SubjectKey(control), StringComparer.Ordinal))
            {
                var matchIndex = FindBestMatch(unmatchedPrevious, currentControl);
                if (matchIndex < 0)
                {
                    entries.Add(Entry(Added, currentControl, currentControl, null, currentControl.MinimumQuotations));
                    continue;
                }

                var previousControl = unmatchedPrevious[matchIndex];
                unmatchedPrevious.RemoveAt(matchIndex);
                entries.Add(Entry(Classify(previousControl, currentControl), previousControl, currentControl,
                    previousControl.MinimumQuotations, currentControl.MinimumQuotations));
            }

            foreach (var leftover in unmatchedPrevious)
            {
                entries.Add(Entry(Removed, leftover, leftover, leftover.MinimumQuotations, null));
            }
        }

        return entries
            .OrderBy(entry => entry.RequirementKey, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type)
            .ThenBy(entry => string.Join(",", entry.SubjectIds.OrderBy(id => id)), StringComparer.Ordinal)
            .ToArray();
    }

    private static string Classify(PolicyGeneratedControl previous, PolicyGeneratedControl current)
    {
        if (ParametersEqual(previous, current) && previous.SubjectIds.SetEquals(current.SubjectIds))
        {
            return Unchanged;
        }

        var compatibleIdentity = CompatibleIdentity(previous, current);
        var coversLess = current.SubjectIds.IsSubsetOf(previous.SubjectIds);
        var demandsLess = !AnyDemandIncrease(previous, current);
        return compatibleIdentity && coversLess && demandsLess ? Removed : Hardened;
    }

    private static bool CompatibleIdentity(PolicyGeneratedControl previous, PolicyGeneratedControl current) =>
        previous.Type == current.Type &&
        string.Equals(previous.Phase, current.Phase, StringComparison.Ordinal) &&
        previous.Approval?.Role == current.Approval?.Role &&
        previous.Approval?.AuthorityType == current.Approval?.AuthorityType &&
        string.Equals(previous.Approval?.DecisionScope, current.Approval?.DecisionScope, StringComparison.Ordinal) &&
        string.Equals(previous.Approval?.BaseCurrency, current.Approval?.BaseCurrency, StringComparison.Ordinal) &&
        previous.Approval?.AuthorityLevel?.Id == current.Approval?.AuthorityLevel?.Id &&
        previous.Approval?.AuthorityLevel?.Version == current.Approval?.AuthorityLevel?.Version &&
        string.Equals(previous.Approval?.AuthorityLevel?.Code, current.Approval?.AuthorityLevel?.Code, StringComparison.Ordinal);

    private static bool ParametersEqual(PolicyGeneratedControl previous, PolicyGeneratedControl current)
    {
        return previous.Type == current.Type &&
            string.Equals(previous.Phase, current.Phase, StringComparison.Ordinal) &&
            ApprovalEqual(previous.Approval, current.Approval) &&
            previous.MinimumQuotations == current.MinimumQuotations &&
            previous.MinimumAllowedQuotations == current.MinimumAllowedQuotations &&
            previous.CostCenterIds.SetEquals(current.CostCenterIds) &&
            previous.AmountBase == current.AmountBase &&
            string.Equals(previous.BaseCurrency, current.BaseCurrency, StringComparison.Ordinal) &&
            previous.SupportingDocumentTypes.SetEquals(current.SupportingDocumentTypes);
    }

    private static bool ApprovalEqual(PolicyApprovalDescriptor? previous, PolicyApprovalDescriptor? current)
    {
        if (previous is null || current is null)
        {
            return previous is null && current is null;
        }

        return previous.Role == current.Role &&
            previous.AuthorityType == current.AuthorityType &&
            previous.AuthorityLevel?.Id == current.AuthorityLevel?.Id &&
            previous.AuthorityLevel?.Version == current.AuthorityLevel?.Version &&
            string.Equals(previous.AuthorityLevel?.Code, current.AuthorityLevel?.Code, StringComparison.Ordinal) &&
            previous.AuthorityLevel?.Rank == current.AuthorityLevel?.Rank &&
            previous.AmountBase == current.AmountBase &&
            string.Equals(previous.BaseCurrency, current.BaseCurrency, StringComparison.Ordinal) &&
            string.Equals(previous.DecisionScope, current.DecisionScope, StringComparison.Ordinal) &&
            previous.Exceptionable == current.Exceptionable &&
            previous.MinimumExceptionAmount == current.MinimumExceptionAmount;
    }

    /// <summary>True when the current control demands strictly more on any ordered dimension.</summary>
    private static bool AnyDemandIncrease(PolicyGeneratedControl previous, PolicyGeneratedControl current)
    {
        if (Bool(current.Type == PolicyEffectType.Block) > Bool(previous.Type == PolicyEffectType.Block))
        {
            return true;
        }
        if ((current.Approval?.AuthorityLevel?.Rank ?? 0) > (previous.Approval?.AuthorityLevel?.Rank ?? 0))
        {
            return true;
        }
        if ((current.Approval?.AmountBase ?? 0m) > (previous.Approval?.AmountBase ?? 0m))
        {
            return true;
        }
        if ((current.AmountBase ?? 0m) > (previous.AmountBase ?? 0m))
        {
            return true;
        }
        if (!previous.CostCenterIds.IsSupersetOf(current.CostCenterIds))
        {
            return true;
        }
        if (!string.Equals(previous.BaseCurrency, current.BaseCurrency, StringComparison.Ordinal))
        {
            return true;
        }
        if ((current.MinimumQuotations ?? 0) > (previous.MinimumQuotations ?? 0))
        {
            return true;
        }
        if ((current.MinimumAllowedQuotations ?? 0) > (previous.MinimumAllowedQuotations ?? 0))
        {
            return true;
        }
        // Losing the option to request an exception is a harder requirement, not an increase in authority.
        if (previous.Approval?.Exceptionable == true && current.Approval?.Exceptionable != true)
        {
            return true;
        }
        if (current.SupportingDocumentTypes.Count > previous.SupportingDocumentTypes.Count)
        {
            return true;
        }
        return !previous.SupportingDocumentTypes.IsSupersetOf(current.SupportingDocumentTypes);
    }

    private static int FindBestMatch(
        IReadOnlyList<PolicyGeneratedControl> candidates,
        PolicyGeneratedControl current)
    {
        var bestIndex = -1;
        var bestOverlap = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.SubjectIds.SetEquals(current.SubjectIds))
            {
                return index;
            }
            if (!candidate.SubjectIds.Overlaps(current.SubjectIds))
            {
                continue;
            }
            var overlap = candidate.SubjectIds.Count(current.SubjectIds.Contains);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestIndex = index;
            }
        }
        return bestIndex;
    }

    private static PolicyEvaluationDiffEntry Entry(
        string change,
        PolicyGeneratedControl source,
        PolicyGeneratedControl target,
        int? previousMinimum,
        int? currentMinimum) =>
        new(
            change,
            source.RequirementKey,
            source.Type,
            source.SubjectIds.Union(target.SubjectIds).ToImmutableHashSet(),
            previousMinimum,
            currentMinimum);

    private static Dictionary<string, IReadOnlyList<PolicyGeneratedControl>> Group(
        IReadOnlyList<PolicyGeneratedControl> controls) =>
        controls.GroupBy(
                control => string.Join("\u001f", control.RequirementKey, control.Type.ToString(), control.Phase),
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PolicyGeneratedControl>)group.ToArray(), StringComparer.Ordinal);

    private static string SubjectKey(PolicyGeneratedControl control) =>
        string.Join(",", control.SubjectIds.OrderBy(id => id));

    private static int Bool(bool value) => value ? 1 : 0;
}
