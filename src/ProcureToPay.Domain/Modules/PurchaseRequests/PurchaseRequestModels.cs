using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseRequests;

/// <summary>Aggregate lifecycle projected from immutable versions and approval results (REQ-09).</summary>
public enum PurchaseRequestStatus
{
    Draft = 1,
    Submitted = 2,
    InApproval = 3,
    Approved = 4,
    PartiallyApproved = 5,
    ChangesRequested = 6,
    Rejected = 7,
    Cancelled = 8
}

/// <summary>Per-line projection of the approval obligations of its current case (REQ-09).</summary>
public enum PurchaseRequestLineStatus
{
    Draft = 1,
    InApproval = 2,
    Approved = 3,
    ChangesRequested = 4,
    Rejected = 5,
    Cancelled = 6
}

/// <summary>Closed assertion vocabulary of the owner attestation (Datos y contratos).</summary>
public enum PurchaseRequestAssertionType
{
    ActiveInOrganization = 1,
    CostCenterOwnedByDepartment = 2,
    FxAttestationValid = 3
}

/// <summary>Closed reference vocabulary of the attestation contract.</summary>
public enum PurchaseRequestReferenceType
{
    LegalEntity = 1,
    User = 2,
    CostCenter = 3,
    Department = 4,
    SpendCategory = 5,
    Product = 6,
    Supplier = 7,
    RiskSchema = 8,
    Fx = 9
}

public enum PurchaseRequestReferenceKind
{
    Entity = 1,
    Code = 2,
    QuestionSchema = 3
}

/// <summary>Closed revision delta vocabulary of <c>approval-supersession-delta/v1</c> (REQ-08).</summary>
public enum PurchaseRequestRevisionChangeKind
{
    Retained = 1,
    Changed = 2,
    Added = 3,
    Removed = 4
}

/// <summary>Durable states of one submission attempt (REQ-06, Datos y contratos).</summary>
public enum PurchaseRequestSubmissionStatus
{
    Pending = 1,
    PolicyConfirmed = 2,
    ApprovalConfirmed = 3,
    Blocked = 4,
    DependencyFailed = 5
}

/// <summary>
/// A payload over the contractual limits (500 lines, 256 risk answers per line, 5 MiB snapshot)
/// is rejected with <c>413</c> before the affected artefact is persisted (REQ-11).
/// </summary>
public sealed class PurchaseRequestPayloadTooLargeException(string message) : DomainException(message)
{
}

/// <summary>Closed action and error codes of the submission lifecycle (REQ-06, REQ-11).</summary>
public static class PurchaseRequestSubmissionCodes
{
    public const string CommandSubmit = "SUBMIT";
    public const string ActionSubmitted = "SUBMITTED";
    public const string ActionInApproval = "IN_APPROVAL";
    public const string ActionApproved = "APPROVED";
    public const string ReasonCodeSubmit = "PURCHASE_REQUEST_SUBMIT";
    public const string ErrorPolicyBlocked = "POLICY_BLOCKED";
    public const string ErrorPolicyDependency = "POLICY_DEPENDENCY_UNAVAILABLE";
    public const string ErrorApprovalDependency = "APPROVAL_DEPENDENCY_UNAVAILABLE";
}

public static class PurchaseRequestCodes
{
    public const string CreateCommandVersion = "purchase-request-create-command/v1";
    public const string RevisionCommandVersion = "purchase-request-revision-command/v1";
    public const string LineContentVersion = "purchase-request-line-version/v1";
    public const string RequestContentVersion = "purchase-request-version/v1";
    public const string AttestationVersion = "purchase-request-reference-attestation/v1";
    public const string ManifestVersion = "purchase-request-completeness-manifest/v1";
    public const string SubmissionVersion = "purchase-request-submission/v1";
    public const string VerificationVersion = "purchase-request-reference-verification/v1";
    public const string MaterialityVersion = "purchase-request-materiality/v1";
    public const string ProviderId = "purchase-request-domain";
    public const string ProviderContractVersion = "purchase-request-policy-facts/v1";
    public const string SubjectType = "PURCHASE_REQUEST";
    public const string EvaluationOperation = "REQUEST_EVALUATE";
    public const string SubmitOperation = "SUBMIT_PURCHASE_REQUEST";

