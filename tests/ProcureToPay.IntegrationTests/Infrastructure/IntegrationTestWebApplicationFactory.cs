using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ProcureToPay.IntegrationTests.Infrastructure;

public sealed class IntegrationTestWebApplicationFactory(IntegrationTestEnvironment environment)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = environment.SqlServer.GetConnectionString(),
                ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                ["AWS:Region"] = "us-east-1",
                ["AWS:ServiceURL"] = environment.LocalStack.GetConnectionString(),
                ["Storage:S3:BucketName"] = "procure-to-pay-tests"
            });
        });
    }
}
