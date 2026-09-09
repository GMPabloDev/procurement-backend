using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed class PolicyDependencyUnavailableException(string message) : DomainException(message)
{
}

public sealed class PolicyConfigurationUnavailableException(string message) : DomainException(message)
{
}

public sealed record PolicyWorkloadIdentity(string Issuer, string ClientId);
public sealed record PolicyWorkloadPrincipal(string Issuer, string ClientId);
public sealed record PolicyFactRequest(string SubjectType, Guid SubjectId, int SubjectVersion,
    string Operation, DateTimeOffset RequestedAtUtc, PolicyWorkloadPrincipal Workload,
    string CorrelationReference);
public sealed record PolicyCompletenessManifest(Guid RequestId, int RequestVersion,
    IReadOnlyList<PolicySubjectReference> Lines, string Digest);
public sealed record PolicyFactBundle(PolicyRequestInput Request, PolicyCompletenessManifest Manifest,
    string ProviderId, string ContractVersion, string FactsDigest);

public interface IPolicyWorkloadAllowlist
{
    bool IsAllowed(PolicyWorkloadIdentity workload);
}

public interface IPolicyFactProvider
{
    string SubjectType { get; }
    string Operation { get; }
    string ProviderId { get; }
    string ContractVersion { get; }
    Task<PolicyFactBundle> GetFactsAsync(PolicyFactRequest request, CancellationToken cancellationToken = default);
}

public interface IPolicyFactProviderRegistry
{
    IPolicyFactProvider Resolve(string subjectType, string operation);
}

public interface IPolicyEvaluationPort
{
    Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestAsync(
        PolicySetVersion policySnapshot,
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken = default);
}

public sealed class PolicyFactProviderRegistry(IEnumerable<IPolicyFactProvider> providers) : IPolicyFactProviderRegistry
{
    private readonly IReadOnlyList<IPolicyFactProvider> providers = providers.ToArray();

    public IPolicyFactProvider Resolve(string subjectType, string operation)
    {
        var matches = providers.Where(provider =>
            string.Equals(provider.SubjectType, subjectType, StringComparison.Ordinal) &&
            string.Equals(provider.Operation, operation, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new PolicyDependencyUnavailableException(
                $"Expected exactly one fact provider for '{subjectType}/{operation}', found {matches.Length}.");
    }
}

public sealed class PolicyEvaluationService(
    PolicyPersistenceService persistenceService,
    PolicyWorkloadAllowlist workloadAllowlist,
    PolicyFactProviderRegistry factProviderRegistry) : IPolicyEvaluationPort
{
    public async Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestAsync(
        PolicySetVersion policySnapshot,
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken = default)
    {
        var workload = new PolicyWorkloadIdentity(factRequest.Workload.Issuer, factRequest.Workload.ClientId);
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }

        // pi-lens-ignore: CS0121
        var provider = factProviderRegistry.Resolve(factRequest.SubjectType, factRequest.Operation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        PolicyFactBundle facts;
        try
        {
            facts = await provider.GetFactsAsync(factRequest, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PolicyDependencyUnavailableException("The policy fact provider timed out.");
        }
        var expectedLines = facts.Request.Lines
            .Select(line => new { id = line.Subject.Id, version = line.Subject.Version })
            .OrderBy(line => line.id)
            .ThenBy(line => line.version)
            .ToArray();
        var expectedManifestDigest = PolicyCanonicalizer.Hash(JsonSerializer.Serialize(expectedLines));
        var manifestLines = facts.Manifest.Lines
            .Select(line => new { id = line.Id, version = line.Version })
            .OrderBy(line => line.id)
            .ThenBy(line => line.version)
            .ToArray();
        if (!string.Equals(facts.ProviderId, provider.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(facts.ContractVersion, provider.ContractVersion, StringComparison.Ordinal) ||
            facts.Request.OrganizationId != policySnapshot.OrganizationId ||
            facts.Manifest.RequestId != facts.Request.Subject.Id ||
            facts.Manifest.RequestVersion != facts.Request.Subject.Version ||
            !expectedLines.SequenceEqual(manifestLines) ||
            !string.Equals(facts.Manifest.Digest, expectedManifestDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("The policy fact bundle or completeness manifest is invalid.");
        }

        var evaluatedAt = DateTimeOffset.UtcNow;
        return await EvaluateActivePurchaseRequestAsync(
            policySnapshot,
            facts.Request,
            workload,
            evaluationKey,
            evaluatedAt,
            factRequest.CorrelationReference,
            cancellationToken);
    }

    public async Task<PolicyEvaluationBundle> EvaluateActivePurchaseRequestAsync(
        PolicySetVersion policySnapshot,
        PolicyRequestInput request,
        PolicyWorkloadIdentity workload,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var active = await persistenceService.FindActiveAsync(
            request.OrganizationId, evaluatedAt, cancellationToken)
            ?? throw new DomainConflictException("No active policy is available.");
        if (active.PolicySetVersionId != policySnapshot.Id ||
            !string.Equals(active.PolicySetVersion.ContentDigest, policySnapshot.ContentDigest,
                StringComparison.Ordinal))
        {
            throw new DomainConflictException("The policy snapshot is not the current active version.");
        }
        return await EvaluatePurchaseRequestAsync(
            policySnapshot, request, workload, evaluationKey, evaluatedAt, correlationReference, cancellationToken);
    }

    public async Task<PolicyEvaluationBundle> EvaluatePurchaseRequestAsync(
        PolicySetVersion policy,
        PolicyRequestInput request,
        PolicyWorkloadIdentity workload,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }

        var bundle = PolicyEvaluator.EvaluateRequest(policy, request, evaluationKey, evaluatedAt);
        var persisted = await persistenceService.AppendEvaluationAsync(
            bundle,
            new PolicyEvaluationCaller(
                request.OrganizationId,
                workload.Issuer,
                workload.ClientId,
                "PURCHASE_REQUEST",
                evaluationKey,
                policy.Id,
                correlationReference),
            cancellationToken);
        return bundle with { Id = persisted.Id };
    }
}

public sealed class PolicyWorkloadAllowlist(IConfiguration configuration) : IPolicyWorkloadAllowlist
{
    private readonly IReadOnlySet<string> entries = configuration
        .GetSection("Policy:Workloads")
        .GetChildren()
        .Select(child => $"{child["Issuer"]}|{child["ClientId"]}")
        .Where(entry => !entry.StartsWith("|", StringComparison.Ordinal) && !entry.EndsWith("|", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    // pi-lens-ignore: CS0246
    public bool IsAllowed(PolicyWorkloadIdentity workload) =>
        entries.Contains($"{workload.Issuer}|{workload.ClientId}");
}
