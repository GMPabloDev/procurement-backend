using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ProcureToPay.ApiE2ETests;

public sealed class UnitTest1
{
    [Fact]
    public async Task Protected_organization_endpoint_requires_a_bearer_token()
    {
        await using var factory = new TestApiFactory();
        // pi-lens-ignore: lsp:CS1061, lsp:CS0246
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.GetAsync(
            "/api/v1/organization",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer",
                "Server=localhost;Database=ProcureToPay_ApiTests;User Id=sa;Password=NotUsed_123!;TrustServerCertificate=True");
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] =
                        "Server=localhost;Database=ProcureToPay_ApiTests;User Id=sa;Password=NotUsed_123!;TrustServerCertificate=True",
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests"
                });
            });
        }
    }
}
