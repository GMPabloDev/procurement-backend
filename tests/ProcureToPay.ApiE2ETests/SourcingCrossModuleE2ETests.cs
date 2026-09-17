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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ProcureToPay.Api.Authentication;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.ReferenceCatalogs;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.Infrastructure.Persistence.Suppliers;
using Testcontainers.MsSql;

namespace ProcureToPay.ApiE2ETests;

/// <summary>
/// Named cross-module contract test of SPEC 10 (CA-07, CA-09): a real Purchase Request is presented,
/// the Policy evaluation creates the two PROCUREMENT prerequisites, the Buyer runs the full sourcing
/// journey, the proposal reaches Policy <c>SOURCING_PO</c> and Approval through the real adapter, the
/// award is published and both sourcing owners signal their real evidence until the Purchase Request
/// is APPROVED. No Policy, Approval, Purchase Request or Supplier double is involved: only the
/// organization master data and the idle worker loops are real.
/// </summary>
public sealed class SourcingCrossModuleE2ETests
{
    private const string WorkloadIssuer = "https://keycloak.test/realms/procure-to-pay";
    private const string HttpWorkloadClient = "procurement-api";
    private const string ProcessorClient = "sourcing-domain";
    private const string InternalIssuer = "internal://procure-to-pay";
    private const string PurchaseRequestClient = "purchase-request-domain";
    private const string BuyerSubject = "buyer-1";
    private const string ApproverSubject = "approver-1";
    private const string RequesterSubject = "requester-1";
    private const string AdminSubject = "admin-bootstrap";

    [Fact]
    public async Task A_purchase_request_reaches_APPROVED_through_sourcing_owners_and_the_award()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var requester = environment.Client(RequesterSubject);
        using var buyer = environment.Client(BuyerSubject);
        using var workload = environment.WorkloadClient(HttpWorkloadClient);
        using var approver = environment.Client(ApproverSubject);

        // 1. A real Purchase Request is created and presented; the real REQUEST_EVALUATE projection
        // creates the quotation and procurement prerequisites of the case (SPEC 05/06).
        var (requestId, lineId, lineVersion, lineDigest) = await environment.CreateAndSubmitRequestAsync(
            requester, cancellationToken);
        var prerequisites = await environment.PrerequisitesAsync(requestId, cancellationToken);
        Assert.Equal(2, prerequisites.Count);
        var quotationPrerequisite = Assert.Single(prerequisites.Where(
            prerequisite => prerequisite.OwnerAdapterId == SourcingCodes.QuotationStatusOwnerAdapterId));
        var procurementPrerequisite = Assert.Single(prerequisites.Where(
            prerequisite => prerequisite.OwnerAdapterId == SourcingCodes.ProcurementStageOwnerAdapterId));
        Assert.Equal(2, quotationPrerequisite.MinimumQuotations);
        Assert.All(prerequisites, prerequisite => Assert.Equal(ProcessorClient, prerequisite.OwnerWorkloadClientId));

        // 2. Buyer: process, RFQ and two valid external answers on the exact request line (REQ-01..04).
        Guid processId;
        using (var created = await buyer.PostAsJsonAsync(
            "/api/v1/sourcing/processes",
            new
            {
                requestId,
                requestVersion = 1,
                lines = new[] { new { lineId, lineVersion, requestedQuantity = 2m, unitCode = "EA" } },
                commandKey = $"process-{Guid.NewGuid():N}"
            },
            cancellationToken))
        {
            var failure = await created.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(created.StatusCode == HttpStatusCode.Created, failure);
            processId = JsonDocument.Parse(failure).RootElement.GetProperty("processId").GetGuid();
        }

