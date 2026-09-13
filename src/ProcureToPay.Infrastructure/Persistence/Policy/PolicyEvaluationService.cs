using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed class PolicyDependencyUnavailableException(string message) : DomainException(message)
{
}

public sealed class PolicyConfigurationUnavailableException(string message) : DomainException(message)
{
}

public sealed class PolicyPayloadTooLargeException(string message) : DomainException(message)
{
}

public sealed record PolicyWorkloadIdentity(string Issuer, string ClientId);
public sealed record PolicyWorkloadPrincipal(string Issuer, string ClientId);
public sealed record PolicyEvaluationMetadata(
    string Operation,
    string SubjectType,
    string FactsDigest,
    string ManifestDigest,
    string InputCanonicalJson,
    Guid? ActivationId = null,
    IReadOnlyDictionary<string, string>? Provenance = null,
    string? ManifestCanonicalJson = null);
public sealed record PolicyFactRequest(string SubjectType, Guid SubjectId, int SubjectVersion,
    string Operation, DateTimeOffset RequestedAtUtc, PolicyWorkloadPrincipal Workload,
    string CorrelationReference)
{
    public Guid? OrganizationId { get; init; }

    /// <summary>Previous evaluation of the same subject used for a reevaluation diff (REQ-13).</summary>
    public Guid? PreviousBundleId { get; init; }

    /// <summary>Declared reevaluation cause: MATERIAL_FACT_CHANGE or POLICY_VERSION_CHANGE (REQ-13).</summary>
    public string? Cause { get; init; }

    /// <summary>Result digest of the previous evaluation declared by the command (REQ-13).</summary>
    public string? PreviousResultDigest { get; init; }
}
public sealed record PolicyCompletenessManifest(Guid RequestId, int RequestVersion,
    IReadOnlyList<PolicySubjectReference> Lines, string Digest);
