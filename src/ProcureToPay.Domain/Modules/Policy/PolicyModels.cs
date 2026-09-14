using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

public enum PolicyScope
{
    Line = 1,
    Request = 2,
    SourcingPo = 3
}

public enum PolicySetStatus
{
    Draft = 1,
    Published = 2,
    Retired = 3
}

public enum PolicyResult
{
    Passed = 1,
    RequirementsGenerated = 2,
    Blocked = 3
}

public enum PolicyEffectType
{
    Allow = 1,
    Block = 2,
    RequireApproval = 3,
    RequireBudgetCheck = 4,
    RequireProcurement = 5,
    RequireQuotations = 6,
    RequirePo = 7,
    AllowDirectPurchase = 8,
    RequireSupportingDocument = 9,
    RequireActiveSupplier = 10
}

public enum PolicyOperator
{
    Equal = 1,
    NotEqual = 2,
    In = 3,
    NotIn = 4,
    IsTrue = 5,
    IsFalse = 6,
    GreaterThan = 7,
    GreaterThanOrEqual = 8,
    LessThan = 9,
    LessThanOrEqual = 10,
    Between = 11
}

public enum PolicyValueKind
{
    Money = 1,
    Boolean = 2,
    Code = 3,
    VersionedEntityRef = 4,
    Set = 5,
    MoneyRange = 6,
    VersionedCodeRef = 7,
    TypedAnswer = 8
}

public sealed record PolicyValue
{
    private PolicyValue(
        PolicyValueKind kind,
        string value,
        string? referenceType = null,
        IReadOnlyList<PolicyValue>? members = null,
        decimal? lowerBound = null,
        decimal? upperBound = null,
        bool lowerInclusive = true,
        bool upperInclusive = false,
        string? currency = null,
        string? catalog = null,
        int? version = null,
        string? digest = null,
        Guid? entityId = null,
        string? questionCode = null,
        int? schemaVersion = null,
        TypedAnswerValueKind? answerKind = null)
    {
        Kind = kind;
        Value = value;
        ReferenceType = referenceType;
        Members = members?.ToImmutableArray() ?? [];
        LowerBound = lowerBound;
        UpperBound = upperBound;
        LowerInclusive = lowerInclusive;
        UpperInclusive = upperInclusive;
        Currency = currency;
        Catalog = catalog;
        Version = version;
        Digest = digest;
        EntityId = entityId;
        QuestionCode = questionCode;
        SchemaVersion = schemaVersion;
        AnswerKind = answerKind;
    }

    public PolicyValueKind Kind { get; }
    public string Value { get; }
    public string? ReferenceType { get; }
    public ImmutableArray<PolicyValue> Members { get; }
    public decimal? LowerBound { get; }
    public decimal? UpperBound { get; }
    public bool LowerInclusive { get; }
    public bool UpperInclusive { get; }
    public string? Currency { get; }
    public string? Catalog { get; }
    public int? Version { get; }
    public string? Digest { get; }
    public Guid? EntityId { get; }
    public string? QuestionCode { get; }
    public int? SchemaVersion { get; }
    public TypedAnswerValueKind? AnswerKind { get; }

