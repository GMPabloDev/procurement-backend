using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

/// <summary>Provider attestation bound to a sourcing subject and its covered request lines.</summary>
public sealed record PolicySourcingManifest
{
    public PolicySourcingManifest(
        string providerId,
        string contractVersion,
        string attestationDigest,
        IReadOnlyList<PolicySubjectReference> coveredLines)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(contractVersion) ||
            coveredLines is null || coveredLines.Count == 0)
        {
            throw new DomainValidationException("Sourcing manifests require provider, contract and covered lines.");
        }
        if (string.IsNullOrWhiteSpace(attestationDigest) ||
            !Regex.IsMatch(attestationDigest, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
        {
            throw new DomainValidationException("Sourcing manifests require a SHA-256 attestation digest.");
        }
        if (coveredLines.Select(line => line.Id).Distinct().Count() != coveredLines.Count)
        {
            throw new DomainConflictException("Sourcing manifests cannot cover the same line twice.");
        }
        ProviderId = providerId.Trim();
        ContractVersion = contractVersion.Trim();
        AttestationDigest = attestationDigest.ToLowerInvariant();
        CoveredLines = coveredLines.OrderBy(line => line.Id).ThenBy(line => line.Version).ToArray();
    }

    public string ProviderId { get; }
    public string ContractVersion { get; }
    public string AttestationDigest { get; }
    public IReadOnlyList<PolicySubjectReference> CoveredLines { get; }
}
