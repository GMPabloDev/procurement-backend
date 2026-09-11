using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.UnitTests.Policy;

public sealed class HttpQuotationWaiverVerifierTests
{
    [Fact]
    public async Task Verified_workflow_response_is_converted_to_evidence()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                verified = true,
                evidenceDigest = new string('a', 64),
                binding = "binding-1",
                nonce = "nonce-1",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                verifierReference = "decision-1",
                workflowDecisionVersion = 1,
                workflowDecisionDigest = new string('d', 64),
                authorityEvidenceDigest = new string('e', 64),
                eligibilityEvidenceDigest = new string('f', 64),
                coveredLineIds = Array.Empty<Guid>(),
                approverId = Guid.NewGuid(),
                approverRole = "PROCUREMENT_APPROVER",
                authorityType = "PROCUREMENT",
                scope = "REQUEST",
                validFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                segregationSatisfied = true
            })
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(client, NullLogger<HttpQuotationWaiverVerifier>.Instance);

        var evidence = await verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(evidence);
        Assert.Equal(new string('a', 64), evidence.EvidenceDigest);
        Assert.Equal("decision-1", evidence.VerifierReference);
        Assert.Equal("PROCUREMENT_APPROVER", evidence.ApproverRole);
        Assert.Equal("APPROVAL_WORKFLOW", evidence.VerifierId);
        Assert.Equal("v1/policy-exceptions/verify", handler.RequestedPath);
    }

    [Fact]
    public async Task Workflow_unavailable_is_reported_as_a_typed_dependency_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://workflow.test/") };
        var verifier = new HttpQuotationWaiverVerifier(client, NullLogger<HttpQuotationWaiverVerifier>.Instance);

        await Assert.ThrowsAsync<PolicyDependencyUnavailableException>(() =>
            verifier.VerifyAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    private static QuotationWaiverRequest CreateRequest() => new(
        PolicyExceptionType.ReduceMinValidQuotations,
        3,
        2,
        1,
        new string('b', 64),
        new string('c', 64),
        "binding-1",
        "nonce-1",
        new string('a', 64),
        "PROCUREMENT_APPROVER",
        "PROCUREMENT",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid())
    {
        TargetRequirementKey = "RFQ"
    };

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