    /// <summary>MoneyBase currency; must equal the base currency of the evaluated bundle (REQ-04).</summary>
    public static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 ||
            !Regex.IsMatch(currency.Trim(), "^[A-Za-z]{3}$", RegexOptions.CultureInvariant))
        {
            throw new DomainValidationException("Policy money requires an ISO 4217 currency.");
        }

        return currency.Trim().ToUpperInvariant();
    }

    public static PolicyValue Money(decimal value, string currency)
    {
        if (value < 0)
        {
            throw new DomainValidationException("Policy money value cannot be negative.");
        }

        return new PolicyValue(
            PolicyValueKind.Money,
            value.ToString("0.##############################", CultureInfo.InvariantCulture),
            currency: NormalizeCurrency(currency));
    }

    public static PolicyValue Boolean(bool value) =>
        new(PolicyValueKind.Boolean, value ? "true" : "false");

    public static PolicyValue Code(string value) =>
        new(PolicyValueKind.Code, NormalizeCode(value, "Policy code value"));

    /// <summary>VersionedCodeRef: catalog, code and version/digest (REQ-04).</summary>
    public static PolicyValue VersionedCode(string catalog, string code, int version, string digest)
    {
        if (version < 1 || string.IsNullOrWhiteSpace(digest) || digest.Length != 64 ||
            !digest.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException("Versioned code references require a version and SHA-256 digest.");
        }

        return new PolicyValue(
            PolicyValueKind.VersionedCodeRef,
            NormalizeCode(code, "Versioned code"),
            catalog: NormalizeCode(catalog, "Versioned code catalog"),
            version: version,
            digest: digest.ToLowerInvariant());
    }

    /// <summary>VersionedEntityRef: entity type, stable id and positive version (REQ-04).</summary>
    public static PolicyValue EntityRef(string entityType, Guid id, int version)
    {
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("Policy reference id and version are required.");
        }

        return new PolicyValue(
            PolicyValueKind.VersionedEntityRef,
            $"{id:D}:{version}",
            NormalizeCode(entityType, "Policy reference type"),
            entityId: id,
            version: version);
    }

    /// <summary>Compatibility alias for <see cref="EntityRef"/>.</summary>
    public static PolicyValue Reference(string referenceType, Guid id, int version) =>
        EntityRef(referenceType, id, version);

    /// <summary>TypedAnswerRef: question code, schema version and one typed value (REQ-04).</summary>
    public static PolicyValue TypedAnswer(
        string questionCode,
        int schemaVersion,
        TypedAnswerValueKind answerKind,
        string value)
    {
        if (schemaVersion < 1 || string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException("Typed answers require a schema version and value.");
        }

        var normalizedValue = answerKind == TypedAnswerValueKind.Boolean
            ? bool.TryParse(value, out var boolean) ? (boolean ? "true" : "false")
                : throw new DomainValidationException("Boolean typed answers must contain true or false.")
            : NormalizeCode(value, "Enum answer");
        return new PolicyValue(
            PolicyValueKind.TypedAnswer,
            normalizedValue,
            questionCode: NormalizeCode(questionCode, "Question code"),
            schemaVersion: schemaVersion,
            answerKind: answerKind);
    }

    // Set values are canonicalized in a deterministic order for IN/NOT_IN predicates.
    public static PolicyValue Set(params PolicyValue[] values)
    {
        if (values is null || values.Length == 0 || values.Any(value => value is null) ||
            values.Select(value => value.Kind).Distinct().Count() != 1)
        {
            throw new DomainValidationException("Policy value sets must contain one non-empty value kind.");
        }

        var members = values
            .Select(value => (value, key: PolicyCanonicalizer.SerializeCanonical(PolicyCanonicalizer.CanonicalizeValue(value))))
            .GroupBy(item => item.key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.key, Comparer<string>.Create(PolicyCanonicalizer.CompareCanonical))
            .Select(item => item.value)
            .ToArray();
        if (members.Length != values.Length)
        {
            throw new DomainConflictException("Policy value sets cannot contain duplicates.");
        }
        return new PolicyValue(
            PolicyValueKind.Set,
            string.Join("|", members.Select(value => value.Value)),
            members: members);
    }

    public static PolicyValue MoneyRange(
        decimal lowerBound,
        decimal upperBound,
        string currency,
        bool lowerInclusive = true,
        bool upperInclusive = false)
    {
        if (lowerBound > upperBound || (lowerBound == upperBound && (!lowerInclusive || !upperInclusive)))
        {
            throw new DomainValidationException("Policy money range is empty or inverted.");
        }

        return new PolicyValue(
            PolicyValueKind.MoneyRange,
            $"{lowerBound.ToString("0.##############################", CultureInfo.InvariantCulture)}:{upperBound.ToString("0.##############################", CultureInfo.InvariantCulture)}",
            lowerBound: lowerBound,
            upperBound: upperBound,
            lowerInclusive: lowerInclusive,
            upperInclusive: upperInclusive,
            currency: NormalizeCurrency(currency));
    }

    public decimal AsMoney() =>
        Kind == PolicyValueKind.Money
            ? decimal.Parse(Value, CultureInfo.InvariantCulture)
            : throw new DomainValidationException("Policy value is not monetary.");

    public string AsCurrency() =>
        Kind == PolicyValueKind.Money
            ? Currency ?? throw new DomainValidationException("Policy money value requires a currency.")
            : throw new DomainValidationException("Policy value is not monetary.");

    public bool AsBoolean() =>
        Kind == PolicyValueKind.Boolean
            ? bool.Parse(Value)
            : throw new DomainValidationException("Policy value is not boolean.");

    /// <summary>Full typed identity, including the fields a bare <see cref="Value"/> would lose.</summary>
    public bool SameAs(PolicyValue other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind &&
            string.Equals(Value, other.Value, StringComparison.Ordinal) &&
            string.Equals(ReferenceType, other.ReferenceType, StringComparison.Ordinal) &&
            string.Equals(Currency, other.Currency, StringComparison.Ordinal) &&
            string.Equals(Catalog, other.Catalog, StringComparison.Ordinal) &&
            Version == other.Version &&
            string.Equals(Digest, other.Digest, StringComparison.Ordinal) &&
            EntityId == other.EntityId &&
            string.Equals(QuestionCode, other.QuestionCode, StringComparison.Ordinal) &&
            SchemaVersion == other.SchemaVersion &&
            AnswerKind == other.AnswerKind &&
            LowerBound == other.LowerBound &&
            UpperBound == other.UpperBound &&
            LowerInclusive == other.LowerInclusive &&
            UpperInclusive == other.UpperInclusive;
    }

    public static string NormalizeCode(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64 ||
            !Regex.IsMatch(value.Trim(), "^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant))
        {
            throw new DomainValidationException($"{field} must use an uppercase contract code.");
        }

        return value.Trim();
    }
}

