using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Storage.S3;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

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
        services.AddScoped<IApprovalSubmissionAdapter>(provider =>
            new PolicyApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                PolicyApprovalAdapter.ContractVersion));
        // SPEC 08 DEC-08: the v3 contract projects the complete budget parameters (Fiscal Year,
        // Spend Category and one demand per covered target) from the confirmed request version.
        services.AddScoped<IApprovalSubmissionAdapter>(provider =>
            new PolicyApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                PolicyApprovalAdapter.ContractVersionV3,
                provider.GetRequiredService<ProcureToPay.Application.Abstractions.IBudgetDemandBuilder>()));
        // SPEC 09 REQ-10: new submissions use v4, which keeps the v3 budget projection and
        // partitions every REQUIRE_ACTIVE_SUPPLIER control by supplier reference.
        services.AddScoped<IApprovalSubmissionAdapter>(provider =>
            new PolicyApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                PolicyApprovalAdapter.ContractVersionV4,
                provider.GetRequiredService<ProcureToPay.Application.Abstractions.IBudgetDemandBuilder>()));
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
        // SPEC 09 REQ-03: a plain human decision publishes approval-result/v2, so the router of that
        // contract dispatches every subject to the domain that owns it; the supplier consumer is the
        // one that materializes an approved supplier or catalogue change.
        services.AddScoped<IApprovalResultConsumer>(provider => new ApprovalResultRouter(
            new PurchaseRequestApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService>(),
                provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
                ApprovalOutboxPolicy.ContractVersion),
            new SupplierApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<SupplierGovernanceService>(),
                provider.GetRequiredService<ApprovedSupplierCatalogGovernanceService>(),
                provider.GetRequiredService<ILogger<SupplierApprovalResultConsumer>>(),
                ApprovalOutboxPolicy.ContractVersion),
            provider.GetRequiredService<ILogger<ApprovalResultRouter>>(),
            ApprovalOutboxPolicy.ContractVersion));
        // SPEC 09 REQ-03: the same contract version is consumed by exactly one consumer, so a
        // router dispatches every delivered event to the domain that owns its subject.
        services.AddScoped<IApprovalResultConsumer>(provider => new ApprovalResultRouter(
            new PurchaseRequestApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService>(),
                provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
                "approval-result/v3"),
            new SupplierApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<SupplierGovernanceService>(),
                provider.GetRequiredService<ApprovedSupplierCatalogGovernanceService>(),
                provider.GetRequiredService<ILogger<SupplierApprovalResultConsumer>>(),
                "approval-result/v3"),
            provider.GetRequiredService<ILogger<ApprovalResultRouter>>(),
            "approval-result/v3"));
        services.AddScoped<IApprovalResultConsumer>(provider => new ApprovalResultRouter(
            new PurchaseRequestApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService>(),
                provider.GetRequiredService<ILogger<PurchaseRequestApprovalResultConsumer>>(),
                "approval-case-lifecycle/v1"),
            new SupplierApprovalResultConsumer(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<SupplierGovernanceService>(),
                provider.GetRequiredService<ApprovedSupplierCatalogGovernanceService>(),
                provider.GetRequiredService<ILogger<SupplierApprovalResultConsumer>>(),
                "approval-case-lifecycle/v1"),
            provider.GetRequiredService<ILogger<ApprovalResultRouter>>(),
            "approval-case-lifecycle/v1"));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPolicyFactProvider>();
        services.AddScoped<IPolicyFactProvider>(provider =>
            provider.GetRequiredService<
                ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestPolicyFactProvider>());
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs.ReferenceCatalogPersistenceService>();
        // SPEC 08: the budget ledger, its precheck orchestration, the server-side demand builder and
        // the real owner of the REQUIRE_BUDGET_CHECK prerequisite (REQ-01..REQ-06).
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPersistenceService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetLedgerService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrecheckService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestBudgetDemandBuilder>();
        services.AddScoped<ProcureToPay.Application.Abstractions.IBudgetDemandBuilder>(provider =>
            provider.GetRequiredService<
                ProcureToPay.Infrastructure.Persistence.PurchaseRequests.PurchaseRequestBudgetDemandBuilder>());
        services.AddScoped(provider =>
            new ProcureToPay.Infrastructure.Persistence.PurchaseRequests.BudgetDemandBuilderRegistry(
                provider.GetServices<ProcureToPay.Application.Abstractions.IBudgetDemandBuilder>()));
        // SPEC 08 REQ-07/REQ-09: the real owner processor and the closed producer registry. A
        // producer only posts with an explicit configuration entry; none ships enabled.
        services.AddSingleton(provider =>
            new ProcureToPay.Infrastructure.Persistence.Budget.BudgetMovementProducerRegistry(configuration));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Budget.BudgetReleaseService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Budget.BudgetTransitionService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.BudgetLedger.BudgetPrerequisiteProcessor>();
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

        // SPEC 09: Supplier Master, approved supplier catalog, its owners and the real processor of
        // the active-supplier prerequisite.
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierPersistenceService>();
        services.AddScoped<
            ProcureToPay.Infrastructure.Persistence.Suppliers.ApprovedSupplierCatalogGovernanceService>();
        services.AddScoped<ProcureToPay.Application.Abstractions.IApprovedSupplierFactOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Suppliers.ApprovedSupplierFactOwner(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<
                    ProcureToPay.Infrastructure.Persistence.Suppliers.ApprovedSupplierCatalogGovernanceService>()));
        services.AddScoped<ProcureToPay.Application.Abstractions.IPurchaseRequestReferenceOwner>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierReferenceOwner(
                provider.GetRequiredService<ProcureToPayDbContext>()));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Policy.IPolicyReferenceCatalog>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierPolicyReferenceCatalog(
                provider.GetRequiredService<ProcureToPayDbContext>()));
        services.AddScoped<ProcureToPay.Application.Abstractions.IApprovalSubmissionAdapter>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierGovernanceApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                configuration,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.ApprovalSubjectType,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.SupplierApprovalOperation,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.ApprovalTargetType));
        services.AddScoped<ProcureToPay.Application.Abstractions.IApprovalSubmissionAdapter>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierGovernanceApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                configuration,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.CatalogApprovalSubjectType,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.CatalogApprovalOperation,
                ProcureToPay.Domain.Modules.Suppliers.SupplierCodes.CatalogApprovalTargetType));
        // SPEC 09 REQ-05: the banking key provider is external to SQL; without a configured key the
        // resolution fails closed instead of degrading to plaintext.
        services.AddSingleton<ProcureToPay.Application.Abstractions.ISupplierBankingKeyProvider>(
            provider => new ProcureToPay.Infrastructure.Persistence.Suppliers.ConfigurationSupplierBankingKeyProvider(
                configuration));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierBankingService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierGovernanceService>();
        services.AddSingleton<ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierProcessorIdentity>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierPrerequisiteProcessor>();

        // SPEC 10: sourcing processes, RFQ lifecycle, quotation evidence and the quotation count.
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingProcessService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingQuotationService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingQuotationQueries>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingEvaluationService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingSelectionService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingWaiverService>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingProposalService>();
        // SPEC 10 REQ-12/REQ-14: the award publisher is the same object that verifies consumption
        // in-process, so a consumer and the publisher can never disagree about eligibility.
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingAwardService>();
        // SPEC 10 REQ-13: the real processors of the two PROCUREMENT-stage owners.
        services.AddSingleton<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingProcessorIdentity>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingPrerequisiteProcessor>();
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.IAwardConsumptionVerifier>(
            provider => provider.GetRequiredService<
                ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingAwardService>());
        // SPEC 10 REQ-11: the proposal approval adapter reuses the SPEC 05 control projection under
        // the published sourcing identity.
        services.AddScoped<IApprovalSubmissionAdapter>(provider =>
            new ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingProposalApprovalAdapter(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                new PolicyApprovalAdapter(
                    provider.GetRequiredService<ProcureToPayDbContext>(),
                    PolicyApprovalAdapter.ContractVersionV4)));
        // SPEC 10 REQ-10: the typed sourcing fact provider and its exact-one registry.
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.ISourcingPolicyFactProvider>(
            provider => new ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingPolicyFactProvider(
                provider.GetRequiredService<ProcureToPayDbContext>(),
                provider.GetRequiredService<IPolicyFactProviderRegistry>()));
        services.AddScoped<ProcureToPay.Infrastructure.Persistence.Sourcing.ISourcingPolicyFactProviderRegistry>(
            provider => new ProcureToPay.Infrastructure.Persistence.Sourcing.SourcingPolicyFactProviderRegistry(
                provider.GetServices<
                    ProcureToPay.Infrastructure.Persistence.Sourcing.ISourcingPolicyFactProvider>()));

        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonS3>();
        services.AddSingleton(S3StorageOptions.FromConfiguration(configuration));
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
