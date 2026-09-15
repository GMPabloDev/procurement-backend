using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>
/// Closed vocabulary, versions, limits and validation of the Budget module (SPEC 08 REQ-01..REQ-10).
/// These are contract identities, not credentials.
/// </summary>
public static class BudgetCodes
{
    public const string PositionKeyContractVersion = "budget-position-key/v1";
    public const string PrerequisiteContractVersion = "budget-check-owner/v1";
    public const string EvidenceContractVersion = "budget-check-evidence/v1";
    public const string PrecheckCommandVersion = "budget-precheck-command/v1";
    public const string PrecheckRequestVersion = "budget-precheck-request/v1";
    public const string PrecheckResponseVersion = "budget-precheck-response/v1";
    public const string TransitionCommandVersion = "budget-transition-command/v1";
    public const string TransitionResponseVersion = "budget-transition-response/v1";
    public const string ReleaseCommandVersion = "budget-release-command/v1";
    public const string DemandBuildVersion = "purchase-request-budget-demand-build/v1";

    public const string BudgetOwnerAdapterId = "budget-check-owner";
    public const string BudgetOwnerAdapterVersion = "v1";
    public const string BudgetOwnerSystemId = "BUDGET_OWNER";
    public const string PurchaseRequestSourceType = "PURCHASE_REQUEST";
    public const string PurchaseOrderSourceType = "PURCHASE_ORDER";
    public const string InvoiceSourceType = "INVOICE";
    public const string SpendCategoryCatalog = "SPEND_CATEGORY";
    public const string PurchaseRequestLineType = "PURCHASE_REQUEST_LINE";
    public const string RequireBudgetCheckType = "REQUIRE_BUDGET_CHECK";

    public const string AuditStreamBudget = "BUDGET";
    public const string AuditStreamApproval = "APPROVAL";
    public const string AuditStreamPurchaseRequest = "PURCHASE_REQUEST";

    public const string ActionAllocated = "BUDGET_ALLOCATED";
    public const string ActionPrechecked = "BUDGET_PRECHECKED";
    public const string ActionReserved = "BUDGET_RESERVED";
    public const string ActionCommitted = "BUDGET_COMMITTED";
    public const string ActionConsumed = "BUDGET_CONSUMED";
    public const string ActionReleased = "BUDGET_RELEASED";
    public const string ActionCompensated = "BUDGET_COMPENSATED";
    public const string ActionInsufficient = "BUDGET_INSUFFICIENT";
    public const string ActionSignalled = "BUDGET_PREREQUISITE_SIGNALLED";

    public const string TargetBudgetPosition = "BudgetPosition";

    /// <summary>Total demands accepted by one precheck, reservation or transition batch (REQ-10).</summary>
    public const int MaxDemandCount = 500;

    public const int MaxReasonScalars = 1_000;
    public const int MaxKeyLength = 128;
    public const int MaxReasonCodeLength = 64;
    public const int MaxCorrelationLength = 120;
    public const int MinFiscalYear = 2_000;
    public const int MaxFiscalYear = 2_100;
    public const int MaxDigestLength = 64;

    /// <summary>Wire version of every posted movement row (REQ-03).</summary>
    public const int MovementVersion = 1;

    /// <summary>Wire version of the parent movement referenced by a child (REQ-09).</summary>
    public const int ParentMovementVersion = 1;

    /// <summary>Decimal precision of every Budget amount (REQ-02).</summary>
    public const int AmountPrecision = 38;
    public const int AmountScale = 12;

