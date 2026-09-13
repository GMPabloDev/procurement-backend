using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>
/// Wire evidence of SPEC 05 REQ-07 (CA-06): the client sends the exact
/// <c>workflow-verification-request/v1</c> contract with a service JWT and fails closed on any
/// incomplete, malformed or incompatible response.
/// </summary>
public sealed class HttpQuotationWaiverVerifierTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BundleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PolicyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ReferenceId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111");
    private static readonly Guid DecisionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ApproverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task Verified_workflow_response_is_converted_to_evidence()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CompleteResponse())
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        var evidence = await verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(evidence);
        Assert.Equal(new string('a', 64), evidence.EvidenceDigest);
        Assert.Equal(Reference("approval-exception"), evidence.VerifierReference.Split("://")[0]);
        Assert.Equal("PROCUREMENT_APPROVER", evidence.ApproverRole);
        Assert.Equal("approval-workflow", evidence.VerifierId);
        Assert.Equal(PolicyExceptionContract.VerificationResponseVersion, evidence.VerifierContractVersion);
        Assert.Single(evidence.CoveredLines);
        Assert.Equal("v1/policy-exceptions/verify", handler.RequestedPath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(new string('t', 32), handler.AuthorizationParameter);
    }

    [Fact]
    public async Task Request_payload_is_the_exact_wire_contract()
    {
        string? body = null;
        var request = CreateRequest();
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(CompleteResponse()) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        await verifier.VerifyAsync(request, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(body!);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "base_bundle_id", "base_result_digest", "binding", "contract_version", "correlation_reference",
                "covered_lines", "evidence_digest", "floor", "from", "manifest_digest", "nonce",
                "organization_id", "originator_id", "policy_content_digest", "policy_version_id", "reference_id",
                "requested_at", "requested_valid_to", "requester_id", "subject_id", "subject_type",
                "subject_version", "target_requirement_key", "to", "workflow_decision_id",
                "workflow_decision_version", "workload_subject_id"
            },
            properties);
        Assert.Equal(
            PolicyExceptionContract.VerificationRequestVersion,
            document.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(
            request.BindingDigest,
            document.RootElement.GetProperty("binding").GetString());
    }

    [Fact]
    public async Task Workflow_unavailable_is_reported_as_a_typed_dependency_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Rejected_evidence_returns_no_authority(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        Assert.Null(await verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Incomplete_response_fails_closed()
    {
        var incomplete = CompleteResponse() with { AuthorityEvidenceDigest = null };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(incomplete)
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_response_fields_fail_closed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"verified\":true,\"legacy\":true}", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new StubTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);
        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_service_credential_fails_closed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(CompleteResponse())
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(
            client, new UnavailablePolicyExceptionTokenProvider(), NullLogger<HttpQuotationWaiverVerifier>.Instance);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    private static string Reference(string scheme) => scheme;

    private static WorkflowVerificationResponse CompleteResponse() => new()
    {
        ContractVersion = PolicyExceptionContract.VerificationResponseVersion,
        Verified = true,
        VerifierId = PolicyExceptionContract.VerifierId,
        VerifierContractVersion = PolicyExceptionContract.VerificationResponseVersion,
        VerifierReference =
            "approval-exception://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        CaseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        RequirementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        RequirementKey = "WR-QUOTES",
        EvidenceDigest = new string('a', 64),
        Binding = CreateRequest().BindingDigest,
        Nonce = "nonce-1",
        WorkflowDecisionId = DecisionId,
        WorkflowDecisionVersion = 1,
        WorkflowDecisionDigest = new string('d', 64),
        ApproverId = ApproverId,
        ApproverRole = "PROCUREMENT_APPROVER",
        AuthorityType = "PROCUREMENT",
        EligibilityEvidence = "{\"evaluated_at\":\"2026-09-13T12:00:00.0000000Z\",\"user_id\":\"" + ApproverId + "\"}",
        EligibilityEvidenceDigest = new string('f', 64),
        AuthorityEvidenceDigest = new string('e', 64),
        DecisionScope = DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
        CoveredLines = [new PolicyExceptionTarget("PURCHASE_REQUEST_LINE", LineId, 7, new string('d', 64))],
        SegregationSatisfied = true,
        ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
        ValidTo = DateTimeOffset.UtcNow.AddMinutes(5),
        RevokedAt = null
    };

    private static QuotationWaiverRequest CreateRequest() => new(
        PolicyExceptionType.ReduceMinValidQuotations,
        new PolicyExceptionBindingRequest(
            OrganizationId,
            "PURCHASE_REQUEST",
            SubjectId,
            3,
            BundleId,
            new string('b', 64),
            PolicyId,
            new string('c', 64),
            new string('9', 64),
            "QUOTES",
            [new PolicyExceptionTarget("PURCHASE_REQUEST_LINE", LineId, 7, new string('d', 64))],
            3,
            2,
            1,
            ReferenceId,
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            DateTimeOffset.Parse("2026-09-13T11:59:00.0000000Z"),
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
            "nonce-1"),
        DecisionId,
        1,
        new string('a', 64),
        "corr-1");

    private sealed class StubTokenProvider : IPolicyExceptionServiceTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(new string('t', 32));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri?.PathAndQuery.TrimStart('/');
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(responder(request));
        }
    }
}
