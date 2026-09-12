using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public enum ApprovalCaseStatus
{
    Open = 1,
    Blocked = 2,
    Completed = 3,
    Cancelled = 4
}

public enum ApprovalRequirementStatus
{
    Waiting = 1,
    Unassigned = 2,
    Pending = 3,
    Approved = 4,
    Rejected = 5,
    ChangesRequested = 6,
    Cancelled = 7
}

public enum ApprovalTaskStatus
{
    Unassigned = 1,
    Pending = 2,
    Approved = 3,
    Rejected = 4,
    ChangesRequested = 5,
    Cancelled = 6
}

public enum ApprovalDecisionAction
{
    Approve = 1,
    Reject = 2,
    RequestChanges = 3
}

public enum ApprovalDecisionOrigin
{
    Human = 1,
    CarryForward = 2
}

public enum PrerequisiteStatus
{
    Waiting = 1,
    Satisfied = 2,
    Failed = 3,
    Cancelled = 4
}

public enum ApprovalOutboxState
{
    Pending = 1,
    Delivered = 2,
    DeadLetter = 3
}

public enum DependencyPredecessorKind
{
    Approval = 1,
    External = 2
}

public enum DependencyMode
{
    All = 1
}

public enum ApprovalAssignmentCause
{
    Initial = 1,
    Reassigned = 2,
    Unassigned = 3
}

/// <summary>SPEC 03 REQ-02/REQ-09 validation limits and key alphabets.</summary>
public static class ApprovalLimits
{
    public const int MaxRequirementsPerCase = 2_000;
    public const int MaxTargetLinksPerCase = 10_000;
    public const int MaxCanonicalCommandBytes = 5 * 1024 * 1024;
    public const int MaxKeyLength = 128;
    public const int MaxReasonScalars = 1_000;

    private static readonly Regex KeyPattern = new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9_.:-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CorrelationPattern = new("^[A-Za-z0-9._:-]{1,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RequireKey(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !KeyPattern.IsMatch(value))
        {
            throw new DomainValidationException(
                $"{field} must contain 1-128 characters of [A-Za-z0-9._:-].");
        }

        return value;
    }

    public static string RequireCode(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !CodePattern.IsMatch(value))
        {
            throw new DomainValidationException(
                $"{field} must match [A-Z][A-Z0-9_.:-]* with 1-128 characters.");
        }

        return value;
    }

    public static string RequireReason(string? value, string field = "Reason")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException($"{field} is required.");
        }

        var normalized = value.Trim().Normalize(System.Text.NormalizationForm.FormC);
        var scalars = normalized.EnumerateRunes().Count();
        if (scalars > MaxReasonScalars)
        {
            throw new DomainValidationException($"{field} cannot exceed 1000 Unicode scalars.");
        }

        return normalized;
    }

    /// <summary>
    /// <c>correlation_reference</c> is opaque and server-projected: 1-120 ASCII characters of
    /// <c>[A-Za-z0-9._:-]</c>. It is never an identity, idempotency key or cause (REQ-08).
    /// </summary>
    public static string RequireCorrelation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !CorrelationPattern.IsMatch(value))
        {
            throw new DomainValidationException(
                "correlation_reference must contain 1-120 characters of [A-Za-z0-9._:-].");
        }

        return value;
    }

    public static string RequireSha256(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new DomainValidationException($"{field} must be a lowercase SHA-256 hexadecimal digest.");
        }

        return value;
    }
}

/// <summary>Immutable target identity: type, id, version and material snapshot digest (REQ-02).</summary>
public sealed record ApprovalTarget
{
    public ApprovalTarget(string type, Guid id, int version, string materialSnapshotDigest)
    {
        Type = ApprovalLimits.RequireCode(type, "Target type");
        if (id == Guid.Empty)
        {
            throw new DomainValidationException("Target id is required.");
        }

        if (version < 1)
        {
            throw new DomainValidationException("Target version must be positive.");
        }

        Id = id;
        Version = version;
        MaterialSnapshotDigest = ApprovalLimits.RequireSha256(materialSnapshotDigest, "Target material digest");
    }

    public string Type { get; }
    public Guid Id { get; }
    public int Version { get; }
    public string MaterialSnapshotDigest { get; }

    public string CanonicalIdentity => $"{Type}:{Id:D}:{Version}:{MaterialSnapshotDigest}";

    public bool SameIdentity(ApprovalTarget other) => CanonicalIdentity == other.CanonicalIdentity;
}

public sealed record ApprovalDependencyRef
{
    public ApprovalDependencyRef(
        DependencyPredecessorKind predecessorKind,
        string predecessorKey,
        IEnumerable<ApprovalTarget> targets,
        DependencyMode mode = DependencyMode.All)
    {
        if (mode != DependencyMode.All)
        {
            throw new DomainValidationException("Only ALL dependency mode is supported.");
        }

        var materialized = (targets ?? throw new DomainValidationException("Dependency targets are required."))
            .ToImmutableHashSet();
        if (materialized.Count == 0)
        {
            throw new DomainValidationException("A dependency edge requires at least one target.");
        }

        if (materialized.Any(target => target is null))
        {
            throw new DomainValidationException("Dependency targets cannot be null.");
        }

        PredecessorKind = predecessorKind;
        PredecessorKey = ApprovalLimits.RequireKey(predecessorKey, "Dependency predecessor key");
        Targets = materialized;
        Mode = mode;
    }

    public DependencyPredecessorKind PredecessorKind { get; }
    public string PredecessorKey { get; }
    public ImmutableHashSet<ApprovalTarget> Targets { get; }
    public DependencyMode Mode { get; }
}

public static class ApprovalTargetRules
{
    /// <summary>An edge target set must be a subset of both nodes by full identity (REQ-03).</summary>
    public static void EnsureSubset(IEnumerable<ApprovalTarget> edgeTargets, IReadOnlyCollection<ApprovalTarget> nodeTargets, string context)
    {
        var targets = nodeTargets.Select(target => target.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);
        if (edgeTargets.Any(edge => !targets.Contains(edge.CanonicalIdentity)))
        {
            throw new DomainValidationException($"Dependency targets are not contained by {context}.");
        }
    }
}

/// <summary>Canonical derived key for a requirement: stable for a given target set (REQ-02).</summary>
public static class ApprovalRequirementKeys
{
    public const string Prefix = "WR-";

    public static string Derive(IEnumerable<ApprovalTarget> targets)
    {
        var canonical = ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Set(targets
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .Select(target => ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(target.Id)),
                ("material_snapshot_digest", ApprovalCanonicalJson.String(target.MaterialSnapshotDigest)),
                ("type", ApprovalCanonicalJson.String(target.Type)),
                ("version", ApprovalCanonicalJson.Number(target.Version))))));
        return $"{Prefix}{canonical[..32].ToUpperInvariant()}";
    }
}
