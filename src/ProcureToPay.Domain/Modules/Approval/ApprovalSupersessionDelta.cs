using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>Closed change kinds of <c>approval-supersession-delta/v1</c> (SPEC 06 REQ-08).</summary>
public enum ApprovalSupersessionChangeKind
{
    Retained = 1,
    Added = 2,
    Removed = 3
}

public static class ApprovalSupersessionDeltaCodes
{
    public const string ChangeKindRetained = "RETAINED";
    public const string ChangeKindAdded = "ADDED";
    public const string ChangeKindRemoved = "REMOVED";

    public static string Code(ApprovalSupersessionChangeKind kind) => kind switch
    {
        ApprovalSupersessionChangeKind.Retained => ChangeKindRetained,
        ApprovalSupersessionChangeKind.Added => ChangeKindAdded,
        ApprovalSupersessionChangeKind.Removed => ChangeKindRemoved,
        _ => throw new DomainValidationException("The supersession change kind is invalid.")
    };

    public static ApprovalSupersessionChangeKind Parse(string? code) => code switch
    {
        ChangeKindRetained => ApprovalSupersessionChangeKind.Retained,
        ChangeKindAdded => ApprovalSupersessionChangeKind.Added,
        ChangeKindRemoved => ApprovalSupersessionChangeKind.Removed,
        _ => throw new DomainValidationException("The stored supersession change kind is invalid.")
    };
}

/// <summary>
/// One closed entry of <c>approval-supersession-delta/v1</c> (SPEC 06 REQ-08): retained coverage
/// of the previous and the new manifest with a declared materiality proof, an addition or a
/// removal. <c>RETAINED</c> keeps the stable target identity, so previous and replacement share
/// type and id while version and material snapshot digest may legitimately change.
/// </summary>
public sealed record ApprovalSupersessionDeltaEntry
{
    private ApprovalSupersessionDeltaEntry(
        ApprovalSupersessionChangeKind changeKind,
        ApprovalTarget? previous,
        ApprovalTarget? replacement,
        string materialitySchemaVersion,
        string materialityDigest)
    {
        ChangeKind = changeKind;
        Previous = previous;
        Replacement = replacement;
        MaterialitySchemaVersion = materialitySchemaVersion;
        MaterialityDigest = materialityDigest;
    }

    public ApprovalSupersessionChangeKind ChangeKind { get; }
    public ApprovalTarget? Previous { get; }
    public ApprovalTarget? Replacement { get; }

    /// <summary>Schema of the materiality proof, e.g. <c>purchase-request-materiality/v1</c>.</summary>
    public string MaterialitySchemaVersion { get; }

    /// <summary>Verified materiality digest that gates carry-forward for a retained pair.</summary>
    public string MaterialityDigest { get; }

    public string PreviousIdentity =>
        Previous?.CanonicalIdentity
        ?? throw new DomainValidationException("The delta entry has no previous target.");

    public string ReplacementIdentity =>
        Replacement?.CanonicalIdentity
        ?? throw new DomainValidationException("The delta entry has no replacement target.");

    /// <summary>Stable identity of a target ignoring version and material digest.</summary>
    public static string StableIdentity(ApprovalTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{target.Type}:{target.Id:D}";
    }

