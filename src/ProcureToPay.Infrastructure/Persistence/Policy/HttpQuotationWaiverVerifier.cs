using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

/// <summary>
/// Supplies the service JWT Policy uses to call the approval workflow verifier. The credential
/// lives in configuration/secret storage, never in the database or logs (SPEC 05 Seguridad).
/// </summary>
public interface IPolicyExceptionServiceTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default deny when no credential source exists: the caller fails closed (503).</summary>
public sealed class UnavailablePolicyExceptionTokenProvider : IPolicyExceptionServiceTokenProvider
{
    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>OAuth2 client-credentials acquisition with in-memory caching until expiry.</summary>
public sealed class ClientCredentialsPolicyExceptionTokenProvider(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<ClientCredentialsPolicyExceptionTokenProvider> logger) : IPolicyExceptionServiceTokenProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedToken;
    private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (cachedToken is not null && DateTimeOffset.UtcNow < expiresAt)
        {
            return cachedToken;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cachedToken is not null && DateTimeOffset.UtcNow < expiresAt)
            {
                return cachedToken;
            }

            var clientId = configuration["Policy:ExceptionWorkflow:ClientId"];
            var clientSecret = configuration["Policy:ExceptionWorkflow:ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                return null;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = configuration["Policy:ExceptionWorkflow:Scope"] ?? "approval-workflow"
            });
            using var response = await httpClient.PostAsync(string.Empty, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Policy exception token endpoint returned HTTP {StatusCode}.", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                return null;
            }

            cachedToken = payload.AccessToken;
            // Refresh ahead of expiry so a token is never reused after it lapsed.
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, payload.ExpiresIn - 30));
            return cachedToken;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Policy exception token endpoint is unavailable.");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>
