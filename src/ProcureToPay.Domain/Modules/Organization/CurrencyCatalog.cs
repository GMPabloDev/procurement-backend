using System.Globalization;

namespace ProcureToPay.Domain.Modules.Organization;

public static class CurrencyCatalog
{
    private static readonly Lazy<IReadOnlySet<string>> Symbols = new(BuildSymbols);

    public static bool IsIso4217(string value) => Symbols.Value.Contains(value);

    private static IReadOnlySet<string> BuildSymbols()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                symbols.Add(new RegionInfo(culture.Name).ISOCurrencySymbol);
            }
            catch (CultureNotFoundException)
            {
                // Ignore cultures without region currency metadata.
            }
        }
        return symbols;
    }
}
