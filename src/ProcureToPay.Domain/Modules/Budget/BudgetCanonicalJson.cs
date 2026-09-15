using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Domain.Modules.Budget;

/// <summary>
/// Canonicalization facade of the Budget module (SPEC 08 Canonicalización y evidencia). Every
/// Budget preimage uses <c>policy-canonical-json/v1</c>: UTF-8 without BOM or whitespace, known
/// properties always present and ordered ordinal, strings normalized to NFC, UUID lowercase D,
/// UTC timestamps with seven decimals, invariant decimals and sets sorted by canonical bytes with
/// duplicates rejected.
///
/// The writer itself is the published canonical serializer already used by the Policy contracts:
/// reimplementing identical byte rules in a second type would create two sources of truth for the
/// same canonical form. This facade pins the version string and the shared entry points so a Budget
/// preimage cannot silently switch to another canonicalization.
/// </summary>
public static class BudgetCanonicalJson
{
    /// <summary>The canonicalization version fixed for every Budget preimage.</summary>
    public const string CanonicalizationVersion = "policy-canonical-json/v1";

    public static string Serialize(CanonicalValue value) => ApprovalCanonicalJson.Serialize(value);

    public static string Digest(CanonicalValue value) => ApprovalCanonicalJson.Digest(value);

    public static string FormatDecimal(decimal value) => ApprovalCanonicalJson.FormatDecimal(value);
}