public sealed record PolicyPredicate
{
    public PolicyPredicate(string factKey, PolicyOperator @operator, PolicyValue value)
    {
        FactKey = PolicyValue.NormalizeCode(factKey, "Policy fact key");
        if (!PolicyFactCatalog.IsKnown(FactKey))
        {
            throw new DomainValidationException($"Unknown policy fact '{FactKey}'.");
        }
        Operator = @operator;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        PolicyFactCatalog.Validate(FactKey, Operator, Value);
    }

    public string FactKey { get; }
    public PolicyOperator Operator { get; }
    public PolicyValue Value { get; }
}

/// <summary>
/// Closed Release 1 fact catalog: every fact declares its Release 1 type and the
/// operators it accepts, validated before publication (REQ-04).
/// </summary>
public static class PolicyFactCatalog
{
    private static readonly IReadOnlySet<string> MoneyFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "GROSS_AMOUNT_BASE"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> EnumFacts =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["PURCHASE_TYPE"] = new HashSet<string>(StringComparer.Ordinal) { "GOOD", "SERVICE", "SUBSCRIPTION" },
            ["EXTERNAL_AGREEMENT_STATUS"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "NONE", "ACTIVE", "INACTIVE", "EXPIRED"
            }
        };

    private static readonly IReadOnlySet<string> BooleanFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "COST_CENTER_ACTIVE", "COST_CENTER_DEPARTMENT_ACTIVE", "PREFERRED_SUPPLIER",
        "CONTRACT_REQUIRED", "NON_STANDARD_TERMS"
    };

    private static readonly IReadOnlySet<string> CodeRefFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "SPEND_CATEGORY", "DATA_RISK"
    };

    private static readonly IReadOnlySet<string> EntityRefFacts = new HashSet<string>(StringComparer.Ordinal)
    {
        "BENEFICIARY_DEPARTMENT", "COST_CENTER", "COST_CENTER_DEPARTMENT", "SUPPLIER",
        "PREFERRED_PRODUCT", "REQUIRED_PRODUCT"
    };

    public static bool IsKnown(string factKey) =>
        MoneyFacts.Contains(factKey) || EnumFacts.ContainsKey(factKey) || BooleanFacts.Contains(factKey) ||
        CodeRefFacts.Contains(factKey) || EntityRefFacts.Contains(factKey) ||
        string.Equals(factKey, "RISK_ANSWER", StringComparison.Ordinal);

    public static void Validate(string factKey, PolicyOperator @operator, PolicyValue value)
    {
        if (MoneyFacts.Contains(factKey))
        {
            if (@operator == PolicyOperator.Between && value.Kind != PolicyValueKind.MoneyRange)
            {
                throw new DomainValidationException($"Fact '{factKey}' requires a money range for BETWEEN.");
            }
            if (@operator is not (PolicyOperator.GreaterThan or PolicyOperator.GreaterThanOrEqual or
                PolicyOperator.LessThan or PolicyOperator.LessThanOrEqual or PolicyOperator.Between))
            {
                throw new DomainValidationException($"Fact '{factKey}' only accepts money comparison operators.");
            }
            if (@operator != PolicyOperator.Between && value.Kind != PolicyValueKind.Money)
            {
                throw new DomainValidationException($"Fact '{factKey}' requires a Money value.");
            }
            return;
        }

        if (EnumFacts.TryGetValue(factKey, out var allowedCodes))
        {
            RequireTextualOperator(factKey, @operator, value);
            var codes = value.Kind == PolicyValueKind.Set ? value.Members : [value];
            if (codes.Any(code => code.Kind != PolicyValueKind.Code || !allowedCodes.Contains(code.Value)))
            {
                throw new DomainValidationException($"Fact '{factKey}' only accepts its enumerated codes.");
            }
            return;
        }

        if (CodeRefFacts.Contains(factKey))
        {
            RequireTextualOperator(factKey, @operator, value);
            RequireKind(factKey, value, PolicyValueKind.VersionedCodeRef);
            return;
        }

        if (EntityRefFacts.Contains(factKey))
        {
            RequireTextualOperator(factKey, @operator, value);
            RequireKind(factKey, value, PolicyValueKind.VersionedEntityRef);
            return;
        }

        if (string.Equals(factKey, "RISK_ANSWER", StringComparison.Ordinal))
        {
            // SPEC 06: one answer per question/schema. EQ/NEQ declare a single typed answer; IN/NOT_IN
            // declare a homogeneous set of answers of the same question and schema version.
            if (@operator is PolicyOperator.In or PolicyOperator.NotIn)
            {
                if (value.Kind != PolicyValueKind.Set || value.Members.IsDefaultOrEmpty ||
                    value.Members.Any(member => member.Kind != PolicyValueKind.TypedAnswer) ||
                    value.Members
                        .Select(member => (member.QuestionCode, member.SchemaVersion, member.AnswerKind))
                        .Distinct().Count() != 1)
                {
                    throw new DomainValidationException(
                        "Fact 'RISK_ANSWER' only accepts IN/NOT_IN over one question, schema and value kind.");
                }

                return;
            }

            RequireTextualOperator(factKey, @operator, value);
            RequireKind(factKey, value, PolicyValueKind.TypedAnswer);
            return;
        }

        if (BooleanFacts.Contains(factKey))
        {
            if (@operator is not (PolicyOperator.IsTrue or PolicyOperator.IsFalse) ||
                value.Kind != PolicyValueKind.Boolean)
            {
                throw new DomainValidationException($"Fact '{factKey}' requires a boolean operator and value.");
            }
        }
    }

    private static void RequireTextualOperator(string factKey, PolicyOperator @operator, PolicyValue value)
    {
        if (@operator is not (PolicyOperator.Equal or PolicyOperator.NotEqual or
            PolicyOperator.In or PolicyOperator.NotIn))
        {
            throw new DomainValidationException($"Fact '{factKey}' only accepts EQ/NEQ/IN/NOT_IN.");
        }
        if (@operator is PolicyOperator.In or PolicyOperator.NotIn && value.Kind != PolicyValueKind.Set)
        {
            throw new DomainValidationException("IN and NOT_IN require a non-empty value set.");
        }
    }

    private static void RequireKind(string factKey, PolicyValue value, PolicyValueKind expected)
    {
        var values = value.Kind == PolicyValueKind.Set ? value.Members : [value];
        if (values.Any(member => member.Kind != expected))
        {
            throw new DomainValidationException($"Fact '{factKey}' only accepts its typed reference values.");
        }
    }
}

