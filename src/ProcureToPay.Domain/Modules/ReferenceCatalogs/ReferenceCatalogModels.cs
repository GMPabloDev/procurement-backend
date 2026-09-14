using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.ReferenceCatalogs;

/// <summary>
/// Closed contract vocabulary of the reference catalogs (SPEC 07 Datos y contratos): catalog ids,
/// owner identities, contract versions and limits. These are contract identities, not credentials.
/// </summary>
public static class ReferenceCatalogCodes
{
    public const string CostCenterCatalog = "COST_CENTER";
    public const string SpendCategoryCatalog = "SPEND_CATEGORY";
    public const string CostCenterReferenceVersion = "cost-center-reference/v1";
    public const string SpendCategoryReferenceVersion = "spend-category-reference/v1";
    public const string SpendCategoryVersion = "spend-category/v1";
    public const string CostCenterOwnerId = "cost-center-domain";
    public const string CostCenterOwnerContractVersion = "cost-center-db/v1";
    public const string SpendCategoryOwnerId = "spend-category-domain";
    public const string SpendCategoryOwnerContractVersion = "spend-category-db/v1";

    public const string ActionCostCenterCreated = "COST_CENTER_CREATED";
    public const string ActionCostCenterUpdated = "COST_CENTER_UPDATED";
    public const string ActionSpendCategoryCreated = "SPEND_CATEGORY_CREATED";
    public const string ActionSpendCategoryUpdated = "SPEND_CATEGORY_UPDATED";
    public const string TargetCostCenter = "CostCenter";
    public const string TargetSpendCategory = "SpendCategory";

    public const int MaxCodeLength = 64;
    public const int MaxNameScalars = 200;
    public const int MaxReasonScalars = 1_000;
    public const int MaxDigestLength = 64;

    private static readonly Regex CodePattern =
        new("^[A-Z][A-Z0-9_.:-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Catalog codes are published uppercase and match the contractual alphabet exactly.</summary>
    public static string RequireCode(string? value, string field)
    {
        var normalized = Normalize(value, field).ToUpperInvariant();
        if (!CodePattern.IsMatch(normalized))
        {
            throw new DomainValidationException(
                $"{field} must match [A-Z][A-Z0-9_.:-]* with at most {MaxCodeLength} characters.");
        }

        return normalized;
    }

    /// <summary>Names are NFC-normalized and bounded by Unicode scalars, not bytes.</summary>
    public static string RequireName(string? value, string field)
    {
        var normalized = Normalize(value, field).Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        if (scalars is < 1 or > MaxNameScalars)
        {
            throw new DomainValidationException(
                $"{field} must contain between 1 and {MaxNameScalars} Unicode scalars.");
        }

        return normalized;
    }

    public static string RequireReason(string? value)
    {
        var normalized = Normalize(value, "Reason").Normalize(NormalizationForm.FormC);
        var scalars = new StringInfo(normalized).LengthInTextElements;
        if (scalars is < 1 or > MaxReasonScalars)
        {
            throw new DomainValidationException(
                $"Reason must contain between 1 and {MaxReasonScalars} Unicode scalars.");
        }

        return normalized;
    }

    public static string RequireDigest(string? value)
    {
        if (value is null || value.Length != MaxDigestLength || !value.All(Uri.IsHexDigit))
        {
            throw new DomainValidationException("A catalog digest must be a 64-character SHA-256 value.");
        }

        return value.ToLowerInvariant();
    }

    private static string Normalize(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{field} is required.")
            : value.Trim();
}

/// <summary>Durable reference of one active Cost Center (REQ-02, <c>cost-center-reference/v1</c>).</summary>
public sealed record CostCenterReference(
    Guid Id,
    string Code,
    string Name,
    int Version,
    Guid DepartmentId,
    int DepartmentVersion)
{
    public Guid Id { get; } = Id == Guid.Empty
        ? throw new DomainValidationException("A cost center reference requires its identity.")
        : Id;

    public string Code { get; } = ReferenceCatalogCodes.RequireCode(Code, "CostCenter code");

    public string Name { get; } = ReferenceCatalogCodes.RequireName(Name, "CostCenter name");

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A cost center reference requires a positive version.");

    public Guid DepartmentId { get; } = DepartmentId == Guid.Empty
        ? throw new DomainValidationException("A cost center reference requires its owning department.")
        : DepartmentId;

    public int DepartmentVersion { get; } = DepartmentVersion >= 1
        ? DepartmentVersion
        : throw new DomainValidationException("A cost center reference requires the department version.");
}

/// <summary>Durable reference of one active Spend Category (REQ-03, <c>spend-category-reference/v1</c>).</summary>
public sealed record SpendCategoryReference(string Code, string Name, int Version, string Digest)
{
    public string Code { get; } = ReferenceCatalogCodes.RequireCode(Code, "SpendCategory code");

    public string Name { get; } = ReferenceCatalogCodes.RequireName(Name, "SpendCategory name");

    public int Version { get; } = Version >= 1
        ? Version
        : throw new DomainValidationException("A spend category reference requires a positive version.");

    public string Digest { get; } = ReferenceCatalogCodes.RequireDigest(Digest);
}

/// <summary>Administrative history entry of one Cost Center version (REQ-01, REQ-08).</summary>
public sealed record CostCenterVersionView(
    int Version,
    string Name,
    Guid DepartmentId,
    string Status,
    int? PredecessorVersion,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Reason);

/// <summary>Administrative history entry of one Spend Category version (REQ-01, REQ-08).</summary>
public sealed record SpendCategoryVersionView(
    int Version,
    string Code,
    string Name,
    string Digest,
    string Status,
    int? PredecessorVersion,
    DateTimeOffset OccurredAt,
    Guid ActorUserId,
    string Reason);

/// <summary>Deterministic digests of the reference catalogs (REQ-03, Canonicalización).</summary>
public static class ReferenceCatalogCanonicalizer
{
    /// <summary>
    /// <c>spend_category_digest</c>: SHA-256 over the exact <c>spend-category/v1</c> preimage under
    /// <c>policy-canonical-json/v1</c>. State, actor, reason and timestamps never enter.
    /// </summary>
    public static string SpendCategoryDigest(string code, string name, Guid organizationId, int version)
    {
        var normalizedCode = ReferenceCatalogCodes.RequireCode(code, "SpendCategory code");
        var normalizedName = ReferenceCatalogCodes.RequireName(name, "SpendCategory name");
        if (organizationId == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("A spend category digest requires its organization and version.");
        }

        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonicalization_version"] = Policy.PolicyCanonicalizer.Version,
            ["code"] = normalizedCode,
            ["contract_version"] = ReferenceCatalogCodes.SpendCategoryVersion,
            ["name"] = normalizedName,
            ["organization_id"] = organizationId.ToString("D"),
            ["version"] = version
        };
        return Policy.PolicyCanonicalizer.Hash(Policy.PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>Reproduces the digest of a stored version so corruption is detectable.</summary>
    public static bool MatchesSpendCategoryDigest(
        string code,
        string name,
        Guid organizationId,
        int version,
        string digest) =>
        string.Equals(
            SpendCategoryDigest(code, name, organizationId, version),
            digest,
            StringComparison.Ordinal);
}
