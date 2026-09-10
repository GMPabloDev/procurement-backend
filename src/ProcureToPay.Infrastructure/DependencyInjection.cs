using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Storage.S3;

namespace ProcureToPay.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException(
                "Connection string 'ConnectionStrings:SqlServer' is not configured.");

        services.AddDbContext<ProcureToPayDbContext>(options =>
            options.UseSqlServer(connectionString));
        services.AddScoped<OrganizationBootstrapper>();
        services.AddScoped<CurrentUserProvisioningService>();
        services.AddScoped<OrganizationEligibilityService>();
        services.AddScoped<PolicyPersistenceService>();
        services.AddSingleton<PolicyWorkloadAllowlist>();
        // pi-lens-ignore: CS0246
        services.AddScoped<PolicyFactProviderRegistry>();
        var workflowUrl = configuration["Policy:ExceptionWorkflow:BaseUrl"];
        if (string.IsNullOrWhiteSpace(workflowUrl))
        {
            services.AddScoped<IQuotationWaiverVerifier, DefaultDenyQuotationWaiverVerifier>();
        }
        else if (Uri.TryCreate(workflowUrl, UriKind.Absolute, out var workflowBaseAddress))
        {
            // pi-lens-ignore: lsp:CS1061
            services.AddHttpClient<HttpQuotationWaiverVerifier>(client =>
            {
                client.BaseAddress = workflowBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(5);
            });
            services.AddScoped<IQuotationWaiverVerifier>(provider =>
                provider.GetRequiredService<HttpQuotationWaiverVerifier>());
        }
        else
        {
            throw new InvalidOperationException("Policy exception workflow base address is invalid.");
        }
        services.AddScoped<PolicyExceptionVerifierRegistry>();
        services.AddScoped<IPolicyReferenceCatalog, DefaultDenyPolicyReferenceCatalog>();
        services.AddScoped<PolicyReferenceCatalogRegistry>();
        services.AddScoped<PolicyEvaluationService>();
        services.AddScoped<IOrganizationEligibilityService>(provider =>
            provider.GetRequiredService<OrganizationEligibilityService>());

        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonS3>();
        services.AddSingleton(S3StorageOptions.FromConfiguration(configuration));
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
