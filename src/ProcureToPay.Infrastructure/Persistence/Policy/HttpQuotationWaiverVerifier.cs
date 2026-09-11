using System.Net;
using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>Adapter for the approved workflow service; it fails closed on malformed decisions.</summary>
public sealed class HttpQuotationWaiverVerifier(
    HttpClient httpClient,
    ILogger<HttpQuotationWaiverVerifier> logger) : IQuotationWaiverVerifier
{
    public const string AdapterId = "APPROVAL_WORKFLOW";
    public const string AdapterContractVersion = "policy-exception-verifier/v1";

    public string VerifierId => AdapterId;
    public string ContractVersion => AdapterContractVersion;

    public async Task<QuotationWaiverEvidence?> VerifyAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendRequestAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or
            HttpStatusCode.UnprocessableEntity)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Quotation waiver workflow returned HTTP {StatusCode}.", response.StatusCode);
            throw new PolicyDependencyUnavailableException("The quotation waiver workflow is unavailable.");
        }

        try
        {
            var result = await response.Content.ReadFromJsonAsync<WorkflowVerificationResponse>(
                cancellationToken);
            if (result is null || !result.Verified)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(result.EvidenceDigest) ||
                string.IsNullOrWhiteSpace(result.Binding) ||
                string.IsNullOrWhiteSpace(result.Nonce) ||
                string.IsNullOrWhiteSpace(result.VerifierReference) ||
                result.WorkflowDecisionVersion < 1 ||
                string.IsNullOrWhiteSpace(result.WorkflowDecisionDigest) ||
                string.IsNullOrWhiteSpace(result.AuthorityEvidenceDigest) ||
                result.ApproverId == Guid.Empty ||
                string.IsNullOrWhiteSpace(result.ApproverRole) ||
                string.IsNullOrWhiteSpace(result.AuthorityType) ||
                string.IsNullOrWhiteSpace(result.EligibilityEvidenceDigest) ||
                string.IsNullOrWhiteSpace(result.Scope) ||
                !result.SegregationSatisfied)
            {
                throw new PolicyDependencyUnavailableException(
                    "The quotation waiver workflow returned incomplete evidence.");
            }
            var evidence = new QuotationWaiverEvidence(
                result.EvidenceDigest,
                result.Binding,
                result.Nonce,
                result.ExpiresAt,
                result.VerifierReference)
            {
                WorkflowDecisionVersion = result.WorkflowDecisionVersion,
                WorkflowDecisionDigest = result.WorkflowDecisionDigest,
                // pi-lens-ignore: lsp:CS1061
                SegregationSatisfied = result.SegregationSatisfied,
                EligibilityEvidenceDigest = result.EligibilityEvidenceDigest!,
                CoveredLineIds = (result.CoveredLineIds ?? []).ToImmutableHashSet(),
                AuthorityEvidenceDigest = result.AuthorityEvidenceDigest,
                ApproverId = result.ApproverId,
                ApproverRole = result.ApproverRole!,
                AuthorityType = result.AuthorityType!,
                Scope = result.Scope!,
                ValidFrom = result.ValidFrom,
                VerifierId = AdapterId,
                VerifierContractVersion = AdapterContractVersion
            };
            return evidence;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Quotation waiver workflow returned malformed evidence.");
            throw new PolicyDependencyUnavailableException(
                "The quotation waiver workflow returned malformed evidence.");
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.PostAsJsonAsync(
                "v1/policy-exceptions/verify", request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Quotation waiver workflow is unavailable.");
            throw new PolicyDependencyUnavailableException("The quotation waiver workflow is unavailable.");
        }
    }

    private sealed record WorkflowVerificationResponse(
        bool Verified,
        string? EvidenceDigest,
        string? Binding,
        string? Nonce,
        DateTimeOffset ExpiresAt,
        DateTimeOffset ValidFrom,
        string? VerifierReference,
        int WorkflowDecisionVersion,
        string? WorkflowDecisionDigest,
        string? AuthorityEvidenceDigest,
        string? EligibilityEvidenceDigest,
        Guid[]? CoveredLineIds,
        Guid ApproverId,
        string? ApproverRole,
        string? AuthorityType,
        string? Scope,
        bool SegregationSatisfied);
}
