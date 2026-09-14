using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

public sealed record PolicySubjectReference
{
    public PolicySubjectReference(Guid id, int version)
    {
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("Policy subject id and version are required.");
        }

        Id = id;
        Version = version;
    }

    public Guid Id { get; }
    public int Version { get; }
}

public sealed record PolicyLineInput(
    PolicySubjectReference Subject,
    IReadOnlyDictionary<string, PolicyValue> Facts);

public sealed record PolicyRequestInput
{
    public PolicyRequestInput(
        PolicySubjectReference subject,
        Guid organizationId,
        Guid legalEntityId,
        string baseCurrency,
        IReadOnlyList<PolicyLineInput> lines,
        IReadOnlyDictionary<string, PolicyValue>? facts = null)
    {
        if (organizationId == Guid.Empty || legalEntityId == Guid.Empty || lines is null || lines.Count == 0)
        {
            throw new DomainValidationException("Policy request identity and lines are required.");
        }

        if (lines.Select(line => line.Subject.Id).Distinct().Count() != lines.Count)
        {
            throw new DomainConflictException("Policy request lines must be unique.");
        }

        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        OrganizationId = organizationId;
        LegalEntityId = legalEntityId;
        BaseCurrency = NormalizeCurrency(baseCurrency);
        Lines = lines;
        Facts = facts;
    }

    public PolicySubjectReference Subject { get; }
    public Guid OrganizationId { get; }
    public Guid LegalEntityId { get; }
    public string BaseCurrency { get; }
    public IReadOnlyList<PolicyLineInput> Lines { get; }
    public IReadOnlyDictionary<string, PolicyValue>? Facts { get; }

    private static string NormalizeCurrency(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length != 3
            ? throw new DomainValidationException("Policy base currency must be ISO 4217.")
            : value.Trim().ToUpperInvariant();
}

public sealed record PolicySourcingInput(
    PolicySubjectReference Subject,
    PolicyRequestInput Request,
    IReadOnlyList<PolicySubjectReference> CoveredLines,
    IReadOnlyDictionary<string, PolicyValue> Facts,
    PolicyEvaluationBundle? CurrentRequestEvaluation,
    PolicySourcingManifest Manifest);

public sealed record PolicyGeneratedControl(
    string RequirementKey,
    PolicyEffectType Type,
    ImmutableHashSet<PolicyScope> OriginScopes,
    ImmutableHashSet<Guid> SubjectIds,
    string Phase,
    PolicyApprovalDescriptor? Approval,
    int? MinimumQuotations,
    ImmutableHashSet<string> SupportingDocumentTypes,
    ImmutableHashSet<string> OriginRuleCodes,
    string Reason)
{
    public int? MinimumAllowedQuotations { get; init; }

    /// <summary>Cost Centers covered by a budget control (REQ-07/REQ-08).</summary>
    public ImmutableHashSet<Guid> CostCenterIds { get; init; } = ImmutableHashSet<Guid>.Empty;

    /// <summary>Base amount and currency carried by a budget control (REQ-07/REQ-08).</summary>
    public decimal? AmountBase { get; init; }
    public string? BaseCurrency { get; init; }

    /// <summary>Fact keys that justified the control and the provider provenance of those facts (CA-05).</summary>
    public ImmutableHashSet<string> OriginFacts { get; init; } = ImmutableHashSet<string>.Empty;
    public ImmutableDictionary<string, string> FactProvenance { get; init; } = ImmutableDictionary<string, string>.Empty;

    public bool Covers(Guid subjectId) => SubjectIds.Contains(subjectId);
}

public sealed record PolicyScopeEvaluation(
    PolicyScope Scope,
    IReadOnlySet<Guid> SubjectIds,
    IReadOnlyList<string> MatchedRuleCodes,
    IReadOnlyList<PolicyGeneratedControl> Controls,
    PolicyResult Result)
{
    public bool HasAllowEffect { get; init; }
    public IReadOnlyList<PolicySubjectReference> SubjectReferences { get; init; } = [];
}

public sealed record PolicyEvaluationDiffEntry(
    string Change,
    string RequirementKey,
    PolicyEffectType Type,
    IReadOnlySet<Guid> SubjectIds,
    int? PreviousMinimumQuotations,
    int? CurrentMinimumQuotations);

public sealed record PolicyEvaluationBundle(
    Guid Id,
    string EvaluationKey,
    PolicySubjectReference Subject,
    DateTimeOffset EvaluatedAt,
    string PolicyContentDigest,
    string InputDigest,
    IReadOnlyList<PolicyScopeEvaluation> ScopeEvaluations,
    IReadOnlyList<PolicyGeneratedControl> Controls,
    PolicyResult Result,
    string ResultDigest)
{
    public IReadOnlyList<PolicyEvaluationDiffEntry> Diff { get; init; } = [];
    public long EvaluationSequence { get; init; }
    public string Operation { get; init; } = string.Empty;
    public string? FactsDigest { get; init; }
    public string? ManifestDigest { get; init; }
    public string? ManifestCanonicalJson { get; init; }
    public string? InputCanonicalJson { get; init; }
    public string? RequestSnapshotJson { get; init; }

    /// <summary>Material projection of every covered line for the approval adapter (SPEC 05 REQ-01).</summary>
    public PolicyMaterialProjection? MaterialProjection { get; init; }
    public Guid? ActivationId { get; init; }
    public Guid? PreviousBundleId { get; init; }

    /// <summary>Reevaluation cause persisted for reproducible idempotency checks (REQ-13).</summary>
    public string? Cause { get; init; }
}

public static class PolicyCanonicalizer
{
    public const string Version = "policy-canonical-json/v1";