public sealed record PolicyFactBundle(PolicyRequestInput Request, PolicyCompletenessManifest Manifest,
    string ProviderId, string ContractVersion, string FactsDigest)
{
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

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
    PolicyFactProviderRegistry factProviderRegistry,
    PolicyExceptionVerifierRegistry exceptionVerifierRegistry,
    PolicyReferenceCatalogRegistry referenceCatalogRegistry,
    ILogger<PolicyEvaluationService> logger) : IPolicyEvaluationPort
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EvaluationGates = new(StringComparer.Ordinal);
    private static readonly Meter PolicyMeter = new("ProcureToPay.Policy", "1.0");
    private static readonly Counter<long> Evaluations = PolicyMeter.CreateCounter<long>(
        "procure_to_pay_policy_evaluations_total");
    private static readonly Counter<long> Replays = PolicyMeter.CreateCounter<long>(
        "procure_to_pay_policy_replays_total");
    private static readonly Counter<long> ProviderFailures = PolicyMeter.CreateCounter<long>(
        "procure_to_pay_policy_provider_failures_total");
    private static readonly Histogram<double> EvaluationDuration = PolicyMeter.CreateHistogram<double>(
        "procure_to_pay_policy_evaluation_duration_ms", "ms");

    private PolicyEvaluationBundle ReplayExistingEvaluation(
        PolicyEvaluationBundleRecord existing,
        PolicyFactRequest factRequest,
        string evaluationKey,
        PolicyWorkloadIdentity workload,
        Activity? activity = null,
        long startedAt = 0)
    {
        var replay = ValidatePersistedEvaluation(existing);
        if (replay.Subject.Id != factRequest.SubjectId ||
            replay.Subject.Version != factRequest.SubjectVersion ||
            replay.PreviousBundleId != factRequest.PreviousBundleId)
        {
            throw new DomainConflictException("The evaluation key is already bound to a different request.");
        }
        var replayCaller = new PolicyEvaluationCaller(
            existing.OrganizationId,
            workload.Issuer,
            workload.ClientId,
            factRequest.Operation,
            evaluationKey,
            existing.PolicySetVersionId,
            string.Empty)
        {
            SubjectType = factRequest.SubjectType,
            // pi-lens-ignore: CS0117
            Cause = factRequest.Cause ?? "FACT_PROVIDER_EVALUATION",
            PreviousResultDigest = factRequest.PreviousResultDigest
        };
        var replayFingerprint = PolicyPersistenceService.ComputeCommandFingerprint(
            replayCaller,
            replay.Subject,
            replay.PreviousBundleId);
        if (!string.Equals(existing.IdempotencyFingerprint, replayFingerprint, StringComparison.Ordinal))
        {
            throw new DomainConflictException("The evaluation key is already bound to a different request.");
        }
        Replays.Add(1, new KeyValuePair<string, object?>("operation", factRequest.Operation));
        activity?.SetTag("policy.selection", "REPLAY");
        activity?.SetTag("policy.version_id", existing.PolicySetVersionId.ToString("D"));
        activity?.SetTag("policy.result", replay.Result.ToString().ToUpperInvariant());
        activity?.SetTag("policy.content_digest", replay.PolicyContentDigest);
        activity?.SetTag("policy.scopes", string.Join(",", replay.ScopeEvaluations
            .Select(scope => scope.Scope.ToString().ToUpperInvariant())
            .Distinct()
            .OrderBy(value => value, StringComparer.Ordinal)));
        activity?.SetTag("policy.duration_ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        logger.LogInformation("Policy evaluation replayed from persistence. EvaluationId={EvaluationId}", existing.Id);
        return replay;
    }

    private static bool HasSameEvaluationContent(
        PolicyEvaluationBundle persisted,
        PolicyEvaluationBundle supplied)
    {
        if (persisted.Subject != supplied.Subject || persisted.ScopeEvaluations.Count != supplied.ScopeEvaluations.Count ||
            persisted.Controls.Count != supplied.Controls.Count || persisted.Result != supplied.Result)
        {
            return false;
        }
        var persistedDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            persisted.InputDigest, persisted.ScopeEvaluations, persisted.Controls, persisted.Result, null));
        var suppliedDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            supplied.InputDigest, supplied.ScopeEvaluations, supplied.Controls, supplied.Result, null));
        return string.Equals(persistedDigest, suppliedDigest, StringComparison.OrdinalIgnoreCase);
    }

    private static PolicyEvaluationBundle ValidatePersistedEvaluation(PolicyEvaluationBundleRecord existing)
    {
        PolicyEvaluationBundle replay;
        try
        {
            replay = PolicyEvaluationBundleReplay.FromJson(existing.BundleJson);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PolicyDependencyUnavailableException("Persisted policy evaluation is corrupted.");
        }
        var recomputedInputDigest = string.IsNullOrWhiteSpace(replay.InputCanonicalJson)
            ? string.Empty
            : PolicyCanonicalizer.Hash(replay.InputCanonicalJson);
        var recomputedResultDigest = string.IsNullOrWhiteSpace(replay.InputCanonicalJson)
            ? string.Empty
            : PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
                recomputedInputDigest,
                replay.ScopeEvaluations,
                replay.Controls,
                // pi-lens-ignore: lsp:CS1061
                replay.Diff.Count == 0 ? null : replay.Diff));
        var recomputedManifestDigest = string.IsNullOrWhiteSpace(replay.ManifestCanonicalJson)
            ? null
            // pi-lens-ignore: lsp:CS1061
            : PolicyCanonicalizer.Hash(replay.ManifestCanonicalJson);
        if (replay.Id != existing.Id ||
            !string.Equals(replay.EvaluationKey, existing.EvaluationKey, StringComparison.Ordinal) ||
            replay.Subject.Id != existing.SubjectId ||
            replay.Subject.Version != existing.SubjectVersion ||
            !string.Equals(replay.Operation, existing.Operation, StringComparison.Ordinal) ||
            replay.EvaluatedAt != existing.EvaluatedAt ||
            !string.Equals(replay.PolicyContentDigest, existing.PolicyContentDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(replay.Result.ToString().ToUpperInvariant(), existing.Result, StringComparison.Ordinal) ||
            replay.PreviousBundleId != existing.PreviousBundleId ||
            !string.Equals(replay.InputDigest, existing.InputDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(replay.ResultDigest, existing.ResultDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(replay.InputDigest, recomputedInputDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(replay.ResultDigest, recomputedResultDigest, StringComparison.OrdinalIgnoreCase) ||
            (replay.ManifestCanonicalJson is not null &&
             !string.Equals(replay.ManifestDigest, recomputedManifestDigest, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PolicyDependencyUnavailableException("Persisted policy evaluation integrity check failed.");
        }
        return replay;
    }

    public async Task<PolicyEvaluationBundle> ApplyQuotationWaiverAsync(
        PolicyEvaluationBundle bundle,
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = PolicyTelemetry.Source.StartActivity("policy.quotation_waiver");
        var waiverStartedAt = Stopwatch.GetTimestamp();
        activity?.SetTag("policy.evaluation_id", bundle.Id.ToString("D"));
        activity?.SetTag("policy.target_requirement_key", request.TargetRequirementKey);
        activity?.SetTag("policy.correlation_reference", request.CorrelationReference);
        var verifier = exceptionVerifierRegistry.Resolve();
        activity?.SetTag("policy.verifier_id", verifier.VerifierId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var persistedRecord = await persistenceService.FindEvaluationAsync(bundle.Id, cancellationToken)
            ?? throw new DomainConflictException("The quotation waiver evaluation is not persisted.");
        var persistedBundle = ValidatePersistedEvaluation(persistedRecord);
        // The target lines come from the persisted evaluation, never from the caller (REQ-01).
        var targetControl = persistedBundle.Controls.Where(control =>
            control.Type == PolicyEffectType.RequireQuotations &&
            string.Equals(control.RequirementKey, request.TargetRequirementKey, StringComparison.Ordinal))
            .ToArray();
        if (targetControl.Length == 0)
        {
            throw new DomainConflictException("The quotation waiver target is not present in the evaluation.");
        }

        var persistedTargets = targetControl.SelectMany(control => control.SubjectIds).ToHashSet();
        if (request.Binding.CoveredLines.Length != persistedTargets.Count ||
            request.Binding.CoveredLines.Any(target => !persistedTargets.Contains(target.Id)) ||
            persistedBundle.MaterialProjection is null ||
            request.Binding.CoveredLines.Any(target =>
                persistedBundle.MaterialProjection.Targets.All(material =>
                    material.Id != target.Id || material.Version != target.Version ||
                    !string.Equals(material.MaterialSnapshotDigest, target.MaterialSnapshotDigest,
                        StringComparison.OrdinalIgnoreCase))))
        {
            throw new DomainConflictException(
                "The quotation waiver covered lines do not match the persisted evaluation.");
        }

        // The binding is recomputed from the persisted evaluation and must reproduce the request.
        var expectedBinding = new PolicyExceptionBindingRequest(
            persistedRecord.OrganizationId,
            persistedBundle.Operation,
            persistedBundle.Subject.Id,
            persistedBundle.Subject.Version,
            persistedRecord.Id,
            persistedBundle.ResultDigest,
            persistedRecord.PolicySetVersionId,
            persistedBundle.PolicyContentDigest,
            persistedBundle.ManifestDigest ?? string.Empty,
            request.Binding.TargetRequirementKey,
            request.Binding.CoveredLines,
            request.Binding.From,
            request.Binding.To,
            request.Binding.Floor,
            request.Binding.ReferenceId,
            request.Binding.RequesterId,
            request.Binding.OriginatorId,
            request.Binding.WorkloadSubjectId,
            request.Binding.RequestedAt,
            request.Binding.RequestedValidTo,
            request.Binding.Nonce);
        if (!string.Equals(expectedBinding.ComputeBinding(), request.BindingDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException("The quotation waiver binding does not match the persisted evaluation.");
        }

        QuotationWaiverEvidence evidence;
        try
        {
            evidence = await QuotationWaiverEvaluator.VerifyAsync(
                request, verifier, DateTimeOffset.UtcNow, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PolicyDependencyUnavailableException("The quotation waiver verifier timed out.");
        }
        activity?.SetTag("policy.version_id", persistedRecord.PolicySetVersionId.ToString("D"));
        activity?.SetTag("policy.content_digest", persistedBundle.PolicyContentDigest);
        activity?.SetTag("policy.scopes", string.Join(",", persistedBundle.ScopeEvaluations
            .Select(scope => scope.Scope.ToString().ToUpperInvariant())
            .Distinct()
            .OrderBy(value => value, StringComparer.Ordinal)));
        if (!HasSameEvaluationContent(persistedBundle, bundle))
        {
            throw new DomainConflictException("The quotation waiver evaluation does not match persisted evidence.");
        }

        var reduced = QuotationWaiverEvaluator.ApplyVerifiedQuotationWaiver(persistedBundle, request, evidence);
        var (_, reevaluation) = await persistenceService.AppendVerifiedQuotationWaiverAsync(
            persistedBundle, reduced, request, evidence, cancellationToken);
        activity?.SetTag("policy.result", reevaluation.Result);
        activity?.SetTag("policy.blocked", string.Equals(reevaluation.Result, "BLOCKED", StringComparison.Ordinal));
        activity?.SetTag("policy.duration_ms", Stopwatch.GetElapsedTime(waiverStartedAt).TotalMilliseconds);
        return ValidatePersistedEvaluation(reevaluation);
    }

    public async Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestAsync(
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken = default)
    {
        var gateKey = string.Join("\u001f", factRequest.Workload.Issuer, factRequest.Workload.ClientId,
            factRequest.Operation, evaluationKey);
        var gate = EvaluationGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await EvaluateEnterprisePurchaseRequestUnserializedAsync(factRequest, evaluationKey, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestUnserializedAsync(
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var activity = PolicyTelemetry.Source.StartActivity("policy.evaluate");
        activity?.SetTag("policy.operation", factRequest.Operation);
        activity?.SetTag("policy.subject_type", factRequest.SubjectType);
        activity?.SetTag("policy.correlation_reference", factRequest.CorrelationReference);
        var workload = new PolicyWorkloadIdentity(factRequest.Workload.Issuer, factRequest.Workload.ClientId);
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }
        ValidateEvaluationKey(evaluationKey);
        if (factRequest.OrganizationId is null)
        {
            throw new DomainValidationException("Policy evaluation requires an organization scope.");
        }
        factRequest = factRequest with { RequestedAtUtc = DateTimeOffset.UtcNow };

        if (factRequest.OrganizationId is Guid requestOrganizationId)
        {
            var persistedByRequestOrganization = await persistenceService.FindByWorkloadEvaluationKeyAsync(
                requestOrganizationId, workload.Issuer, workload.ClientId, factRequest.Operation,
                evaluationKey, cancellationToken);
            if (persistedByRequestOrganization is not null)
            {
                return ReplayExistingEvaluation(
                    persistedByRequestOrganization, factRequest, evaluationKey, workload, activity, startedAt);
            }
        }
        var provider = factProviderRegistry.Resolve(factRequest.SubjectType, factRequest.Operation);
        PolicyEvaluationReservationRecord? reservation = null;
        if (factRequest.OrganizationId is Guid organizationId)
        {
            reservation = await persistenceService.ReserveEvaluationKeyAsync(
                organizationId, workload.Issuer, workload.ClientId, factRequest.Operation, evaluationKey,
                factRequest.SubjectId, factRequest.SubjectVersion,
                ComputePreProviderFingerprint(factRequest, workload), cancellationToken);
            if (reservation is null)
            {
                var persisted = await persistenceService.FindByWorkloadEvaluationKeyAsync(
                    organizationId, workload.Issuer, workload.ClientId, factRequest.Operation,
                    evaluationKey, cancellationToken)
                    ?? throw new PolicyDependencyUnavailableException("Evaluation reservation completed without a bundle.");
                return ReplayExistingEvaluation(persisted, factRequest, evaluationKey, workload, activity, startedAt);
            }
        }

        try
        {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        PolicyFactBundle facts;
        try
        {
            facts = await provider.GetFactsAsync(factRequest, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ProviderFailures.Add(1, new KeyValuePair<string, object?>("operation", factRequest.Operation));
            throw new PolicyDependencyUnavailableException("The policy fact provider timed out.");
        }
        catch (Exception exception) when (exception is not DomainException)
        {
            ProviderFailures.Add(1, new KeyValuePair<string, object?>("operation", factRequest.Operation));
            logger.LogWarning(exception, "Policy fact provider failed.");
            throw new PolicyDependencyUnavailableException("The policy fact provider is unavailable.");
        }
        var at = factRequest.RequestedAtUtc;
        var active = await persistenceService.FindActiveAsync(facts.Request.OrganizationId, at, cancellationToken)
            ?? throw new PolicyConfigurationUnavailableException("No active policy is available.");
        var scopedExisting = await persistenceService.FindByWorkloadEvaluationKeyAsync(
            facts.Request.OrganizationId, workload.Issuer, workload.ClientId, factRequest.Operation,
            evaluationKey, cancellationToken);
        if (scopedExisting is not null)
        {
            return ReplayExistingEvaluation(scopedExisting, factRequest, evaluationKey, workload, activity, startedAt);
        }
        var policy = PolicyDocumentParser.Parse(
            active.PolicySetVersion.ContentJson,
            active.PolicySetVersion.Id,
            active.PolicySetVersion.OrganizationId,
            active.PolicySetVersion.Sequence,
            (PolicySetStatus)active.PolicySetVersion.Status,
            active.PolicySetVersion.ContentDigest);
        activity?.SetTag("policy.version_id", policy.Id.ToString("D"));
        activity?.SetTag("policy.content_digest", policy.ContentDigest);
        activity?.SetTag("policy.scopes", string.Join(",", policy.Scopes
            .Select(scope => scope.ToString().ToUpperInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)));
        var result = await EvaluateEnterprisePurchaseRequestCoreAsync(
            policy, factRequest with { RequestedAtUtc = at }, evaluationKey, cancellationToken, facts);
        Evaluations.Add(1,
            new KeyValuePair<string, object?>("operation", factRequest.Operation),
            new KeyValuePair<string, object?>("subject_type", factRequest.SubjectType),
            new KeyValuePair<string, object?>("result", result.Result.ToString()));
        activity?.SetTag("policy.result", result.Result.ToString().ToUpperInvariant());
        if (result.Result == PolicyResult.Blocked)
        {
            activity?.SetTag("policy.blocked", true);
        }
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        activity?.SetTag("policy.duration_ms", elapsedMilliseconds);
        EvaluationDuration.Record(elapsedMilliseconds,
            new KeyValuePair<string, object?>("operation", factRequest.Operation));
        return result;
        }
        finally
        {
            if (reservation is not null)
            {
                await persistenceService.ReleaseEvaluationKeyAsync(reservation.Id, CancellationToken.None);
            }
        }
    }

    public async Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestAsync(
        PolicySetVersion policySnapshot,
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken = default)
    {
        ValidateEvaluationKey(evaluationKey);
        if (factRequest.OrganizationId != policySnapshot.OrganizationId)
        {
            throw new DomainConflictException("Policy and request organizations differ.");
        }
        return await EvaluateEnterprisePurchaseRequestAsync(factRequest, evaluationKey, cancellationToken);
    }

    private async Task<PolicyEvaluationBundle> EvaluateEnterprisePurchaseRequestCoreAsync(
        PolicySetVersion policySnapshot,
        PolicyFactRequest factRequest,
        string evaluationKey,
        CancellationToken cancellationToken,
        PolicyFactBundle? preloadedFacts)
    {
        var workload = new PolicyWorkloadIdentity(factRequest.Workload.Issuer, factRequest.Workload.ClientId);
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }

        var provider = factProviderRegistry.Resolve(factRequest.SubjectType, factRequest.Operation);
        ValidateEvaluationKey(evaluationKey);
        if (factRequest.OrganizationId is null)
        {
            throw new DomainValidationException("Policy evaluation requires an organization scope.");
        }
        if (preloadedFacts is null)
        {
            factRequest = factRequest with { RequestedAtUtc = DateTimeOffset.UtcNow };
        }
        PolicyFactBundle facts;
        if (preloadedFacts is not null)
        {
            facts = preloadedFacts;
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                facts = await provider.GetFactsAsync(factRequest, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PolicyDependencyUnavailableException("The policy fact provider timed out.");
            }
            catch (Exception exception) when (exception is not DomainException)
            {
                logger.LogWarning(exception, "Policy fact provider failed.");
                throw new PolicyDependencyUnavailableException("The policy fact provider is unavailable.");
            }
        }
        if (facts.Request.Lines.Count > 500)
        {
            throw new PolicyPayloadTooLargeException("Policy requests support at most 500 lines.");
        }
        if (facts.Request.Lines.Any(line => line.Facts.Keys.Count(key =>
                key.StartsWith("RISK_ANSWER", StringComparison.Ordinal)) > 256))
        {
            throw new PolicyPayloadTooLargeException("Policy lines support at most 256 risk answers.");
        }
        if (facts.Provenance.Any(pair =>
                System.Text.Encoding.UTF8.GetByteCount(pair.Value) > 4096))
        {
            throw new PolicyPayloadTooLargeException("Policy provenance entries support at most 4 KiB each.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(
                PolicyCanonicalizer.CanonicalizeRequest(facts.Request, factRequest.RequestedAtUtc)) > 5 * 1024 * 1024)
        {
            throw new PolicyPayloadTooLargeException("Policy snapshots support at most 5 MiB.");
        }

        var expectedLines = facts.Request.Lines
            .Select(line => new { id = line.Subject.Id, version = line.Subject.Version })
            .OrderBy(line => line.id)
            .ThenBy(line => line.version)
            .ToArray();
        var expectedManifestDigest = ComputeManifestDigest(facts.Manifest);
        var manifestLines = facts.Manifest.Lines
            .Select(line => new { id = line.Id, version = line.Version })
            .OrderBy(line => line.id)
            .ThenBy(line => line.version)
            .ToArray();
        await ValidateReferenceCatalogsAsync(facts.Request, cancellationToken);
        if (!string.Equals(facts.ProviderId, provider.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(facts.ContractVersion, provider.ContractVersion, StringComparison.Ordinal) ||
            facts.Request.OrganizationId != policySnapshot.OrganizationId ||
            (factRequest.OrganizationId is Guid requestedOrganizationId &&
             facts.Request.OrganizationId != requestedOrganizationId) ||
            facts.Manifest.RequestId != facts.Request.Subject.Id ||
            facts.Manifest.RequestVersion != facts.Request.Subject.Version ||
            !expectedLines.SequenceEqual(manifestLines) ||
            !string.Equals(facts.Manifest.Digest, expectedManifestDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(facts.FactsDigest, ComputeFactsDigest(facts), StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("The policy fact bundle or completeness manifest is invalid.");
        }

        var evaluatedAt = factRequest.RequestedAtUtc;
        if (factRequest.PreviousBundleId is null && factRequest.Cause is not null)
        {
            throw new DomainValidationException("A reevaluation cause requires a previous bundle reference.");
        }
        PolicyEvaluationBundle? previousBundle = null;
        if (factRequest.PreviousBundleId is Guid previousEvaluationId)
        {
            if (factRequest.Cause is not ("MATERIAL_FACT_CHANGE" or "POLICY_VERSION_CHANGE"))
            {
                throw new DomainValidationException(
                    "A reevaluation must declare MATERIAL_FACT_CHANGE or POLICY_VERSION_CHANGE.");
            }
            if (string.IsNullOrWhiteSpace(factRequest.PreviousResultDigest))
            {
                throw new DomainValidationException("A reevaluation must declare the previous result digest.");
            }
            var previousRecord = await persistenceService.FindEvaluationAsync(
                previousEvaluationId, cancellationToken)
                ?? throw new DomainConflictException("The previous policy evaluation is not available.");
            if (previousRecord.OrganizationId != policySnapshot.OrganizationId ||
                previousRecord.SubjectId != facts.Request.Subject.Id)
            {
                throw new DomainConflictException("The previous policy evaluation does not match the subject.");
            }
            previousBundle = ValidatePersistedEvaluation(previousRecord);
            if (!string.Equals(factRequest.PreviousResultDigest, previousBundle.ResultDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainConflictException(
                    "The declared previous result digest does not match the persisted evaluation.");
            }
        }
        return await EvaluateActivePurchaseRequestAsync(
            policySnapshot,
            facts.Request,
            workload,
            evaluationKey,
            evaluatedAt,
            factRequest.CorrelationReference,
            cancellationToken,
            new PolicyEvaluationMetadata(
                factRequest.Operation,
                factRequest.SubjectType,
                facts.FactsDigest,
                facts.Manifest.Digest,
                PolicyCanonicalizer.CanonicalizeRequest(facts.Request, evaluatedAt),
                Provenance: facts.Provenance,
                ManifestCanonicalJson: CanonicalizeManifest(facts.Manifest)),
            previousBundle,
            factRequest.Cause,
            factRequest.PreviousResultDigest);
    }

    public static string ComputeManifestDigest(PolicyCompletenessManifest manifest) =>
        PolicyCanonicalizer.Hash(CanonicalizeManifest(manifest));

    /// <summary>Canonical JSON of the confirmed completeness manifest (SPEC 05 REQ-01).</summary>
    public static string CanonicalizeManifest(PolicyCompletenessManifest manifest)
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["line_count"] = manifest.Lines.Count,
            ["lines"] = manifest.Lines
                .Select(line => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = line.Id.ToString("D"),
                    ["version"] = line.Version
                })
                .OrderBy(line => PolicyCanonicalizer.SerializeCanonical(line),
                    Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
                .ToArray(),
            ["request_id"] = manifest.RequestId.ToString("D"),
            ["request_version"] = manifest.RequestVersion
        };
        return PolicyCanonicalizer.SerializeCanonical(preimage);
    }

    private static object CanonicalizeFactPayload(PolicyRequestInput request)
    {
        using var document = JsonDocument.Parse(PolicyCanonicalizer.CanonicalizeRequest(
            request, DateTimeOffset.UnixEpoch));
        var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "evaluated_at_utc", StringComparison.Ordinal))
            {
                var name = string.Equals(property.Name, "subject", StringComparison.Ordinal)
                    ? "subject_ref" : property.Name;
                properties[name] = property.Value.Clone();
            }
        }
        return properties;
    }

    private static void ValidateEvaluationKey(string evaluationKey)
    {
        if (string.IsNullOrWhiteSpace(evaluationKey) || evaluationKey.Length > 128 ||
            !evaluationKey.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or ':' or '-'))
        {
            throw new DomainValidationException("Evaluation key is invalid.");
        }
    }

    private static string ComputePreProviderFingerprint(
        PolicyFactRequest factRequest,
        PolicyWorkloadIdentity workload) =>
        PolicyPersistenceService.ComputeCommandFingerprint(
            new PolicyEvaluationCaller(
                factRequest.OrganizationId ?? Guid.Empty,
                workload.Issuer,
                workload.ClientId,
                factRequest.Operation,
                string.Empty,
                Guid.Empty,
                string.Empty)
            {
                SubjectType = factRequest.SubjectType,
                Cause = "FACT_PROVIDER_EVALUATION"
            },
            new PolicySubjectReference(factRequest.SubjectId, factRequest.SubjectVersion));

    public static string ComputeFactsDigest(PolicyFactBundle bundle)
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["completeness_manifest"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["digest"] = bundle.Manifest.Digest,
                ["lines"] = bundle.Manifest.Lines
                    .Select(line => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = line.Id.ToString("D"),
                        ["version"] = line.Version
                    })
                    .OrderBy(line => PolicyCanonicalizer.SerializeCanonical(line),
                        Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
                    .ToArray(),
                ["request_id"] = bundle.Manifest.RequestId.ToString("D"),
                ["request_version"] = bundle.Manifest.RequestVersion
            },
            ["facts"] = CanonicalizeFactPayload(bundle.Request),
            ["provenance"] = new SortedDictionary<string, object?>(
                bundle.Provenance.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            ["provider_contract_version"] = bundle.ContractVersion,
            ["provider_id"] = bundle.ProviderId,
            ["subject_ref"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = bundle.Request.Subject.Id.ToString("D"),
                ["version"] = bundle.Request.Subject.Version
            },
            ["organization_id"] = bundle.Request.OrganizationId.ToString("D"),
            ["legal_entity_id"] = bundle.Request.LegalEntityId.ToString("D"),
            ["base_currency"] = bundle.Request.BaseCurrency
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    private async Task ValidateReferenceCatalogsAsync(
        PolicyRequestInput request,
        CancellationToken cancellationToken)
    {
        var values = Flatten(request.Facts?.Values ?? [])
            .Concat(request.Lines.SelectMany(line => Flatten(line.Facts.Values)))
            .Where(RequiresCatalog)
            .ToArray();
        foreach (var value in values)
        {
            var resolverKey = ResolverKey(value);
            var catalog = referenceCatalogRegistry.Resolve(resolverKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            bool exists;
            try
            {
                exists = await catalog.ExistsAsync(Lookup(resolverKey, value), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PolicyDependencyUnavailableException("The policy reference catalog timed out.");
            }
            if (!exists)
            {
                throw new DomainValidationException("A policy reference could not be verified by its catalog.");
            }
        }
    }

    private static IEnumerable<PolicyValue> Flatten(IEnumerable<PolicyValue> values)
    {
        foreach (var value in values)
        {
            if (value.Kind == PolicyValueKind.Set)
            {
                foreach (var member in value.Members)
                {
                    yield return member;
                }
            }
            else
            {
                yield return value;
            }
        }
    }

    /// <summary>Catalog-keyed references resolved by catalog, entity type or question schema (REQ-05).</summary>
    private static bool RequiresCatalog(PolicyValue value) =>
        value.Kind is PolicyValueKind.VersionedEntityRef or PolicyValueKind.VersionedCodeRef or PolicyValueKind.TypedAnswer;

    private static string ResolverKey(PolicyValue value) => value.Kind switch
    {
        PolicyValueKind.VersionedEntityRef => value.ReferenceType!,
        PolicyValueKind.VersionedCodeRef => value.Catalog!,
        PolicyValueKind.TypedAnswer => value.QuestionCode!,
        _ => throw new DomainValidationException("Unsupported policy reference kind.")
    };

    private static PolicyReferenceLookup Lookup(string resolverKey, PolicyValue value)
    {
        switch (value.Kind)
        {
            case PolicyValueKind.VersionedEntityRef:
                return new PolicyReferenceLookup(
                    resolverKey, value.EntityId ?? Guid.Empty, value.Version ?? 1, string.Empty);
            case PolicyValueKind.VersionedCodeRef:
                return new PolicyReferenceLookup(
                    resolverKey, Guid.Empty, value.Version ?? 1, value.Digest ?? string.Empty)
                {
                    Code = value.Value
                };
            case PolicyValueKind.TypedAnswer:
                return new PolicyReferenceLookup(
                    resolverKey, Guid.Empty, value.SchemaVersion ?? 1, string.Empty)
                {
                    Code = value.Value,
                    ValueKind = value.AnswerKind switch
                {
                    TypedAnswerValueKind.Boolean => "BOOLEAN",
                    TypedAnswerValueKind.EnumCode => "ENUM_CODE",
                    _ => null
                }
                };
            default:
                throw new DomainValidationException("Unsupported policy reference kind.");
        }
    }

    public async Task<PolicyEvaluationBundle> EvaluateSourcingAsync(
        PolicySetVersion policy,
        PolicySourcingInput sourcing,
        PolicyWorkloadIdentity workload,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        using var activity = PolicyTelemetry.Source.StartActivity("policy.evaluate_sourcing");
        var sourcingStartedAt = Stopwatch.GetTimestamp();
        activity?.SetTag("policy.operation", "SOURCING_PO");
        activity?.SetTag("policy.evaluation_key", evaluationKey);
        activity?.SetTag("policy.correlation_reference", correlationReference);
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }
        ValidateEvaluationKey(evaluationKey);
        if (policy.OrganizationId != sourcing.Request.OrganizationId ||
            !string.Equals(
                PolicyCanonicalizer.ComputePolicyDigest(policy), policy.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PolicyConfigurationUnavailableException("The sourcing policy snapshot is invalid.");
        }
        var activePolicy = await persistenceService.FindActiveAsync(
            sourcing.Request.OrganizationId, evaluatedAt, cancellationToken);
        if (activePolicy is null || activePolicy.PolicySetVersionId != policy.Id ||
            !string.Equals(activePolicy.PolicySetVersion.ContentDigest, policy.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException("The sourcing policy is not current.");
        }
        if (sourcing.CurrentRequestEvaluation is null)
        {
            throw new DomainConflictException("Sourcing evaluation requires a request evaluation.");
        }
        if (!sourcing.Manifest.CoveredLines.ToHashSet().SetEquals(sourcing.CoveredLines))
        {
            throw new DomainConflictException("The sourcing attestation does not cover the requested line set.");
        }
        var current = await persistenceService.FindEvaluationAsync(
            sourcing.CurrentRequestEvaluation.Id, cancellationToken);
        var latest = await persistenceService.FindLatestEvaluationAsync(
            sourcing.Request.OrganizationId, sourcing.Request.Subject.Id, sourcing.Request.Subject.Version,
            cancellationToken);
        if (current is null || latest is null || latest.Id != current.Id ||
            current.SubjectId != sourcing.Request.Subject.Id ||
            current.SubjectVersion != sourcing.Request.Subject.Version ||
            current.EvaluationSequence != sourcing.CurrentRequestEvaluation.EvaluationSequence ||
            !string.Equals(current.InputDigest, sourcing.CurrentRequestEvaluation.InputDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.PolicyContentDigest, sourcing.CurrentRequestEvaluation.PolicyContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException("The request evaluation is not current or persisted.");
        }
        var persistedCurrent = ValidatePersistedEvaluation(current);
        if (!string.Equals(persistedCurrent.ResultDigest, sourcing.CurrentRequestEvaluation.ResultDigest,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(persistedCurrent.FactsDigest, sourcing.CurrentRequestEvaluation.FactsDigest,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(persistedCurrent.ManifestDigest, sourcing.CurrentRequestEvaluation.ManifestDigest,
                StringComparison.OrdinalIgnoreCase) ||
            !HasSameEvaluationContent(persistedCurrent, sourcing.CurrentRequestEvaluation))
        {
            throw new DomainConflictException("The request evaluation evidence is not current.");
        }
        var bundle = PolicyEvaluator.EvaluateSourcing(policy, sourcing, evaluationKey, evaluatedAt);
        var existingSourcing = await persistenceService.FindLatestEvaluationAsync(
            sourcing.Request.OrganizationId, sourcing.Subject.Id, sourcing.Subject.Version,
            cancellationToken);
        if (existingSourcing is not null &&
            string.Equals(existingSourcing.Operation, "SOURCING_PO", StringComparison.Ordinal) &&
            !string.Equals(existingSourcing.InputDigest, bundle.InputDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException(
                "Sourcing facts changed without incrementing the sourcing subject version.");
        }
        var persisted = await persistenceService.AppendEvaluationAsync(
            bundle,
            new PolicyEvaluationCaller(
                sourcing.Request.OrganizationId,
                workload.Issuer,
                workload.ClientId,
                "SOURCING_PO",
                evaluationKey,
                policy.Id,
                correlationReference)
            {
                SubjectType = "SOURCING_PO"
            },
            cancellationToken);
        activity?.SetTag("policy.result", bundle.Result.ToString().ToUpperInvariant());
        activity?.SetTag("policy.blocked", bundle.Result == PolicyResult.Blocked);
        activity?.SetTag("policy.version_id", policy.Id.ToString("D"));
        activity?.SetTag("policy.content_digest", policy.ContentDigest);
        activity?.SetTag("policy.scopes", string.Join(",", policy.Scopes
            .Select(scope => scope.ToString().ToUpperInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)));
        activity?.SetTag("policy.duration_ms", Stopwatch.GetElapsedTime(sourcingStartedAt).TotalMilliseconds);
        return ValidatePersistedEvaluation(persisted);
    }

    public async Task<PolicyEvaluationBundle> EvaluateActivePurchaseRequestAsync(
        PolicySetVersion policySnapshot,
        PolicyRequestInput request,
        PolicyWorkloadIdentity workload,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        string correlationReference,
        CancellationToken cancellationToken = default,
        PolicyEvaluationMetadata? metadata = null,
        PolicyEvaluationBundle? previousBundle = null,
        string? declaredCause = null,
        string? previousResultDigest = null)
    {
        var active = await persistenceService.FindActiveAsync(
            request.OrganizationId, evaluatedAt, cancellationToken)
            ?? throw new PolicyConfigurationUnavailableException("No active policy is available.");
        if (active.PolicySetVersionId != policySnapshot.Id ||
            !string.Equals(active.PolicySetVersion.ContentDigest, policySnapshot.ContentDigest,
                StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException("The selected policy snapshot is not current.");
        }
        if (!string.Equals(PolicyCanonicalizer.ComputePolicyDigest(policySnapshot),
                active.PolicySetVersion.ContentDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new PolicyDependencyUnavailableException("The active policy digest is corrupted.");
        }
        return await EvaluatePurchaseRequestAsync(
            policySnapshot, request, workload, evaluationKey, evaluatedAt, correlationReference, cancellationToken,
            metadata is null ? null : metadata with { ActivationId = active.Id }, previousBundle,
            declaredCause, previousResultDigest);
    }

    public async Task<PolicyEvaluationBundle> EvaluatePurchaseRequestAsync(
        PolicySetVersion policy,
        PolicyRequestInput request,
        PolicyWorkloadIdentity workload,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        string correlationReference,
        CancellationToken cancellationToken = default,
        PolicyEvaluationMetadata? metadata = null,
        PolicyEvaluationBundle? previousBundle = null,
        string? declaredCause = null,
        string? previousResultDigest = null)
    {
        if (!workloadAllowlist.IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload is not allowlisted for policy evaluation.");
        }

        var bundle = PolicyEvaluator.EvaluateRequest(policy, request, evaluationKey, evaluatedAt);
        if (metadata?.Provenance is { Count: > 0 } provenance)
        {
            bundle = bundle with
            {
                Controls = AttachProvenance(bundle.Controls, provenance),
                ScopeEvaluations = bundle.ScopeEvaluations
                    .Select(scope => scope with { Controls = AttachProvenance(scope.Controls, provenance) })
                    .ToArray()
            };
        }
        if (previousBundle is not null)
        {
            if (previousBundle.Subject.Id != request.Subject.Id)
            {
                throw new DomainConflictException("A reevaluation must reference the same policy subject.");
            }
            if (declaredCause is not ("MATERIAL_FACT_CHANGE" or "POLICY_VERSION_CHANGE"))
            {
                throw new DomainValidationException(
                    "A reevaluation must declare MATERIAL_FACT_CHANGE or POLICY_VERSION_CHANGE.");
            }
            bundle = bundle with
            {
                PreviousBundleId = previousBundle.Id,
                Diff = PolicyEvaluationDiff.Compute(previousBundle, bundle)
            };
        }
        var operation = metadata?.Operation ?? "PURCHASE_REQUEST";
        var inputCanonical = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            evaluatedAt,
            operation,
            request.Subject,
            policy.ContentDigest!,
            metadata?.ActivationId,
            metadata?.FactsDigest,
            metadata?.ManifestDigest,
            bundle.PreviousBundleId,
            metadata?.InputCanonicalJson ?? string.Empty);
        var inputDigest = PolicyCanonicalizer.Hash(inputCanonical);
        var resultDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            inputDigest, bundle.ScopeEvaluations, bundle.Controls,
            bundle.Diff.Count == 0 ? null : bundle.Diff));
        var cause = previousBundle is null
            ? metadata is null ? "INITIAL" : "FACT_PROVIDER_EVALUATION"
            : declaredCause!;
        // SPEC 05 REQ-01: the material projection is derived from the exact persisted snapshot,
        // the confirmed manifest and the provider provenance; no provider is consulted again.
        var requestSnapshot = metadata?.InputCanonicalJson;
        var materialProjection = requestSnapshot is null || metadata?.ManifestDigest is null
            ? null
            : PolicyApprovalTargets.Build(requestSnapshot, metadata.ManifestDigest, metadata.Provenance);
        bundle = bundle with
        {
            Operation = operation,
            ActivationId = metadata?.ActivationId,
            FactsDigest = metadata?.FactsDigest,
            ManifestDigest = metadata?.ManifestDigest,
            ManifestCanonicalJson = metadata?.ManifestCanonicalJson,
            InputCanonicalJson = inputCanonical,
            // pi-lens-ignore: CS0117
            RequestSnapshotJson = requestSnapshot,
            MaterialProjection = materialProjection,
            InputDigest = inputDigest,
            ResultDigest = resultDigest,
            // pi-lens-ignore: CS0117
            Cause = cause
        };
        var persisted = await persistenceService.AppendEvaluationAsync(
            bundle,
            new PolicyEvaluationCaller(
                request.OrganizationId,
                workload.Issuer,
                workload.ClientId,
                operation,
                evaluationKey,
                policy.Id,
                correlationReference)
            {
                SubjectType = metadata?.SubjectType ?? "PURCHASE_REQUEST",
                Cause = cause,
                PreviousResultDigest = previousResultDigest,
                FactsDigest = metadata?.FactsDigest,
                ManifestDigest = metadata?.ManifestDigest,
                InputCanonicalJson = inputCanonical
            },
            cancellationToken);
        logger.LogInformation(
            "Policy evaluation persisted. EvaluationId={EvaluationId} Operation={Operation} Result={Result}",
            persisted.Id, metadata?.Operation ?? "PURCHASE_REQUEST", bundle.Result);
        return ValidatePersistedEvaluation(persisted);
    }

    /// <summary>Attaches provider provenance to the facts that justified each control (CA-05).</summary>
    private static IReadOnlyList<PolicyGeneratedControl> AttachProvenance(
        IReadOnlyList<PolicyGeneratedControl> controls,
        IReadOnlyDictionary<string, string> provenance) =>
        controls.Select(control => control with
        {
            FactProvenance = control.OriginFacts
                .Where(provenance.ContainsKey)
                .ToImmutableDictionary(fact => fact, fact => provenance[fact], StringComparer.Ordinal)
        }).ToArray();
}

internal static class PolicyEvaluationBundleReplay
{
    public static PolicyEvaluationBundle FromJson(string json)
    {
        var type = typeof(PolicyEvaluationBundle).Assembly.GetType(
            "ProcureToPay.Domain.Modules.Policy.PolicyEvaluationBundleRehydrator")
            ?? throw new PolicyDependencyUnavailableException("Policy bundle rehydrator is unavailable.");
        var method = type.GetMethod("FromJson")
            ?? throw new PolicyDependencyUnavailableException("Policy bundle rehydrator is unavailable.");
        try
        {
            return (PolicyEvaluationBundle)method.Invoke(null, [json])!;
        }
        catch (TargetInvocationException)
        {
            throw new PolicyDependencyUnavailableException("Persisted policy evaluation is corrupted.");
        }
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