        var processVersion = await environment.ProcessVersionAsync(processId, cancellationToken);
        Guid rfqId;
        using (var draft = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/processes/{processId}/rfq",
            new
            {
                expectedProcessVersion = processVersion,
                currency = "PEN",
                terms = new { deliveryDays = 15, incotermCode = "EXW", paymentTermsCode = "NET30", warrantyDays = 365 },
                weights = new object[]
                {
                    // Closed enum codes: PRICE=1, DELIVERY_TIME=2, WARRANTY=3, PAYMENT_TERMS=4,
                    // TECHNICAL_COMPLIANCE=5, SUPPLIER_PERFORMANCE=6.
                    new { criterion = 1, weight = 100 },
                    new { criterion = 2, weight = 0 },
                    new { criterion = 3, weight = 0 },
                    new { criterion = 4, weight = 0 },
                    new { criterion = 5, weight = 0 },
                    new { criterion = 6, weight = 0 }
                },
                responseDeadline = DateTimeOffset.UtcNow.AddDays(7),
                commandKey = $"rfq-{Guid.NewGuid():N}"
            },
            cancellationToken))
        {
            var failure = await draft.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(draft.StatusCode == HttpStatusCode.Created, failure);
            rfqId = JsonDocument.Parse(failure).RootElement.GetProperty("rfqId").GetGuid();
        }

        using (var opened = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/rfqs/{rfqId}/open",
            new { expectedVersion = 1, commandKey = $"open-{Guid.NewGuid():N}" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        foreach (var (supplierId, unitPrice) in new[]
                 {
                     (environment.SupplierAId, "10"), (environment.SupplierBId, "12")
                 })
        {
            await environment.RegisterQuotationAsync(
                buyer, rfqId, lineId, lineVersion, lineDigest, supplierId, unitPrice, cancellationToken);
        }

        // 3. Evaluation, human selection, proposal and the real SOURCING_PO Policy dispatch (CA-07).
        using (var evaluated = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/rfqs/{rfqId}/evaluations",
            new { commandKey = $"evaluate-{Guid.NewGuid():N}" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, evaluated.StatusCode);
        }

        using (var selection = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/rfqs/{rfqId}/selections",
            new
            {
                lineId,
                supplierId = environment.SupplierAId,
                commandKey = $"select-{Guid.NewGuid():N}"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, selection.StatusCode);
        }

        Guid proposalId;
        int proposalVersion;
        using (var proposal = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/rfqs/{rfqId}/proposals",
            new { supplierId = environment.SupplierAId, commandKey = $"proposal-{Guid.NewGuid():N}" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, proposal.StatusCode);
            var body = await proposal.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            proposalId = body.GetProperty("proposalId").GetGuid();
            proposalVersion = body.GetProperty("version").GetInt32();
        }

        Guid sourcingEvaluationId;
        using (var sourced = await workload.PostAsJsonAsync(
            "/api/v1/policies/evaluate",
            new
            {
                subjectType = "SOURCING_PROPOSAL",
                subjectId = proposalId,
                subjectVersion = proposalVersion,
                operation = "SOURCING_PO",
                evaluationKey = $"sourcing-{proposalId:N}-v{proposalVersion}"
            },
            cancellationToken))
        {
            var failure = await sourced.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(sourced.StatusCode == HttpStatusCode.OK, failure);
            sourcingEvaluationId = JsonDocument.Parse(failure).RootElement.GetProperty("id").GetGuid();
        }

        // 4. The real adapter turns the proposal into an approval case with a PROCUREMENT task and the
        // procurement prerequisite of the proposal subject.
        Guid proposalCaseId;
        using (var submitted = await workload.PostAsJsonAsync(
            "/api/v1/approval/submissions",
            new
            {
                organizationId = environment.OrganizationId,
                subjectType = SourcingCodes.ApprovalSubjectType,
                subjectId = proposalId,
                subjectVersion = proposalVersion,
                operation = SourcingCodes.ApprovalOperation,
                contractVersion = "v1",
                submissionKey = $"sourcing-submission-{proposalId:N}-v{proposalVersion}",
                requesterId = environment.BuyerId,
                originatorId = environment.BuyerId
            },
            cancellationToken))
        {
            var failure = await submitted.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(submitted.StatusCode == HttpStatusCode.OK, failure);
            proposalCaseId = JsonDocument.Parse(failure).RootElement.GetProperty("caseId").GetGuid();
        }

        Assert.Contains(
            sourcingEvaluationId,
            await environment.ProposalCasePolicyRefsAsync(proposalCaseId, cancellationToken));

        // The sourcing owner satisfies the proposal procurement prerequisite before any human task;
        // the approver then decides it through the real workflow endpoint (CA-09, no cycle).
        await environment.WaitForPrerequisiteSatisfiedAsync(proposalCaseId, cancellationToken);
        var task = await environment.WaitForTaskAsync(proposalCaseId, cancellationToken);
        using (var decided = await approver.PostAsJsonAsync(
            $"/api/v1/approval/tasks/{task.TaskId}/decisions",
            new
            {
                action = "APPROVE",
                reason = "Sourcing proposal approved by procurement authority",
                decisionKey = $"sourcing-decision-{proposalId:N}",
                expectedTaskVersion = task.Version
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        }

        Assert.Equal("COMPLETED", await environment.CaseStatusAsync(proposalCaseId, cancellationToken));

        // 5. The award publishes under the takeover and the PR takeover is never released (REQ-12).
        var processVersionBeforeAward = await environment.ProcessVersionAsync(processId, cancellationToken);
        Guid awardId;
        int awardVersion;
        using (var award = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/proposals/{proposalId}/versions/{proposalVersion}/award",
            new
            {
                expectedProcessVersion = processVersionBeforeAward,
                expectedAwardVersion = (int?)null,
                awardKey = $"award-{Guid.NewGuid():N}",
                reason = "Award the recommended supplier"
            },
            cancellationToken))
        {
            var failure = await award.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(award.StatusCode == HttpStatusCode.Created, failure);
            var body = JsonDocument.Parse(failure).RootElement;
            awardId = body.GetProperty("awardId").GetGuid();
            awardVersion = body.GetProperty("version").GetInt32();
        }

        // 6. Both owners signal with real evidence and the Purchase Request becomes APPROVED.
        await environment.WaitForAwardCoverageAsync(awardId, awardVersion, cancellationToken);
        Assert.Equal(
            (int)PrerequisiteStatus.Satisfied,
            await environment.PrerequisiteStatusAsync(quotationPrerequisite.Id, cancellationToken));
        await environment.WaitForPrerequisiteStatusAsync(
            procurementPrerequisite.Id, PrerequisiteStatus.Satisfied, cancellationToken);
        await environment.WaitForRequestStatusAsync(
            requester, requestId, PurchaseRequestStatus.Approved, cancellationToken);
        Assert.Equal(
            (int)PrerequisiteStatus.Satisfied,
            await environment.PrerequisiteStatusAsync(procurementPrerequisite.Id, cancellationToken));
    }

    [Fact]
    public async Task Limits_and_problem_details_are_fail_closed_over_http()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await Environment.StartAsync(cancellationToken);
        using var buyer = environment.Client(BuyerSubject);
        using var approver = environment.Client(ApproverSubject);
        using var workload = environment.WorkloadClient(HttpWorkloadClient);

        // 401: sourcing requires an authenticated workload or user.
        using (var anonymous = environment.AnonymousClient())
        {
            using var unauthorized = await anonymous.GetAsync("/api/v1/sourcing/processes", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        // 403: only an active PROCUREMENT_BUYER with an organization assignment starts outsourcing.
        using (var forbidden = await approver.PostAsJsonAsync(
            "/api/v1/sourcing/processes",
            new
            {
                requestId = Guid.NewGuid(), requestVersion = 1,
                lines = Array.Empty<object>(), commandKey = "forbidden-process"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("/problems/forbidden", await Environment.ProblemTypeAsync(forbidden, cancellationToken));
        }

        // 413: 501 lines exceed the contractual maximum before any row is written (REQ-14).
        using (var tooMany = await buyer.PostAsJsonAsync(
            "/api/v1/sourcing/processes",
            new
            {
                requestId = Guid.NewGuid(),
                requestVersion = 1,
                lines = Enumerable.Range(0, 501).Select(_ => new
                {
                    lineId = Guid.NewGuid(), lineVersion = 1, requestedQuantity = 1m, unitCode = "EA"
                }).ToArray(),
                commandKey = "too-many-lines"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
            Assert.Equal("/problems/payload-too-large", await Environment.ProblemTypeAsync(tooMany, cancellationToken));
        }

        // 400: a command key outside 1-128 [A-Za-z0-9._:-] is payload validation, not a dependency.
        using (var longKey = await buyer.PostAsJsonAsync(
            "/api/v1/sourcing/processes",
            new
            {
                requestId = Guid.NewGuid(),
                requestVersion = 1,
                lines = new[] { new { lineId = Guid.NewGuid(), lineVersion = 1, requestedQuantity = 1m, unitCode = "EA" } },
                commandKey = new string('k', 129)
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, longKey.StatusCode);
            Assert.Equal("/problems/validation", await Environment.ProblemTypeAsync(longKey, cancellationToken));
        }

        // 400: only REQUEST_EVALUATE and SOURCING_PO are dispatched; no other operation reaches a
        // PR-shaped provider or accepts an input directly (REQ-10, CA-07).
        using (var unknownOperation = await workload.PostAsJsonAsync(
            "/api/v1/policies/evaluate",
            new
            {
                subjectType = "SOURCING_PROPOSAL", subjectId = Guid.NewGuid(), subjectVersion = 1,
                operation = "SOURCING_PO_DIRECT", evaluationKey = "unknown-operation"
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownOperation.StatusCode);
            Assert.Equal("/problems/validation", await Environment.ProblemTypeAsync(unknownOperation, cancellationToken));
        }

        // 404: an invisible award is not visible to the buyer either, never a partial response.
        using (var invisible = await buyer.GetAsync(
            $"/api/v1/sourcing/awards/{Guid.NewGuid():D}/versions/1", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
            Assert.Equal("/problems/not-found", await Environment.ProblemTypeAsync(invisible, cancellationToken));
        }
    }

    [Fact]
    public async Task Telemetry_and_readiness_never_carry_commercial_evidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string marker = "telemetry-marker-technical-response";
        var capture = new CaptureLoggerProvider();
        await using var environment = await Environment.StartAsync(cancellationToken, capture);
        using var requester = environment.Client(RequesterSubject);
        using var buyer = environment.Client(BuyerSubject);

        var (requestId, lineId, lineVersion, lineDigest) = await environment.CreateAndSubmitRequestAsync(
            requester, cancellationToken);
        _ = requestId;
        Guid processId;
        using (var created = await buyer.PostAsJsonAsync(
            "/api/v1/sourcing/processes",
            new
            {
                requestId,
                requestVersion = 1,
                lines = new[] { new { lineId, lineVersion, requestedQuantity = 2m, unitCode = "EA" } },
                commandKey = $"process-{Guid.NewGuid():N}"
            },
            cancellationToken))
        {
            var failure = await created.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(created.StatusCode == HttpStatusCode.Created, failure);
            processId = JsonDocument.Parse(failure).RootElement.GetProperty("processId").GetGuid();
        }

        Guid rfqId;
        using (var draft = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/processes/{processId}/rfq",
            new
            {
                expectedProcessVersion = await environment.ProcessVersionAsync(processId, cancellationToken),
                currency = "PEN",
                terms = new { deliveryDays = 15, incotermCode = "EXW", paymentTermsCode = "NET30", warrantyDays = 365 },
                weights = new object[] { new { criterion = 1, weight = 100 } } .Concat(
                    Enumerable.Range(2, 5).Select(criterion => new { criterion, weight = 0 })).ToArray(),
                responseDeadline = DateTimeOffset.UtcNow.AddDays(7),
                commandKey = $"rfq-{Guid.NewGuid():N}"
            },
            cancellationToken))
        {
            var failure = await draft.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(draft.StatusCode == HttpStatusCode.Created, failure);
            rfqId = JsonDocument.Parse(failure).RootElement.GetProperty("rfqId").GetGuid();
        }

        using (var opened = await buyer.PostAsJsonAsync(
            $"/api/v1/sourcing/rfqs/{rfqId}/open",
            new { expectedVersion = 1, commandKey = $"open-{Guid.NewGuid():N}" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        await environment.RegisterQuotationAsync(
            buyer, rfqId, lineId, lineVersion, lineDigest, environment.SupplierAId, "987654.321",
            cancellationToken, marker);

        var captured = capture.Messages.ToArray();
        Assert.NotEmpty(captured);
        var forbidden = new[]
        {
            marker,
            "987654.321",
            $"Supplier {environment.SupplierAId:N}",
            "https://sourcing.test/"
        };
        foreach (var message in captured)
        {
            foreach (var candidate in forbidden)
            {
                Assert.DoesNotContain(candidate, message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Readiness publishes typed reasons and codes, never ids, prices or full digests (NFR-05).
        using var health = await buyer.GetAsync("/health/ready", cancellationToken);
        var healthBody = await health.Content.ReadAsStringAsync(cancellationToken);
        Assert.DoesNotContain(marker, healthBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654.321", healthBody, StringComparison.Ordinal);
        Assert.DoesNotContain(environment.OrganizationId.ToString("D"), healthBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("[0-9a-f]{64}", healthBody);
    }

    private sealed record PrerequisiteRow(
        Guid Id,
        string OwnerAdapterId,
        string OwnerWorkloadClientId,
        int MinimumQuotations);

    private sealed record TaskRow(Guid TaskId, int Version);

    private sealed class Environment : IAsyncDisposable
    {
        private MsSqlContainer container = null!;
        private TestApiFactory factory = null!;

        public Guid OrganizationId { get; private set; }
        public Guid LegalEntityId { get; private set; }
        public Guid DepartmentId { get; private set; }
        public Guid BuyerId { get; private set; }
        public Guid RequesterId { get; private set; }
        public Guid ApproverId { get; private set; }
        public Guid CostCenterId { get; private set; }
        public string SpendCategoryDigest { get; private set; } = string.Empty;
        public Guid SupplierAId { get; } = Guid.Parse("33333333-9999-9999-9999-999999999991");
        public Guid SupplierBId { get; } = Guid.Parse("33333333-9999-9999-9999-999999999992");
        public string ConnectionString { get; private set; } = string.Empty;

        public static async Task<Environment> StartAsync(
            CancellationToken cancellationToken,
            ILoggerProvider? telemetry = null)
        {
            var environment = new Environment
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
                        WorkloadIssuer, AdminSubject, "SPEC 10 cross-module bootstrap"),
                    cancellationToken);
            environment.OrganizationId = (await context.Organizations.SingleAsync(cancellationToken)).Id;
            environment.LegalEntityId = (await context.LegalEntities.SingleAsync(cancellationToken)).Id;
            environment.DepartmentId = (await context.Departments.SingleAsync(cancellationToken)).Id;
            environment.SeedUsers(context);
            environment.SeedSuppliers(context);
            await context.SaveChangesAsync(cancellationToken);
            var catalogs = new ReferenceCatalogPersistenceService(environment.CreateContext());
            var costCenter = await catalogs.CreateCostCenterAsync(
                environment.OrganizationId, environment.AdminId(context), "CC-E2E", "Sourcing E2E",
                environment.DepartmentId, "Seed", "corr-seed-cc", cancellationToken);
            environment.CostCenterId = costCenter.Id;
            var category = await catalogs.CreateSpendCategoryAsync(
                environment.OrganizationId, environment.AdminId(context), "HARDWARE", "Hardware",
                "Seed", "corr-seed-sc", cancellationToken);
            environment.SpendCategoryDigest = category.Digest;
            await environment.PublishPolicyAsync(context, cancellationToken);
            environment.RegisterOwnerProcessors();
            environment.factory = new TestApiFactory(environment.ConnectionString, telemetry);
            return environment;
        }

        private Guid AdminId(ProcureToPayDbContext context) => context.UserProfiles
            .AsNoTracking()
            .Single(profile => profile.Subject == AdminSubject).Id;

        public ProcureToPayDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProcureToPayDbContext>().UseSqlServer(ConnectionString).Options);

        public HttpClient Client(string subject)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestApiFactory.CreateUserToken(subject));
            return client;
        }

        public HttpClient AnonymousClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        public static async Task<string?> ProblemTypeAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("type", out var type) ? type.GetString() : null;
        }

        public HttpClient WorkloadClient(string clientId)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestApiFactory.CreateWorkloadToken(clientId));
            return client;
        }

        private void SeedUsers(ProcureToPayDbContext context)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var subject in new[] { BuyerSubject, ApproverSubject, RequesterSubject })
            {
                var id = Guid.NewGuid();
                context.UserProfiles.Add(new UserProfileRecord
                {
                    Id = id,
                    OrganizationId = OrganizationId,
                    Issuer = WorkloadIssuer,
                    Subject = subject,
                    // Null email/display name: the provisioner only bumps the profile version when the
                    // token claims diverge, and the PR line references this exact version.
                    Email = null,
                    DisplayName = null,
                    DepartmentId = DepartmentId,
                    Status = (int)UserProfileStatus.Active,
                    Version = 1
                });
                switch (subject)
                {
                    case BuyerSubject:
                        BuyerId = id;
                        context.RoleAssignments.Add(Assignment(id, SystemRole.ProcurementBuyer, id));
                        break;
                    case ApproverSubject:
                        ApproverId = id;
                        context.RoleAssignments.Add(Assignment(id, SystemRole.ProcurementApprover, id));
                        var levelId = Guid.NewGuid();
                        context.AuthorityLevels.Add(new AuthorityLevelRecord
                        {
                            Id = levelId,
                            Type = (int)ApprovalAuthorityType.Procurement,
                            Code = "PROCUREMENT_L1",
                            Rank = 1,
                            LevelVersion = 1,
                            IsActive = true
                        });
                        context.AuthorityGrants.Add(new AuthorityGrantRecord
                        {
                            Id = Guid.NewGuid(),
                            UserProfileId = id,
                            AuthorityLevelId = levelId,
                            MaxAmountBase = 10_000_000m,
                            BaseCurrency = "PEN",
                            ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
                            ValidFrom = now.AddMinutes(-1),
                            ValidTo = now.AddDays(30),
                            Status = (int)AssignmentStatus.Active,
                            GrantedAt = now.AddMinutes(-1),
                            GrantedBy = id,
                            Version = 1
                        });
                        break;
                    default:
                        RequesterId = id;
                        break;
                }
            }
        }

        private static RoleAssignmentRecord Assignment(Guid userId, SystemRole role, Guid? assignedBy) => new()
        {
            Id = Guid.NewGuid(),
            UserProfileId = userId,
            Role = (int)role,
            ScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]",
            Status = (int)AssignmentStatus.Active,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = assignedBy ?? userId,
            Version = 1
        };

        private void SeedSuppliers(ProcureToPayDbContext context)
        {
            foreach (var (supplierId, identityDigest) in new[]
                     {
                         (SupplierAId, new string('c', 64)), (SupplierBId, new string('e', 64))
                     })
            {
                var identityId = Guid.NewGuid();
                context.SupplierFiscalIdentities.Add(new SupplierFiscalIdentityRecord
                {
                    Id = identityId,
                    OrganizationId = OrganizationId,
                    SupplierId = supplierId,
                    CountryCode = "PE",
                    TaxIdKey = $"RUC{supplierId:N}",
                    TaxId = $"RUC{supplierId:N}",
                    IdentityKeyDigest = identityDigest
                });
                context.Suppliers.Add(new SupplierRecord
                {
                    Id = supplierId,
                    OrganizationId = OrganizationId,
                    FiscalIdentityId = identityId,
                    OperationalVersion = 1
                });
                context.SupplierVersions.Add(new SupplierVersionRecord
                {
                    SupplierId = supplierId,
                    Version = 1,
                    OrganizationId = OrganizationId,
                    LegalName = $"Supplier {supplierId:N}",
                    CountryCode = "PE",
                    TaxId = $"RUC{supplierId:N}",
                    AddressesJson = "[]",
                    ContactsJson = "[]",
                    PaymentTermsJson = "{\"code\":\"NET30\",\"net_days\":30}",
                    SupportedCurrenciesJson = "[\"PEN\"]",
                    CategoriesSuppliedJson = "[]",
                    PerformanceJson = "null",
                    BankingRefsJson = "[]",
                    Status = (int)SupplierOperationalStatus.Active,
                    RiskStatus = (int)SupplierRiskStatus.Low,
                    ContentDigest = new string('d', 64),
                    ActorUserId = BuyerId,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Reason = "E2E seed"
                });
            }
        }

        private async Task PublishPolicyAsync(ProcureToPayDbContext context, CancellationToken cancellationToken)
        {
            var decisionScope = DecisionScopeDescriptor.Create(
                    OrganizationId,
                    [new DecisionScopeEntry(ScopeDimension.Organization, null, null)])
                .ToCanonicalJson();
            var policy = new PolicySetVersion(Guid.NewGuid(), OrganizationId, 1,
                [PolicyScope.Line, PolicyScope.SourcingPo]);
            policy.AddRule(new PolicyRule("QUOTATIONS", PolicyScope.Line, [],
                [new PolicyEffect(
                    PolicyEffectType.RequireQuotations, "RFQ", minimumQuotations: 2,
                    minimumExceptionQuotations: 1)]));
            policy.AddRule(new PolicyRule("PROCUREMENT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireProcurement, "PROC")]));
            policy.AddRule(new PolicyRule("SOURCING_PROCUREMENT", PolicyScope.SourcingPo, [],
                [new PolicyEffect(PolicyEffectType.RequireProcurement, "PROC")]));
            policy.AddRule(new PolicyRule("SOURCING_APPROVAL", PolicyScope.SourcingPo, [],
                [
                    new PolicyEffect(
                        PolicyEffectType.RequireApproval,
                        "SOURCING_APPROVAL",
                        approval: new PolicyApprovalDescriptor(
                            SystemRole.ProcurementApprover,
                            ApprovalAuthorityType.Procurement,
                            new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "PROCUREMENT_L1", 1),
                            10_000_000m,
                            "PEN",
                            decisionScope))
                ]));
            policy.AddRule(new PolicyRule(
                "LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
            policy.AddRule(new PolicyRule(
                "SOURCING_DEFAULT", PolicyScope.SourcingPo, [],
                [new PolicyEffect(PolicyEffectType.Allow, "SOURCING_DEFAULT")], isFallback: true));
            var content = PolicyCanonicalizer.CanonicalizePolicy(policy);
            policy.Publish(PolicyCanonicalizer.Hash(content));
            context.PolicySetVersions.Add(new PolicySetVersionRecord
            {
                Id = policy.Id,
                OrganizationId = OrganizationId,
                Sequence = 1,
                Status = (int)PolicySetStatus.Published,
                ScopesJson = "[\"LINE\",\"SOURCING_PO\"]",
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
                ActorUserId = AdminId(context),
                Reason = "SPEC 10 cross-module activation",
                OccurredAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        private void RegisterOwnerProcessors()
        {
            using var context = CreateContext();
            foreach (var adapterId in new[]
                     {
                         SourcingCodes.QuotationStatusOwnerAdapterId, SourcingCodes.ProcurementStageOwnerAdapterId
                     })
            {
                context.SourcingPrerequisiteProcessorRegistrations.Add(
                    new SourcingPrerequisiteProcessorRegistrationRecord
                    {
                        AdapterId = adapterId,
                        AdapterVersion = SourcingCodes.OwnerAdapterVersion,
                        ProcessorId = ProcessorClient,
                        WorkloadIssuer = WorkloadIssuer,
                        WorkloadClientId = ProcessorClient
                    });
            }

            context.SaveChanges();
        }

        public async Task<(Guid RequestId, Guid LineId, int LineVersion, string LineDigest)> CreateAndSubmitRequestAsync(
            HttpClient requester,
            CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, object?>
            {
                ["business_justification"] = "Cross-module sourcing evidence",
                ["command_version"] = PurchaseRequestCodes.CreateCommandVersion,
                ["legal_entity_ref"] = new { entity_type = "LEGAL_ENTITY", id = LegalEntityId, version = 1 },
                ["line_drafts"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["client_line_key"] = "line-0",
                        ["content"] = new Dictionary<string, object?>
                        {
                            ["base_amount"] = "100",
                            ["base_currency"] = "PEN",
                            ["beneficiary_department_ref"] = new { entity_type = "DEPARTMENT", id = DepartmentId, version = 1 },
                            ["contract_required"] = false,
                            ["cost_center_department_ref"] = new { entity_type = "DEPARTMENT", id = DepartmentId, version = 1 },
                            ["cost_center_ref"] = new { entity_type = "COST_CENTER", id = CostCenterId, version = 1 },
                            ["estimated_gross_amount"] = "100",
                            ["fiscal_year"] = 2026,
                            ["fx_attestation_ref"] = (object?)null,
                            ["need_summary"] = "Cross-module sourcing need",
                            ["non_standard_terms"] = false,
                            ["preferred_product_ref"] = (object?)null,
                            ["purchase_type"] = "GOOD",
                            ["requested_for_user_ref"] = new { entity_type = "USER", id = RequesterId, version = 1 },
                            ["required_product_ref"] = (object?)null,
                            ["risk_answers"] = Array.Empty<object>(),
                            ["spend_category_ref"] = new
                            {
                                catalog = "SPEND_CATEGORY", code = "HARDWARE", version = 1,
                                digest = SpendCategoryDigest
                            },
                            ["supplier_ref"] = (object?)null,
                            ["transaction_currency"] = "PEN"
                        }
                    }
                },
                ["reason"] = "Initial request",
                ["revision_key"] = $"request-{Guid.NewGuid():N}"
            };
            Guid requestId;
            using (var created = await requester.PostAsJsonAsync("/v1/purchase-requests", body, cancellationToken))
            {
                var failure = await created.Content.ReadAsStringAsync(cancellationToken);
                Assert.True(created.StatusCode == HttpStatusCode.Created, failure);
                requestId = JsonDocument.Parse(failure).RootElement.GetProperty("requestId").GetGuid();
            }

            using (var submitted = await requester.PostAsJsonAsync(
                $"/v1/purchase-requests/{requestId}/submission",
                new
                {
                    expected_version = 1,
                    reason = "Present for approval",
                    submission_key = $"submission-{Guid.NewGuid():N}"
                },
                cancellationToken))
            {
                var failure = await submitted.Content.ReadAsStringAsync(cancellationToken);
                Assert.True(submitted.StatusCode == HttpStatusCode.OK, failure);
            }

            using var read = await requester.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
            read.EnsureSuccessStatusCode();
            var version = (await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .GetProperty("versions").EnumerateArray()
                .Single(candidate => candidate.GetProperty("version").GetInt32() == 1);
            var line = version.GetProperty("lines")[0];
            return (
                requestId,
                line.GetProperty("lineId").GetGuid(),
                line.GetProperty("lineVersion").GetInt32(),
                line.GetProperty("contentDigest").GetString()!);
        }

        public async Task RegisterQuotationAsync(
            HttpClient buyer,
            Guid rfqId,
            Guid lineId,
            int lineVersion,
            string lineDigest,
            Guid supplierId,
            string unitPrice,
            CancellationToken cancellationToken,
            string technicalResponse = "Delivered as offered")
        {
            Guid fileId;
            int fileVersion;
            long length;
            string sha256;
            using (var form = new MultipartFormDataContent())
            {
                var payload = new ByteArrayContent("sourcing evidence"u8.ToArray());
                payload.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                form.Add(payload, "File", "evidence.pdf");
                using var staged = await buyer.PostAsync(
                    $"/api/v1/sourcing/rfqs/{rfqId}/attachments", form, cancellationToken);
                var failure = await staged.Content.ReadAsStringAsync(cancellationToken);
                Assert.True(staged.StatusCode == HttpStatusCode.Created, failure);
                var body = JsonDocument.Parse(failure).RootElement;
                fileId = body.GetProperty("fileId").GetGuid();
                fileVersion = body.GetProperty("version").GetInt32();
                length = body.GetProperty("length").GetInt64();
                sha256 = body.GetProperty("sha256").GetString()!;
            }

            using (var confirmed = await buyer.PostAsJsonAsync(
                $"/api/v1/sourcing/attachments/{fileId}/versions/{fileVersion}/confirm",
                new { sha256 },
                cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            }

            var subtotal = 2m * decimal.Parse(unitPrice, System.Globalization.CultureInfo.InvariantCulture);
            var gross = subtotal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Guid quotationId;
            using (var registered = await buyer.PostAsJsonAsync(
                $"/api/v1/sourcing/rfqs/{rfqId}/quotations",
                new
                {
                    supplierId,
                    supplierVersion = 1,
                    currency = "PEN",
                    terms = new
                    {
                        deliveryDays = 20, incotermCode = "EXW", paymentTermsCode = "NET30", warrantyDays = 365
                    },
                    receivedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                    lines = new[]
                    {
                        new
                        {
                            lineId, quantity = "2", unitPrice, subtotal = gross, taxes = "0",
                            additionalCharges = "0", discounts = "0", grossTotal = gross,
                            technicalResponse
                        }
                    },
                    attachments = new[]
                    {
                        new
                        {
                            fileId, version = fileVersion, fileName = "evidence.pdf",
                            contentType = "application/pdf", length, sha256
                        }
                    },
                    commandKey = $"quote-{Guid.NewGuid():N}"
                },
                cancellationToken))
            {
                var failure = await registered.Content.ReadAsStringAsync(cancellationToken);
                Assert.True(registered.StatusCode == HttpStatusCode.Created, failure);
                quotationId = JsonDocument.Parse(failure).RootElement.GetProperty("quotationId").GetGuid();
            }

            using (var reviewed = await buyer.PostAsJsonAsync(
                $"/api/v1/sourcing/quotations/{quotationId}/review",
                new
                {
                    expectedVersion = 1,
                    // QuotationReviewStatus.VALID = 2 (closed enum code).
                    status = 2,
                    codes = Array.Empty<string>(),
                    commandKey = $"review-{Guid.NewGuid():N}"
                },
                cancellationToken))
            {
                var failure = await reviewed.Content.ReadAsStringAsync(cancellationToken);
                Assert.True(reviewed.StatusCode == HttpStatusCode.OK, failure);
            }
        }

        public async Task<IReadOnlyList<PrerequisiteRow>> PrerequisitesAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var caseId = await context.ApprovalCases
                .AsNoTracking()
                .Where(record => record.SubjectId == requestId &&
                                 record.SubjectType == PurchaseRequestCodes.SubjectType)
                .OrderByDescending(record => record.Version)
                .Select(record => record.Id)
                .FirstAsync(cancellationToken);
            var rows = await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            return rows.Select(record => new PrerequisiteRow(
                record.Id,
                record.OwnerAdapterId,
                record.OwnerWorkloadClientId,
                ReadMinimum(record.ParametersJson))).ToArray();
        }

        private static int ReadMinimum(string parametersJson)
        {
            var value = JsonDocument.Parse(parametersJson).RootElement;
            return value.TryGetProperty("minimum_quotations", out var minimum) &&
                   minimum.ValueKind == JsonValueKind.Number
                ? minimum.GetInt32()
                : 0;
        }

        public async Task<int> ProcessVersionAsync(Guid processId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.SourcingProcesses
                .AsNoTracking()
                .Where(record => record.Id == processId)
                .Select(record => record.Version)
                .SingleAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<Guid>> ProposalCasePolicyRefsAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var sourceId = await context.ApprovalCases
                .AsNoTracking()
                .Where(record => record.Id == caseId)
                .Select(record => record.SubjectId)
                .SingleAsync(cancellationToken);
            return await context.PolicyEvaluationBundles
                .AsNoTracking()
                .Where(record => record.SubjectId == sourceId && record.Operation == "SOURCING_PO")
                .Select(record => record.Id)
                .ToArrayAsync(cancellationToken);
        }

        public async Task WaitForPrerequisiteSatisfiedAsync(Guid caseId, CancellationToken cancellationToken) =>
            await PollAsync(async () =>
            {
                await using var context = CreateContext();
                var prerequisites = await context.ApprovalPrerequisites
                    .AsNoTracking()
                    .Where(record => record.CaseId == caseId)
                    .ToArrayAsync(cancellationToken);
                return prerequisites.Length > 0 &&
                       prerequisites.All(record => record.Status == (int)PrerequisiteStatus.Satisfied);
            }, cancellationToken);

        public async Task<TaskRow> WaitForTaskAsync(Guid caseId, CancellationToken cancellationToken)
        {
            TaskRow? task = null;
            await PollAsync(async () =>
            {
                await using var context = CreateContext();
                task = await (
                        from requirement in context.ApprovalRequirements.AsNoTracking()
                        join candidate in context.ApprovalTasks.AsNoTracking()
                            on requirement.Id equals candidate.RequirementId
                        where requirement.CaseId == caseId
                        orderby candidate.Id
                        select new TaskRow(candidate.Id, candidate.Version))
                    .FirstOrDefaultAsync(cancellationToken);
                return task is not null;
            }, cancellationToken);
            return task!;
        }

        public async Task WaitForAwardCoverageAsync(
            Guid awardId,
            int awardVersion,
            CancellationToken cancellationToken) =>
            await PollAsync(async () =>
            {
                await using var context = CreateContext();
                return await context.SourcingCurrentAwardLines
                    .AsNoTracking()
                    .AnyAsync(
                        record => record.AwardId == awardId && record.AwardVersion == awardVersion,
                        cancellationToken);
            }, cancellationToken);

        public async Task<int> PrerequisiteStatusAsync(Guid prerequisiteId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context.ApprovalPrerequisites
                .AsNoTracking()
                .Where(record => record.Id == prerequisiteId)
                .Select(record => record.Status)
                .SingleAsync(cancellationToken);
        }

        public async Task WaitForPrerequisiteStatusAsync(
            Guid prerequisiteId,
            PrerequisiteStatus expected,
            CancellationToken cancellationToken) =>
            await PollAsync(
                async () => await PrerequisiteStatusAsync(prerequisiteId, cancellationToken) == (int)expected,
                cancellationToken);

        public async Task WaitForRequestStatusAsync(
            HttpClient requester,
            Guid requestId,
            PurchaseRequestStatus expected,
            CancellationToken cancellationToken) =>
            await PollAsync(async () =>
            {
                using var response = await requester.GetAsync($"/v1/purchase-requests/{requestId}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                return body.GetProperty("status").GetInt32() == (int)expected;
            }, cancellationToken);

        public async Task<string> CaseStatusAsync(Guid caseId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            var status = await context.ApprovalCases
                .AsNoTracking()
                .Where(record => record.Id == caseId)
                .Select(record => record.Status)
                .SingleAsync(cancellationToken);
            return ((ApprovalCaseStatus)status).ToString().ToUpperInvariant();
        }

        private static async Task PollAsync(
            Func<Task<bool>> condition,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            Assert.Fail("The cross-module condition did not become true within the operational budget.");
        }

        public async ValueTask DisposeAsync()
        {
            factory.Dispose();
            await container.DisposeAsync();
        }
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(CaptureLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(
                    $"{categoryName}|{logLevel}|{formatter(state, exception)}|{exception?.Message}");
        }
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public async Task UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            objects[request.ObjectKey] = buffer.ToArray();
        }

        public Uri GenerateTemporaryDownloadUrl(string objectKey) =>
            new($"https://sourcing.test/{Uri.EscapeDataString(objectKey)}");

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestApiFactory(string connectionString, ILoggerProvider? telemetry = null)
        : WebApplicationFactory<Program>
    {
        private static readonly SymmetricSecurityKey SigningKey = new(
            Encoding.UTF8.GetBytes("procure-to-pay-e2e-signing-key-2026-32-bytes!"));

        internal static string CreateUserToken(string subject) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                WorkloadIssuer,
                "procure-to-pay-tests",
                claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        internal static string CreateWorkloadToken(string clientId) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                WorkloadIssuer,
                "procure-to-pay-tests",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"service-account-{clientId}"),
                    new Claim("client_id", clientId)
                ],
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:SqlServer", connectionString);
            builder.UseSetting("Authentication:JwtBearer:Authority", "https://issuer.invalid");
            builder.UseSetting("Authentication:JwtBearer:Audience", "procure-to-pay-tests");
            builder.UseSetting("Authentication:ServiceJwt:Audience", ApprovalServiceAuthentication.DefaultAudience);
            // The real background sweep runs in this E2E host so both sourcing owners signal their
            // evidence and the approval outbox projects the PR result without an administrative call.
            builder.UseSetting("Approval:Worker:Enabled", "true");
            builder.UseSetting("AWS:Region", "us-east-1");
            builder.UseSetting("Storage:S3:BucketName", "procure-to-pay-api-tests");
            // The supplier banking key provider is resolved by the real approval sweep; a test key
            // keeps the deployment fail-closed configuration valid without production material.
            builder.UseSetting(
                "Supplier:Banking:KeyBase64", Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
            builder.UseSetting("Supplier:Banking:KeyVersion", "e2e-v1");
            var workloads = new (string Issuer, string ClientId)[]
            {
                (WorkloadIssuer, HttpWorkloadClient),
                (InternalIssuer, PurchaseRequestClient),
                (WorkloadIssuer, ProcessorClient)
            };
            for (var index = 0; index < workloads.Length; index++)
            {
                builder.UseSetting($"Policy:Workloads:{index}:Issuer", workloads[index].Issuer);
                builder.UseSetting($"Policy:Workloads:{index}:ClientId", workloads[index].ClientId);
                builder.UseSetting($"Approval:Workloads:{index}:Issuer", workloads[index].Issuer);
                builder.UseSetting($"Approval:Workloads:{index}:ClientId", workloads[index].ClientId);
            }

            var owners = new (string AdapterId, string ClientId)[]
            {
                ("budget-check-owner", "budget-check-owner"),
                ("supporting-document-owner", "supporting-document-owner"),
                ("active-supplier-owner", "active-supplier-owner"),
                (SourcingCodes.QuotationStatusOwnerAdapterId, ProcessorClient),
                (SourcingCodes.ProcurementStageOwnerAdapterId, ProcessorClient)
            };
            for (var index = 0; index < owners.Length; index++)
            {
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:AdapterId", owners[index].AdapterId);
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:AdapterVersion", "v1");
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:Issuer", WorkloadIssuer);
                builder.UseSetting($"Approval:OwnerWorkloads:{index}:ClientId", owners[index].ClientId);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = connectionString,
                    ["Authentication:JwtBearer:Authority"] = "https://issuer.invalid",
                    ["Authentication:JwtBearer:Audience"] = "procure-to-pay-tests",
                    ["Authentication:ServiceJwt:Audience"] = ApprovalServiceAuthentication.DefaultAudience,
                    ["AWS:Region"] = "us-east-1",
                    ["Storage:S3:BucketName"] = "procure-to-pay-api-tests",
                    ["Supplier:Banking:KeyBase64"] = Convert.ToBase64String(
                        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
                    ["Supplier:Banking:KeyVersion"] = "e2e-v1"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                ConfigureScheme(services, JwtBearerDefaults.AuthenticationScheme, "procure-to-pay-tests");
                ConfigureScheme(
                    services, ApprovalServiceAuthentication.Scheme, ApprovalServiceAuthentication.DefaultAudience);
                // The quotation evidence storage is an external service: the E2E keeps the real
                // in-process contract with an in-memory object store instead of S3.
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(new InMemoryFileStorage());
                if (telemetry is not null)
                {
                    // NFR-05: the negative capture reads every emitted log without changing the
                    // production providers.
                    services.AddSingleton(telemetry);
                }
            });
        }

        private static void ConfigureScheme(IServiceCollection services, string scheme, string audience) =>
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