    private static readonly Regex KeyPattern =
        new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReasonCodePattern =
        new("^[A-Z][A-Z0-9_.:-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CorrelationPattern =
        new("^[A-Za-z0-9._:-]{1,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Keys are bounded ASCII identifiers, never free text (REQ-10).</summary>
    public static string RequireKey(string? value, string field)
    {
        var normalized = RequireText(value, field);
        if (!KeyPattern.IsMatch(normalized))
        {
            throw new DomainValidationException(
                $"{field} must match [A-Za-z0-9._:-]* with at most {MaxKeyLength} characters.");
        }

        return normalized;
    }

    /// <summary>Workload reason codes are uppercase contract codes, not user text (REQ-09).</summary>
    public static string RequireReasonCode(string? value)
    {
        var normalized = RequireText(value, "Reason code");
        if (!ReasonCodePattern.IsMatch(normalized))
        {
            throw new DomainValidationException(
                $"Reason code must match [A-Z][A-Z0-9_.:-]* with at most {MaxReasonCodeLength} characters.");
        }

        return normalized;
    }

    /// <summary>Motive of an administrative allocation: bounded Unicode scalars (REQ-01).</summary>
    public static string RequireReason(string? value)
    {
        var normalized = RequireText(value, "Reason").Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        if (scalars is < 1 or > MaxReasonScalars)
        {
            throw new DomainValidationException(
                $"Reason must contain between 1 and {MaxReasonScalars} Unicode scalars.");
        }

        return normalized;
    }

    /// <summary>Workload issuer of a service identity: an absolute URI form, bounded at 320 chars.</summary>
    public static string RequireWorkloadIssuer(string? value)
    {
        var normalized = RequireText(value, "Workload issuer");
        if (normalized.Length > 320 || normalized.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException(
                "Workload issuer must be a bounded identity without whitespace.");
        }

        return normalized;
    }

    /// <summary>Workload client id: a bounded token, never free text.</summary>
    public static string RequireWorkloadClientId(string? value)
    {
        var normalized = RequireText(value, "Workload client id");
        if (normalized.Length > 128 || normalized.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException(
                "Workload client id must be a bounded identity without whitespace.");
        }

        return normalized;
    }

    /// <summary>Bounded UTF-8 string used as the name of a target or source type.</summary>
    public static string RequireBoundedToken(string? value, string field)
    {
        var normalized = RequireText(value, field);
        if (normalized.Length > 64 || normalized.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException($"{field} must be a bounded token without whitespace.");
        }

        return normalized;
    }

    public static string RequireCorrelation(string? value)
    {
        var normalized = RequireText(value, "Correlation reference");
        if (!CorrelationPattern.IsMatch(normalized))
        {
            throw new DomainValidationException(
                $"Correlation reference must be a 1–{MaxCorrelationLength} character opaque identifier.");
        }

        return normalized;
    }

    public static string RequireBaseCurrency(string? value)
    {
        var normalized = RequireText(value, "Base currency").ToUpperInvariant();
        if (!CurrencyPattern.IsMatch(normalized))
        {
            throw new DomainValidationException("The base currency must be an ISO 4217 alphabetic code.");
        }

        return normalized;
    }

    public static int RequireFiscalYear(int value)
    {
        if (value is < MinFiscalYear or > MaxFiscalYear)
        {
            throw new DomainValidationException(
                $"The fiscal year must be between {MinFiscalYear} and {MaxFiscalYear}.");
        }

        return value;
    }

    public static Guid RequireIdentity(Guid value, string field) =>
        value == Guid.Empty ? throw new DomainValidationException($"{field} is required.") : value;

    public static string RequireDigest(string? value, string field)
    {
        if (value is null || value.Length != MaxDigestLength || !value.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException($"{field} must be a 64-character SHA-256 value.");
        }

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Amounts are positive, scaled to the contractual precision and never zero: a movement of zero
    /// would be an unobservable no-op (REQ-03).
    /// </summary>
    public static decimal RequirePositiveAmount(decimal value, string field)
    {
        if (value <= 0m)
        {
            throw new DomainValidationException($"{field} must be greater than zero.");
        }

        return RequireAmount(value, field);
    }

    /// <summary>Amounts are non-negative and fit the contractual scale (REQ-01, REQ-02).</summary>
    public static decimal RequireAmount(decimal value, string field)
    {
        if (value < 0m)
        {
            throw new DomainValidationException($"{field} cannot be negative.");
        }

        if (decimal.Round(value, AmountScale, MidpointRounding.ToZero) != value)
        {
            throw new DomainValidationException($"{field} cannot carry more than {AmountScale} decimals.");
        }

        var magnitude = Math.Abs(value);
        if (magnitude >= 1e26m)
        {
            throw new DomainValidationException($"{field} exceeds the supported decimal magnitude.");
        }

        return value;
    }

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim();
}

/// <summary>Position of one budget line (REQ-01, DEC-02): Cost Center + Fiscal Year + Spend Category.</summary>
public sealed record BudgetPositionKey(Guid CostCenterId, int FiscalYear, string SpendCategoryCode)
{
    public Guid CostCenterId { get; } = BudgetCodes.RequireIdentity(CostCenterId, "Cost center");

    public int FiscalYear { get; } = BudgetCodes.RequireFiscalYear(FiscalYear);

    public string SpendCategoryCode { get; } =
        SpendCategoryCode is null
            ? throw new DomainValidationException("Spend Category code is required.")
            : SpendCategoryCode.Trim().ToUpperInvariant();
}

/// <summary>Cost Center reference frozen by the catalog when the position was created or revised.</summary>
public sealed record BudgetCostCenterRef(Guid Id, int Version)
{
    public Guid Id { get; } = BudgetCodes.RequireIdentity(Id, "Cost center reference");

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A cost center reference requires a positive version.");
}

/// <summary>
/// Spend Category reference frozen by the catalog (REQ-01): the public
/// <c>{catalog,code,digest,version}</c> form already published by SPEC 07.
/// </summary>
public sealed record BudgetSpendCategoryRef(string Code, int Version, string Digest)
{
    public string Code { get; } = Code is null
        ? throw new DomainValidationException("Spend Category code is required.")
        : Code.Trim().ToUpperInvariant();

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A spend category reference requires a positive version.");

    public string Digest { get; } = BudgetCodes.RequireDigest(Digest, "Spend category digest");
}

/// <summary>
/// Full position payload of one allocation or demand: the stable identity plus the exact current
/// references that were attested (REQ-01, REQ-05).
/// </summary>
public sealed record BudgetPositionPayload(
    BudgetPositionKey Position,
    BudgetCostCenterRef CostCenter,
    BudgetSpendCategoryRef SpendCategory)
{
    public BudgetPositionKey Position { get; } =
        Position ?? throw new DomainValidationException("A budget position is required.");

    public BudgetCostCenterRef CostCenter { get; } =
        CostCenter ?? throw new DomainValidationException("A cost center reference is required.");

    public BudgetSpendCategoryRef SpendCategory { get; } =
        SpendCategory ?? throw new DomainValidationException("A spend category reference is required.");

    /// <summary>Builds the payload from the attested current references of one demand.</summary>
    public static BudgetPositionPayload For(
        Guid costCenterId,
        int fiscalYear,
        BudgetCostCenterRef costCenter,
        BudgetSpendCategoryRef spendCategory) =>
        new(new BudgetPositionKey(costCenterId, fiscalYear, spendCategory.Code), costCenter, spendCategory);
}

/// <summary>Delegated body of one movement transition.</summary>
public sealed record BudgetActor(string Type, Guid? UserId, string? WorkloadIssuer, string? WorkloadClientId, string? SystemId)
{
    public const string UserType = "USER";
    public const string WorkloadType = "WORKLOAD";
    public const string SystemType = "SYSTEM";

    /// <summary>Administrative allocation actor: exactly one USER variant (REQ-01).</summary>
    public static BudgetActor ForUser(Guid userId) =>
        new(UserType, BudgetCodes.RequireIdentity(userId, "Actor"), null, null, null);

    /// <summary>Workload actor of a transition or signal (REQ-09).</summary>
    public static BudgetActor ForWorkload(string issuer, string clientId) =>
        new(
            WorkloadType,
            null,
            BudgetCodes.RequireWorkloadIssuer(issuer),
            BudgetCodes.RequireWorkloadClientId(clientId),
            null);

    /// <summary>Automatic actor of the Budget owner (REQ-10).</summary>
    public static BudgetActor ForSystem(string systemId) =>
        new(SystemType, null, null, null, BudgetCodes.RequireBoundedToken(systemId, "System id"));

    public string Type { get; } = Validate(Type, UserId, WorkloadIssuer, WorkloadClientId, SystemId);

    private static string Validate(
        string? type,
        Guid? userId,
        string? workloadIssuer,
        string? workloadClientId,
        string? systemId)
    {
        var complete = type switch
        {
            UserType => userId is not null && workloadIssuer is null && workloadClientId is null && systemId is null,
            WorkloadType => userId is null && workloadIssuer is not null && workloadClientId is not null && systemId is null,
            SystemType => userId is null && workloadIssuer is null && workloadClientId is null && systemId is not null,
            _ => false
        };
        return complete
            ? type!
            : throw new DomainValidationException(
                "A budget actor must complete exactly one USER, WORKLOAD or SYSTEM variant.");
    }
}

/// <summary>Typed cause of one audit record (REQ-10); the destination must exist and never mutate.</summary>
public sealed record BudgetCause(string AuditStream, Guid AuditId)
{
    public string AuditStream { get; } = AuditStream is BudgetCodes.AuditStreamBudget
        or BudgetCodes.AuditStreamApproval
        or BudgetCodes.AuditStreamPurchaseRequest
        ? AuditStream
        : throw new DomainValidationException("The audit stream of a budget cause is not recognized.");

    public Guid AuditId { get; } = BudgetCodes.RequireIdentity(AuditId, "Cause audit id");
}

/// <summary>Typed source of one operation (REQ-03, REQ-09).</summary>
public sealed record BudgetSource(string Type, Guid Id, int Version, string Digest)
{
    public string Type { get; } = BudgetCodes.RequireBoundedToken(Type, "Source type");

    public Guid Id { get; } = BudgetCodes.RequireIdentity(Id, "Source id");

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A budget source requires a positive version.");

    public string Digest { get; } = BudgetCodes.RequireDigest(Digest, "Source digest");
}

/// <summary>Full Approval target used by Budget movements and evidence (SPEC 03 target schema).</summary>
public sealed record BudgetTarget(Guid Id, int Version, string MaterialSnapshotDigest, string Type)
{
    public Guid Id { get; } = BudgetCodes.RequireIdentity(Id, "Target id");

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A budget target requires a positive version.");

    public string MaterialSnapshotDigest { get; } =
        BudgetCodes.RequireDigest(MaterialSnapshotDigest, "Target material snapshot digest");

    public string Type { get; } = BudgetCodes.RequireBoundedToken(Type, "Target type");
}

/// <summary>
/// One server-built demand of a budget check (REQ-05): the confirmed amount of a single Purchase
/// Request line together with the attested position and the material Approval target.
/// </summary>
public sealed record BudgetDemand(
    decimal AmountBase,
    BudgetPositionPayload Payload,
    Guid SourceLineId,
    int SourceLineVersion,
    string SourceLineContentDigest,
    BudgetTarget? Target)
{
    public decimal AmountBase { get; } = BudgetCodes.RequirePositiveAmount(AmountBase, "amount_base");

    public BudgetPositionPayload Payload { get; } =
        Payload ?? throw new DomainValidationException("A demand requires its budget position.");

    public Guid SourceLineId { get; } = BudgetCodes.RequireIdentity(SourceLineId, "Source line id");

    public int SourceLineVersion { get; } = SourceLineVersion >= 1
        ? SourceLineVersion
        : throw new DomainValidationException("A demand requires a positive source line version.");

    public string SourceLineContentDigest { get; } =
        BudgetCodes.RequireDigest(SourceLineContentDigest, "Source line content digest");
}

/// <summary>One movement of a transition command before it is posted (REQ-09).</summary>
public sealed record BudgetMovementCommandItem(decimal Amount, Guid? ParentMovementId, BudgetTarget? Target)
{
    public decimal Amount { get; } = BudgetCodes.RequirePositiveAmount(Amount, "Movement amount");

    public Guid? ParentMovementId { get; } =
        ParentMovementId == Guid.Empty
            ? throw new DomainValidationException("A parent movement id cannot be empty.")
            : ParentMovementId;
}

/// <summary>Event that triggered a durable release (REQ-08); null for a Purchase Request cancel.</summary>
public sealed record BudgetTriggerEvent(string ContractVersion, Guid EventId)
{
    public string ContractVersion { get; } = BudgetCodes.RequireKey(ContractVersion, "Trigger contract version");

    public Guid EventId { get; } = BudgetCodes.RequireIdentity(EventId, "Trigger event id");
}

/// <summary>One posted movement as it appears inside the reproducible evidence (REQ-10).</summary>
public sealed record BudgetEvidenceMovement(
    Guid Id,
    BudgetMovementType Type,
    decimal Amount,
    string PositionKeyDigest,
    BudgetTarget? Target)
{
    public Guid Id { get; } = BudgetCodes.RequireIdentity(Id, "Movement id");

    public decimal Amount { get; } = BudgetCodes.RequirePositiveAmount(Amount, "Movement amount");

    public string PositionKeyDigest { get; } =
        BudgetCodes.RequireDigest(PositionKeyDigest, "position_key_digest");
}
