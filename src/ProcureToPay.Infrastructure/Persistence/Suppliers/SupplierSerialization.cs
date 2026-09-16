using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// Exact JSON shapes of the Supplier persistence rows. Readers reject unknown properties so a
/// corrupted or tampered row can never be reinterpreted as valid content (SPEC 09 NFR-01).
/// </summary>
public static class SupplierSerialization
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Addresses(IEnumerable<SupplierAddress> addresses) =>
        new JsonArray((addresses ?? []).Select(address => (JsonNode)new JsonObject
        {
            ["city"] = address.City,
            ["country_code"] = address.CountryCode,
            ["id"] = address.Id.ToString("D"),
            ["label"] = address.Label,
            ["line1"] = address.Line1,
            ["line2"] = address.Line2,
            ["postal_code"] = address.PostalCode,
            ["region"] = address.Region
        }).ToArray()).ToJsonString(Options);

    public static string Contacts(IEnumerable<SupplierContact> contacts) =>
        new JsonArray((contacts ?? []).Select(contact => (JsonNode)new JsonObject
        {
            ["email"] = contact.Email,
            ["id"] = contact.Id.ToString("D"),
            ["job_title"] = contact.JobTitle,
            ["name"] = contact.Name,
            ["phone"] = contact.Phone
        }).ToArray()).ToJsonString(Options);

    public static string PaymentTerms(SupplierPaymentTerms terms) => new JsonObject
    {
        ["code"] = terms.Code,
        ["net_days"] = terms.NetDays
    }.ToJsonString(Options);

    public static string Currencies(IEnumerable<string> currencies) =>
        new JsonArray((currencies ?? []).Select(currency => (JsonNode)JsonValue.Create(currency)).ToArray())
            .ToJsonString(Options);

    public static string Categories(IEnumerable<VersionedCodeRef> categories) =>
        new JsonArray((categories ?? []).Select(reference => (JsonNode)new JsonObject
        {
            ["catalog"] = reference.Catalog,
            ["code"] = reference.Code,
            ["digest"] = reference.Digest,
            ["version"] = reference.Version
        }).ToArray()).ToJsonString(Options);

    public static string Performance(SupplierPerformanceScore? score) => score is null
        ? "null"
        : new JsonObject
        {
            ["measured_at"] = SupplierCodes.FormatUtc(score.MeasuredAt),
            ["score"] = Decimal(score.Score),
            ["source"] = score.Source
        }.ToJsonString(Options);

    public static string BankingRefs(IEnumerable<SupplierBankingRef> references) =>
        new JsonArray((references ?? []).Select(reference => (JsonNode)new JsonObject
        {
            ["id"] = reference.Id.ToString("D"),
            ["version"] = reference.Version
        }).ToArray()).ToJsonString(Options);

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

    public static SupplierVersionContent ReadContent(SupplierVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return new SupplierVersionContent(
                record.LegalName,
                record.TradeName,
                record.CountryCode,
                record.TaxId,
                ReadAddresses(record.AddressesJson),
                ReadContacts(record.ContactsJson),
                ReadPaymentTerms(record.PaymentTermsJson),
                ReadCurrencies(record.SupportedCurrenciesJson),
                ReadCategories(record.CategoriesSuppliedJson),
                (SupplierOperationalStatus)record.Status,
                (SupplierRiskStatus)record.RiskStatus,
                ReadPerformance(record.PerformanceJson),
                ReadBankingRefs(record.BankingRefsJson));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                              InvalidOperationException or FormatException or
                                              ArgumentException)
        {
            throw new SupplierDependencyUnavailableException("A stored supplier version is corrupted.");
        }
    }

    public static SupplierVersionView ReadView(SupplierVersionRecord record) => new(
        record.SupplierId,
        record.Version,
        ReadContent(record),
        record.ContentDigest,
        record.PredecessorVersion,
        record.ActorUserId,
        record.OccurredAt,
        record.Reason);

    public static IReadOnlyList<SupplierAddress> ReadAddresses(string json)
    {
        var array = Root(json).AsArray();
        var addresses = new List<SupplierAddress>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["city", "country_code", "id", "label", "line1", "line2", "postal_code", "region"]);
            addresses.Add(new SupplierAddress(
                Guid.Parse(item["id"]!.GetValue<string>()),
                item["label"]!.GetValue<string>(),
                item["line1"]!.GetValue<string>(),
                Optional(item["line2"]),
                item["city"]!.GetValue<string>(),
                Optional(item["region"]),
                Optional(item["postal_code"]),
                item["country_code"]!.GetValue<string>()));
        }

        return addresses;
    }

    public static IReadOnlyList<SupplierContact> ReadContacts(string json)
    {
        var array = Root(json).AsArray();
        var contacts = new List<SupplierContact>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["email", "id", "job_title", "name", "phone"]);
            contacts.Add(new SupplierContact(
                Guid.Parse(item["id"]!.GetValue<string>()),
                item["name"]!.GetValue<string>(),
                Optional(item["email"]),
                Optional(item["phone"]),
                Optional(item["job_title"])));
        }

        return contacts;
    }

    public static SupplierPaymentTerms ReadPaymentTerms(string json)
    {
        if (string.Equals(json, "null", StringComparison.Ordinal))
        {
            throw new JsonException("Payment terms are required.");
        }

        var item = RequireObject(JsonNode.Parse(json), ["code", "net_days"]);
        return new SupplierPaymentTerms(
            item["code"]!.GetValue<string>(),
            item["net_days"]!.GetValue<int>());
    }

    public static IReadOnlyList<string> ReadCurrencies(string json)
    {
        var array = Root(json).AsArray();
        return [.. array.Select(node => node!.GetValue<string>())];
    }

    public static IReadOnlyList<VersionedCodeRef> ReadCategories(string json)
    {
        var array = Root(json).AsArray();
        var categories = new List<VersionedCodeRef>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["catalog", "code", "digest", "version"]);
            categories.Add(new VersionedCodeRef(
                item["catalog"]!.GetValue<string>(),
                item["code"]!.GetValue<string>(),
                item["version"]!.GetValue<int>(),
                item["digest"]!.GetValue<string>()));
        }

        return categories;
    }

    public static SupplierPerformanceScore? ReadPerformance(string json)
    {
        if (string.Equals(json, "null", StringComparison.Ordinal))
        {
            return null;
        }

        var item = RequireObject(JsonNode.Parse(json), ["measured_at", "score", "source"]);
        return new SupplierPerformanceScore(
            decimal.Parse(item["score"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            item["source"]!.GetValue<string>(),
            DateTimeOffset.Parse(
                item["measured_at"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
    }

    public static IReadOnlyList<SupplierBankingRef> ReadBankingRefs(string json)
    {
        var array = Root(json).AsArray();
        var references = new List<SupplierBankingRef>(array.Count);
        foreach (var node in array)
        {
            var item = RequireObject(node, ["id", "version"]);
            references.Add(new SupplierBankingRef(
                Guid.Parse(item["id"]!.GetValue<string>()),
                item["version"]!.GetValue<int>()));
        }

        return references;
    }

    public static VersionedEntityRef? ReadEntityRef(string? json) =>
        string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal)
            ? null
            : ReadEntityRefNode(JsonNode.Parse(json));

    public static VersionedEntityRef ReadRequiredEntityRef(string json) =>
        ReadEntityRefNode(JsonNode.Parse(json));

    public static VersionedCodeRef ReadCodeRef(string json)
    {
        var item = RequireObject(JsonNode.Parse(json), ["catalog", "code", "digest", "version"]);
        return new VersionedCodeRef(
            item["catalog"]!.GetValue<string>(),
            item["code"]!.GetValue<string>(),
            item["version"]!.GetValue<int>(),
            item["digest"]!.GetValue<string>());
    }

    public static string Decimal(decimal value) =>
        value.ToString("0.##############################", CultureInfo.InvariantCulture);

    private static VersionedEntityRef ReadEntityRefNode(JsonNode? node)
    {
        var item = RequireObject(node, ["entity_type", "id", "version"]);
        return new VersionedEntityRef(
            item["entity_type"]!.GetValue<string>(),
            Guid.Parse(item["id"]!.GetValue<string>()),
            item["version"]!.GetValue<int>());
    }

    private static JsonNode Root(string json) =>
        JsonNode.Parse(json) ?? throw new JsonException("A stored supplier row is empty.");

    private static string? Optional(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null ? null : node.GetValue<string>();

    private static JsonObject RequireObject(JsonNode? node, string[] expected)
    {
        if (node is not JsonObject value)
        {
            throw new JsonException("The stored supplier document is not an object.");
        }

        var names = value.Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new JsonException("The stored supplier document has an unexpected property set.");
        }

        return value;
    }
}
