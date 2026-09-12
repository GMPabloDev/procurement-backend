using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Approval;
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
        var catalogUrl = configuration["Policy:ReferenceCatalog:BaseUrl"];
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            services.AddScoped<IPolicyReferenceCatalog>(provider =>
                new DatabasePolicyReferenceCatalog(
                    provider.GetRequiredService<ProcureToPayDbContext>(), "DEPARTMENT"));
            services.AddScoped<IPolicyReferenceCatalog>(provider =>
                new DatabasePolicyReferenceCatalog(
                    provider.GetRequiredService<ProcureToPayDbContext>(), "LEGAL_ENTITY"));
        }
        else if (Uri.TryCreate(catalogUrl, UriKind.Absolute, out var catalogBaseAddress))
        {
            // pi-lens-ignore: lsp:CS1061
            services.AddHttpClient<HttpPolicyReferenceCatalog>(client =>
            {
                client.BaseAddress = catalogBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(5);
            });
            services.AddScoped<IPolicyReferenceCatalog>(provider =>
                provider.GetRequiredService<HttpPolicyReferenceCatalog>());
        }
        else
        {
            throw new InvalidOperationException("Policy reference catalog base address is invalid.");
        }
        services.AddScoped<PolicyReferenceCatalogRegistry>();
        services.AddScoped<PolicyEvaluationService>();
        services.AddScoped<IOrganizationEligibilityService>(provider =>
            provider.GetRequiredService<OrganizationEligibilityService>());

        services.AddSingleton<ApprovalWorkloadAllowlist>();
        services.AddSingleton<ApprovalInstanceIdentity>();
        services.AddSingleton<IApprovalOwnerWorkloadRegistry>(provider =>
            new ApprovalOwnerWorkloadRegistry(
                configuration,
                provider.GetRequiredService<ApprovalWorkloadAllowlist>()));
        services.AddScoped<ApprovalContractPreflight>();
        services.AddScoped<IApprovalSubmissionAdapterRegistry>(provider =>
            new ApprovalSubmissionAdapterRegistry(provider.GetServices<IApprovalSubmissionAdapter>()));
        services.AddScoped<ApprovalSubmissionService>();
        services.AddScoped<ApprovalWorkflowService>();
        services.AddScoped<ApprovalDecisionService>();
        services.AddScoped<ApprovalQueryService>();
        services.AddScoped<ApprovalScopeResolver>();
        services.AddScoped<ApprovalAssignmentEngine>();
        services.AddScoped<ApprovalReconciliationService>();
        services.AddScoped<ApprovalOperationsQueryService>();
        services.AddScoped<ApprovalInboxQueryService>();
        services.AddScoped<ApprovalOutboxAdministrationService>();
        services.AddScoped<IApprovalResultConsumerRegistry>(provider =>
            new ApprovalResultConsumerRegistry(provider.GetServices<IApprovalResultConsumer>()));
        services.AddScoped<ApprovalOutboxDispatcher>();

        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonS3>();
        services.AddSingleton(S3StorageOptions.FromConfiguration(configuration));
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