public sealed record PolicyAuthorityLevelSnapshot
{
    public PolicyAuthorityLevelSnapshot(Guid id, int version, string code, int rank)
    {
        if (id == Guid.Empty || version < 1 || rank < 1)
        {
            throw new DomainValidationException("Authority level snapshot is invalid.");
        }

        Id = id;
        Version = version;
        Code = PolicyValue.NormalizeCode(code, "Authority level code");
        Rank = rank;
    }

    public Guid Id { get; }
    public int Version { get; }
    public string Code { get; }
    public int Rank { get; }
}

public sealed record PolicyApprovalDescriptor
{
    public PolicyApprovalDescriptor(
        SystemRole role,
        ApprovalAuthorityType? authorityType,
        PolicyAuthorityLevelSnapshot? authorityLevel,
        decimal? amountBase,
        string? baseCurrency,
        string decisionScope,
        bool exceptionable = false,
        decimal? minimumExceptionAmount = null,
        Guid? organizationId = null)
    {
        Role = role;
        AuthorityType = authorityType;
        AuthorityLevel = authorityLevel;
        AmountBase = amountBase;
        BaseCurrency = NormalizeCurrency(baseCurrency, amountBase);
        // SPEC 05 REQ-02: the decision scope is exactly the canonical `decision-scope/v1`
        // descriptor of SPEC 03. Tokens such as ORGANIZATION/COST_CENTER, non-canonical JSON,
        // extra fields and inactive references are rejected; the value is stored canonical.
        DecisionScope = Approval.DecisionScopeDescriptor.Parse(decisionScope, organizationId).ToCanonicalJson();
        Exceptionable = exceptionable;
        MinimumExceptionAmount = minimumExceptionAmount;

        if (role is SystemRole.ItReviewer or SystemRole.LegalReviewer)
        {
            if (authorityType is not null || authorityLevel is not null || amountBase is not null ||
                BaseCurrency is not null || exceptionable)
            {
                throw new DomainValidationException("IT and Legal approvals cannot carry authority.");
            }
        }
        else if (authorityType is null || authorityLevel is null)
        {
            throw new DomainValidationException("This approval role requires an authority snapshot.");
        }

        if (amountBase is < 0 || minimumExceptionAmount is < 0 ||
            (minimumExceptionAmount is not null && amountBase is not null && minimumExceptionAmount > amountBase))
        {
            throw new DomainValidationException("Approval monetary limits are invalid.");
        }
    }