    public static string CanonicalizePolicy(PolicySetVersion policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["policy_schema_version"] = "policy-schema/v2",
            ["organization_id"] = policy.OrganizationId.ToString("D"),
            ["scopes"] = policy.Scopes
                .Select(CanonicalName)
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .ToArray(),
            ["rules"] = SortCanonical(policy.Rules.Select(CanonicalizeRule))
        };

        return JsonSerializer.Serialize(root, CanonicalJsonOptions);
    }

    public static string ComputePolicyDigest(PolicySetVersion policy) => Hash(CanonicalizePolicy(policy));

    public static string CanonicalizeExceptionVerification(PolicyExceptionVerificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authority_evidence"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["approver_id"] = CanonicalGuid(input.ApproverId),
                ["evidence_digest"] = input.AuthorityEvidenceDigest
            },
            ["bindings"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["base_bundle_id"] = CanonicalGuid(input.BaseBundleId),
                ["base_result_digest"] = input.BaseResultDigest,
                ["binding"] = input.Binding,
                ["manifest_digest"] = input.ManifestDigest,
                ["policy_content_digest"] = input.PolicyContentDigest,
                ["policy_version_id"] = CanonicalGuid(input.PolicyVersionId),
                ["requester_id"] = CanonicalGuid(input.RequesterId),
                ["subject_ref"] = Subject(input.Subject),
                ["target_requirement_key"] = input.TargetRequirementKey,
                ["from"] = input.From,
                ["to"] = input.To
            },
            ["canonicalization_version"] = Version,
            ["nonce"] = input.Nonce,
            ["scope"] = CanonicalString(input.Scope),
            ["segregation_satisfied"] = input.SegregationSatisfied,
            ["validity"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["valid_from"] = FormatUtc(input.ValidFrom),
                ["valid_to"] = FormatUtc(input.ValidTo)
            },
            ["verifier_contract_version"] = input.VerifierContractVersion,
            ["verifier_id"] = input.VerifierId,
            ["workflow_decision"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["digest"] = input.WorkflowDecisionDigest,
                ["id"] = input.WorkflowDecisionId,
                ["version"] = input.WorkflowDecisionVersion
            }
        };
        return SerializeCanonical(root);
    }

    public static string ComputeExceptionVerificationDigest(PolicyExceptionVerificationInput input) =>
        Hash(CanonicalizeExceptionVerification(input));

    public static PolicyEvaluationBundle WithEvaluationMetadata(
        PolicyEvaluationBundle bundle,
        string operation,
        string factsDigest,
        string manifestDigest,
        string inputCanonicalJson) => bundle with
        {
            Operation = operation,
            FactsDigest = factsDigest,
            ManifestDigest = manifestDigest,
            InputCanonicalJson = inputCanonicalJson
        };

    public static string CanonicalizeEvaluationInput(
        DateTimeOffset evaluatedAt,
        string operation,
        PolicySubjectReference subject,
        string policyDigest,
        Guid? activationId,
        string? factsDigest,
        Guid? previousBundleId,
        string? previousResultDigest,
        IReadOnlyList<string>? exceptionVerificationDigests = null)
    {
        var input = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["activation_id"] = activationId?.ToString("D"),
            ["canonicalization_version"] = Version,
            ["evaluated_at_utc"] = FormatUtc(evaluatedAt),
            ["exception_verification_digests"] = (exceptionVerificationDigests ?? []).Order(StringComparer.Ordinal).ToArray(),
            ["fact_manifest_digest"] = factsDigest,
            ["operation"] = operation,
            ["policy_content_digest"] = policyDigest,
            ["previous_bundle_id"] = previousBundleId?.ToString("D"),
            ["previous_result_digest"] = previousResultDigest,
            ["subject_ref"] = Subject(subject)
        };
        return JsonSerializer.Serialize(input, CanonicalJsonOptions);
    }

    public static string CanonicalizeEvaluationInput(
        DateTimeOffset evaluatedAt,
        string operation,
        PolicySubjectReference subject,
        string policyDigest,
        Guid? activationId,
        string? factsDigest,
        string? manifestDigest,
        Guid? previousBundleId,
        string requestCanonical) =>
        CanonicalizeEvaluationInput(evaluatedAt, operation, subject, policyDigest, activationId,
            factsDigest, previousBundleId, null);

    public static string CanonicalizeEvaluationResult(
        string inputDigest,
        IReadOnlyList<PolicyScopeEvaluation> scopes,
        IReadOnlyList<PolicyGeneratedControl> controls,
        object? diff) =>
        CanonicalizeEvaluationResult(inputDigest, scopes, controls,
            scopes.Any(scope => scope.Result == PolicyResult.Blocked) || controls.Any(control => control.Type == PolicyEffectType.Block)
                ? PolicyResult.Blocked
                : controls.Count > 0 ? PolicyResult.RequirementsGenerated : PolicyResult.Passed,
            diff);

    public static string CanonicalizeEvaluationResult(
        string inputDigest,
        IReadOnlyList<PolicyScopeEvaluation> scopes,
        IReadOnlyList<PolicyGeneratedControl> controls,
        PolicyResult combinedResult,
        object? diff)
    {
        var canonicalControls = SortCanonical(controls.Select(CanonicalizeControl));
        var canonicalScopes = SortCanonical(scopes.Select(scope => new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["controls"] = SortCanonical(scope.Controls.Select(CanonicalizeControl)),
            ["matched_rules"] = scope.MatchedRuleCodes.Order(StringComparer.Ordinal).ToArray(),
            ["result"] = CanonicalName(scope.Result),
            ["scope"] = CanonicalName(scope.Scope),
            ["subjects"] = scope.SubjectIds
                .Select(CanonicalGuid)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
        }));
        return JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["combined_controls"] = canonicalControls,
            ["combined_result"] = CanonicalName(combinedResult),
            ["diff"] = diff is IReadOnlyList<PolicyEvaluationDiffEntry> entries
                ? CanonicalizeDiff(entries)
                : diff,
            ["evaluation_input_digest"] = inputDigest,
            ["scope_evaluations"] = canonicalScopes
        }, CanonicalJsonOptions);
    }

    private static object CanonicalizeControl(PolicyGeneratedControl control) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount_base"] = control.AmountBase?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["approval"] = control.Approval is null ? null : CanonicalizeApproval(control.Approval),
            ["base_currency"] = control.BaseCurrency,
            ["cost_center_ids"] = control.CostCenterIds
                .Select(CanonicalGuid)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            ["fact_provenance"] = new SortedDictionary<string, object?>(
                control.FactProvenance.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            ["minimum_quotations"] = control.MinimumQuotations,
            ["minimum_allowed_quotations"] = control.MinimumAllowedQuotations,
            ["origin_facts"] = control.OriginFacts.Order(StringComparer.Ordinal).ToArray(),
            ["origin_rules"] = control.OriginRuleCodes.Order(StringComparer.Ordinal).ToArray(),
            ["origin_scopes"] = control.OriginScopes
                .Select(CanonicalName)
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .ToArray(),
            ["supporting_document_types"] = control.SupportingDocumentTypes.Order(StringComparer.Ordinal).ToArray(),
            ["reason"] = CanonicalString(control.Reason),
            ["phase"] = CanonicalString(control.Phase),
            ["requirement_key"] = CanonicalString(control.RequirementKey),
            ["subjects"] = control.SubjectIds
                .Select(CanonicalGuid)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            ["type"] = CanonicalName(control.Type)
        };

    public static object CanonicalizeFactPayload(PolicyRequestInput request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_currency"] = request.BaseCurrency,
            ["facts"] = CanonicalizeFacts(request.Facts),
            ["legal_entity_id"] = request.LegalEntityId.ToString("D"),
            ["lines"] = request.Lines
                .OrderBy(line => line.Subject.Id)
                .ThenBy(line => line.Subject.Version)
                .Select(line => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["facts"] = CanonicalizeFacts(line.Facts),
                    ["subject_ref"] = Subject(line.Subject)
                }).ToArray(),
            ["organization_id"] = request.OrganizationId.ToString("D"),
            ["subject_ref"] = Subject(request.Subject)
        };
    }

    public static object CanonicalizeDiff(IReadOnlyList<PolicyEvaluationDiffEntry> diff) =>
        SortCanonical(diff.Select(entry => new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["change"] = entry.Change,
            ["current_minimum_quotations"] = entry.CurrentMinimumQuotations,
            ["previous_minimum_quotations"] = entry.PreviousMinimumQuotations,
            ["requirement_key"] = entry.RequirementKey,
            ["subject_ids"] = entry.SubjectIds
                .Select(CanonicalGuid)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            ["type"] = CanonicalName(entry.Type)
        }));

    public static string CanonicalizeSourcingManifest(PolicySourcingInput sourcing) =>
        SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["covered_lines"] = SortCanonical(sourcing.CoveredLines.Select(Subject)),
            ["request_subject_ref"] = Subject(sourcing.Request.Subject),
            ["sourcing_subject_ref"] = Subject(sourcing.Subject),
            ["provider_attestation"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["attestation_digest"] = sourcing.Manifest.AttestationDigest,
                ["contract_version"] = sourcing.Manifest.ContractVersion,
                ["covered_lines"] = SortCanonical(sourcing.Manifest.CoveredLines.Select(Subject)),
                ["provider_id"] = sourcing.Manifest.ProviderId
            }
        });

    public static string ComputeSourcingManifestDigest(PolicySourcingInput sourcing) =>
        Hash(CanonicalizeSourcingManifest(sourcing));

    public static string CanonicalizeSourcingFacts(PolicySourcingInput sourcing, string manifestDigest) =>
        SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["facts"] = CanonicalizeFacts(sourcing.Facts),
            ["manifest_digest"] = manifestDigest,
            ["request_subject_ref"] = Subject(sourcing.Request.Subject),
            ["sourcing_subject_ref"] = Subject(sourcing.Subject)
        });

    public static string ComputeSourcingFactsDigest(PolicySourcingInput sourcing, string manifestDigest) =>
        Hash(CanonicalizeSourcingFacts(sourcing, manifestDigest));

    public static string CanonicalizeRequest(PolicyRequestInput request, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["evaluated_at_utc"] = FormatUtc(evaluatedAt),
            ["subject"] = Subject(request.Subject),
            ["organization_id"] = request.OrganizationId.ToString("D"),
            ["legal_entity_id"] = request.LegalEntityId.ToString("D"),
            ["base_currency"] = request.BaseCurrency,
            ["facts"] = CanonicalizeFacts(request.Facts),
            ["lines"] = request.Lines
                .OrderBy(line => line.Subject.Id)
                .ThenBy(line => line.Subject.Version)
                .Select(line => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["subject"] = Subject(line.Subject),
                    ["facts"] = CanonicalizeFacts(line.Facts)
                }).ToArray()
        };

        return JsonSerializer.Serialize(root, CanonicalJsonOptions);
    }

    public static string SerializeCanonical(object value) =>
        JsonSerializer.Serialize(value, CanonicalJsonOptions);

    public static string Hash(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

    private static T[] SortCanonical<T>(IEnumerable<T> values) => values
        .OrderBy(value => JsonSerializer.Serialize(value, CanonicalJsonOptions), CanonicalByteComparer)
        .ToArray();

    public static int CompareCanonical(string left, string right) =>
        CompareBytes(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static readonly IComparer<string> CanonicalByteComparer = Comparer<string>.Create(CompareCanonical);

    private static string CanonicalString(string value) => value.Normalize(NormalizationForm.FormC);

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string CanonicalGuid(Guid value) => value.ToString("D");

    private static SortedDictionary<string, object?> CanonicalizeRule(PolicyRule rule) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = rule.Code,
            ["effects"] = SortCanonical(rule.Effects.Select(CanonicalizeEffect)),
            ["fallback"] = rule.IsFallback,
            ["predicates"] = SortCanonical(rule.Predicates.Select(predicate => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fact_key"] = predicate.FactKey,
                ["operator"] = CanonicalOperator(predicate.Operator),
                ["value"] = CanonicalizeValue(predicate.Value)
            })),
            ["revision"] = rule.Revision,
            ["scope"] = CanonicalName(rule.Scope)
        };

    private static SortedDictionary<string, object?> CanonicalizeEffect(PolicyEffect effect) =>
        new(StringComparer.Ordinal)
        {
            ["approval"] = effect.Approval is null ? null : CanonicalizeApproval(effect.Approval),
            ["documents"] = effect.SupportingDocumentTypes?.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            ["exception_floor"] = effect.MinimumExceptionQuotations,
            ["minimum_quotations"] = effect.MinimumQuotations,
            ["reason"] = effect.Reason is null ? null : CanonicalString(effect.Reason),
            ["requirement_key"] = CanonicalString(effect.RequirementKey),
            ["type"] = CanonicalName(effect.Type)
        };

    private static string CanonicalName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Regex.Replace(value.ToString(), "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant();

    private static string CanonicalOperator(PolicyOperator value) => value switch
    {
        PolicyOperator.Equal => "EQ",
        PolicyOperator.NotEqual => "NEQ",
        PolicyOperator.In => "IN",
        PolicyOperator.NotIn => "NOT_IN",
        PolicyOperator.IsTrue => "IS_TRUE",
        PolicyOperator.IsFalse => "IS_FALSE",
        PolicyOperator.GreaterThan => "GT",
        PolicyOperator.GreaterThanOrEqual => "GTE",
        PolicyOperator.LessThan => "LT",
        PolicyOperator.LessThanOrEqual => "LTE",
        PolicyOperator.Between => "BETWEEN",
        _ => throw new DomainValidationException("Unknown policy operator.")
    };

    private static SortedDictionary<string, object?> CanonicalizeApproval(PolicyApprovalDescriptor approval) =>
        new(StringComparer.Ordinal)
        {
            ["amount_base"] = approval.AmountBase?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["authority_level"] = approval.AuthorityLevel is null ? null : new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = approval.AuthorityLevel.Code,
                ["id"] = approval.AuthorityLevel.Id.ToString("D"),
                ["rank"] = approval.AuthorityLevel.Rank,
                ["version"] = approval.AuthorityLevel.Version
            },
            ["authority_type"] = approval.AuthorityType is null ? null : CanonicalName(approval.AuthorityType.Value),
            ["base_currency"] = approval.BaseCurrency,
            ["decision_scope"] = approval.DecisionScope,
            ["exceptionable"] = approval.Exceptionable,
            ["role"] = approval.Role.ToString().ToUpperInvariant()
        };

    private static object Subject(PolicySubjectReference subject) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = subject.Id.ToString("D"),
            ["version"] = subject.Version
        };

    private static SortedDictionary<string, object?> CanonicalizeFacts(
        IReadOnlyDictionary<string, PolicyValue>? facts) =>
        new(
            (facts ?? new Dictionary<string, PolicyValue>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => (object?)CanonicalizeValue(pair.Value), StringComparer.Ordinal),
            StringComparer.Ordinal);

    internal static object CanonicalizeValue(PolicyValue value) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["catalog"] = value.Catalog,
            ["currency"] = value.Currency,
            ["digest"] = value.Digest,
            ["entity_id"] = value.EntityId?.ToString("D"),
            ["kind"] = CanonicalName(value.Kind),
            ["lower_bound"] = value.LowerBound?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["lower_inclusive"] = value.LowerInclusive,
            ["members"] = value.Members.IsDefaultOrEmpty ? null : value.Members.Select(CanonicalizeValue).ToArray(),
            ["question_code"] = value.QuestionCode,
            ["reference_type"] = value.ReferenceType,
            ["schema_version"] = value.SchemaVersion,
            ["upper_bound"] = value.UpperBound?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["upper_inclusive"] = value.UpperInclusive,
            ["value"] = value.Value,
            ["value_kind"] = value.AnswerKind is null ? null : CanonicalName(value.AnswerKind.Value),
            ["version"] = value.Version
        };

    private sealed class NfcStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(CanonicalString(value));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WritePropertyName(CanonicalString(value));

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() ?? string.Empty;
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new NfcStringConverter() }
    };
}

