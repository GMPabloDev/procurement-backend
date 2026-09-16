using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>
/// Server-side classification of one governed change (SPEC 09 REQ-02). The caller never declares a
/// change as non-sensitive: the classification is derived from the persisted predecessor and the
/// candidate content, so disguising a governed field as a cosmetic edit is impossible.
/// </summary>
public static class SupplierChangeClassifier
{
    public const string LegalName = "legal_name";
    public const string CountryCode = "country_code";
    public const string TaxId = "tax_id";
    public const string PaymentTerms = "payment_terms";
    public const string SupportedCurrencies = "supported_currencies";
    public const string CategoriesSupplied = "categories_supplied";
    public const string RiskStatus = "risk_status";
    public const string PerformanceScore = "performance_score";
    public const string Status = "status";
    public const string BankingDetails = "banking_details";
    public const string TradeName = "trade_name";
    public const string Addresses = "addresses";
    public const string Contacts = "contacts";

    private static readonly string[] SensitiveOrder =
    [
        LegalName, CountryCode, TaxId, PaymentTerms, SupportedCurrencies, CategoriesSupplied,
        RiskStatus, PerformanceScore, Status, BankingDetails
    ];

    /// <summary>
    /// Sensitive fields whose value differs between the predecessor and the candidate. A creation has
    /// no predecessor, so every governed field of the new supplier counts as sensitive.
    /// </summary>
    public static IReadOnlySet<string> SensitiveChanges(
        SupplierVersionContent? previous,
        SupplierVersionContent candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (previous is null)
        {
            changed.UnionWith(SensitiveOrder);
            return changed;
        }

        if (!string.Equals(previous.LegalName, candidate.LegalName, StringComparison.Ordinal))
        {
            changed.Add(LegalName);
        }

        if (!string.Equals(previous.CountryCode, candidate.CountryCode, StringComparison.Ordinal))
        {
            changed.Add(CountryCode);
        }

        if (!string.Equals(previous.TaxId, candidate.TaxId, StringComparison.Ordinal))
        {
            changed.Add(TaxId);
        }

        if (previous.PaymentTerms != candidate.PaymentTerms)
        {
            changed.Add(PaymentTerms);
        }

        if (!previous.SupportedCurrencies.SequenceEqual(candidate.SupportedCurrencies, StringComparer.Ordinal))
        {
            changed.Add(SupportedCurrencies);
        }

        if (!previous.CategoriesSupplied
                .Select(reference => $"{reference.Catalog}|{reference.Code}|{reference.Version}|{reference.Digest}")
                .SequenceEqual(
                    candidate.CategoriesSupplied.Select(reference =>
                        $"{reference.Catalog}|{reference.Code}|{reference.Version}|{reference.Digest}"),
                    StringComparer.Ordinal))
        {
            changed.Add(CategoriesSupplied);
        }

        if (previous.RiskStatus != candidate.RiskStatus)
        {
            changed.Add(RiskStatus);
        }

        if (previous.PerformanceScore != candidate.PerformanceScore)
        {
            changed.Add(PerformanceScore);
        }

        if (previous.Status != candidate.Status)
        {
            changed.Add(Status);
        }

        if (!previous.BankingRefs
                .Select(reference => $"{reference.Id:D}|{reference.Version}")
                .SequenceEqual(
                    candidate.BankingRefs.Select(reference => $"{reference.Id:D}|{reference.Version}"),
                    StringComparer.Ordinal))
        {
            changed.Add(BankingDetails);
        }

        return changed;
    }

    /// <summary>Names of the fields that changed at all, in the deterministic published order.</summary>
    public static IReadOnlyList<string> ChangedFields(
        SupplierVersionContent? previous,
        SupplierVersionContent candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var changed = new List<string>();
        if (previous is null)
        {
            return [.. SensitiveOrder, TradeName, Addresses, Contacts];
        }

        if (!string.Equals(previous.LegalName, candidate.LegalName, StringComparison.Ordinal)) changed.Add(LegalName);
        if (!string.Equals(previous.CountryCode, candidate.CountryCode, StringComparison.Ordinal)) changed.Add(CountryCode);
        if (!string.Equals(previous.TaxId, candidate.TaxId, StringComparison.Ordinal)) changed.Add(TaxId);
        if (previous.PaymentTerms != candidate.PaymentTerms) changed.Add(PaymentTerms);
        if (!previous.SupportedCurrencies.SequenceEqual(candidate.SupportedCurrencies, StringComparer.Ordinal))
            changed.Add(SupportedCurrencies);
        if (previous.CategoriesSupplied.Count != candidate.CategoriesSupplied.Count ||
            !previous.CategoriesSupplied.Select(reference => reference.Code)
                .SequenceEqual(candidate.CategoriesSupplied.Select(reference => reference.Code), StringComparer.Ordinal))
            changed.Add(CategoriesSupplied);
        if (previous.RiskStatus != candidate.RiskStatus) changed.Add(RiskStatus);
        if (previous.PerformanceScore != candidate.PerformanceScore) changed.Add(PerformanceScore);
        if (previous.Status != candidate.Status) changed.Add(Status);
        if (!previous.BankingRefs.Select(reference => $"{reference.Id:D}|{reference.Version}")
                .SequenceEqual(candidate.BankingRefs.Select(reference => $"{reference.Id:D}|{reference.Version}"),
                    StringComparer.Ordinal))
            changed.Add(BankingDetails);
        if (!string.Equals(previous.TradeName, candidate.TradeName, StringComparison.Ordinal)) changed.Add(TradeName);
        if (!AddressesEqual(previous.Addresses, candidate.Addresses)) changed.Add(Addresses);
        if (!ContactsEqual(previous.Contacts, candidate.Contacts)) changed.Add(Contacts);
        return changed;
    }

    /// <summary>
    /// Legal transitions of the operational cycle (REQ-03). A supplier without an approved version can
    /// only be activated; a suspended or blocked supplier recovers through a new activation.
    /// </summary>
    public static bool IsAllowedTransition(SupplierOperationalStatus? current, SupplierOperationalStatus requested)
    {
        if (requested == SupplierOperationalStatus.Draft || requested == SupplierOperationalStatus.PendingApproval)
        {
            return false;
        }

        return current switch
        {
            null => requested == SupplierOperationalStatus.Active,
            SupplierOperationalStatus.Active => true,
            SupplierOperationalStatus.Suspended or SupplierOperationalStatus.Blocked =>
                requested == SupplierOperationalStatus.Active,
            _ => false
        };
    }

    /// <summary>Requested status of one submitted candidate; an open proposal always targets a final state.</summary>
    public static string Code(SupplierOperationalStatus status) => SupplierStatusCodes.Code(status);

    private static bool AddressesEqual(IReadOnlyList<SupplierAddress> left, IReadOnlyList<SupplierAddress> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First == pair.Second);

    private static bool ContactsEqual(IReadOnlyList<SupplierContact> left, IReadOnlyList<SupplierContact> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First == pair.Second);
}