/// Adapter for the approved workflow service; it sends the exact
/// <c>workflow-verification-request/v1</c> contract with a service JWT and fails closed on
/// malformed or incomplete decisions (SPEC 05 REQ-06, REQ-07).
/// </summary>
public sealed class HttpQuotationWaiverVerifier(
    HttpClient httpClient,
    IPolicyExceptionServiceTokenProvider tokenProvider,
    ILogger<HttpQuotationWaiverVerifier> logger) : IQuotationWaiverVerifier
{
    public const string AdapterId = "approval-workflow";
    public const string AdapterContractVersion = PolicyExceptionContract.VerificationResponseVersion;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string VerifierId => AdapterId;
    public string ContractVersion => AdapterContractVersion;

    public async Task<QuotationWaiverEvidence?> VerifyAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PolicyDependencyUnavailableException(
                "The quotation waiver service credential is unavailable.");
        }

        using var response = await SendRequestAsync(BuildContext(request), token, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or
            HttpStatusCode.UnprocessableEntity)
        {
            logger.LogWarning(
                "Quotation waiver workflow rejected the verification with HTTP {StatusCode}.",
                response.StatusCode);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Quotation waiver workflow returned HTTP {StatusCode}.", response.StatusCode);
            throw new PolicyDependencyUnavailableException("The quotation waiver workflow is unavailable.");
        }

        string payload;
        try
        {
            payload = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Quotation waiver workflow returned an unreadable body.");
            throw new PolicyDependencyUnavailableException(
                "The quotation waiver workflow returned malformed evidence.");
        }

        WorkflowVerificationResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<WorkflowVerificationResponse>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Quotation waiver workflow returned malformed evidence.");
            throw new PolicyDependencyUnavailableException(
                "The quotation waiver workflow returned malformed evidence.");
        }

        if (result is null || !result.Verified)
        {
            // No payload, nonce or binding is logged: only the outcome (SPEC 05 NFR-04).
            logger.LogWarning("Quotation waiver workflow did not verify the policy exception.");
            return null;
        }

        return ToEvidence(result);
    }

    /// <summary>Turns the Policy request into the exact wire contract (SPEC 05 REQ-07).</summary>
    public static WorkflowVerificationRequest BuildContext(QuotationWaiverRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var binding = request.Binding;
        return new WorkflowVerificationRequest
        {
            ContractVersion = PolicyExceptionContract.VerificationRequestVersion,
            ReferenceId = binding.ReferenceId,
            OrganizationId = binding.OrganizationId,
            SubjectType = binding.SubjectType,
            SubjectId = binding.SubjectId,
            SubjectVersion = binding.SubjectVersion,
            BaseBundleId = binding.BaseBundleId,
            BaseResultDigest = binding.BaseResultDigest,
            PolicyVersionId = binding.PolicyVersionId,
            PolicyContentDigest = binding.PolicyContentDigest,
            ManifestDigest = binding.ManifestDigest,
            TargetRequirementKey = binding.TargetRequirementKey,
            CoveredLines = binding.CoveredLines,
            From = binding.From,
            To = binding.To,
            Floor = binding.Floor,
            RequesterId = binding.RequesterId,
            OriginatorId = binding.OriginatorId,
            WorkloadSubjectId = binding.WorkloadSubjectId,
            RequestedAt = binding.RequestedAt,
            RequestedValidTo = binding.RequestedValidTo,
            Binding = binding.ComputeBinding(),
            Nonce = binding.Nonce,
            WorkflowDecisionId = request.WorkflowDecisionId,
            WorkflowDecisionVersion = request.WorkflowDecisionVersion,
            EvidenceDigest = request.EvidenceDigest,
            CorrelationReference = request.CorrelationReference
        };
    }

    private static QuotationWaiverEvidence ToEvidence(WorkflowVerificationResponse result)
    {
        // verified=true requires the complete contract; anything missing is a dependency failure.
        if (!string.Equals(
                result.ContractVersion, PolicyExceptionContract.VerificationResponseVersion, StringComparison.Ordinal) ||
            !string.Equals(result.VerifierId, AdapterId, StringComparison.Ordinal) ||
            !string.Equals(
                result.VerifierContractVersion, PolicyExceptionContract.VerificationResponseVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.VerifierReference) ||
            result.CaseId is null || result.RequirementId is null ||
            string.IsNullOrWhiteSpace(result.RequirementKey) ||
            !IsSha256(result.EvidenceDigest) ||
            !IsSha256(result.Binding) ||
            string.IsNullOrWhiteSpace(result.Nonce) ||
            result.WorkflowDecisionId is null || result.WorkflowDecisionVersion is null or < 1 ||
            !IsSha256(result.WorkflowDecisionDigest) ||
            result.ApproverId is null || result.ApproverId == Guid.Empty ||
            string.IsNullOrWhiteSpace(result.ApproverRole) ||
            string.IsNullOrWhiteSpace(result.AuthorityType) ||
            string.IsNullOrWhiteSpace(result.EligibilityEvidence) ||
            !IsSha256(result.EligibilityEvidenceDigest) ||
            !IsSha256(result.AuthorityEvidenceDigest) ||
            result.DecisionScope is null ||
            result.CoveredLines is null || result.CoveredLines.Count == 0 ||
            !result.SegregationSatisfied ||
            result.ValidFrom is null || result.ValidTo is null ||
            result.ValidTo <= result.ValidFrom)
        {
            throw new PolicyDependencyUnavailableException(
                "The quotation waiver workflow returned incomplete evidence.");
        }

        return new QuotationWaiverEvidence(
            result.EvidenceDigest!,
            result.Binding!,
            result.Nonce!,
            result.ValidFrom.Value,
            result.ValidTo.Value,
            result.VerifierReference!)
        {
            WorkflowDecisionVersion = result.WorkflowDecisionVersion.Value,
            WorkflowDecisionDigest = result.WorkflowDecisionDigest!,
            AuthorityEvidenceDigest = result.AuthorityEvidenceDigest!,
            SegregationSatisfied = result.SegregationSatisfied,
            EligibilityEvidenceDigest = result.EligibilityEvidenceDigest!,
            ApproverId = result.ApproverId.Value,
            ApproverRole = result.ApproverRole!,
            AuthorityType = result.AuthorityType!,
            DecisionScope = result.DecisionScope,
            CoveredLines = result.CoveredLines,
            VerifierId = AdapterId,
            VerifierContractVersion = PolicyExceptionContract.VerificationResponseVersion
        };
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private async Task<HttpResponseMessage> SendRequestAsync(
        WorkflowVerificationRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/policy-exceptions/verify")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Quotation waiver workflow is unavailable.");
            throw new PolicyDependencyUnavailableException("The quotation waiver workflow is unavailable.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Quotation waiver workflow timed out.");
            throw new PolicyDependencyUnavailableException("The quotation waiver workflow timed out.");
        }
    }
}