    public static string Code(PurchaseRequestReferenceType type) => type switch
    {
        PurchaseRequestReferenceType.LegalEntity => "LEGAL_ENTITY",
        PurchaseRequestReferenceType.User => "USER",
        PurchaseRequestReferenceType.CostCenter => "COST_CENTER",
        PurchaseRequestReferenceType.Department => "DEPARTMENT",
        PurchaseRequestReferenceType.SpendCategory => "SPEND_CATEGORY",
        PurchaseRequestReferenceType.Product => "PRODUCT",
        PurchaseRequestReferenceType.Supplier => "SUPPLIER",
        PurchaseRequestReferenceType.RiskSchema => "RISK_SCHEMA",
        PurchaseRequestReferenceType.Fx => "FX",
        _ => throw new DomainValidationException("The purchase request reference type is invalid.")
    };

    public static string Code(PurchaseRequestAssertionType type) => type switch
    {
        PurchaseRequestAssertionType.ActiveInOrganization => "ACTIVE_IN_ORGANIZATION",
        PurchaseRequestAssertionType.CostCenterOwnedByDepartment => "COST_CENTER_OWNED_BY_DEPARTMENT",
        PurchaseRequestAssertionType.FxAttestationValid => "FX_ATTESTATION_VALID",
        _ => throw new DomainValidationException("The purchase request assertion type is invalid.")
    };

    public static string Code(PurchaseRequestRevisionChangeKind kind) => kind switch
    {
        PurchaseRequestRevisionChangeKind.Retained => "RETAINED",
        PurchaseRequestRevisionChangeKind.Changed => "CHANGED",
        PurchaseRequestRevisionChangeKind.Added => "ADDED",
        PurchaseRequestRevisionChangeKind.Removed => "REMOVED",
        _ => throw new DomainValidationException("The revision change kind is invalid.")
    };

    public static PurchaseRequestRevisionChangeKind ParseChangeKind(string? code) => code switch
    {
        "RETAINED" => PurchaseRequestRevisionChangeKind.Retained,
        "CHANGED" => PurchaseRequestRevisionChangeKind.Changed,
        "ADDED" => PurchaseRequestRevisionChangeKind.Added,
        "REMOVED" => PurchaseRequestRevisionChangeKind.Removed,
        _ => throw new DomainValidationException("The stored revision change kind is invalid.")
    };

    public static string PurchaseTypeCode(string purchaseType) => purchaseType switch
    {
        "GOOD" or "SERVICE" or "SUBSCRIPTION" => purchaseType,
        _ => throw new DomainValidationException("The purchase type must be GOOD, SERVICE or SUBSCRIPTION.")
    };

    public static string AgreementStatus(string code) => code switch
    {
        "NONE" or "ACTIVE" or "INACTIVE" or "EXPIRED" => code,
        _ => throw new DomainValidationException("The external agreement status is invalid.")
    };
}

public static class PurchaseRequestLimits
{
    public const int MaxLines = 500;
    public const int MaxRiskAnswersPerLine = 256;
    public const int MaxSnapshotBytes = 5 * 1024 * 1024;
    public const int MaxKeyLength = 128;
    public const int MaxSummaryScalars = 500;
    public const int MaxReasonScalars = 1_000;
    public const int MinFiscalYear = 2000;
    public const int MaxFiscalYear = 2100;

    private static readonly Regex KeyPattern =
        new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RequireKey(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !KeyPattern.IsMatch(value))
        {
            throw new DomainValidationException(
                $"{field} must contain 1-128 characters of [A-Za-z0-9._:-].");
        }

        return value;
    }

    public static string RequireSummary(string? value, string field)
    {
        var scalars = value is null ? 0 : new StringInfo(value).LengthInTextElements;
        if (scalars is < 1 or > MaxSummaryScalars)
        {
            throw new DomainValidationException($"{field} must contain 1-{MaxSummaryScalars} Unicode scalars.");
        }

        return value!;
    }

    public static string RequireReason(string? value, string field)
    {
        var scalars = value is null ? 0 : new StringInfo(value).LengthInTextElements;
        if (scalars is < 1 or > MaxReasonScalars)
        {
            throw new DomainValidationException($"{field} must contain 1-{MaxReasonScalars} Unicode scalars.");
        }

        return value!;
    }
}

