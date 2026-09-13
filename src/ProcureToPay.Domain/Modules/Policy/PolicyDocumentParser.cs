using System.Globalization;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

public static class PolicyDocumentParser
{
    public static PolicyRequestInput ParseSimulationRequest(JsonElement root)
    {
        var subject = ParseSubject(root.GetProperty("subject"));
        var lines = root.GetProperty("lines").EnumerateArray().Select(line =>
            new PolicyLineInput(
                ParseSubject(line.GetProperty("subject")),
                ParseFacts(line.GetProperty("facts")))).ToArray();
        return new PolicyRequestInput(
            subject,
            root.GetProperty("organization_id").GetGuid(),
            root.GetProperty("legal_entity_id").GetGuid(),
            root.GetProperty("base_currency").GetString()!,
            lines,
            root.TryGetProperty("facts", out var facts) ? ParseFacts(facts) : null);
    }

    public static PolicySetVersion Parse(
        string canonicalJson,
        Guid id,
        Guid organizationId,
        long sequence,
        PolicySetStatus status,
        string contentDigest)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        if (root.GetProperty("canonicalization_version").GetString() != PolicyCanonicalizer.Version)
        {
            throw new DomainValidationException("Unsupported policy canonicalization version.");
        }

        var policy = new PolicySetVersion(id, organizationId, sequence,
            root.GetProperty("scopes").EnumerateArray().Select(scope => ParseScope(scope.GetString())));
        foreach (var rule in root.GetProperty("rules").EnumerateArray())
        {
            var predicates = rule.GetProperty("predicates").EnumerateArray().Select(predicate =>
                new PolicyPredicate(
                    predicate.GetProperty("fact_key").GetString()!,
                    ParseOperator(predicate.GetProperty("operator").GetString()!),
                    ParseValue(predicate.GetProperty("value"))));
            var effects = rule.GetProperty("effects").EnumerateArray().Select(effect => ParseEffect(effect, organizationId));
            policy.AddRule(new PolicyRule(
                rule.GetProperty("code").GetString()!,
                ParseScope(rule.GetProperty("scope").GetString()),
                predicates,
                effects,
                rule.GetProperty("fallback").GetBoolean(),
                rule.GetProperty("revision").GetInt32()));
        }