    public static ApprovalSupersessionDeltaEntry CreateRetained(
        ApprovalTarget previous,
        ApprovalTarget replacement,
        string materialitySchemaVersion,
        string materialityDigest)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(StableIdentity(previous), StableIdentity(replacement), StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                "A retained entry requires previous and replacement with the same target type and id.");
        }

        return new ApprovalSupersessionDeltaEntry(
            ApprovalSupersessionChangeKind.Retained,
            previous,
            replacement,
            MaterialitySchema(materialitySchemaVersion),
            MaterialityProof(materialityDigest));
    }

    public static ApprovalSupersessionDeltaEntry CreateAdded(
        ApprovalTarget replacement,
        string materialitySchemaVersion,
        string materialityDigest)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return new ApprovalSupersessionDeltaEntry(
            ApprovalSupersessionChangeKind.Added,
            null,
            replacement,
            MaterialitySchema(materialitySchemaVersion),
            MaterialityProof(materialityDigest));
    }

    public static ApprovalSupersessionDeltaEntry CreateRemoved(
        ApprovalTarget previous,
        string materialitySchemaVersion,
        string materialityDigest)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return new ApprovalSupersessionDeltaEntry(
            ApprovalSupersessionChangeKind.Removed,
            previous,
            null,
            MaterialitySchema(materialitySchemaVersion),
            MaterialityProof(materialityDigest));
    }

    /// <summary>Rehydrates a persisted entry after validating its change kind cardinalities.</summary>
    public static ApprovalSupersessionDeltaEntry Restore(
        string? changeKind,
        ApprovalTarget? previous,
        ApprovalTarget? replacement,
        string? materialitySchemaVersion,
        string? materialityDigest) =>
        ApprovalSupersessionDeltaCodes.Parse(changeKind) switch
        {
            ApprovalSupersessionChangeKind.Retained => CreateRetained(
                previous ?? throw new DomainValidationException("A retained entry requires its previous target."),
                replacement ?? throw new DomainValidationException("A retained entry requires its replacement."),
                materialitySchemaVersion!,
                materialityDigest!),
            ApprovalSupersessionChangeKind.Added => CreateAdded(
                replacement ?? throw new DomainValidationException("An added entry requires its replacement."),
                materialitySchemaVersion!,
                materialityDigest!),
            _ => CreateRemoved(
                previous ?? throw new DomainValidationException("A removed entry requires its previous target."),
                materialitySchemaVersion!,
                materialityDigest!)
        };

    /// <summary>Exact canonical value of one <c>target_delta</c> entry (SPEC 06, Datos y contratos).</summary>
    public CanonicalValue ToCanonicalValue() => ApprovalCanonicalJson.Object(
        ("change_kind", ApprovalCanonicalJson.String(ApprovalSupersessionDeltaCodes.Code(ChangeKind))),
        ("materiality_digest", ApprovalCanonicalJson.String(MaterialityDigest)),
        ("materiality_schema_version", ApprovalCanonicalJson.String(MaterialitySchemaVersion)),
        ("previous", Previous is null
            ? ApprovalCanonicalJson.Null()
            : ApprovalRequirementDefinition.TargetValue(Previous)),
        ("replacement", Replacement is null
            ? ApprovalCanonicalJson.Null()
            : ApprovalRequirementDefinition.TargetValue(Replacement)));

    private static string MaterialitySchema(string? value) =>
        ApprovalLimits.RequireSchemaVersion(value, "materiality_schema_version");

    private static string MaterialityProof(string? value) =>
        ApprovalLimits.RequireSha256(value, "materiality_digest");
}

/// <summary>
/// Structural rules of a closed <c>approval-supersession-delta/v1</c> (SPEC 06 REQ-08): every
/// previous target appears exactly once as <c>RETAINED|REMOVED</c>, every replacement exactly once
/// as <c>RETAINED|ADDED</c>, additions never reuse an existing stable identity, and no target is
/// fused, split or omitted. Coverage against the persisted graphs is checked by the workflow.
/// </summary>
public static class ApprovalSupersessionDeltaRules
{
    public static void Validate(IEnumerable<ApprovalSupersessionDeltaEntry>? entries)
    {
        var delta = (entries ?? throw new DomainValidationException("A supersession delta is required."))
            .ToArray();
        if (delta.Length == 0)
        {
            throw new DomainValidationException("A supersession delta needs at least one entry.");
        }

        var previousIdentities = delta
            .Where(entry => entry.Previous is not null)
            .Select(entry => entry.PreviousIdentity)
            .ToArray();
        var replacementIdentities = delta
            .Where(entry => entry.Replacement is not null)
            .Select(entry => entry.ReplacementIdentity)
            .ToArray();
        if (previousIdentities.Length != previousIdentities.Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("A supersession delta cannot repeat a previous target.");
        }

        if (replacementIdentities.Length != replacementIdentities.Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("A supersession delta cannot repeat a replacement target.");
        }

        var previousStable = delta
            .Where(entry => entry.Previous is not null)
            .Select(entry => ApprovalSupersessionDeltaEntry.StableIdentity(entry.Previous!))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in delta.Where(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Added))
        {
            if (previousStable.Contains(ApprovalSupersessionDeltaEntry.StableIdentity(entry.Replacement!)))
            {
                throw new DomainConflictException(
                    "An added target cannot reuse the stable identity of a previous target.");
            }
        }
    }
}
