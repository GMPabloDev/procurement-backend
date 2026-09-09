namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed record PolicyReferenceLookup(
    string ReferenceType,
    Guid Id,
    int Version,
    string Digest);

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
