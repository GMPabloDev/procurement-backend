using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>Adapter for an authoritative versioned reference catalog.</summary>
public sealed class HttpPolicyReferenceCatalog(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<HttpPolicyReferenceCatalog> logger) : IPolicyReferenceCatalog
{
    public string CatalogId => configuration["Policy:ReferenceCatalog:CatalogId"] ?? "DEFAULT";
    public string ContractVersion => configuration["Policy:ReferenceCatalog:ContractVersion"] ?? "v1";

    public async Task<bool> ExistsAsync(
        PolicyReferenceLookup reference,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(
                "v1/policy-references/resolve", reference, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Policy reference catalog is unavailable.");
            throw new PolicyDependencyUnavailableException("The policy reference catalog is unavailable.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Policy reference catalog returned HTTP {StatusCode}.", response.StatusCode);
                throw new PolicyDependencyUnavailableException("The policy reference catalog is unavailable.");
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<ReferenceResolutionResponse>(
                    cancellationToken);
                return result?.Exists ?? throw new PolicyDependencyUnavailableException(
                    "The policy reference catalog returned incomplete evidence.");
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Policy reference catalog returned malformed evidence.");
                throw new PolicyDependencyUnavailableException(
                    "The policy reference catalog returned malformed evidence.");
            }
        }
    }

    private sealed record ReferenceResolutionResponse(bool Exists);
}