public static class PolicyEvaluator
{
    public static PolicyEvaluationBundle EvaluateRequest(
        PolicySetVersion policy,
        PolicyRequestInput request,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        Guid? evaluationId = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublished(policy, evaluationKey);
        if (request.OrganizationId != policy.OrganizationId)
        {
            throw new DomainConflictException("Policy and request organizations differ.");
        }

        EnsureBaseCurrency(request.Facts, request.BaseCurrency);
        foreach (var line in request.Lines)
        {
            EnsureBaseCurrency(line.Facts, request.BaseCurrency);
        }

        var lineEvaluations = policy.Scopes.Contains(PolicyScope.Line)
            ? request.Lines
                .Select(line => EvaluateScope(
                    policy,
                    PolicyScope.Line,
                    line.Subject,
                    line.Facts,
                    ImmutableHashSet.Create(line.Subject.Id)))
                .ToArray()
            : Array.Empty<PolicyScopeEvaluation>();
        var requestFacts = new Dictionary<string, PolicyValue>(request.Facts ?? new Dictionary<string, PolicyValue>(), StringComparer.Ordinal)
        {
            ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(
                request.Lines.Sum(line => GetMoney(line.Facts, "GROSS_AMOUNT_BASE")),
                request.BaseCurrency)
        };
        var requestEvaluations = policy.Scopes.Contains(PolicyScope.Request)
            ? new[] { EvaluateScope(
                policy,
                PolicyScope.Request,
                request.Subject,
                requestFacts,
                request.Lines.Select(line => line.Subject.Id).ToImmutableHashSet()) }
            : Array.Empty<PolicyScopeEvaluation>();

        var scopeEvaluations = lineEvaluations.Concat(requestEvaluations).ToArray();
        var controls = Combine(scopeEvaluations);
        var result = ResolveResult(scopeEvaluations, controls);
        var inputCanonical = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            evaluatedAt,
            "PURCHASE_REQUEST",
            request.Subject,
            policy.ContentDigest!,
            null,
            null,
            null,
            null);
        var inputDigest = PolicyCanonicalizer.Hash(inputCanonical);
        var policyDigest = policy.ContentDigest!;
        var resultCanonical = PolicyCanonicalizer.CanonicalizeEvaluationResult(
            inputDigest, scopeEvaluations, controls, result, null);