/// <summary>
/// Exact <c>purchase-request-line-content/v1</c> payload (REQ-03). Text is minimized: the
/// summary never reaches Policy or a materiality digest; only typed facts do.
/// </summary>
public sealed record PurchaseRequestLineContent
{
    public PurchaseRequestLineContent(
        decimal estimatedGrossAmount,
        string transactionCurrency,
        decimal baseAmount,
        string baseCurrency,
        int fiscalYear,
        string purchaseType,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef costCenterRef,
        VersionedEntityRef costCenterDepartmentRef,
        VersionedEntityRef beneficiaryDepartmentRef,
        VersionedEntityRef requestedForUserRef,
        VersionedEntityRef? supplierRef,
        VersionedEntityRef? preferredProductRef,
        VersionedEntityRef? requiredProductRef,
        bool contractRequired,
        bool nonStandardTerms,
        string agreementStatus,
        string needSummary,
        IEnumerable<TypedAnswerRef> riskAnswers,
        PurchaseRequestFxRef? fxAttestationRef)
    {
        if (estimatedGrossAmount < 0 || baseAmount < 0)
        {
            throw new DomainValidationException("Purchase request amounts cannot be negative.");
        }

        if (fiscalYear is < PurchaseRequestLimits.MinFiscalYear or > PurchaseRequestLimits.MaxFiscalYear)
        {
            throw new DomainValidationException("The fiscal year is outside the supported range.");
        }

        if (preferredProductRef is not null && requiredProductRef is not null)
        {
            throw new DomainValidationException("A line cannot declare preferred and required product at once.");
        }

        TransactionCurrency = PolicyValue.NormalizeCurrency(transactionCurrency);
        BaseCurrency = PolicyValue.NormalizeCurrency(baseCurrency);
        if (string.Equals(TransactionCurrency, BaseCurrency, StringComparison.Ordinal) != (fxAttestationRef is null))
        {
            throw new DomainValidationException(
                "An FX attestation is required exactly when the transaction currency differs from the base currency.");
        }

        EstimatedGrossAmount = estimatedGrossAmount;
        BaseAmount = baseAmount;
        FiscalYear = fiscalYear;
        PurchaseType = PurchaseRequestCodes.PurchaseTypeCode(purchaseType);
        SpendCategoryRef = spendCategoryRef ??
            throw new DomainValidationException("A line requires its spend category reference.");
        CostCenterRef = costCenterRef ??
            throw new DomainValidationException("A line requires its cost center reference.");
        CostCenterDepartmentRef = costCenterDepartmentRef ??
            throw new DomainValidationException("A line requires the cost center department reference.");
        BeneficiaryDepartmentRef = beneficiaryDepartmentRef ??
            throw new DomainValidationException("A line requires its beneficiary department reference.");
        RequestedForUserRef = requestedForUserRef ??
            throw new DomainValidationException("A line requires its requested-for reference.");
        SupplierRef = supplierRef;
        PreferredProductRef = preferredProductRef;
        RequiredProductRef = requiredProductRef;
        ContractRequired = contractRequired;
        NonStandardTerms = nonStandardTerms;
        AgreementStatus = PurchaseRequestCodes.AgreementStatus(agreementStatus);
        NeedSummary = PurchaseRequestLimits.RequireSummary(needSummary, "Need summary");
        var answers = (riskAnswers ?? []).ToImmutableArray();
        if (answers.Length > PurchaseRequestLimits.MaxRiskAnswersPerLine)
        {
            throw new PurchaseRequestPayloadTooLargeException(
                $"A line supports at most {PurchaseRequestLimits.MaxRiskAnswersPerLine} risk answers.");
        }

        var duplicateAnswer = answers
            .GroupBy(answer => (answer.QuestionCode, answer.SchemaVersion))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAnswer is not null)
        {
            throw new DomainValidationException("Risk answers are unique per question and schema version.");
        }