        if (status == PolicySetStatus.Published)
        {
            policy.Publish(contentDigest);
        }
        else if (status == PolicySetStatus.Retired)
        {
            policy.Publish(contentDigest);
            policy.Retire();
        }
        return policy;
    }

    private static PolicyEffect ParseEffect(JsonElement effect, Guid organizationId)
    {
        PolicyApprovalDescriptor? approval = null;
        if (effect.TryGetProperty("approval", out var approvalElement) &&
            approvalElement.ValueKind != JsonValueKind.Null)
        {
            approval = ParseApproval(approvalElement, organizationId);
        }
        var documents = effect.TryGetProperty("documents", out var documentsElement) &&
                        documentsElement.ValueKind == JsonValueKind.Array
            ? documentsElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : null;
        var minimumQuotations = NullableInt(effect, "minimum_quotations");
        var exceptionFloor = NullableInt(effect, "exception_floor");
        return new PolicyEffect(
            ParseEnum<PolicyEffectType>(effect.GetProperty("type").GetString()!),
            effect.GetProperty("requirement_key").GetString()!,
            NullableString(effect, "reason"),
            approval,
            minimumQuotations,
            exceptionFloor,
            documents);
    }

    private static PolicyApprovalDescriptor ParseApproval(JsonElement approval, Guid organizationId)
    {
        PolicyAuthorityLevelSnapshot? level = null;
        if (approval.TryGetProperty("authority_level", out var levelElement) &&
            levelElement.ValueKind != JsonValueKind.Null)
        {
            level = new PolicyAuthorityLevelSnapshot(
                levelElement.GetProperty("id").GetGuid(),
                levelElement.GetProperty("version").GetInt32(),
                levelElement.GetProperty("code").GetString()!,
                levelElement.GetProperty("rank").GetInt32());
        }
        var authorityType = NullableEnum<ApprovalAuthorityType>(approval, "authority_type");
        var amount = NullableDecimal(approval, "amount_base");
        var currency = NullableString(approval, "base_currency");
        var exceptionable = approval.TryGetProperty("exceptionable", out var exceptionElement) &&
                            exceptionElement.GetBoolean();
        return new PolicyApprovalDescriptor(
            ParseEnum<SystemRole>(approval.GetProperty("role").GetString()!),
            authorityType,
            level,
            amount,
            currency,
            approval.GetProperty("decision_scope").GetString()!,
            exceptionable,
            null,
            organizationId);
    }

    private static PolicySubjectReference ParseSubject(JsonElement value) =>
        new(value.GetProperty("id").GetGuid(), value.GetProperty("version").GetInt32());

    private static IReadOnlyDictionary<string, PolicyValue> ParseFacts(JsonElement facts) =>
        facts.EnumerateObject().ToDictionary(property => property.Name, property => ParseValue(property.Value), StringComparer.Ordinal);

    public static PolicyValue ParseValue(JsonElement value)
    {
        var kind = ParseEnum<PolicyValueKind>(value.GetProperty("kind").GetString()!);
        return kind switch
        {
            PolicyValueKind.Money => PolicyValue.Money(
                decimal.Parse(value.GetProperty("value").GetString()!, CultureInfo.InvariantCulture),
                value.GetProperty("currency").GetString()!),
            PolicyValueKind.Boolean => PolicyValue.Boolean(bool.Parse(value.GetProperty("value").GetString()!)),
            PolicyValueKind.Code => PolicyValue.Code(value.GetProperty("value").GetString()!),
            PolicyValueKind.VersionedEntityRef => ParseEntityRef(value),
            PolicyValueKind.VersionedCodeRef => PolicyValue.VersionedCode(
                value.GetProperty("catalog").GetString()!,
                value.GetProperty("value").GetString()!,
                value.GetProperty("version").GetInt32(),
                value.GetProperty("digest").GetString()!),
            PolicyValueKind.TypedAnswer => PolicyValue.TypedAnswer(
                value.GetProperty("question_code").GetString()!,
                value.GetProperty("schema_version").GetInt32(),
                ParseEnum<TypedAnswerValueKind>(value.GetProperty("value_kind").GetString()!),
                value.GetProperty("value").GetString()!),
            PolicyValueKind.Set => PolicyValue.Set(value.GetProperty("members").EnumerateArray().Select(ParseValue).ToArray()),
            PolicyValueKind.MoneyRange => PolicyValue.MoneyRange(
                decimal.Parse(value.GetProperty("lower_bound").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(value.GetProperty("upper_bound").GetString()!, CultureInfo.InvariantCulture),
                value.GetProperty("currency").GetString()!,
                value.GetProperty("lower_inclusive").GetBoolean(),
                value.GetProperty("upper_inclusive").GetBoolean()),
            _ => throw new DomainValidationException("Unknown policy value kind.")
        };
    }

    private static PolicyValue ParseEntityRef(JsonElement value)
    {
        var entityType = value.GetProperty("reference_type").GetString()!;
        if (value.TryGetProperty("entity_id", out var entityId) && entityId.ValueKind != JsonValueKind.Null)
        {
            return PolicyValue.EntityRef(entityType, entityId.GetGuid(), value.GetProperty("version").GetInt32());
        }
        var parts = value.GetProperty("value").GetString()!.Split(':', 2);
        return PolicyValue.EntityRef(
            entityType, Guid.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static PolicyScope ParseScope(string? value) => value switch
    {
        "LINE" => PolicyScope.Line,
        "REQUEST" => PolicyScope.Request,
        "SOURCING_PO" => PolicyScope.SourcingPo,
        _ => throw new DomainValidationException("Unknown policy scope.")
    };

    private static PolicyOperator ParseOperator(string value) => value switch
    {
        "EQ" => PolicyOperator.Equal,
        "NEQ" => PolicyOperator.NotEqual,
        "IN" => PolicyOperator.In,
        "NOT_IN" => PolicyOperator.NotIn,
        "IS_TRUE" => PolicyOperator.IsTrue,
        "IS_FALSE" => PolicyOperator.IsFalse,
        "GT" => PolicyOperator.GreaterThan,
        "GTE" => PolicyOperator.GreaterThanOrEqual,
        "LT" => PolicyOperator.LessThan,
        "LTE" => PolicyOperator.LessThanOrEqual,
        "BETWEEN" => PolicyOperator.Between,
        _ => throw new DomainValidationException($"Unknown policy operator '{value}'.")
    };

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, true, out var direct))
        {
            return direct;
        }
        var pascal = string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        return Enum.TryParse<TEnum>(pascal, true, out var result)
            ? result
            : throw new DomainValidationException($"Unknown policy enum value '{value}'.");
    }

    private static TEnum? NullableEnum<TEnum>(JsonElement element, string property) where TEnum : struct, Enum =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : ParseEnum<TEnum>(value.GetString()!);

    private static string? NullableString(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetString();

    private static int? NullableInt(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetInt32();

    private static decimal? NullableDecimal(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);
}
