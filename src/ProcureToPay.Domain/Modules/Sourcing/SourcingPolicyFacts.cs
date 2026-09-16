using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Sourcing;

/// <summary>
/// Typed fact envelope of the sourcing proposal (<c>sourcing-policy-fact-envelope/v1</c>, REQ-10). It
/// is produced server-side by the Sourcing domain: the request input is derived from the attested
/// Purchase Request, the covered lines are the selected ones, the manifest is the canonical
/// <c>sourcing-completeness-manifest/v1</c> document and the current request evaluation is the
/// <c>policy-evaluation-ref/v1</c> the proposal was built against. No HTTP workload supplies a
/// snapshot, an input, a fact, a manifest or a bundle request.
/// </summary>
public sealed record SourcingPolicyFactEnvelope
{
    public const string ContractVersion = SourcingCodes.PolicyFactEnvelopeContract;

    public SourcingPolicyFactEnvelope(
        string providerId,
        string providerContractVersion,
        PolicySubjectReference subject,
        PolicyRequestInput request,
        IReadOnlyList<PolicySubjectReference> coveredLines,
        IReadOnlyDictionary<string, PolicyValue> facts,
        IReadOnlyDictionary<string, string> provenance,
        SourcingCompletenessManifest manifest,
        SourcingPolicyEvaluationRef currentRequestEvaluationRef)
    {
        SourcingCodes.ContractId(providerId, "provider_id");
        SourcingCodes.ContractId(providerContractVersion, "provider_contract_version");
        ProviderId = providerId;
        ProviderContractVersion = providerContractVersion;
        Subject = subject ?? throw new DomainValidationException("A fact envelope requires its subject.");
        Request = request ?? throw new DomainValidationException("A fact envelope requires its request input.");
        var lines = (coveredLines ?? []).ToImmutableArray();
        if (lines.Length == 0 || lines.Select(line => line.Id).Distinct().Count() != lines.Length)
        {
            throw new DomainValidationException("A fact envelope requires its distinct covered lines.");
        }

        CoveredLines = lines.OrderBy(line => line.Id).ToImmutableArray();
        Facts = new SortedDictionary<string, PolicyValue>(
            (facts ?? new Dictionary<string, PolicyValue>()).ToDictionary(pair => pair.Key, pair => pair.Value),
            StringComparer.Ordinal);
        Provenance = new SortedDictionary<string, string>(
            (provenance ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => pair.Value),
            StringComparer.Ordinal);
        Manifest = manifest ?? throw new DomainValidationException("A fact envelope requires its manifest.");
        CurrentRequestEvaluationRef = currentRequestEvaluationRef
            ?? throw new DomainValidationException("A fact envelope requires the current request evaluation.");
        if (!string.Equals(ProviderId, Manifest.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(ProviderContractVersion, Manifest.ProviderContractVersion, StringComparison.Ordinal))
        {
            throw new DomainConflictException("The envelope and its manifest declare different providers.");
        }

        if (Manifest.SourcingProposalId != Subject.Id || Manifest.SourcingProposalVersion != Subject.Version)
        {
            throw new DomainConflictException("The manifest belongs to another proposal version.");
        }

        if (!Manifest.CoveredLines.Select(line => line.Id).ToHashSet().SetEquals(CoveredLines.Select(line => line.Id)))
        {
            throw new DomainConflictException("The manifest does not cover the envelope line set.");
        }

        if (Manifest.RequestId != Request.Subject.Id || Manifest.RequestVersion != Request.Subject.Version)
        {
            throw new DomainConflictException("The manifest belongs to another request version.");
        }

        // The whole envelope is reproducible from its parts: the manifest digest is recomputed from the
        // canonical document, so a tampered manifest cannot reach Policy (REQ-10, NFR-01).
        ManifestDigest = Manifest.ComputeDigest();
        if (!string.Equals(ManifestDigest, Manifest.ComputeDigest(), StringComparison.Ordinal))
        {
            throw new DomainConflictException("The completeness manifest is not reproducible.");
        }

        FactsDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["contract_version"] = ContractVersion,
                ["facts"] = Facts.Select(pair => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = pair.Key,
                    ["value"] = CanonicalFact(pair.Value)
                }).ToArray(),
                ["manifest_digest"] = ManifestDigest,
                ["proposal_id"] = Subject.Id.ToString("D"),
                ["proposal_version"] = Subject.Version,
                ["request_id"] = Request.Subject.Id.ToString("D"),
                ["request_version"] = Request.Subject.Version
            }));
    }

    public string ProviderId { get; }
    public string ProviderContractVersion { get; }
    public PolicySubjectReference Subject { get; }
    public PolicyRequestInput Request { get; }
    public IReadOnlyList<PolicySubjectReference> CoveredLines { get; }
    public IReadOnlyDictionary<string, PolicyValue> Facts { get; }
    public IReadOnlyDictionary<string, string> Provenance { get; }
    public SourcingCompletenessManifest Manifest { get; }
    public SourcingPolicyEvaluationRef CurrentRequestEvaluationRef { get; }
    public string ManifestDigest { get; }
    public string FactsDigest { get; }

    /// <summary>
    /// Internal manifest Policy consumes: the published provider attestation bound to the digest of the
    /// completeness document. The internal model never replaces the historical
    /// <see cref="PolicySourcingManifest"/> reading (REQ-10).
    /// </summary>
    public PolicySourcingManifest InternalManifest() =>
        new(ProviderId, ProviderContractVersion, ManifestDigest, CoveredLines);

    /// <summary>
    /// Exact <see cref="PolicySourcingInput"/> Policy must build: the typed envelope translated without
    /// adding, dropping or reinterpreting a fact (REQ-10).
    /// </summary>
    public PolicySourcingInput ToPolicyInput(PolicyEvaluationBundle currentRequestEvaluation) =>
        new(
            Subject,
            Request,
            CoveredLines,
            Facts,
            currentRequestEvaluation ?? throw new DomainValidationException(
                "A sourcing policy input requires the current request evaluation."),
            InternalManifest());

    private static object CanonicalFact(PolicyValue value) => PolicyCanonicalizer.CanonicalizeValue(value);
}
