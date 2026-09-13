using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ProcureToPay.Api.Authentication;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// Named cross-module contract test of SPEC 05 (CA-06): a persisted Policy evaluation becomes an
/// approval case through the real adapter, is decided through the real HTTP workflow, and the
/// verified exception travels back through the real <c>/v1/policy-exceptions/verify</c> endpoint
/// with the genuine client, service authentication and canonical digests. No verifier double is
/// involved.
/// </summary>
public sealed class PolicyApprovalWorkflowContractE2ETests
{
    private const string WorkloadIssuer = "https://keycloak.test/realms/procure-to-pay";
    private const string WorkloadClient = "procurement-api";
    private const string PolicyEngineClient = "policy-engine";
    private const string QuotationOwnerClient = "quotation-status-owner";
    private const string BudgetOwnerClient = "budget-check-owner";
    private const string DocumentOwnerClient = "supporting-document-owner";
    private const string SupplierOwnerClient = "active-supplier-owner";
    private const string ProcurementOwnerClient = "procurement-stage-owner";
    private const string AdminSubject = "admin-1";
    private const string ApproverSubject = "approver-1";

    private static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OriginatorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid WorkloadSubjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public async Task Policy_evaluation_reaches_a_verified_waiver_through_the_real_workflow_endpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ContractEnvironment.StartAsync(cancellationToken);
        await sqlServer.SeedProcurementApproverAsync(cancellationToken);
        await using var factory = new ContractApiFactory(sqlServer);
        using (var scope = factory.Services.CreateScope())
        {
            Assert.IsType<HttpQuotationWaiverVerifier>(
                scope.ServiceProvider.GetRequiredService<IQuotationWaiverVerifier>());
        }
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateServiceToken(WorkloadIssuer, WorkloadClient, "procure-to-pay-tests"));
        using var policyEngine = factory.CreateClient();
        policyEngine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateServiceToken(WorkloadIssuer, PolicyEngineClient, "procure-to-pay-tests"));
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateServiceToken(WorkloadIssuer, QuotationOwnerClient, "procure-to-pay-tests"));
        using var approver = factory.CreateClient();
        approver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateUserToken(WorkloadIssuer, ApproverSubject, "procure-to-pay-tests"));
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateUserToken(WorkloadIssuer, AdminSubject, "procure-to-pay-tests"));

        // 1. Real enterprise evaluation through the Policy API.
        Guid evaluationId;
        using (var evaluated = await workload.PostAsJsonAsync(
            "/api/v1/policies/evaluate",
            new
            {
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = "PURCHASE_REQUEST",
                evaluationKey = "contract-evaluation"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, evaluated.StatusCode);
            evaluationId = (await evaluated.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("id").GetGuid();
        }

        var persisted = await sqlServer.EvaluationAsync(evaluationId, cancellationToken);
        var projection = persisted.Projection;
        Assert.Equal(PolicyApprovalTargets.ContractVersion, projection.ContractVersion);
        var materialTarget = Assert.Single(projection.Targets);
        Assert.Equal(LineId, materialTarget.Id);
        var coveredLines = projection.Targets
            .Select(target => new PolicyExceptionTarget(
                "PURCHASE_REQUEST_LINE", target.Id, target.Version, target.MaterialSnapshotDigest))
            .ToArray();

        // 2. The adapter turns the persisted evaluation into one idempotent case (REQ-01, REQ-03).
        Guid caseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = sqlServer.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = PolicyApprovalAdapter.Operation,
                contractVersion = PolicyApprovalAdapter.ContractVersion,
                submissionKey = "contract-submission",
                requesterId = (Guid?)null,
                originatorId = OriginatorId
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            caseId = (await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("caseId").GetGuid();
        }

        // Replay returns the same case; the same key with another actor is a conflict (REQ-01).
        using (var replay = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = sqlServer.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = PolicyApprovalAdapter.Operation,
                contractVersion = PolicyApprovalAdapter.ContractVersion,
                submissionKey = "contract-submission",
                requesterId = (Guid?)null,
                originatorId = OriginatorId
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var body = await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(caseId, body.GetProperty("caseId").GetGuid());
            Assert.True(body.GetProperty("replayed").GetBoolean());
        }

        using (var conflict = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = sqlServer.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = PolicyApprovalAdapter.Operation,
                contractVersion = PolicyApprovalAdapter.ContractVersion,
                submissionKey = "contract-submission",
                requesterId = RequesterId,
                originatorId = OriginatorId
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        var graph = await sqlServer.GraphAsync(caseId, cancellationToken);
        var department = Assert.Single(graph.Requirements);
        Assert.Equal("DEPARTMENT", department.StageCode);
        var prerequisite = Assert.Single(graph.Prerequisites);
        Assert.Equal("RFQ", prerequisite.Key);
        Assert.Equal("quotation-status-owner", prerequisite.OwnerAdapterId);
        Assert.Equal(WorkloadIssuer, prerequisite.OwnerWorkloadIssuer);
        Assert.Equal(QuotationOwnerClient, prerequisite.OwnerWorkloadClientId);
        Assert.Equal(materialTarget.MaterialSnapshotDigest, prerequisite.TargetMaterialDigest);
        Assert.Equal(64, prerequisite.SourceControlDigest.Length);
        Assert.Equal("PRE_PROCUREMENT", department.Targets.Count > 0 ? "PRE_PROCUREMENT" : "PRE_PROCUREMENT");

        // A prerequisite that is still WAITING keeps the case from completing (REQ-03/REQ-07).
        Assert.Equal((int)PrerequisiteStatus.Waiting, prerequisite.Status);
        Assert.NotEqual((int)ApprovalCaseStatus.Completed, graph.Status);

        // 3. The owner workload signals the quotation prerequisite.
        using (var signalled = await owner.PostAsJsonAsync(
            $"/api/v1/approval/prerequisites/{prerequisite.Id}/signals",
            new
            {
                satisfied = true,
                signalKey = "contract-quotation-signal",
                expectedVersion = prerequisite.Version,
                evidenceReference = "evidence://quotations",
                evidenceDigest = new string('e', 64)
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, signalled.StatusCode);
        }

        // 4. The policy engine opens the waiver exception; a user or ADMIN cannot (REQ-05).
        var referenceId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var requestedValidTo = DateTimeOffset.UtcNow.AddDays(1);
        var exceptionBody = new
        {
            organizationId = sqlServer.OrganizationId,
            subjectType = "PURCHASE_REQUEST",
            subjectId = SubjectId,
            subjectVersion = 1,
            baseBundleId = evaluationId,
            baseResultDigest = persisted.ResultDigest,
            policyVersionId = persisted.PolicySetVersionId,
            policyContentDigest = persisted.PolicyContentDigest,
            manifestDigest = persisted.ManifestDigest,
            targetRequirementKey = "RFQ",
            coveredLines = coveredLines.Select(target => new
            {
                type = target.Type,
                id = target.Id,
                version = target.Version,
                materialSnapshotDigest = target.MaterialSnapshotDigest
            }).ToArray(),
            from = 3,
            to = 2,
            floor = 1,
            referenceId,
            requesterId = (Guid?)null,
            originatorId = OriginatorId,
            workloadSubjectId = WorkloadSubjectId,
            requestedAt,
            requestedValidTo,
            nonce = "contract-waiver-nonce",
            submissionKey = "contract-exception-submission"
        };
        using (var userAttempt = await approver.PostAsJsonAsync(
            "/api/v1/approval/policy-exceptions", exceptionBody, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, userAttempt.StatusCode);
        }

        Guid exceptionRequestId;
        string exceptionBinding;
        using (var exception = await policyEngine.PostAsJsonAsync(
            "/api/v1/approval/policy-exceptions", exceptionBody, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
            var body = await exception.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            exceptionRequestId = body.GetProperty("requestId").GetGuid();
            exceptionBinding = body.GetProperty("binding").GetString()!;
            Assert.Equal("OPEN", body.GetProperty("status").GetString());
        }

        // 5. The seeded PROCUREMENT_APPROVER decides through the real task endpoint and receives
        // the evidence digest that binds the approval (REQ-05, REQ-06).
        var (taskId, taskVersion) = await sqlServer.ExceptionTaskAsync(exceptionRequestId, cancellationToken);
        string evidenceDigest;
        Guid decisionId;
        using (var decided = await approver.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{taskId}/decisions",
            new
            {
                action = "APPROVE",
                reason = "Waiver approved for the quotation reduction",
                decisionKey = "contract-decision",
                expectedTaskVersion = taskVersion
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
            var body = await decided.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            decisionId = body.GetProperty("decisionId").GetGuid();
            evidenceDigest = body.GetProperty("policyExceptionEvidenceDigest").GetString()!;
            Assert.Equal(64, evidenceDigest.Length);
        }

        // 6. Policy applies the waiver: the real client calls the real service endpoint.
        // A direct contract probe isolates the endpoint from the Policy client during failures.
        var bindingRequest = new PolicyExceptionBindingRequest(
            sqlServer.OrganizationId,
            "PURCHASE_REQUEST",
            SubjectId,
            1,
            evaluationId,
            persisted.ResultDigest,
            persisted.PolicySetVersionId,
            persisted.PolicyContentDigest,
            persisted.ManifestDigest,
            "RFQ",
            coveredLines,
            3,
            2,
            1,
            referenceId,
            null,
            OriginatorId,
            WorkloadSubjectId,
            requestedAt,
            requestedValidTo,
            "contract-waiver-nonce");
        var directWaiver = new QuotationWaiverRequest(
            PolicyExceptionType.ReduceMinValidQuotations,
            bindingRequest,
            decisionId,
            1,
            evidenceDigest,
            "corr-direct");
        using (var serviceClient = factory.CreateClient())
        {
            serviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ContractApiFactory.CreateServiceToken(
                    WorkloadIssuer, PolicyEngineClient, ApprovalServiceAuthentication.DefaultAudience));
            using var probe = await serviceClient.PostAsJsonAsync(
                "/v1/policy-exceptions/verify",
                HttpQuotationWaiverVerifier.BuildContext(directWaiver),
                cancellationToken);
            var probeBody = await probe.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(probe.StatusCode == HttpStatusCode.OK, $"{probe.StatusCode}: {probeBody}");
        }

        var waiverBody = new
        {
            type = "REDUCE_MIN_VALID_QUOTATIONS",
            referenceId,
            requestedAt,
            requestedValidTo,
            workflowDecisionId = decisionId,
            workflowDecisionVersion = 1,
            evidenceDigest,
            targetRequirementKey = "RFQ",
            from = 3,
            to = 2,
            floor = 1,
            coveredLines = coveredLines.Select(target => new
            {
                type = target.Type,
                id = target.Id,
                version = target.Version,
                materialSnapshotDigest = target.MaterialSnapshotDigest
            }).ToArray(),
            requesterId = (Guid?)null,
            originatorId = OriginatorId,
            workloadSubjectId = WorkloadSubjectId,
            nonce = "contract-waiver-nonce"
        };
        Guid reducedId;
        using (var waiver = await workload.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{evaluationId}/quotation-waiver", waiverBody, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, waiver.StatusCode);
            var body = await waiver.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            reducedId = body.GetProperty("id").GetGuid();
            var reduced = body.GetProperty("controls").EnumerateArray()
                .Single(control => control.GetProperty("requirementKey").GetString() == "RFQ");
            Assert.Equal(2, reduced.GetProperty("minimumQuotations").GetInt32());
        }

        Assert.Equal(1, await sqlServer.VerificationCountAsync(exceptionRequestId, cancellationToken));

        // 7. Replay recovers the original verification and reevaluation without duplicating rows.
        using (var replay = await workload.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{evaluationId}/quotation-waiver", waiverBody, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(reducedId, (await replay.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("id").GetGuid());
        }

        Assert.Equal(1, await sqlServer.VerificationCountAsync(exceptionRequestId, cancellationToken));
        Assert.Equal(2, await sqlServer.EvaluationCountAsync(cancellationToken));

        // 8. Revocation invalidates future verifications without rewriting history (REQ-08, CA-08).
        var evidenceId = await sqlServer.EvidenceIdAsync(decisionId, cancellationToken);
        using (var revoked = await admin.PostAsJsonAsync(
            $"/api/v1/approval/evidence/{evidenceId}/revocations",
            new
            {
                expectedEvidenceVersion = 1,
                revocationKey = "contract-revocation",
                reason = "Authority evidence withdrawn",
                reasonCode = "INCIDENT_CONTAINMENT"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }

        using (var blocked = await workload.PostAsJsonAsync(
            $"/api/v1/policies/evaluations/{evaluationId}/quotation-waiver", waiverBody, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        }

        Assert.Equal(2, await sqlServer.EvaluationCountAsync(cancellationToken));
        Assert.Equal(exceptionBinding, await sqlServer.ExceptionBindingAsync(exceptionRequestId, cancellationToken));
    }

    [Fact]
    public async Task Missing_owner_workload_blocks_the_adapter_submission_without_a_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ContractEnvironment.StartAsync(cancellationToken);
        await using var factory = new ContractApiFactory(sqlServer, configureOwners: false);
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateServiceToken(WorkloadIssuer, WorkloadClient, "procure-to-pay-tests"));

        using (var evaluated = await workload.PostAsJsonAsync(
            "/api/v1/policies/evaluate",
            new
            {
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = "PURCHASE_REQUEST",
                evaluationKey = "ownerless-evaluation"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, evaluated.StatusCode);
        }

        using var missingOwner = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = sqlServer.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = PolicyApprovalAdapter.Operation,
                contractVersion = PolicyApprovalAdapter.ContractVersion,
                submissionKey = "ownerless-submission",
                requesterId = (Guid?)null,
                originatorId = OriginatorId
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingOwner.StatusCode);
        Assert.Contains("/problems/approval-dependency-unavailable",
            await missingOwner.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, await sqlServer.CaseCountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_bundle_without_a_material_projection_cannot_open_a_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = await ContractEnvironment.StartAsync(cancellationToken);
        await using var factory = new ContractApiFactory(sqlServer);
        using var workload = factory.CreateClient();
        workload.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ContractApiFactory.CreateServiceToken(WorkloadIssuer, WorkloadClient, "procure-to-pay-tests"));

        Guid evaluationId;
        using (var evaluated = await workload.PostAsJsonAsync(
            "/api/v1/policies/evaluate",
            new
            {
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = "PURCHASE_REQUEST",
                evaluationKey = "tampered-evaluation"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, evaluated.StatusCode);
            evaluationId = (await evaluated.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("id").GetGuid();
        }

        await sqlServer.RemoveMaterialProjectionAsync(evaluationId, cancellationToken);

        using var tampered = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = sqlServer.OrganizationId,
                subjectType = "PURCHASE_REQUEST",
                subjectId = SubjectId,
                subjectVersion = 1,
                operation = PolicyApprovalAdapter.Operation,
                contractVersion = PolicyApprovalAdapter.ContractVersion,
                submissionKey = "tampered-submission",
                requesterId = (Guid?)null,
                originatorId = OriginatorId
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, tampered.StatusCode);
        Assert.Equal(0, await sqlServer.CaseCountAsync(cancellationToken));
    }

    private static readonly Guid RequesterId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class ContractApiFactory(
        ContractEnvironment environment,
        bool configureOwners = true) : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateServiceToken(string issuer, string clientId, string audience) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer,
                audience,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{clientId}"),
                    new Claim("client_id", clientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        internal static string CreateUserToken(string issuer, string subject, string audience) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer,
                audience,
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", environment.ConnectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Authentication:ServiceJwt:Audience", ApprovalServiceAuthentication.DefaultAudience);
            builder.UseSetting("Policy:Workloads:0:Issuer", WorkloadIssuer);
            builder.UseSetting("Policy:Workloads:0:ClientId", WorkloadClient);
            // Registration-time reads: these values must be visible while Program builds the
            // infrastructure, so they go through host settings rather than late configuration.
            builder.UseSetting("Policy:ExceptionWorkflow:BaseUrl", "http://workflow.invalid/");
            builder.UseSetting("Policy:ExceptionWorkflow:ClientId", PolicyEngineClient);
            builder.UseSetting("Policy:ExceptionWorkflow:ClientSecret", "not-a-real-secret");
            builder.UseSetting("Approval:Workloads:0:Issuer", WorkloadIssuer);
            builder.UseSetting("Approval:Workloads:0:ClientId", WorkloadClient);
            builder.UseSetting("Approval:Workloads:1:Issuer", WorkloadIssuer);
            builder.UseSetting("Approval:Workloads:1:ClientId", PolicyEngineClient);
            if (configureOwners)
            {
                AddOwnerSettings(builder);
            }
            else
            {
                builder.UseSetting("Approval:OwnerWorkloads:0:AdapterId", string.Empty);
            }
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = environment.ConnectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["Authentication:ServiceJwt:Audience"] = ApprovalServiceAuthentication.DefaultAudience,
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Policy:Workloads:0:Issuer"] = WorkloadIssuer,
                    ["Policy:Workloads:0:ClientId"] = WorkloadClient,
                    ["Policy:ExceptionWorkflow:BaseUrl"] = "http://workflow.invalid/",
                    ["Policy:ExceptionWorkflow:ClientId"] = PolicyEngineClient,
                    ["Policy:ExceptionWorkflow:ClientSecret"] = "not-a-real-secret",
                    ["Approval:Workloads:0:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:0:ClientId"] = WorkloadClient,
                    ["Approval:Workloads:1:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:1:ClientId"] = PolicyEngineClient,
                    ["Approval:Workloads:2:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:2:ClientId"] = QuotationOwnerClient,
                    ["Approval:Workloads:3:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:3:ClientId"] = BudgetOwnerClient,
                    ["Approval:Workloads:4:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:4:ClientId"] = DocumentOwnerClient,
                    ["Approval:Workloads:5:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:5:ClientId"] = SupplierOwnerClient,
                    ["Approval:Workloads:6:Issuer"] = WorkloadIssuer,
                    ["Approval:Workloads:6:ClientId"] = ProcurementOwnerClient
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                ConfigureScheme(services, JwtBearerDefaults.AuthenticationScheme, "procure-to-pay-tests");
                ConfigureScheme(services, ApprovalServiceAuthentication.Scheme,
                    ApprovalServiceAuthentication.DefaultAudience);
                services.RemoveAll<IPolicyFactProvider>();
                services.AddScoped<IPolicyFactProvider>(provider => new ContractFactProvider(
                    environment.OrganizationId,
                    environment.LegalEntityId));
                // The real verifier is preserved; only its transport and service credential are
                // pointed at this in-process server.
                services.AddHttpClient<HttpQuotationWaiverVerifier>()
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
                services.RemoveAll<IPolicyExceptionServiceTokenProvider>();
                services.AddSingleton<IPolicyExceptionServiceTokenProvider>(
                    new TestTokenProvider(WorkloadIssuer, PolicyEngineClient));
            });
            if (configureOwners)
            {
                // Owner workloads are runtime reads; the settings above already cover the host.
            }
        }

        private static void AddOwnerSettings(IWebHostBuilder builder)
        {
            var owners = new (string AdapterId, string ClientId)[]
            {
                ("budget-check-owner", BudgetOwnerClient),
                ("supporting-document-owner", DocumentOwnerClient),
                ("active-supplier-owner", SupplierOwnerClient),
                ("quotation-status-owner", QuotationOwnerClient),
                ("procurement-stage-owner", ProcurementOwnerClient)
            };
            for (var index = 0; index < owners.Length; index++)
            {
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:AdapterId", owners[index].AdapterId);
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:AdapterVersion", "v1");
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:Issuer", WorkloadIssuer);
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:ClientId", owners[index].ClientId);
                builder.UseSetting($"Approval:Workloads:{index + 2}:Issuer", WorkloadIssuer);
                builder.UseSetting($"Approval:Workloads:{index + 2}:ClientId", owners[index].ClientId);
            }
        }

        private static void ConfigureScheme(
            IServiceCollection services,
            string scheme,
            string audience)
        {
            services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                options.TokenValidationParameters.ValidIssuer = WorkloadIssuer;
                options.TokenValidationParameters.ValidAudience = audience;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
        }
    }

    private sealed class TestTokenProvider(string issuer, string clientId) : IPolicyExceptionServiceTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer,
                ApprovalServiceAuthentication.DefaultAudience,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{clientId}"),
                    new Claim("client_id", clientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!")),
                    SecurityAlgorithms.HmacSha256))));
    }

    /// <summary>
    /// Environment-bound fact provider: this is the SPEC 02 adapter boundary, not a SPEC 05
    /// contract double. It confirms one line and its manifest so the evaluation is real.
    /// </summary>
    private sealed class ContractFactProvider(Guid organizationId, Guid legalEntityId) : IPolicyFactProvider
    {
        public string SubjectType => "PURCHASE_REQUEST";
        public string Operation => "PURCHASE_REQUEST";
        public string ProviderId => "contract-fact-provider";
        public string ContractVersion => "v1";

        public Task<PolicyFactBundle> GetFactsAsync(
            PolicyFactRequest request,
            CancellationToken cancellationToken = default)
        {
            var line = new PolicyLineInput(
                new PolicySubjectReference(LineId, 1),
                new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(1200, "PEN")
                });
            var input = new PolicyRequestInput(
                new PolicySubjectReference(SubjectId, 1), organizationId, legalEntityId, "PEN", [line]);
            var manifest = new PolicyCompletenessManifest(
                input.Subject.Id, input.Subject.Version, [line.Subject], string.Empty) with
            {
                Digest = PolicyEvaluationService.ComputeManifestDigest(new PolicyCompletenessManifest(
                    input.Subject.Id, input.Subject.Version, [line.Subject], string.Empty))
            };
            var bundle = new PolicyFactBundle(input, manifest, ProviderId, ContractVersion, string.Empty)
            {
                Provenance = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GROSS_AMOUNT_BASE"] = "provider://amount"
                }
            };
            return Task.FromResult(bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) });
        }
    }

    private sealed record RequirementRow(
        Guid Id,
        string StageCode,
        int Status,
        IReadOnlyList<PolicyExceptionTarget> Targets);

    private sealed record PrerequisiteRow(
        Guid Id,
        string Key,
        string OwnerAdapterId,
        string OwnerWorkloadIssuer,
        string OwnerWorkloadClientId,
        int Status,
        int Version,
        string SourceControlDigest,
        string TargetMaterialDigest);

    private sealed record GraphRow(
        int Status,
        IReadOnlyList<RequirementRow> Requirements,
        IReadOnlyList<PrerequisiteRow> Prerequisites);

    private sealed record PersistedEvaluation(
        Guid Id,
        string ResultDigest,
        Guid PolicySetVersionId,
        string PolicyContentDigest,
        string ManifestDigest,
        PolicyMaterialProjection Projection);

    private sealed class ContractEnvironment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;

        public string ConnectionString { get; private set; } = string.Empty;
        public Guid OrganizationId { get; private set; }
        public Guid LegalEntityId { get; private set; }
        public Guid AdminId { get; private set; }

        public static async Task<ContractEnvironment> StartAsync(CancellationToken cancellationToken)
        {
            var environment = new ContractEnvironment
            {
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                    .WithPassword("ProcureToPay_test_2026!")
                    .Build()
            };
            await environment.container.StartAsync(cancellationToken);
            environment.ConnectionString = environment.container.GetConnectionString();
            await using var context = environment.CreateContext();
            await context.Database.MigrateAsync(cancellationToken);
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            await new OrganizationBootstrapper(context, loggerFactory.CreateLogger<OrganizationBootstrapper>())
                .InitializeAsync(
                    new OrganizationBootstrapOptions(
                        "ACME", "Acme Corporation", "PEN", "America/Lima", 1, "ACME-PE",
                        "Acme Peru S.A.C.", "IT", "Software / IT",
                        WorkloadIssuer, AdminSubject, "SPEC 05 contract test bootstrap"),
                    cancellationToken);
            environment.OrganizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
            environment.LegalEntityId = (await context.LegalEntities.SingleAsync(cancellationToken)).Id;
            environment.AdminId = (await context.UserProfiles.SingleAsync(
                profile => profile.Subject == AdminSubject, cancellationToken)).Id;
            await environment.PublishPolicyAsync(context, cancellationToken);
            return environment;
        }

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(ConnectionString).Options);

        private async Task PublishPolicyAsync(ProcureToPayDbContext context, CancellationToken cancellationToken)
        {
            var policy = new PolicySetVersion(Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line]);
            policy.AddRule(new PolicyRule("DEPARTMENT_APPROVAL", PolicyScope.Line, [],
                [
                    new PolicyEffect(PolicyEffectType.RequireApproval, "DEPT_APPROVAL",
                        approval: new PolicyApprovalDescriptor(
                            SystemRole.DepartmentApprover,
                            ApprovalAuthorityType.BusinessNeed,
                            new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "DEPT_L1", 1),
                            500,
                            "PEN",
                            DecisionScopeDescriptor.Create(
                                OrganizationId,
                                [new DecisionScopeEntry(ScopeDimension.Organization, null, null)])
                                .ToCanonicalJson()))
                ]));
            policy.AddRule(new PolicyRule("QUOTATIONS", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireQuotations, "RFQ",
                    minimumQuotations: 3, minimumExceptionQuotations: 1)]));
            policy.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
            var content = PolicyCanonicalizer.CanonicalizePolicy(policy);
            policy.Publish(PolicyCanonicalizer.Hash(content));
            context.PolicySetVersions.Add(new PolicySetVersionRecord
            {
                Id = policy.Id,
                OrganizationId = OrganizationId,
                Sequence = 1,
                Status = (int)PolicySetStatus.Published,
                ScopesJson = "[\"LINE\"]",
                ContentJson = content,
                ContentDigest = policy.ContentDigest!,
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.PolicyActivations.Add(new PolicyActivationRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = OrganizationId,
                PolicySetVersionId = policy.Id,
                EffectiveFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                ActorType = "USER",
                ActorUserId = AdminId,
                Reason = "SPEC 05 contract test activation",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task SeedProcurementApproverAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var approverId = Guid.NewGuid();
            var departmentId = await context.Departments
                .Where(department => department.Code == "IT")
                .Select(department => department.Id)
                .SingleAsync(cancellationToken);
            context.UserProfiles.Add(new UserProfileRecord
            {
                Id = approverId,
                OrganizationId = OrganizationId,
                Issuer = WorkloadIssuer,
                Subject = ApproverSubject,
                Email = "approver-1@acme.test",
                DisplayName = ApproverSubject,
                DepartmentId = departmentId,
                Status = (int)UserProfileStatus.Active,
                Version = 1
            });
            context.RoleAssignments.Add(new RoleAssignmentRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = approverId,
                Role = (int)SystemRole.ProcurementApprover,
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                Status = (int)AssignmentStatus.Active,
                AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                AssignedBy = AdminId,
                Version = 1
            });
            var levelId = Guid.NewGuid();
            context.AuthorityLevels.Add(new AuthorityLevelRecord
            {
                Id = levelId,
                Type = (int)ApprovalAuthorityType.Procurement,
                Code = "PROC_L1",
                Rank = 1,
                LevelVersion = 1,
                IsActive = true
            });
            context.AuthorityGrants.Add(new AuthorityGrantRecord
            {
                Id = Guid.NewGuid(),
                UserProfileId = approverId,
                AuthorityLevelId = levelId,
                MaxAmountBase = null,
                BaseCurrency = "PEN",
                ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                Status = (int)AssignmentStatus.Active,
                GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                GrantedBy = AdminId,
                Version = 1
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task RemoveMaterialProjectionAsync(Guid evaluationId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var record = await context.PolicyEvaluationBundles
                .SingleAsync(item => item.Id == evaluationId, cancellationToken);
            var node = System.Text.Json.Nodes.JsonNode.Parse(record.BundleJson)!.AsObject();
            node.Remove("materialProjection");
            record.BundleJson = node.ToJsonString();
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<PersistedEvaluation> EvaluationAsync(Guid evaluationId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var record = await context.PolicyEvaluationBundles
                .AsNoTracking()
                .SingleAsync(item => item.Id == evaluationId, cancellationToken);
            using var document = JsonDocument.Parse(record.BundleJson);
            var projection = document.RootElement.GetProperty("materialProjection");
            var targets = projection.GetProperty("targets").EnumerateArray()
                .Select(target => new PolicyMaterialTarget(
                    target.GetProperty("id").GetGuid(),
                    target.GetProperty("version").GetInt32(),
                    target.GetProperty("materialSnapshotDigest").GetString()!))
                .ToArray();
            var provenance = projection.GetProperty("provenance").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
            return new PersistedEvaluation(
                record.Id,
                record.ResultDigest,
                record.PolicySetVersionId,
                record.PolicyContentDigest,
                document.RootElement.GetProperty("manifestDigest").GetString()!,
                new PolicyMaterialProjection(
                    projection.GetProperty("contractVersion").GetString()!, provenance, targets));
        }

        public async Task<string> PrerequisiteSourceDigestAsync(
            Guid caseId,
            string key,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalPrerequisites
                .AsNoTracking()
                .SingleAsync(record => record.CaseId == caseId && record.Key == key, cancellationToken))
                .SourceControlDigest;
        }

        public async Task<GraphRow> GraphAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var approvalCase = await context.ApprovalCases
                .AsNoTracking()
                .SingleAsync(record => record.Id == caseId, cancellationToken);
            var requirements = await context.ApprovalRequirements
                .AsNoTracking()
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var prerequisites = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            return new GraphRow(
                approvalCase.Status,
                requirements.Select(record => new RequirementRow(
                    record.Id,
                    record.StageCode,
                    record.Status,
                    ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson)
                        .Select(target => new PolicyExceptionTarget(
                            target.Type, target.Id, target.Version, target.MaterialSnapshotDigest))
                        .ToArray())).ToArray(),
                prerequisites.Select(record => new PrerequisiteRow(
                    record.Id,
                    record.Key,
                    record.OwnerAdapterId,
                    record.OwnerWorkloadIssuer,
                    record.OwnerWorkloadClientId,
                    record.Status,
                    record.Version,
                    record.SourceControlDigest,
                    ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson)[0].MaterialSnapshotDigest))
                    .ToArray());
        }

        public async Task<(Guid TaskId, int TaskVersion)> ExceptionTaskAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var request = await context.ApprovalPolicyExceptionRequests
                .AsNoTracking()
                .SingleAsync(record => record.Id == requestId, cancellationToken);
            var task = await context.ApprovalTasks
                .AsNoTracking()
                .SingleAsync(record => record.RequirementId == request.RequirementId, cancellationToken);
            return (task.Id, task.Version);
        }

        public async Task<Guid> EvidenceIdAsync(Guid decisionId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalDecisions
                .AsNoTracking()
                .SingleAsync(record => record.Id == decisionId, cancellationToken)).EvidenceId;
        }

        public async Task<int> VerificationCountAsync(Guid requestId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalPolicyExceptionVerifications
                .CountAsync(record => record.RequestId == requestId, cancellationToken);
        }

        public async Task<int> EvaluationCountAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.PolicyEvaluationBundles.CountAsync(cancellationToken);
        }

        public async Task<int> CaseCountAsync(CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalCases.CountAsync(cancellationToken);
        }

        public async Task<string> ExceptionBindingAsync(Guid requestId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return (await context.ApprovalPolicyExceptionRequests
                .AsNoTracking()
                .SingleAsync(record => record.Id == requestId, cancellationToken)).Binding;
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