    public SystemRole Role { get; }
    public ApprovalAuthorityType? AuthorityType { get; }
    public PolicyAuthorityLevelSnapshot? AuthorityLevel { get; }
    public decimal? AmountBase { get; }
    public string? BaseCurrency { get; }
    public string DecisionScope { get; }
    public bool Exceptionable { get; }
    public decimal? MinimumExceptionAmount { get; }

    private static string? NormalizeCurrency(string? currency, decimal? amount)
    {
        if (amount is null)
        {
            if (!string.IsNullOrWhiteSpace(currency))
            {
                throw new DomainValidationException("A currency requires an approval amount.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            throw new DomainValidationException("Approval amount requires an ISO currency.");
        }

        return currency.Trim().ToUpperInvariant();
    }

}

public sealed record PolicyEffect
{
    public PolicyEffect(
        PolicyEffectType type,
        string requirementKey,
        string? reason = null,
        PolicyApprovalDescriptor? approval = null,
        int? minimumQuotations = null,
        int? minimumExceptionQuotations = null,
        IReadOnlySet<string>? supportingDocumentTypes = null)
    {
        Type = type;
        RequirementKey = PolicyValue.NormalizeCode(requirementKey, "Policy requirement key");
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Approval = approval;
        MinimumQuotations = minimumQuotations;
        MinimumExceptionQuotations = minimumExceptionQuotations;
        SupportingDocumentTypes = supportingDocumentTypes is null
            ? null
            : supportingDocumentTypes
                .Select(value => PolicyValue.NormalizeCode(value, "Supporting document type"))
                .ToImmutableHashSet(StringComparer.Ordinal);

        if (type == PolicyEffectType.RequireApproval && approval is null)
        {
            throw new DomainValidationException("Approval effects require an approval descriptor.");
        }

        if (type != PolicyEffectType.RequireApproval && approval is not null)
        {
            throw new DomainValidationException("Only approval effects can carry an approval descriptor.");
        }

        if (type == PolicyEffectType.RequireQuotations &&
            (minimumQuotations is null or < 1 ||
             minimumExceptionQuotations is < 1 ||
             minimumExceptionQuotations >= minimumQuotations))
        {
            throw new DomainValidationException("Quotation requirements are invalid.");
        }

        if (type != PolicyEffectType.RequireQuotations &&
            (minimumQuotations is not null || minimumExceptionQuotations is not null))
        {
            throw new DomainValidationException("Quotation limits belong only to quotation effects.");
        }
    }

    public PolicyEffectType Type { get; }
    public string RequirementKey { get; }
    public string? Reason { get; }
    public PolicyApprovalDescriptor? Approval { get; }
    public int? MinimumQuotations { get; }
    public int? MinimumExceptionQuotations { get; }
    public IReadOnlySet<string>? SupportingDocumentTypes { get; }
}

public sealed record PolicyRule
{
    public PolicyRule(
        string code,
        PolicyScope scope,
        IEnumerable<PolicyPredicate> predicates,
        IEnumerable<PolicyEffect> effects,
        bool isFallback = false,
        int revision = 1)
    {
        Code = PolicyValue.NormalizeCode(code, "Policy rule code");
        Scope = scope;
        Predicates = predicates?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(predicates));
        Effects = effects?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(effects));
        IsFallback = isFallback;
        Revision = revision;

        if (Revision < 1 || Effects.IsDefaultOrEmpty || Effects.Length > 16 || Predicates.Length > 32)
        {
            throw new DomainValidationException("Policy rule size or revision is invalid.");
        }

        if (IsFallback && Predicates.Length != 0)
        {
            throw new DomainValidationException("A policy fallback cannot contain predicates.");
        }

        if (IsFallback && Effects.Any(effect => effect.Type is not (PolicyEffectType.Allow or PolicyEffectType.Block)))
        {
            throw new DomainValidationException("A policy fallback can only allow or block.");
        }
    }