        RiskAnswers = answers
            .OrderBy(answer => answer.QuestionCode, StringComparer.Ordinal)
            .ThenBy(answer => answer.SchemaVersion)
            .ToImmutableArray();
        FxAttestationRef = fxAttestationRef;
    }

    public decimal EstimatedGrossAmount { get; }
    public string TransactionCurrency { get; }
    public decimal BaseAmount { get; }
    public string BaseCurrency { get; }
    public int FiscalYear { get; }
    public string PurchaseType { get; }
    public VersionedCodeRef SpendCategoryRef { get; }
    public VersionedEntityRef CostCenterRef { get; }
    public VersionedEntityRef CostCenterDepartmentRef { get; }
    public VersionedEntityRef BeneficiaryDepartmentRef { get; }
    public VersionedEntityRef RequestedForUserRef { get; }
    public VersionedEntityRef? SupplierRef { get; }
    public VersionedEntityRef? PreferredProductRef { get; }
    public VersionedEntityRef? RequiredProductRef { get; }
    public bool ContractRequired { get; }
    public bool NonStandardTerms { get; }
    public string AgreementStatus { get; }
    public string NeedSummary { get; }
    public IReadOnlyList<TypedAnswerRef> RiskAnswers { get; }
    public PurchaseRequestFxRef? FxAttestationRef { get; }
}

/// <summary>Attested FX equivalence of one line: rate, date and owner evidence reference.</summary>
public sealed record PurchaseRequestFxRef(
    Guid AttestationId,
    int AttestationVersion,
    string BaseCurrency,
    string TransactionCurrency,
    decimal EffectiveRate,
    DateOnly RateDate)
{
    public static PurchaseRequestFxRef Create(
        Guid attestationId,
        int attestationVersion,
        string baseCurrency,
        string transactionCurrency,
        decimal effectiveRate,
        DateOnly rateDate)
    {
        if (attestationId == Guid.Empty || attestationVersion < 1)
        {
            throw new DomainValidationException("An FX attestation requires its identity and version.");
        }

        if (effectiveRate <= 0)
        {
            throw new DomainValidationException("An FX attestation requires a positive rate.");
        }

        var normalizedBase = PolicyValue.NormalizeCurrency(baseCurrency);
        var normalizedTransaction = PolicyValue.NormalizeCurrency(transactionCurrency);
        if (string.Equals(normalizedBase, normalizedTransaction, StringComparison.Ordinal))
        {
            throw new DomainValidationException("An FX attestation requires two different currencies.");
        }

        return new PurchaseRequestFxRef(
            attestationId, attestationVersion, normalizedBase, normalizedTransaction, effectiveRate, rateDate);
    }
}

/// <summary>Exact line reference used by request versions, deltas and the completeness manifest.</summary>
public sealed record PurchaseRequestLineRef
{
    public PurchaseRequestLineRef(Guid id, int version, string contentDigest)
    {
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("A line reference requires its identity and version.");
        }

        Id = id;
        Version = version;
        ContentDigest = RequireDigest(contentDigest, "Line content digest");
    }

    public Guid Id { get; }
    public int Version { get; }
    public string ContentDigest { get; }

    public string CanonicalIdentity => $"{Id:D}:{Version}";

    public static string RequireDigest(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z"))
        {
            throw new DomainValidationException($"{field} must be a lowercase SHA-256 digest.");
        }

        return value;
    }
}

/// <summary>Immutable request version snapshot (REQ-01, REQ-02).</summary>
public sealed record PurchaseRequestSnapshot
{
    public PurchaseRequestSnapshot(
        Guid requestId,
        int version,
        int? predecessorVersion,
        Guid organizationId,
        Guid requesterId,
        VersionedEntityRef legalEntityRef,
        string businessJustification,
        IEnumerable<PurchaseRequestLineRef> lineRefs)
    {
        if (requestId == Guid.Empty || organizationId == Guid.Empty || requesterId == Guid.Empty)
        {
            throw new DomainValidationException("A request version requires its complete identity.");
        }

        if (version < 1)
        {
            throw new DomainValidationException("A request version must be positive.");
        }

        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException(
                "Only the first request version has no predecessor, and every successor has one.");
        }

