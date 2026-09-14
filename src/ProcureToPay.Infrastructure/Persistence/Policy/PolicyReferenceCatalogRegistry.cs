namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>
/// Exact in-process lookup of SPEC 07 Datos y contratos: `reference_type`, `organization_id`,
/// `id`, `version`, `digest`, `code` and `value_kind` are always present and carry the shape's own
/// nullability. `Guid.Empty` and empty strings never substitute a real `null`.
/// </summary>
public sealed record PolicyReferenceLookup(
    string ReferenceType,
    Guid OrganizationId,
    Guid? Id,
    int? Version,
    string? Digest,
    string? Code,
    string? ValueKind);

public interface IPolicyReferenceCatalog
{
    string CatalogId { get; }
    string ContractVersion { get; }
    Task<bool> ExistsAsync(PolicyReferenceLookup reference, CancellationToken cancellationToken = default);
}

public sealed class PolicyReferenceCatalogRegistry(
    IEnumerable<IPolicyReferenceCatalog> catalogs)
{
    private readonly IReadOnlyList<IPolicyReferenceCatalog> catalogs = catalogs.ToArray();

    public IPolicyReferenceCatalog Resolve(string catalogId)
    {
        var matches = catalogs.Where(catalog =>
            string.Equals(catalog.CatalogId, catalogId, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new PolicyDependencyUnavailableException(
                $"Expected exactly one policy reference catalog for '{catalogId}', found {matches.Length}.");
    }
}

public sealed class DefaultDenyPolicyReferenceCatalog : IPolicyReferenceCatalog
{
    public string CatalogId => "DEFAULT_DENY";
    public string ContractVersion => "1";

    public Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