    public string Code { get; }
    public PolicyScope Scope { get; }
    public ImmutableArray<PolicyPredicate> Predicates { get; }
    public ImmutableArray<PolicyEffect> Effects { get; }
    public bool IsFallback { get; }
    public int Revision { get; }
}

public sealed class PolicySetVersion
{
    private readonly List<PolicyRule> rules = [];

    public PolicySetVersion(Guid id, Guid organizationId, long sequence, IEnumerable<PolicyScope> scopes)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || sequence < 1)
        {
            throw new DomainValidationException("Policy version identifiers are required.");
        }

        Id = id;
        OrganizationId = organizationId;
        Sequence = sequence;
        Scopes = scopes?.Distinct().ToImmutableHashSet() ?? throw new ArgumentNullException(nameof(scopes));
        if (Scopes.Count == 0)
        {
            throw new DomainValidationException("A policy version must enable at least one scope.");
        }
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public long Sequence { get; }
    public PolicySetStatus Status { get; private set; } = PolicySetStatus.Draft;
    public IReadOnlySet<PolicyScope> Scopes { get; }
    public IReadOnlyList<PolicyRule> Rules => rules;
    public string? ContentDigest { get; private set; }

    public void AddRule(PolicyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        EnsureDraft();
        if (rules.Any(existing => existing.Code == rule.Code))
        {
            throw new DomainConflictException($"Policy rule '{rule.Code}' is duplicated.");
        }

        rules.Add(rule);
    }

    public void Publish(string contentDigest)
    {
        EnsureDraft();
        ValidateForPublication();
        if (string.IsNullOrWhiteSpace(contentDigest) || !Regex.IsMatch(contentDigest, "^[0-9a-f]{64}$"))
        {
            throw new DomainValidationException("A SHA-256 policy content digest is required.");
        }

        ContentDigest = contentDigest;
        Status = PolicySetStatus.Published;
    }

    public void Retire()
    {
        if (Status != PolicySetStatus.Published)
        {
            throw new DomainConflictException("Only a published policy can be retired.");
        }

        Status = PolicySetStatus.Retired;
    }

    public void ValidateForPublication()
    {
        if (rules.Count == 0)
        {
            throw new DomainValidationException("A policy version must contain rules.");
        }

        foreach (var scope in Scopes)
        {
            var fallback = rules.Where(rule => rule.Scope == scope && rule.IsFallback).ToArray();
            if (fallback.Length != 1)
            {
                throw new DomainValidationException($"Scope {scope} requires exactly one fallback rule.");
            }
            if (fallback[0].Predicates.Length != 0 ||
                fallback[0].Effects[0].Type is not (PolicyEffectType.Allow or PolicyEffectType.Block))
            {
                throw new DomainValidationException("A fallback must be unconditional and allow or block.");
            }
        }

        if (rules.GroupBy(rule => (rule.Scope, rule.Code)).Any(group => group.Count() > 1))
        {
            throw new DomainConflictException("Policy rule codes must be unique within a scope.");
        }

        foreach (var rule in rules)
        {
            if (!Scopes.Contains(rule.Scope))
            {
                throw new DomainValidationException("A rule cannot target a disabled policy scope.");
            }

            ValidateEffects(rule);
        }

        foreach (var group in rules.SelectMany(rule => rule.Effects)
                     .GroupBy(effect => effect.RequirementKey, StringComparer.Ordinal))
        {
            var types = group.Select(effect => effect.Type).Distinct().ToArray();
            var directAndPoOnly = types.Length == 2 &&
                types.Contains(PolicyEffectType.RequirePo) &&
                types.Contains(PolicyEffectType.AllowDirectPurchase);
            if (types.Length > 1 && !directAndPoOnly)
            {
                throw new DomainConflictException(
                    $"Requirement key '{group.Key}' combines incompatible effect types.");
            }

            var approvals = group.Where(effect => effect.Type == PolicyEffectType.RequireApproval)
                .Select(effect => effect.Approval!)
                .ToArray();
            if (approvals.Any(approval => approval.Role != approvals[0].Role ||
                approval.AuthorityType != approvals[0].AuthorityType ||
                approval.DecisionScope != approvals[0].DecisionScope))
            {
                throw new DomainConflictException(
                    $"Approval requirement key '{group.Key}' has incompatible authority descriptors.");
            }
        }
    }

    private static void ValidateEffects(PolicyRule rule)
    {
        var approvalGroups = rule.Effects
            .Where(effect => effect.Type == PolicyEffectType.RequireApproval)
            .GroupBy(effect => effect.RequirementKey, StringComparer.Ordinal);

        foreach (var group in approvalGroups)
        {
            var descriptors = group.Select(effect => effect.Approval!).ToArray();
            if (descriptors.Select(descriptor => descriptor.Role).Distinct().Count() > 1 ||
                descriptors.Select(descriptor => descriptor.AuthorityType).Distinct().Count() > 1)
            {
                throw new DomainValidationException(
                    $"Approval effects for '{group.Key}' must have compatible roles and authority types.");
            }
        }

        if (rule.IsFallback && rule.Effects.Length != 1)
        {
            throw new DomainValidationException("A fallback must contain exactly one effect.");
        }
    }

    private void EnsureDraft()
    {
        if (Status != PolicySetStatus.Draft)
        {
            throw new DomainConflictException("Published policy content is immutable.");
        }
    }
}