        var lines = (lineRefs ?? []).ToImmutableArray();
        if (lines.Length == 0)
        {
            throw new DomainValidationException("A request version requires at least one line.");
        }

        if (lines.Length > PurchaseRequestLimits.MaxLines)
        {
            throw new PurchaseRequestPayloadTooLargeException(
                $"A request version supports at most {PurchaseRequestLimits.MaxLines} lines.");
        }

        if (lines.Select(line => line.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() != lines.Length)
        {
            throw new DomainConflictException("A request version cannot repeat a line version.");
        }

        if (lines.Select(line => line.Id).Distinct().Count() != lines.Length)
        {
            throw new DomainConflictException("A request version cannot repeat a line identity.");
        }

        RequestId = requestId;
        Version = version;
        PredecessorVersion = predecessorVersion;
        OrganizationId = organizationId;
        RequesterId = requesterId;
        LegalEntityRef = legalEntityRef ??
            throw new DomainValidationException("A request version requires its legal entity reference.");
        BusinessJustification = PurchaseRequestLimits.RequireSummary(
            businessJustification, "Business justification");
        LineRefs = lines
            .OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public Guid RequestId { get; }
    public int Version { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public Guid RequesterId { get; }
    public VersionedEntityRef LegalEntityRef { get; }
    public string BusinessJustification { get; }
    public IReadOnlyList<PurchaseRequestLineRef> LineRefs { get; }
}

/// <summary>
/// <c>purchase-request-create-command/v1</c>: immutable root plus the first complete line set.
/// </summary>
public sealed record PurchaseRequestCreateCommand(
    Guid OrganizationId,
    Guid RequesterId,
    VersionedEntityRef LegalEntityRef,
    string BusinessJustification,
    string RevisionKey,
    string Reason,
    IEnumerable<PurchaseRequestLineDraft> Lines);

public sealed record PurchaseRequestLineDraft(string ClientLineKey, PurchaseRequestLineContent Content)
{
    public string Key => PurchaseRequestLimits.RequireKey(ClientLineKey, "client_line_key");
}

/// <summary>
/// <c>purchase-request-revision-command/v1</c>: full successor justification and the four exact
/// sets of the delta. Organization, requester, request id and legal entity are never accepted here.
/// </summary>
public sealed record PurchaseRequestRevisionCommand(
    Guid RequestId,
    int ExpectedRequestVersion,
    string BusinessJustification,
    string RevisionKey,
    string Reason,
    IEnumerable<PurchaseRequestLineRef> Retained,
    IEnumerable<PurchaseRequestLineChange> Changed,
    IEnumerable<PurchaseRequestLineDraft> Added,
    IEnumerable<PurchaseRequestLineRef> Removed);

public sealed record PurchaseRequestLineChange(
    Guid LineId,
    int ExpectedVersion,
    string ExpectedContentDigest,
    PurchaseRequestLineContent Content);

/// <summary>
/// Persisted <c>PurchaseRequestRevisionDelta</c> (Datos y contratos): the successor arrays carry
/// previous/replacement references so coverage of both manifests is reproducible.
/// </summary>
public sealed record PurchaseRequestRevisionDelta
{
    public PurchaseRequestRevisionDelta(
        int fromRequestVersion,
        int toRequestVersion,
        IEnumerable<PurchaseRequestLineRef> retained,
        IEnumerable<PurchaseRequestLineReplacement> changed,
        IEnumerable<PurchaseRequestLineAddition> added,
        IEnumerable<PurchaseRequestLineRef> removed)
    {
        if (toRequestVersion != fromRequestVersion + 1)
        {
            throw new DomainValidationException("A revision delta connects consecutive request versions.");
        }

        FromRequestVersion = fromRequestVersion;
        ToRequestVersion = toRequestVersion;
        Retained = Canonical(retained);
        Changed = (changed ?? []).OrderBy(entry => entry.Previous.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        Added = (added ?? []).OrderBy(entry => entry.ClientLineKey, StringComparer.Ordinal).ToImmutableArray();
        Removed = Canonical(removed);

        var previousCoverage = Retained.Select(line => line.CanonicalIdentity)
            .Concat(Changed.Select(entry => entry.Previous.CanonicalIdentity))
            .Concat(Removed.Select(line => line.CanonicalIdentity))
            .ToArray();
        var successorCoverage = Retained.Select(line => line.CanonicalIdentity)
            .Concat(Changed.Select(entry => entry.Replacement.CanonicalIdentity))
            .Concat(Added.Select(entry => entry.Replacement.CanonicalIdentity))
            .ToArray();
        if (previousCoverage.Length != previousCoverage.Distinct(StringComparer.Ordinal).Count() ||
            successorCoverage.Length != successorCoverage.Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("A revision delta cannot repeat a covered line version.");
        }

        var previousIds = Retained.Select(line => line.Id)
            .Concat(Changed.Select(entry => entry.Previous.Id))
            .Concat(Removed.Select(line => line.Id))
            .ToArray();
        var successorIds = Retained.Select(line => line.Id)
            .Concat(Changed.Select(entry => entry.Replacement.Id))
            .Concat(Added.Select(entry => entry.Replacement.Id))
            .ToArray();
        if (previousIds.Length != previousIds.Distinct().Count() ||
            successorIds.Length != successorIds.Distinct().Count())
        {
            throw new DomainConflictException("A revision delta cannot cover a line identity twice.");
        }
    }

    public int FromRequestVersion { get; }
    public int ToRequestVersion { get; }
    public IReadOnlyList<PurchaseRequestLineRef> Retained { get; }
    public IReadOnlyList<PurchaseRequestLineReplacement> Changed { get; }
    public IReadOnlyList<PurchaseRequestLineAddition> Added { get; }
    public IReadOnlyList<PurchaseRequestLineRef> Removed { get; }

    private static ImmutableArray<PurchaseRequestLineRef> Canonical(IEnumerable<PurchaseRequestLineRef>? lines) =>
        (lines ?? []).OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal).ToImmutableArray();
}

public sealed record PurchaseRequestLineReplacement
{
    public PurchaseRequestLineReplacement(PurchaseRequestLineRef previous, PurchaseRequestLineRef replacement)
    {
        Previous = previous ?? throw new DomainValidationException("A changed line requires its previous reference.");
        Replacement = replacement ?? throw new DomainValidationException("A changed line requires its replacement.");
        if (Previous.Id != Replacement.Id)
        {
            throw new DomainValidationException("A changed line keeps its line identity.");
        }

        if (Replacement.Version != Previous.Version + 1)
        {
            throw new DomainValidationException("A changed line advances exactly one line version.");
        }
    }

    public PurchaseRequestLineRef Previous { get; }

    public PurchaseRequestLineRef Replacement { get; }
}

public sealed record PurchaseRequestLineAddition
{
    public PurchaseRequestLineAddition(string clientLineKey, PurchaseRequestLineRef replacement)
    {
        ClientLineKey = PurchaseRequestLimits.RequireKey(clientLineKey, "client_line_key");
        Replacement = replacement ?? throw new DomainValidationException("An added line requires its reference.");
        if (Replacement.Version != 1)
        {
            throw new DomainValidationException("An added line starts at line version 1.");
        }
    }

    public string ClientLineKey { get; }

    public PurchaseRequestLineRef Replacement { get; }
}

/// <summary>Append-only lifecycle record backing the reconstructible aggregate state (NFR-01).</summary>
public sealed record PurchaseRequestLifecycleEvent(
    Guid Id,
    Guid RequestId,
    int RequestVersion,
    string Action,
    PurchaseRequestStatus? BeforeStatus,
    PurchaseRequestStatus AfterStatus,
    Guid? ActorUserId,
    string? WorkloadIssuer,
    string? WorkloadClientId,
    string ReasonCode,
    string Reason,
    string CorrelationReference,
    DateTimeOffset OccurredAt);
