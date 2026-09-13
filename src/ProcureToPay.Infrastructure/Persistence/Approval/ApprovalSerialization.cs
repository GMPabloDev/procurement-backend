using System.Text.Json;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>JSON round-trip for append-only approval payloads stored in SQL Server.</summary>
public static class ApprovalJsonPersistence
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeTargets(IEnumerable<ApprovalTarget> targets) =>
        JsonSerializer.Serialize(targets.Select(TargetPayload.From).ToArray(), Options);

    public static IReadOnlyList<ApprovalTarget> DeserializeTargets(string json) =>
        (JsonSerializer.Deserialize<TargetPayload[]>(json, Options) ?? [])
        .Select(payload => new ApprovalTarget(payload.Type!, payload.Id, payload.Version, payload.MaterialSnapshotDigest!))
        .ToArray();

    public static string SerializeDependencies(IEnumerable<ApprovalDependencyRef> dependencies) =>
        JsonSerializer.Serialize(dependencies.Select(dependency => new DependencyPayload(
            dependency.PredecessorKind.ToString().ToUpperInvariant(),
            dependency.PredecessorKey,
            dependency.Mode.ToString().ToUpperInvariant(),
            dependency.Targets.Select(TargetPayload.From).ToArray())).ToArray(), Options);

    public static IReadOnlyList<ApprovalDependencyRef> DeserializeDependencies(string json) =>
        (JsonSerializer.Deserialize<DependencyPayload[]>(json, Options) ?? [])
        .Select(payload => new ApprovalDependencyRef(
            Enum.TryParse<DependencyPredecessorKind>(payload.PredecessorKind, ignoreCase: true, out var kind)
                ? kind
                : throw new DomainValidationException("Stored dependency predecessor kind is invalid."),
            payload.PredecessorKey!,
            (payload.Targets ?? []).Select(target =>
                new ApprovalTarget(target.Type!, target.Id, target.Version, target.MaterialSnapshotDigest!)),
            Enum.TryParse<DependencyMode>(payload.Mode, ignoreCase: true, out var mode)
                ? mode
                : throw new DomainValidationException("Stored dependency mode is invalid.")))
        .ToArray();

    public static string SerializeAuthority(AuthorityRequirement authority) =>
        JsonSerializer.Serialize(new AuthorityPayload(
            authority.Kind.ToString().ToUpperInvariant(),
            authority.Type?.ToString().ToUpperInvariant(),
            authority.MinimumRank,
            authority.AmountBase,
            authority.BaseCurrency), Options);

    public static AuthorityRequirement DeserializeAuthority(string json)
    {
        var payload = JsonSerializer.Deserialize<AuthorityPayload>(json, Options)
            ?? throw new DomainValidationException("Stored authority requirement is invalid.");
        if (!Enum.TryParse<AuthorityRequirementKind>(payload.Kind, ignoreCase: true, out var kind))
        {
            throw new DomainValidationException("Stored authority kind is invalid.");
        }

        if (kind == AuthorityRequirementKind.None)
        {
            return AuthorityRequirement.None;
        }

        if (!Enum.TryParse<ApprovalAuthorityType>(payload.Type, ignoreCase: true, out var type) ||
            payload.MinimumRank is null)
        {
            throw new DomainValidationException("Stored authority requirement is incomplete.");
        }

        return AuthorityRequirement.Required(type, payload.MinimumRank.Value, payload.AmountBase, payload.BaseCurrency);
    }

    public static string SerializeGuids(IEnumerable<Guid> values) =>
        JsonSerializer.Serialize(values.OrderBy(value => value).ToArray(), Options);

    /// <summary>Single append-only target of a carry-forward record (SPEC 04 REQ-05).</summary>
    public static string SerializeTarget(ApprovalTarget target) =>
        JsonSerializer.Serialize(TargetPayload.From(target), Options);

    public static ApprovalTarget DeserializeTarget(string json)
    {
        var payload = JsonSerializer.Deserialize<TargetPayload>(json, Options)
            ?? throw new DomainValidationException("Stored target is invalid.");
        return new ApprovalTarget(payload.Type!, payload.Id, payload.Version, payload.MaterialSnapshotDigest!);
    }

    /// <summary>Bijective target mapping of a supersession (SPEC 04 REQ-04).</summary>
    public static string SerializeSupersessionMapping(IEnumerable<ApprovalSupersessionMapping> mapping) =>
        JsonSerializer.Serialize(
            mapping.Select(entry => new SupersessionMappingPayload(
                TargetPayload.From(entry.Previous),
                TargetPayload.From(entry.Replacement),
                entry.MaterialitySchemaVersion,
                entry.MaterialityDigest)).ToArray(),
            Options);

    public static IReadOnlyList<ApprovalSupersessionMapping> DeserializeSupersessionMapping(string json)
    {
        var payloads = JsonSerializer.Deserialize<SupersessionMappingPayload[]>(json, Options) ?? [];
        return payloads.Select(payload => ApprovalSupersessionMapping.Create(
            new ApprovalTarget(payload.Previous!.Type!, payload.Previous.Id, payload.Previous.Version,
                payload.Previous.MaterialSnapshotDigest!),
            new ApprovalTarget(payload.Replacement!.Type!, payload.Replacement.Id, payload.Replacement.Version,
                payload.Replacement.MaterialSnapshotDigest!),
            payload.MaterialitySchemaVersion!,
            payload.MaterialityDigest!)).ToArray();
    }

    public static IReadOnlyList<Guid> DeserializeGuids(string json) =>
        JsonSerializer.Deserialize<Guid[]>(json, Options) ?? [];

    /// <summary>Minimized scope snapshot kept for audit and evidence (SPEC 01 scope set).</summary>
    public static string SerializeScope(AuthorizationScopeSet scope) => scope.ToString();

    /// <summary>The declared decision actions of a requirement (REQ-02); stored uppercase.</summary>
    public static string SerializeActions(IEnumerable<ApprovalDecisionAction> actions) =>
        JsonSerializer.Serialize(
            actions.Select(action => action.ToString().ToUpperInvariant()).ToArray(), Options);

    public static IReadOnlyList<ApprovalDecisionAction> DeserializeActions(string json)
    {
        var codes = JsonSerializer.Deserialize<string[]>(json, Options) ?? [];
        var actions = new List<ApprovalDecisionAction>(codes.Length);
        foreach (var code in codes)
        {
            if (!Enum.TryParse<ApprovalDecisionAction>(code, ignoreCase: true, out var action))
            {
                throw new DomainValidationException("Stored approval action is invalid.");
            }

            actions.Add(action);
        }

        return actions;
    }

    private sealed record TargetPayload(string? Type, Guid Id, int Version, string? MaterialSnapshotDigest)
    {
        public static TargetPayload From(ApprovalTarget target) =>
            new(target.Type, target.Id, target.Version, target.MaterialSnapshotDigest);
    }

    private sealed record DependencyPayload(
        string? PredecessorKind,
        string? PredecessorKey,
        string? Mode,
        TargetPayload[]? Targets);

    private sealed record AuthorityPayload(
        string? Kind,
        string? Type,
        int? MinimumRank,
        decimal? AmountBase,
        string? BaseCurrency);

    private sealed record SupersessionMappingPayload(
        TargetPayload? Previous,
        TargetPayload? Replacement,
        string? MaterialitySchemaVersion,
        string? MaterialityDigest);
}
