using System.Collections.Immutable;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

public static class PolicyEvaluationBundleRehydrator
{
    public static PolicyEvaluationBundle FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var scopes = root.GetProperty("scopeEvaluations").EnumerateArray()
            .Select(ParseScope)
            .ToArray();
        var controls = root.GetProperty("controls").EnumerateArray()
            .Select(ParseControl)
            .ToArray();
        var bundle = new PolicyEvaluationBundle(
            root.GetProperty("id").GetGuid(),
            root.GetProperty("evaluationKey").GetString()!,
            ParseSubject(root.GetProperty("subject")),
            root.GetProperty("evaluatedAt").GetDateTimeOffset(),
            root.GetProperty("policyContentDigest").GetString()!,
            root.GetProperty("inputDigest").GetString()!,
            scopes,
            controls,
            ParseEnum<PolicyResult>(root.GetProperty("result")),
            root.GetProperty("resultDigest").GetString()!)
        {
            EvaluationSequence = root.TryGetProperty("evaluationSequence", out var sequence)
                ? sequence.GetInt64() : 0,
            Operation = root.TryGetProperty("operation", out var operation)
                ? operation.GetString() ?? string.Empty : string.Empty,
            FactsDigest = NullableString(root, "factsDigest"),
            ManifestDigest = NullableString(root, "manifestDigest"),
            InputCanonicalJson = NullableString(root, "inputCanonicalJson"),
            RequestSnapshotJson = NullableString(root, "requestSnapshotJson"),
            ActivationId = NullableGuid(root, "activationId"),
            PreviousBundleId = NullableGuid(root, "previousBundleId")
        };
        return bundle;
    }

    private static PolicyScopeEvaluation ParseScope(JsonElement value)
    {
        var references = value.TryGetProperty("subjectReferences", out var refs)
            ? refs.EnumerateArray().Select(ParseSubject).ToArray()
            : Array.Empty<PolicySubjectReference>();
        return new PolicyScopeEvaluation(
            ParseEnum<PolicyScope>(value.GetProperty("scope")),
            value.GetProperty("subjectIds").EnumerateArray().Select(item => item.GetGuid()).ToHashSet(),
            value.GetProperty("matchedRuleCodes").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            value.GetProperty("controls").EnumerateArray().Select(ParseControl).ToArray(),
            ParseEnum<PolicyResult>(value.GetProperty("result")))
        {
            HasAllowEffect = value.TryGetProperty("hasAllowEffect", out var allow) && allow.GetBoolean(),
            SubjectReferences = references
        };
    }

    private static PolicyGeneratedControl ParseControl(JsonElement value)
    {
        var documents = value.GetProperty("supportingDocumentTypes").EnumerateArray()
            .Select(item => item.GetString()!).ToImmutableHashSet(StringComparer.Ordinal);
        var rules = value.GetProperty("originRuleCodes").EnumerateArray()
            .Select(item => item.GetString()!).ToImmutableHashSet(StringComparer.Ordinal);
        var scopes = value.GetProperty("originScopes").EnumerateArray()
            .Select(item => ParseEnum<PolicyScope>(item)).ToImmutableHashSet();
        var subjects = value.GetProperty("subjectIds").EnumerateArray()
            .Select(item => item.GetGuid()).ToImmutableHashSet();
        return new PolicyGeneratedControl(
            value.GetProperty("requirementKey").GetString()!,
            ParseEnum<PolicyEffectType>(value.GetProperty("type")),
            scopes,
            subjects,
            value.GetProperty("phase").GetString()!,
            value.TryGetProperty("approval", out var approval) && approval.ValueKind != JsonValueKind.Null
                ? ParseApproval(approval) : null,
            NullableInt(value, "minimumQuotations"),
            documents,
            rules,
            value.GetProperty("reason").GetString() ?? string.Empty)
        {
            MinimumAllowedQuotations = NullableInt(value, "minimumAllowedQuotations")
        };
    }

    private static PolicyApprovalDescriptor ParseApproval(JsonElement value)
    {
        PolicyAuthorityLevelSnapshot? level = null;
        if (value.TryGetProperty("authorityLevel", out var levelValue) && levelValue.ValueKind != JsonValueKind.Null)
        {
            level = new PolicyAuthorityLevelSnapshot(
                levelValue.GetProperty("id").GetGuid(),
                levelValue.GetProperty("version").GetInt32(),
                levelValue.GetProperty("code").GetString()!,
                levelValue.GetProperty("rank").GetInt32());
        }
        return new PolicyApprovalDescriptor(
            ParseEnum<SystemRole>(value.GetProperty("role")),
            NullableEnum<ApprovalAuthorityType>(value, "authorityType"),
            level,
            NullableDecimal(value, "amountBase"),
            NullableString(value, "baseCurrency"),
            value.GetProperty("decisionScope").GetString()!,
            value.TryGetProperty("exceptionable", out var exceptionable) && exceptionable.GetBoolean(),
            NullableDecimal(value, "minimumExceptionAmount"));
    }

    private static PolicySubjectReference ParseSubject(JsonElement value) =>
        new(value.GetProperty("id").GetGuid(), value.GetProperty("version").GetInt32());

    private static TEnum ParseEnum<TEnum>(JsonElement value) where TEnum : struct, Enum =>
        value.ValueKind == JsonValueKind.Number
            ? (TEnum)Enum.ToObject(typeof(TEnum), value.GetInt32())
            : Enum.Parse<TEnum>(value.GetString()!, true);

    private static string? NullableString(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : item.GetString();

    private static Guid? NullableGuid(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : item.GetGuid();

    private static int? NullableInt(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : item.GetInt32();

    private static decimal? NullableDecimal(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : item.GetDecimal();

    private static TEnum? NullableEnum<TEnum>(JsonElement value, string property) where TEnum : struct, Enum =>
        !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null
            ? null : ParseEnum<TEnum>(item);
}