        return new PolicyEvaluationBundle(
            evaluationId ?? Guid.NewGuid(),
            evaluationKey,
            request.Subject,
            evaluatedAt.ToUniversalTime(),
            policyDigest,
            inputDigest,
            scopeEvaluations,
            controls,
            result,
            PolicyCanonicalizer.Hash(resultCanonical))
        {
            Operation = "PURCHASE_REQUEST",
            InputCanonicalJson = inputCanonical
        };
    }

    public static PolicyEvaluationBundle EvaluateSourcing(
        PolicySetVersion policy,
        PolicySourcingInput sourcing,
        string evaluationKey,
        DateTimeOffset evaluatedAt,
        Guid? evaluationId = null)
    {
        ArgumentNullException.ThrowIfNull(sourcing);
        ValidatePublished(policy, evaluationKey);
        var coveredLineReferences = sourcing.CoveredLines.ToImmutableHashSet();
        var currentLineReferences = sourcing.CurrentRequestEvaluation?.ScopeEvaluations
            .Where(scope => scope.Scope == PolicyScope.Line)
            .SelectMany(scope => scope.SubjectReferences)
            .ToImmutableHashSet() ?? [];
        if (sourcing.CurrentRequestEvaluation is null ||
            sourcing.CurrentRequestEvaluation.Subject != sourcing.Request.Subject ||
            !coveredLineReferences.SetEquals(currentLineReferences))
        {
            throw new DomainConflictException("Sourcing evaluation requires the current request evaluation and its exact lines.");
        }

        var subjectIds = coveredLineReferences.Select(line => line.Id).ToImmutableHashSet();
        var scope = EvaluateScope(policy, PolicyScope.SourcingPo, sourcing.Subject, sourcing.Facts, subjectIds);
        var manifestDigest = PolicyCanonicalizer.ComputeSourcingManifestDigest(sourcing);
        var factsDigest = PolicyCanonicalizer.ComputeSourcingFactsDigest(sourcing, manifestDigest);
        var inputCanonical = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            evaluatedAt,
            "SOURCING_PO",
            sourcing.Subject,
            policy.ContentDigest!,
            null,
            factsDigest,
            sourcing.CurrentRequestEvaluation.Id,
            sourcing.CurrentRequestEvaluation.ResultDigest);
        var inputDigest = PolicyCanonicalizer.Hash(inputCanonical);
        var controls = scope.Controls;
        var result = scope.Result;
        var resultDigest = PolicyCanonicalizer.Hash(
            PolicyCanonicalizer.CanonicalizeEvaluationResult(inputDigest, [scope], controls, result, null));
        return new PolicyEvaluationBundle(
            evaluationId ?? Guid.NewGuid(),
            evaluationKey,
            sourcing.Subject,
            evaluatedAt.ToUniversalTime(),
            policy.ContentDigest!,
            inputDigest,
            [scope],
            controls,
            result,
            resultDigest)
        {
            PreviousBundleId = sourcing.CurrentRequestEvaluation.Id,
            Operation = "SOURCING_PO",
            FactsDigest = factsDigest,
            ManifestDigest = manifestDigest,
            ManifestCanonicalJson = PolicyCanonicalizer.CanonicalizeSourcingManifest(sourcing),
            InputCanonicalJson = inputCanonical
        };
    }

    private static PolicyScopeEvaluation EvaluateScope(
        PolicySetVersion policy,
        PolicyScope scope,
        PolicySubjectReference subject,
        IReadOnlyDictionary<string, PolicyValue> facts,
        IReadOnlySet<Guid> subjectIds)
    {
        var rules = policy.Rules.Where(rule => rule.Scope == scope).ToArray();
        if (rules.Length == 0)
        {
            throw new DomainConflictException($"Policy scope '{scope}' has no configured rules.");
        }

        var matching = rules.Where(rule => !rule.IsFallback && rule.Predicates.All(predicate => Matches(predicate, facts))).ToArray();
        if (matching.Length == 0)
        {
            matching = rules.Where(rule => rule.IsFallback).ToArray();
        }

        var hasAllowEffect = matching.SelectMany(rule => rule.Effects)
            .Any(effect => effect.Type is PolicyEffectType.Allow or PolicyEffectType.AllowDirectPurchase);
        var controls = matching
            .SelectMany(rule => rule.Effects
                .Where(effect => effect.Type is not (PolicyEffectType.Allow or PolicyEffectType.AllowDirectPurchase))
                .Select(effect => ToControl(effect, scope, subjectIds, rule, facts)))
            .ToArray();
        return new PolicyScopeEvaluation(
            scope,
            subjectIds,
            matching.Select(rule => $"{rule.Code}:{rule.Revision}").ToImmutableArray(),
            controls,
            ResolveResult(controls, hasAllowEffect))
        {
            HasAllowEffect = hasAllowEffect,
            SubjectReferences = [subject]
        };
    }

    private static IReadOnlyList<PolicyGeneratedControl> Combine(IReadOnlyList<PolicyScopeEvaluation> scopes)
    {
        var lineIds = scopes.SelectMany(scope => scope.SubjectIds).ToImmutableHashSet();
        var expanded = scopes.SelectMany(scope => scope.Controls.SelectMany(control =>
            scope.Scope == PolicyScope.Request
                ? lineIds.Select(lineId => control with
                {
                    SubjectIds = ImmutableHashSet.Create(lineId),
                    OriginScopes = control.OriginScopes.Add(scope.Scope)
                })
                : [control])).ToArray();

        var perLine = expanded
            .GroupBy(control => new
            {
                control.RequirementKey,
                control.Type,
                Line = control.SubjectIds.Count == 1 ? control.SubjectIds.Single() : Guid.Empty,
                ApprovalRole = control.Approval?.Role,
                ApprovalAuthority = control.Approval?.AuthorityType,
                ApprovalScope = control.Approval?.DecisionScope,
                control.Phase
            })
            .Select(MergeControls)
            .ToArray();

        var combined = perLine
            .GroupBy(control => new
            {
                control.RequirementKey,
                control.Type,
                control.Phase,
                Approval = control.Approval is null ? string.Empty : JsonSerializer.Serialize(control.Approval),
                control.MinimumQuotations,
                Documents = string.Join("|", control.SupportingDocumentTypes.Order(StringComparer.Ordinal))
            })
            .Select(MergeControls)
            .Where(control => control.Type != PolicyEffectType.AllowDirectPurchase ||
                !perLine.Any(other => other.RequirementKey == control.RequirementKey && other.Type == PolicyEffectType.RequirePo))
            .OrderBy(control => control.RequirementKey, StringComparer.Ordinal)
            .ThenBy(control => control.Type)
            .ToArray();
        return combined;
    }

    private static PolicyGeneratedControl MergeControls(IEnumerable<PolicyGeneratedControl> controls)
    {
        var group = controls.ToArray();
        var first = group[0];
        var approval = first.Type == PolicyEffectType.RequireApproval
            ? CombineApproval(group.Where(control => control.Approval is not null).Select(control => control.Approval!))
            : first.Approval;
        return first with
        {
            OriginScopes = group.SelectMany(control => control.OriginScopes).ToImmutableHashSet(),
            SubjectIds = group.SelectMany(control => control.SubjectIds).ToImmutableHashSet(),
            Approval = approval,
            MinimumQuotations = first.Type == PolicyEffectType.RequireQuotations
                ? group.Max(control => control.MinimumQuotations)
                : first.MinimumQuotations,
            MinimumAllowedQuotations = first.Type == PolicyEffectType.RequireQuotations
                ? group.Any(control => control.MinimumAllowedQuotations is null)
                    ? null
                    : group.Max(control => control.MinimumAllowedQuotations)
                : first.MinimumAllowedQuotations,
            SupportingDocumentTypes = group.SelectMany(control => control.SupportingDocumentTypes)
                .ToImmutableHashSet(StringComparer.Ordinal),
            OriginRuleCodes = group.SelectMany(control => control.OriginRuleCodes)
                .ToImmutableHashSet(StringComparer.Ordinal),
            Reason = string.Join("; ", group.Select(control => control.Reason)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal))
        };
    }

    private static PolicyApprovalDescriptor CombineApproval(IEnumerable<PolicyApprovalDescriptor> approvals)
    {
        var values = approvals.ToArray();
        var first = values[0];
        if (values.Any(value => value.Role != first.Role || value.AuthorityType != first.AuthorityType || value.DecisionScope != first.DecisionScope))
        {
            throw new DomainConflictException("Incompatible approval controls cannot be combined.");
        }

        var strongest = values.OrderByDescending(value => value.AuthorityLevel?.Rank ?? 0).First();
        return new PolicyApprovalDescriptor(
            strongest.Role,
            strongest.AuthorityType,
            strongest.AuthorityLevel,
            values.Max(value => value.AmountBase),
            strongest.BaseCurrency,
            strongest.DecisionScope,
            values.Any(value => value.Exceptionable),
            values.Max(value => value.MinimumExceptionAmount));
    }

    private static PolicyGeneratedControl ToControl(
        PolicyEffect effect,
        PolicyScope scope,
        IReadOnlySet<Guid> subjectIds,
        PolicyRule rule,
        IReadOnlyDictionary<string, PolicyValue> facts)
    {
        var costCenterIds = ImmutableHashSet<Guid>.Empty;
        decimal? amountBase = null;
        string? baseCurrency = null;
        if (effect.Type == PolicyEffectType.RequireBudgetCheck)
        {
            if (facts.TryGetValue("COST_CENTER", out var costCenter))
            {
                var centers = costCenter.Kind == PolicyValueKind.Set ? costCenter.Members : [costCenter];
                costCenterIds = centers
                    .Where(value => value.EntityId is not null)
                    .Select(value => value.EntityId!.Value)
                    .ToImmutableHashSet();
            }
            if (facts.TryGetValue("GROSS_AMOUNT_BASE", out var gross) && gross.Kind == PolicyValueKind.Money)
            {
                amountBase = gross.AsMoney();
                baseCurrency = gross.AsCurrency();
            }
        }

        return new PolicyGeneratedControl(
            effect.RequirementKey,
            effect.Type,
            ImmutableHashSet.Create(scope),
            subjectIds.ToImmutableHashSet(),
            PhaseFor(effect),
            effect.Approval,
            effect.MinimumQuotations,
            effect.SupportingDocumentTypes?.ToImmutableHashSet(StringComparer.Ordinal) ?? ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, $"{rule.Code}:{rule.Revision}"),
            effect.Reason ?? rule.Code)
        {
            MinimumAllowedQuotations = effect.MinimumExceptionQuotations,
            CostCenterIds = costCenterIds,
            AmountBase = amountBase,
            BaseCurrency = baseCurrency,
            OriginFacts = rule.Predicates.Select(predicate => predicate.FactKey)
                .ToImmutableHashSet(StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// Functional phase of a control (SPEC 02 phase contract, SPEC 05 REQ-04): Department runs
    /// first, Finance/IT/Legal stay parallel in PRE_PROCUREMENT, Procurement runs after the
    /// previous controls and PRE_PO precedes the PO-enabling result. `stage_code` only labels the
    /// DAG; it never replaces it.
    /// </summary>
    private static string PhaseFor(PolicyEffect effect) => effect.Type switch
    {
        PolicyEffectType.RequireApproval => PhaseForApprovalRole(effect.Approval!.Role),
        PolicyEffectType.RequireProcurement => "PROCUREMENT",
        PolicyEffectType.RequirePo => "PRE_PO",
        _ => "PRE_PROCUREMENT"
    };

    private static string PhaseForApprovalRole(SystemRole role) => role switch
    {
        SystemRole.DepartmentApprover => "DEPARTMENT",
        SystemRole.ProcurementApprover or SystemRole.ProcurementBuyer => "PROCUREMENT",
        SystemRole.FinanceApprover or SystemRole.ItReviewer or SystemRole.LegalReviewer => "PRE_PROCUREMENT",
        _ => "PRE_PROCUREMENT"
    };

    private static bool Matches(PolicyPredicate predicate, IReadOnlyDictionary<string, PolicyValue> facts)
    {
        if (!facts.TryGetValue(predicate.FactKey, out var actual))
        {
            return false;
        }

        if (string.Equals(predicate.FactKey, "RISK_ANSWER", StringComparison.Ordinal))
        {
            return MatchesRiskAnswer(predicate, actual);
        }

        if (predicate.Operator is PolicyOperator.In or PolicyOperator.NotIn)
        {
            if (predicate.Value.Kind != PolicyValueKind.Set || predicate.Value.Members.IsDefaultOrEmpty ||
                predicate.Value.Members.Any(member => member.Kind != actual.Kind))
            {
                return false;
            }

            var contains = predicate.Value.Members.Any(member => member.SameAs(actual));
            return predicate.Operator == PolicyOperator.In ? contains : !contains;
        }

        if (predicate.Operator == PolicyOperator.Between)
        {
            if (actual.Kind != PolicyValueKind.Money || predicate.Value.Kind != PolicyValueKind.MoneyRange ||
                !string.Equals(actual.Currency, predicate.Value.Currency, StringComparison.Ordinal))
            {
                return false;
            }

            var amount = actual.AsMoney();
            var lower = predicate.Value.LowerInclusive
                ? amount >= predicate.Value.LowerBound
                : amount > predicate.Value.LowerBound;
            var upper = predicate.Value.UpperInclusive
                ? amount <= predicate.Value.UpperBound
                : amount < predicate.Value.UpperBound;
            return lower && upper;
        }

        if (actual.Kind != predicate.Value.Kind)
        {
            return false;
        }

        if (actual.Kind == PolicyValueKind.Money &&
            !string.Equals(actual.Currency, predicate.Value.Currency, StringComparison.Ordinal))
        {
            return false;
        }

        return predicate.Operator switch
        {
            PolicyOperator.Equal => actual.SameAs(predicate.Value),
            PolicyOperator.NotEqual => !actual.SameAs(predicate.Value),
            PolicyOperator.IsTrue => actual.AsBoolean(),
            PolicyOperator.IsFalse => !actual.AsBoolean(),
            PolicyOperator.GreaterThan => actual.AsMoney() > predicate.Value.AsMoney(),
            PolicyOperator.GreaterThanOrEqual => actual.AsMoney() >= predicate.Value.AsMoney(),
            PolicyOperator.LessThan => actual.AsMoney() < predicate.Value.AsMoney(),
            PolicyOperator.LessThanOrEqual => actual.AsMoney() <= predicate.Value.AsMoney(),
            _ => false
        };
    }

    private static decimal GetMoney(IReadOnlyDictionary<string, PolicyValue> facts, string key) =>
        facts.TryGetValue(key, out var value) && value.Kind == PolicyValueKind.Money
            ? value.AsMoney()
            : throw new DomainValidationException($"Fact '{key}' is required as MoneyBase.");

    /// <summary>Money facts must be expressed in the base currency of the evaluated bundle (REQ-04).</summary>
    private static void EnsureBaseCurrency(
        IReadOnlyDictionary<string, PolicyValue>? facts,
        string baseCurrency)
    {
        if (facts is null)
        {
            return;
        }

        foreach (var value in facts.Values)
        {
            if (value.Kind is PolicyValueKind.Money or PolicyValueKind.MoneyRange &&
                !string.Equals(value.Currency, baseCurrency, StringComparison.Ordinal))
            {
                throw new DomainValidationException(
                    "Policy money facts must use the bundle base currency.");
            }
        }
    }

    /// <summary>
    /// SPEC 06 matcher for <c>RISK_ANSWER</c>: the fact is a non-empty set of typed answers unique
    /// per question and schema. The predicate identity selects exactly one answer; a missing
    /// identity never matches (not even a negative operator), while duplicates or members of
    /// another kind invalidate the bundle before any evaluation is persisted.
    /// </summary>
    private static bool MatchesRiskAnswer(PolicyPredicate predicate, PolicyValue actual)
    {
        if (actual.Kind != PolicyValueKind.Set || actual.Members.IsDefaultOrEmpty ||
            actual.Members.Any(member => member.Kind != PolicyValueKind.TypedAnswer))
        {
            throw new DomainValidationException(
                "Fact 'RISK_ANSWER' requires a non-empty set of typed answers.");
        }

        var identities = actual.Members
            .Select(member => (member.QuestionCode, member.SchemaVersion))
            .ToArray();
        if (identities.Distinct().Count() != identities.Length)
        {
            throw new DomainValidationException(
                "Fact 'RISK_ANSWER' cannot repeat a question and schema version.");
        }

        var isSet = predicate.Value.Kind == PolicyValueKind.Set;
        var question = isSet ? predicate.Value.Members[0].QuestionCode : predicate.Value.QuestionCode;
        var schema = isSet ? predicate.Value.Members[0].SchemaVersion : predicate.Value.SchemaVersion;
        var matching = actual.Members
            .Where(member => string.Equals(member.QuestionCode, question, StringComparison.Ordinal) &&
                             member.SchemaVersion == schema)
            .ToArray();
        if (matching.Length == 0)
        {
            return false;
        }

        var answer = matching[0];
        return predicate.Operator switch
        {
            PolicyOperator.Equal => answer.SameAs(predicate.Value),
            PolicyOperator.NotEqual => !answer.SameAs(predicate.Value),
            PolicyOperator.In => predicate.Value.Members.Any(member => answer.SameAs(member)),
            PolicyOperator.NotIn => !predicate.Value.Members.Any(member => answer.SameAs(member)),
            _ => throw new DomainValidationException("Fact 'RISK_ANSWER' only accepts EQ/NEQ/IN/NOT_IN.")
        };
    }

    private static PolicyResult ResolveResult(IEnumerable<PolicyGeneratedControl> controls, bool hasAllowEffect)
    {
        var materialized = controls.ToArray();
        return materialized.Any(control => control.Type == PolicyEffectType.Block)
            ? PolicyResult.Blocked
            : materialized.Length > 0
                ? PolicyResult.RequirementsGenerated
                : hasAllowEffect
                    ? PolicyResult.Passed
                    : PolicyResult.Blocked;
    }

    private static PolicyResult ResolveResult(
        IReadOnlyList<PolicyScopeEvaluation> scopes,
        IReadOnlyList<PolicyGeneratedControl> controls) =>
        scopes.Any(scope => scope.Result == PolicyResult.Blocked) ||
        controls.Any(control => control.Type == PolicyEffectType.Block)
            ? PolicyResult.Blocked
            : controls.Count > 0
                ? PolicyResult.RequirementsGenerated
                : PolicyResult.Passed;

    private static void ValidatePublished(PolicySetVersion policy, string evaluationKey)
    {
        if (policy.Status != PolicySetStatus.Published || string.IsNullOrWhiteSpace(policy.ContentDigest))
        {
            throw new DomainConflictException("Only a published policy can be evaluated.");
        }

        if (string.IsNullOrWhiteSpace(evaluationKey) || evaluationKey.Length > 128 ||
            !evaluationKey.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-'))
        {
            throw new DomainValidationException("Evaluation key is invalid.");
        }

        if (!string.Equals(policy.ContentDigest, PolicyCanonicalizer.ComputePolicyDigest(policy), StringComparison.Ordinal))
        {
            throw new DomainConflictException("Policy content digest does not match its immutable content.");
        }
    }
}
