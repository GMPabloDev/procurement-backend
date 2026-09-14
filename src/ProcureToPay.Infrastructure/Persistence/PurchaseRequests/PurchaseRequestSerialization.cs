using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Exact JSON shapes of the Purchase Requests contracts. Readers reject unknown properties so a
/// corrupted row can never be reinterpreted as a valid snapshot (NFR-01, REQ-05).
/// </summary>
public static class PurchaseRequestSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false
    };

    public static string EntityRef(VersionedEntityRef? reference) => reference is null
        ? "null"
        : new JsonObject
        {
            ["entity_type"] = reference.EntityType,
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        }.ToJsonString(Options);

    public static string CodeRef(VersionedCodeRef reference) => new JsonObject
    {
        ["catalog"] = reference.Catalog,
        ["code"] = reference.Code,
        ["digest"] = reference.Digest,
        ["version"] = reference.Version
    }.ToJsonString(Options);

    public static string RiskAnswers(IEnumerable<TypedAnswerRef> answers) =>
        new JsonArray((answers ?? []).Select(answer => (JsonNode)new JsonObject
        {
            ["question_code"] = answer.QuestionCode,
            ["schema_version"] = answer.SchemaVersion,
            ["value"] = answer.Value,
            ["value_kind"] = answer.ValueKind == TypedAnswerValueKind.Boolean ? "BOOLEAN" : "ENUM_CODE"
        }).ToArray()).ToJsonString(Options);

    public static string FxRef(PurchaseRequestFxRef? reference) => reference is null
        ? "null"
        : new JsonObject
        {
            ["attestation_id"] = reference.AttestationId.ToString("D"),
            ["attestation_version"] = reference.AttestationVersion,
            ["base_currency"] = reference.BaseCurrency,
            ["transaction_currency"] = reference.TransactionCurrency,
            ["effective_rate"] = reference.EffectiveRate.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["rate_date"] = reference.RateDate.ToString("yyyy-MM-dd")
        }.ToJsonString(Options);

    public static string LineRefs(IEnumerable<PurchaseRequestLineRef> references) =>
        new JsonArray((references ?? []).Select(reference => (JsonNode)LineRefValue(reference)).ToArray())
            .ToJsonString(Options);

    public static string Delta(PurchaseRequestRevisionDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var value = new JsonObject
        {
            ["added"] = new JsonArray(delta.Added.Select(entry => (JsonNode)new JsonObject
            {
                ["client_line_key"] = entry.ClientLineKey,
                ["replacement"] = LineRefValue(entry.Replacement)
            }).ToArray()),
            ["changed"] = new JsonArray(delta.Changed.Select(entry => (JsonNode)new JsonObject
            {
                ["previous"] = LineRefValue(entry.Previous),
                ["replacement"] = LineRefValue(entry.Replacement)
            }).ToArray()),
            ["contract_version"] = "purchase-request-revision-delta/v1",
            ["from_request_version"] = delta.FromRequestVersion,
            ["removed"] = new JsonArray(delta.Removed.Select(reference => (JsonNode)LineRefValue(reference)).ToArray()),
            ["retained"] = new JsonArray(delta.Retained.Select(reference => (JsonNode)LineRefValue(reference)).ToArray()),
            ["to_request_version"] = delta.ToRequestVersion
        };
        return value.ToJsonString(Options);
    }

    public static string Assertions(IEnumerable<PurchaseRequestReferenceAssertion> assertions) =>
        new JsonArray((assertions ?? []).Select(assertion => (JsonNode)new JsonObject
        {
            ["assertion_type"] = PurchaseRequestCodes.Code(assertion.AssertionType),
            ["owner_contract_version"] = assertion.OwnerContractVersion,
            ["owner_id"] = assertion.OwnerId,
            ["source_ref"] = AttestedRefValue(assertion.SourceRef),
            ["status"] = assertion.Status,
            ["target_ref"] = assertion.TargetRef is null ? null : AttestedRefValue(assertion.TargetRef)
        }).ToArray()).ToJsonString(Options);

    public static PurchaseRequestLineContent ReadLineContent(
        decimal estimatedGrossAmount,
        string transactionCurrency,
        decimal baseAmount,
        string baseCurrency,
        int fiscalYear,
        string purchaseType,
        string spendCategoryJson,
        string costCenterJson,
        string costCenterDepartmentJson,
        string beneficiaryDepartmentJson,
        string requestedForUserJson,
        string? supplierJson,
        string? preferredProductJson,
        string? requiredProductJson,
        bool contractRequired,
        bool nonStandardTerms,
        string agreementStatus,
        string needSummary,
        string riskAnswersJson,
        string? fxAttestationJson) =>
        new(
            estimatedGrossAmount,
            transactionCurrency,
            baseAmount,
            baseCurrency,
            fiscalYear,
            purchaseType,
            ReadCodeRef(spendCategoryJson),
            ReadEntityRef(costCenterJson, "cost center"),
            ReadEntityRef(costCenterDepartmentJson, "cost center department"),
            ReadEntityRef(beneficiaryDepartmentJson, "beneficiary department"),
            ReadEntityRef(requestedForUserJson, "requested-for user"),
            ReadOptionalEntityRef(supplierJson, "supplier"),
            ReadOptionalEntityRef(preferredProductJson, "preferred product"),
            ReadOptionalEntityRef(requiredProductJson, "required product"),
            contractRequired,
            nonStandardTerms,
            agreementStatus,
            needSummary,
            ReadRiskAnswers(riskAnswersJson),
            ReadFxRef(fxAttestationJson));

    public static PurchaseRequestLineRef ReadLineRef(string json)
    {
        var root = Parse(json);
        RequireProperties(root, "content_digest", "id", "version");
        return new PurchaseRequestLineRef(
            Guid.Parse(root["id"]!.GetValue<string>()),
            root["version"]!.GetValue<int>(),
            root["content_digest"]!.GetValue<string>());
    }

    public static IReadOnlyList<PurchaseRequestLineRef> ReadLineRefs(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray
            ?? throw new DomainValidationException("The stored line references are not a JSON array.");
        var references = array.Select(node => ReadLineRef(
            node?.ToJsonString() ?? throw new DomainValidationException("A stored line reference is empty.")))
            .ToArray();
        if (references.Select(reference => reference.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() !=
            references.Length)
        {
            throw new DomainConflictException("The stored line references repeat a line version.");
        }

        return references;
    }

    public static IReadOnlyList<PurchaseRequestReferenceAssertion> ReadAssertions(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray
            ?? throw new DomainValidationException("The stored attestation assertions are not a JSON array.");
        var assertions = array.Select(node =>
        {
            var root = node as JsonObject
                ?? throw new DomainValidationException("A stored attestation assertion is empty.");
            RequireProperties(root, "assertion_type", "owner_contract_version", "owner_id", "source_ref", "status", "target_ref");
            var assertionType = root["assertion_type"]!.GetValue<string>() switch
            {
                "ACTIVE_IN_ORGANIZATION" => PurchaseRequestAssertionType.ActiveInOrganization,
                "COST_CENTER_OWNED_BY_DEPARTMENT" => PurchaseRequestAssertionType.CostCenterOwnedByDepartment,
                "FX_ATTESTATION_VALID" => PurchaseRequestAssertionType.FxAttestationValid,
                _ => throw new DomainValidationException("The stored assertion type is invalid.")
            };
            return new PurchaseRequestReferenceAssertion(
                assertionType,
                root["owner_contract_version"]!.GetValue<string>(),
                root["owner_id"]!.GetValue<string>(),
                ReadAttestedRef(root["source_ref"]!.AsObject()),
                root["status"]!.GetValue<string>(),
                root["target_ref"] is null or JsonObject { Count: 0 }
                    ? null
                    : ReadAttestedRef(root["target_ref"]!.AsObject()));
        }).ToArray();
        return assertions;
    }

    public static string ManifestLines(IEnumerable<PurchaseRequestLineRef> lines) => LineRefs(lines);

    private static JsonObject LineRefValue(PurchaseRequestLineRef reference) => new()
    {
        ["content_digest"] = reference.ContentDigest,
        ["id"] = reference.Id.ToString("D"),
        ["version"] = reference.Version
    };

    private static JsonObject AttestedRefValue(PurchaseRequestAttestedRef reference) => new()
    {
        ["code"] = reference.Code,
        ["digest"] = reference.Digest,
        ["id"] = reference.Id?.ToString("D"),
        ["kind"] = reference.KindCode,
        ["type"] = PurchaseRequestCodes.Code(reference.Type),
        ["version"] = reference.Version
    };

    private static PurchaseRequestAttestedRef ReadAttestedRef(JsonObject root)
    {
        RequireProperties(root, "code", "digest", "id", "kind", "type", "version");
        var kind = root["kind"]!.GetValue<string>() switch
        {
            "ENTITY" => PurchaseRequestReferenceKind.Entity,
            "CODE" => PurchaseRequestReferenceKind.Code,
            "QUESTION_SCHEMA" => PurchaseRequestReferenceKind.QuestionSchema,
            _ => throw new DomainValidationException("The stored attested reference kind is invalid.")
        };
        var type = root["type"]!.GetValue<string>() switch
        {
            "LEGAL_ENTITY" => PurchaseRequestReferenceType.LegalEntity,
            "USER" => PurchaseRequestReferenceType.User,
            "COST_CENTER" => PurchaseRequestReferenceType.CostCenter,
            "DEPARTMENT" => PurchaseRequestReferenceType.Department,
            "SPEND_CATEGORY" => PurchaseRequestReferenceType.SpendCategory,
            "PRODUCT" => PurchaseRequestReferenceType.Product,
            "SUPPLIER" => PurchaseRequestReferenceType.Supplier,
            "RISK_SCHEMA" => PurchaseRequestReferenceType.RiskSchema,
            "FX" => PurchaseRequestReferenceType.Fx,
            _ => throw new DomainValidationException("The stored attested reference type is invalid.")
        };
        return PurchaseRequestAttestedRef.Restore(
            kind,
            type,
            root["id"] is null ? null : Guid.Parse(root["id"]!.GetValue<string>()),
            root["version"] is null ? null : root["version"]!.GetValue<int>(),
            root["code"]?.GetValue<string>(),
            root["digest"]?.GetValue<string>());
    }

    private static VersionedEntityRef ReadEntityRef(string json, string field)
    {
        var root = Parse(json);
        RequireProperties(root, "entity_type", "id", "version");
        return new VersionedEntityRef(
            root["entity_type"]!.GetValue<string>(),
            Guid.Parse(root["id"]!.GetValue<string>()),
            root["version"]!.GetValue<int>());
    }

    private static VersionedEntityRef? ReadOptionalEntityRef(string? json, string field) =>
        string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal)
            ? null
            : ReadEntityRef(json, field);

    private static VersionedCodeRef ReadCodeRef(string json)
    {
        var root = Parse(json);
        RequireProperties(root, "catalog", "code", "digest", "version");
        return new VersionedCodeRef(
            root["catalog"]!.GetValue<string>(),
            root["code"]!.GetValue<string>(),
            root["version"]!.GetValue<int>(),
            root["digest"]!.GetValue<string>());
    }

    private static PurchaseRequestFxRef? ReadFxRef(string? json) =>
        string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal)
            ? null
            : PurchaseRequestFxRef.Create(
                Guid.Parse(Parse(json)["attestation_id"]!.GetValue<string>()),
                Parse(json)["attestation_version"]!.GetValue<int>(),
                Parse(json)["base_currency"]!.GetValue<string>(),
                Parse(json)["transaction_currency"]!.GetValue<string>(),
                decimal.Parse(
                    Parse(json)["effective_rate"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture),
                DateOnly.Parse(Parse(json)["rate_date"]!.GetValue<string>()));

    private static IReadOnlyList<TypedAnswerRef> ReadRiskAnswers(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray
            ?? throw new DomainValidationException("The stored risk answers are not a JSON array.");
        var answers = array.Select(node =>
        {
            var root = node as JsonObject
                ?? throw new DomainValidationException("A stored risk answer is empty.");
            RequireProperties(root, "question_code", "schema_version", "value", "value_kind");
            var kind = root["value_kind"]!.GetValue<string>() switch
            {
                "BOOLEAN" => TypedAnswerValueKind.Boolean,
                "ENUM_CODE" => TypedAnswerValueKind.EnumCode,
                _ => throw new DomainValidationException("The stored risk answer kind is invalid.")
            };
            return new TypedAnswerRef(
                root["question_code"]!.GetValue<string>(),
                root["schema_version"]!.GetValue<int>(),
                kind,
                root["value"]!.GetValue<string>());
        }).ToImmutableArray();
        return answers;
    }

    private static JsonObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException("A stored contract value is empty.");
        }

        return JsonNode.Parse(json) as JsonObject
            ?? throw new DomainValidationException("A stored contract value is not a JSON object.");
    }

    private static void RequireProperties(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!root.ContainsKey(property))
            {
                throw new DomainValidationException("A stored contract value is missing a required property.");
            }
        }
    }
}
