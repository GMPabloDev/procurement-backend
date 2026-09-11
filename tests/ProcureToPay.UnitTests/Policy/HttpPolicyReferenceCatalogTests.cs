using System.Net;
using System.Net.Http.Json;
// pi-lens-ignore: lsp:CS0234
using Microsoft.Extensions.Configuration;
// pi-lens-ignore: lsp:CS0234
using Microsoft.Extensions.Logging.Abstractions;
// pi-lens-ignore: lsp:CS0234
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.UnitTests.Policy;

public sealed class HttpPolicyReferenceCatalogTests
{
    [Fact]
    public async Task Catalog_resolution_returns_the_authoritative_existence_result()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { exists = true })
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://catalog.test/") };
        var catalog = new HttpPolicyReferenceCatalog(
            client,
            CreateConfiguration(),
            NullLogger<HttpPolicyReferenceCatalog>.Instance);

        var exists = await catalog.ExistsAsync(
            new PolicyReferenceLookup("SPEND_CATEGORY", Guid.NewGuid(), 2, new string('a', 64)),
            TestContext.Current.CancellationToken);

        Assert.True(exists);
        Assert.Equal("v1/policy-references/resolve", handler.RequestedPath);
        Assert.Equal("SPEND_CATEGORY", catalog.CatalogId);
    }

    [Fact]
    public async Task Catalog_failure_is_reported_as_a_typed_dependency_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://catalog.test/") };
        var catalog = new HttpPolicyReferenceCatalog(
            client,
            CreateConfiguration(),
            NullLogger<HttpPolicyReferenceCatalog>.Instance);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() => catalog.ExistsAsync(
            new PolicyReferenceLookup("SPEND_CATEGORY", Guid.NewGuid(), 2, new string('a', 64)),
            TestContext.Current.CancellationToken));
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Policy:ReferenceCatalog:CatalogId"] = "SPEND_CATEGORY",
            ["Policy:ReferenceCatalog:ContractVersion"] = "v2"
        })
        .Build();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri?.PathAndQuery.TrimStart('/');
            return Task.FromResult(responder(request));
        }
    }
}
