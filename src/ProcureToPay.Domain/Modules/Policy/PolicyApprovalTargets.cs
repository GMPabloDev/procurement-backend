using System.Text.Json;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

/// <summary>Material projection of one approval target line (SPEC 05 REQ-01, DEC-03).</summary>
public sealed record PolicyMaterialTarget(Guid Id, int Version, string MaterialSnapshotDigest);

/// <summary>
/// Persisted projection Policy adds to every new evaluation so that the approval adapter can
/// rebuild the exact <c>policy-approval-target/v1</c> identity of each covered line without a
/// mutable provider lookup and without accepting targets from the caller (SPEC 05 REQ-01).
/// </summary>
public sealed record PolicyMaterialProjection(
    string ContractVersion,
    IReadOnlyDictionary<string, string> Provenance,
    IReadOnlyList<PolicyMaterialTarget> Targets);

/// <summary>
/// Deterministic <c>policy-approval-target/v1</c> preimage of a Purchase Request line. The root
/// keeps <c>canonicalization_version</c> as its first ordered property (policy-canonical-json/v1)
/// while the target body lives under <c>target_snapshot</c>, so both the SPEC 02 precedence rule
/// and the ordinal ordering rule hold at the same time.
/// </summary>
public static class PolicyApprovalTargets
{
    public const string ContractVersion = "policy-approval-target/v1";
    public const string TargetType = "PURCHASE_REQUEST_LINE";

    /// <summary>SHA-256 of the canonical target preimage under <c>policy-canonical-json/v1</c>.</summary>
    public static string ComputeMaterialDigest(
        string canonicalRequestJson,
        string factManifestDigest,
        IReadOnlyDictionary<string, string> provenance,
        PolicySubjectReference target)
    {
        if (string.IsNullOrWhiteSpace(canonicalRequestJson))
        {
            throw new DomainValidationException(
                "The policy input snapshot is required to derive an approval target.");
        }

        if (target is null)
        {
            throw new DomainValidationException("An approval target is required.");
        }

        if (target.Id == Guid.Empty || target.Version < 1)
        {
            throw new DomainValidationException("An approval target requires its immutable identity.");
        }

        if (string.IsNullOrWhiteSpace(factManifestDigest) || factManifestDigest.Length != 64 ||
            !factManifestDigest.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException("An approval target requires the confirmed manifest digest.");
        }

        using var document = JsonDocument.Parse(canonicalRequestJson);
        var root = document.RootElement;
        var line = FindLine(root, target)
            ?? throw new DomainValidationException(
                "The persisted policy snapshot does not contain the covered line.");
        var lineFacts = line.GetProperty("facts");
        var requestFacts = root.GetProperty("facts");
        var provenanceValue = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        AddProvenance(provenanceValue, "line", lineFacts, provenance);
        AddProvenance(provenanceValue, "request", requestFacts, provenance);

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["target_contract_version"] = ContractVersion,
            ["target_snapshot"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["base_currency"] = root.GetProperty("base_currency").GetString(),
                ["fact_manifest_digest"] = factManifestDigest,
                ["facts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["line"] = lineFacts,
                    ["request"] = requestFacts
                },
                ["legal_entity_id"] = root.GetProperty("legal_entity_id").GetString(),
                ["organization_id"] = root.GetProperty("organization_id").GetString(),
                ["provenance"] = provenanceValue,
                ["request_id"] = root.GetProperty("subject").GetProperty("id").GetString(),
                ["request_version"] = root.GetProperty("subject").GetProperty("version").GetInt32(),
                ["target_id"] = target.Id.ToString("D"),
                ["target_type"] = TargetType,
                ["target_version"] = target.Version
            }
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>Builds the persisted projection for every line of a persisted request snapshot.</summary>
    public static PolicyMaterialProjection Build(
        string canonicalRequestJson,
        string factManifestDigest,
        IReadOnlyDictionary<string, string>? provenance)
    {
        var map = provenance is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(provenance, StringComparer.Ordinal);
        using var document = JsonDocument.Parse(canonicalRequestJson);
        var targets = document.RootElement.GetProperty("lines").EnumerateArray()
            .Select(line => new
            {
                Id = line.GetProperty("subject").GetProperty("id").GetGuid(),
                Version = line.GetProperty("subject").GetProperty("version").GetInt32()
            })
            .OrderBy(line => line.Id)
            .ThenBy(line => line.Version)
            .Select(line => new PolicyMaterialTarget(
                line.Id,
                line.Version,
                ComputeMaterialDigest(
                    canonicalRequestJson, factManifestDigest, map,
                    new PolicySubjectReference(line.Id, line.Version))))
            .ToArray();
        return new PolicyMaterialProjection(ContractVersion, map, targets);
    }

    private static JsonElement? FindLine(JsonElement root, PolicySubjectReference target)
    {
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            var subject = line.GetProperty("subject");
            if (subject.GetProperty("id").GetGuid() == target.Id &&
                subject.GetProperty("version").GetInt32() == target.Version)
            {
                return line;
            }
        }

        return null;
    }

    private static void AddProvenance(
        SortedDictionary<string, object?> target,
        string prefix,
        JsonElement facts,
        IReadOnlyDictionary<string, string> provenance)
    {
        foreach (var fact in facts.EnumerateObject())
        {
            if (provenance.TryGetValue(fact.Name, out var source))
            {
                target[$"{prefix}.{fact.Name}"] = source;
            }
        }
    }
}
