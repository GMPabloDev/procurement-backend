using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    PolicyEvaluationBundle? CurrentRequestEvaluation = null);

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
    public long EvaluationSequence { get; init; }
    public string Operation { get; init; } = string.Empty;
    public string? FactsDigest { get; init; }
    public string? ManifestDigest { get; init; }
    public string? InputCanonicalJson { get; init; }
    public Guid? ActivationId { get; init; }
    public Guid? PreviousBundleId { get; init; }
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
            ["policy_schema_version"] = "policy-schema/v1",
            ["organization_id"] = policy.OrganizationId.ToString("D"),
            ["scopes"] = policy.Scopes.OrderBy(scope => scope).Select(CanonicalName).ToArray(),
            ["rules"] = policy.Rules
                .OrderBy(rule => rule.Scope)
                .ThenBy(rule => rule.Code, StringComparer.Ordinal)
                .ThenBy(rule => rule.Revision)
                .Select(CanonicalizeRule)
                .ToArray()
        };

        return JsonSerializer.Serialize(root, CanonicalJsonOptions);
    }

    public static string ComputePolicyDigest(PolicySetVersion policy) => Hash(CanonicalizePolicy(policy));

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
            ["evaluated_at_utc"] = evaluatedAt.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
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
        var canonicalControls = controls
            .OrderBy(control => control.RequirementKey, StringComparer.Ordinal)
            .ThenBy(control => control.Type)
            .ThenBy(control => control.SubjectIds.Order().FirstOrDefault())
            .Select(CanonicalizeControl)
            .ToArray();
        var canonicalScopes = scopes
            .OrderBy(scope => scope.Scope)
            .ThenBy(scope => scope.SubjectIds.Order().FirstOrDefault())
            .Select(scope => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["controls"] = scope.Controls.OrderBy(control => control.RequirementKey, StringComparer.Ordinal)
                    .ThenBy(control => control.Type).Select(CanonicalizeControl).ToArray(),
                ["matched_rules"] = scope.MatchedRuleCodes.Order(StringComparer.Ordinal).ToArray(),
                ["result"] = scope.Result.ToString(),
                ["scope"] = scope.Scope.ToString(),
                ["subjects"] = scope.SubjectIds.Order().Select(id => id.ToString("D")).ToArray()
            }).ToArray();
        return JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["combined_controls"] = canonicalControls,
            ["combined_result"] = combinedResult.ToString(),
            ["diff"] = diff,
            ["evaluation_input_digest"] = inputDigest,
            ["scope_evaluations"] = canonicalScopes
        }, CanonicalJsonOptions);
    }

    private static object CanonicalizeControl(PolicyGeneratedControl control) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["approval"] = control.Approval,
            ["minimum_quotations"] = control.MinimumQuotations,
            ["origin_rules"] = control.OriginRuleCodes.Order(StringComparer.Ordinal).ToArray(),
            ["origin_scopes"] = control.OriginScopes.Order().Select(scope => scope.ToString()).ToArray(),
            ["phase"] = control.Phase,
            ["requirement_key"] = control.RequirementKey,
            ["subjects"] = control.SubjectIds.Order().Select(id => id.ToString("D")).ToArray(),
            ["type"] = control.Type.ToString()
        };

    public static string CanonicalizeRequest(PolicyRequestInput request, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Version,
            ["evaluated_at_utc"] = evaluatedAt.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
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

    public static string Hash(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

    private static SortedDictionary<string, object?> CanonicalizeRule(PolicyRule rule) =>
        new(StringComparer.Ordinal)
        {
            ["code"] = rule.Code,
            ["effects"] = rule.Effects
                .OrderBy(effect => effect.RequirementKey, StringComparer.Ordinal)
                .ThenBy(effect => effect.Type)
                .Select(CanonicalizeEffect)
                .ToArray(),
            ["fallback"] = rule.IsFallback,
            ["predicates"] = rule.Predicates
                .OrderBy(predicate => predicate.FactKey, StringComparer.Ordinal)
                .ThenBy(predicate => predicate.Operator)
                .ThenBy(predicate => predicate.Value.Value, StringComparer.Ordinal)
                .Select(predicate => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fact_key"] = predicate.FactKey,
                    ["operator"] = CanonicalOperator(predicate.Operator),
                    ["value"] = CanonicalizeValue(predicate.Value)
                }).ToArray(),
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
            ["reason"] = effect.Reason,
            ["requirement_key"] = effect.RequirementKey,
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
            ["authority_type"] = approval.AuthorityType?.ToString().ToUpperInvariant(),
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

    private static object CanonicalizeValue(PolicyValue value) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = value.Kind.ToString().ToUpperInvariant(),
            ["lower_bound"] = value.LowerBound?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["lower_inclusive"] = value.LowerInclusive,
            ["members"] = value.Members.IsDefaultOrEmpty ? null : value.Members.Select(CanonicalizeValue).ToArray(),
            ["reference_type"] = value.ReferenceType,
            ["upper_bound"] = value.UpperBound?.ToString("0.##############################", CultureInfo.InvariantCulture),
            ["upper_inclusive"] = value.UpperInclusive,
            ["value"] = value.Value
        };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
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
                request.Lines.Sum(line => GetMoney(line.Facts, "GROSS_AMOUNT_BASE")))
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
        var inputCanonical = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            evaluatedAt,
            "SOURCING_PO",
            sourcing.Subject,
            policy.ContentDigest!,
            null,
            sourcing.CurrentRequestEvaluation.FactsDigest,
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
                .Select(effect => ToControl(effect, scope, subjectIds, rule)))
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
        PolicyRule rule) =>
        new(
            effect.RequirementKey,
            effect.Type,
            ImmutableHashSet.Create(scope),
            subjectIds.ToImmutableHashSet(),
            PhaseFor(effect),
            effect.Approval,
            effect.MinimumQuotations,
            effect.SupportingDocumentTypes?.ToImmutableHashSet(StringComparer.Ordinal) ?? ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, $"{rule.Code}:{rule.Revision}"),
            effect.Reason ?? rule.Code);

    private static string PhaseFor(PolicyEffect effect) => effect.Type switch
    {
        PolicyEffectType.RequireApproval => "DEPARTMENT",
        PolicyEffectType.RequireProcurement => "PROCUREMENT",
        PolicyEffectType.RequirePo => "PRE_PO",
        _ => "PRE_PROCUREMENT"
    };

    private static bool Matches(PolicyPredicate predicate, IReadOnlyDictionary<string, PolicyValue> facts)
    {
        if (!facts.TryGetValue(predicate.FactKey, out var actual))
        {
            return false;
        }

        if (predicate.Operator is PolicyOperator.In or PolicyOperator.NotIn)
        {
            if (predicate.Value.Kind != PolicyValueKind.Set || predicate.Value.Members.IsDefaultOrEmpty ||
                predicate.Value.Members.Any(member => member.Kind != actual.Kind))
            {
                return false;
            }

            var contains = predicate.Value.Members.Any(member => member.Value == actual.Value);
            return predicate.Operator == PolicyOperator.In ? contains : !contains;
        }

        if (predicate.Operator == PolicyOperator.Between)
        {
            if (actual.Kind != PolicyValueKind.Money || predicate.Value.Kind != PolicyValueKind.MoneyRange)
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

        return predicate.Operator switch
        {
            PolicyOperator.Equal => actual.Value == predicate.Value.Value,
            PolicyOperator.NotEqual => actual.Value != predicate.Value.Value,
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
        facts.TryGetValue(key, out var value)
            ? value.AsMoney()
            : throw new DomainValidationException($"Fact '{key}' is required.");

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
