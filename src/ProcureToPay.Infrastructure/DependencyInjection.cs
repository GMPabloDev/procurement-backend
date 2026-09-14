using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
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
        services.AddSingleton<IPolicyWorkloadAllowlist>(provider =>
            provider.GetRequiredService<PolicyWorkloadAllowlist>());
        // pi-lens-ignore: CS0246
        services.AddScoped<PolicyFactProviderRegistry>();
        services.AddScoped<IPolicyFactProviderRegistry>(provider =>
            provider.GetRequiredService<PolicyFactProviderRegistry>());
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
            var tokenEndpoint = configuration["Policy:ExceptionWorkflow:TokenEndpoint"];
            if (!string.IsNullOrWhiteSpace(tokenEndpoint))
            {
                if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenBaseAddress))
                {
                    throw new InvalidOperationException("Policy exception token endpoint is invalid.");
                }

                services.AddHttpClient<ClientCredentialsPolicyExceptionTokenProvider>(client =>
                {
                    client.BaseAddress = tokenBaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(5);
                });
                services.AddScoped<IPolicyExceptionServiceTokenProvider>(provider =>
                    provider.GetRequiredService<ClientCredentialsPolicyExceptionTokenProvider>());
            }
            else
            {
                // No credential source: the verifier fails closed with 503 instead of calling
                // the workflow unauthenticated (SPEC 05 NFR-03).
                services.AddSingleton<IPolicyExceptionServiceTokenProvider, UnavailablePolicyExceptionTokenProvider>();
            }
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
            // SPEC 07 REQ-05: the reference catalogs own the COST_CENTER and SPEND_CATEGORY
            // families; these registrations are exact-one and organization-bound.
            services.AddScoped<IPolicyReferenceCatalog>(provider =>
                new CostCenterPolicyReferenceCatalog(
                    provider.GetRequiredService<ProcureToPayDbContext>()));
            services.AddScoped<IPolicyReferenceCatalog>(provider =>
                new SpendCategoryPolicyReferenceCatalog(
                    provider.GetRequiredService<ProcureToPayDbContext>()));
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
        services.AddSingleton<IApprovalWorkloadAllowlist>(provider =>
            provider.GetRequiredService<ApprovalWorkloadAllowlist>());
        services.AddSingleton<ApprovalInstanceIdentity>();
        services.AddSingleton<IApprovalOwnerWorkloadRegistry>(provider =>
            new ApprovalOwnerWorkloadRegistry(
                configuration,
                provider.GetRequiredService<ApprovalWorkloadAllowlist>()));
        services.AddScoped<ApprovalContractPreflight>();
        services.AddScoped<IApprovalSubmissionAdapterRegistry>(provider =>
            new ApprovalSubmissionAdapterRegistry(provider.GetServices<IApprovalSubmissionAdapter>()));
        services.AddScoped<ApprovalSubmissionService>();
        services.AddScoped<IApprovalSubmissionAdapter, PolicyApprovalAdapter>();
        services.AddScoped<PolicyExceptionSubmissionService>();
        services.AddScoped<PolicyExceptionVerificationService>();
        services.AddScoped<ApprovalWorkflowService>();
        services.AddScoped<ApprovalDecisionService>();
        services.AddScoped<ApprovalQueryService>();
        services.AddScoped<ApprovalScopeResolver>();
        services.AddScoped<ApprovalAssignmentEngine>();
        services.AddScoped<ApprovalReconciliationService>();
        services.AddScoped<ApprovalDelegationService>();
        services.AddScoped<ApprovalDelegationTransitionProcessor>();
        services.AddScoped<ApprovalSupersessionService>();
        services.AddScoped<ApprovalEvidenceRevocationService>();
        services.AddScoped<ApprovalHistoryQueryService>();
        services.AddScoped<ApprovalOperationsQueryService>();
        services.AddScoped<ApprovalInboxQueryService>();
        services.AddScoped<ApprovalOutboxAdministrationService>();
        services.AddScoped<IApprovalResultConsumerRegistry>(provider =>
            new ApprovalResultConsumerRegistry(provider.GetServices<IApprovalResultConsumer>()));
        services.AddScoped<ApprovalOutboxDispatcher>();

        // SPEC 06: immutable Purchase Requests, owner attestation and the real in-process fact
        // provider evaluated by the SPEC 02 engine (REQ-05, DEC-03).
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPersistenceService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestAttestationService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestSubmissionService>();
        // SPEC 06 REQ-09: one consumer per result contract version plus the SPEC 04 lifecycle.
        services.AddScoped<IApprovalResultConsumer>(provider => new PurchaseRequestApprovalResultConsumer(
            provider.GetRequiredService<ProcureToPayDbContext>(),
            provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
            "approval-result/v2"));
        services.AddScoped<IApprovalResultConsumer>(provider => new PurchaseRequestApprovalResultConsumer(
            provider.GetRequiredService<ProcureToPayDbContext>(),
            provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
            "approval-result/v3"));
        services.AddScoped<IApprovalResultConsumer>(provider => new PurchaseRequestApprovalResultConsumer(
            provider.GetRequiredService<ProcureToPayDbContext>(),
            provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
            "approval-case-lifecycle/v1"));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPolicyFactProvider>();
        services.AddScoped<IPolicyFactProvider>(provider =>
            provider.GetRequiredService<
                ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPolicyFactProvider>());
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs.ReferenceCatalogPersistenceService>();
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.OrganizationReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                ProcureToPay.Domain.Modules.PurchaseRequests.PurchaseRequestReferenceType.LegalEntity));
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.OrganizationReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                ProcureToPay.Domain.Modules.PurchaseRequests.PurchaseRequestReferenceType.User));
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.OrganizationReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                ProcureToPay.Domain.Modules.PurchaseRequests.PurchaseRequestReferenceType.Department));
        // SPEC 07 REQ-06: the two Cost Center slots and the Spend Category slot are declared
        // explicitly; the registry rejects missing or wildcard registrations.
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.CostCenterReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                ProcureToPay.Domain.Modules.PurchaseRequests.PurchaseRequestAssertionType.ActiveInOrganization));
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.CostCenterReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                ProcureToPay.Domain.Modules.PurchaseRequests.PurchaseRequestAssertionType.CostCenterOwnedByDepartment));
        services.AddScoped<IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.SpendCategoryReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>()));
        services.AddScoped<IPurchaseRequestReferenceOwnerRegistry>(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestReferenceOwnerRegistry(
                provider.GetServices<IPurchaseRequestReferenceOwner>()));

        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonS3>();
        services.AddSingleton(S3StorageOptions.FromConfiguration(configuration));
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
