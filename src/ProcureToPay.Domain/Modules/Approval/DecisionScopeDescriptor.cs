using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Canonical decision scope object <c>decision-scope/v1</c> (SPEC 03 REQ-02): exact
/// properties <c>{schema_version, organization_id, scopes}</c> where every scope entry is
/// <c>{dimension, reference_id, reference_version}</c>.
/// </summary>
[JsonConverter(typeof(DecisionScopeDescriptorJsonConverter))]
public sealed class DecisionScopeDescriptor
{
    public const string SchemaVersion = "decision-scope/v1";

    private readonly ImmutableArray<DecisionScopeEntry> scopes;

    private DecisionScopeDescriptor(Guid organizationId, ImmutableArray<DecisionScopeEntry> scopes)
    {
        OrganizationId = organizationId;
        this.scopes = scopes;
    }

    public Guid OrganizationId { get; }

    public IReadOnlyList<DecisionScopeEntry> Scopes => scopes;

    public static DecisionScopeDescriptor Create(Guid organizationId, IEnumerable<DecisionScopeEntry> scopes)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A decision scope needs an organization.");
        }

        var materialized = (scopes ?? throw new DomainValidationException("Decision scopes are required."))
            .ToImmutableArray();
        ValidateEntries(materialized);
        return new DecisionScopeDescriptor(organizationId, materialized);
    }

    /// <summary>Strict parse: the stored string must be exactly the canonical representation.</summary>
    public static DecisionScopeDescriptor Parse(string? json, Guid? expectedOrganizationId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException("A decision scope descriptor is required.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DomainValidationException("The decision scope descriptor is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DomainValidationException("The decision scope descriptor must be an object.");
            }

            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            var expected = new[] { "organization_id", "schema_version", "scopes" };
            if (properties.Length != expected.Length ||
                !properties.OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new DomainValidationException(
                    "The decision scope descriptor must declare exactly organization_id, schema_version and scopes.");
            }

            if (root.GetProperty("schema_version").GetString() != SchemaVersion)
            {
                throw new DomainValidationException($"The decision scope schema must be '{SchemaVersion}'.");
            }

            if (!Guid.TryParse(root.GetProperty("organization_id").GetString(), out var organizationId) ||
                organizationId == Guid.Empty)
            {
                throw new DomainValidationException("The decision scope organization must be a UUID.");
            }

            if (expectedOrganizationId is not null && expectedOrganizationId != organizationId)
            {
                throw new DomainValidationException("The decision scope organization does not match the case.");
            }

            var scopes = ImmutableArray.CreateBuilder<DecisionScopeEntry>();
            var entries = root.GetProperty("scopes");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            {
                throw new DomainValidationException("A decision scope requires at least one scope entry.");
            }

            foreach (var entry in entries.EnumerateArray())
            {
                scopes.Add(ParseEntry(entry));
            }

            var descriptor = Create(organizationId, scopes.ToImmutable());
            if (!string.Equals(descriptor.ToCanonicalJson(), json, StringComparison.Ordinal))
            {
                throw new DomainValidationException("The decision scope descriptor is not canonical JSON.");
            }

            return descriptor;
        }
    }

    public string ToCanonicalJson() => ApprovalCanonicalJson.Serialize(ToCanonicalValue());

    public CanonicalValue ToCanonicalValue() => ApprovalCanonicalJson.Object(
        ("organization_id", ApprovalCanonicalJson.String(OrganizationId)),
        ("schema_version", ApprovalCanonicalJson.String(SchemaVersion)),
        ("scopes", ApprovalCanonicalJson.Array(scopes.Select(ToCanonicalValue))));

    public string Digest() => ApprovalCanonicalJson.Digest(ToCanonicalValue());

    /// <summary>
    /// Maps every scope entry to an <see cref="AuthorizationScopeSet"/> entry once the catalog
    /// resolved the reference identifier to the stable reference string used by SPEC 01.
    /// </summary>
    public AuthorizationScopeSet ToAuthorizationScopeSet(
        Func<DecisionScopeEntry, string> referenceResolver)
    {
        ArgumentNullException.ThrowIfNull(referenceResolver);
        return AuthorizationScopeSet.Create(scopes.Select(entry => entry.Dimension == ScopeDimension.Organization
            ? AuthorizationScope.Global()
            : AuthorizationScope.For(entry.Dimension, referenceResolver(entry))));
    }

    private static CanonicalValue ToCanonicalValue(DecisionScopeEntry entry) => ApprovalCanonicalJson.Object(
        ("dimension", ApprovalCanonicalJson.String(OrganizationContractCodes.Scope(entry.Dimension))),
        ("reference_id", ApprovalCanonicalJson.StringOrNull(entry.ReferenceId)),
        ("reference_version", ApprovalCanonicalJson.NumberOrNull(entry.ReferenceVersion)));

    private static DecisionScopeEntry ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DomainValidationException("Every decision scope entry must be an object.");
        }

        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        var expected = new[] { "dimension", "reference_id", "reference_version" };
        if (names.Length != expected.Length ||
            !names.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new DomainValidationException(
                "Every decision scope entry must declare exactly dimension, reference_id and reference_version.");
        }

        if (!OrganizationContractCodes.TryScope(element.GetProperty("dimension").GetString(), out var dimension))
        {
            throw new DomainValidationException("The decision scope dimension is invalid.");
        }

        Guid? referenceId = null;
        var referenceIdElement = element.GetProperty("reference_id");
        if (referenceIdElement.ValueKind == JsonValueKind.String)
        {
            if (!Guid.TryParse(referenceIdElement.GetString(), out var parsedId))
            {
                throw new DomainValidationException("A decision scope reference must be a UUID.");
            }

            referenceId = parsedId;
        }
        else if (referenceIdElement.ValueKind != JsonValueKind.Null)
        {
            throw new DomainValidationException("A decision scope reference must be a UUID or null.");
        }

        int? referenceVersion = null;
        var referenceVersionElement = element.GetProperty("reference_version");
        if (referenceVersionElement.ValueKind == JsonValueKind.Number)
        {
            referenceVersion = referenceVersionElement.GetInt32();
        }
        else if (referenceVersionElement.ValueKind != JsonValueKind.Null)
        {
            throw new DomainValidationException("A decision scope reference version must be a number or null.");
        }

        return new DecisionScopeEntry(dimension, referenceId, referenceVersion);
    }

    private static void ValidateEntries(ImmutableArray<DecisionScopeEntry> entries)
    {
        if (entries.Length == 0)
        {
            throw new DomainValidationException("A decision scope requires at least one scope entry.");
        }

        foreach (var entry in entries)
        {
            ValidateEntry(entry);
        }

        var duplicate = entries
            .GroupBy(entry => (entry.Dimension, entry.ReferenceId, entry.ReferenceVersion))
            .Any(group => group.Count() > 1);
        if (duplicate)
        {
            throw new DomainConflictException("Decision scope entries cannot be duplicated.");
        }

        var hasOrganization = entries.Any(entry => entry.Dimension == ScopeDimension.Organization);
        if (hasOrganization && entries.Length > 1)
        {
            throw new DomainValidationException("Organization scope cannot be combined with narrower scopes.");
        }
    }

    private static void ValidateEntry(DecisionScopeEntry entry)
    {
        switch (entry.Dimension)
        {
            case ScopeDimension.Organization:
                if (entry.ReferenceId is not null || entry.ReferenceVersion is not null)
                {
                    throw new DomainValidationException("Organization scope must not carry references.");
                }

                break;
            case ScopeDimension.LegalEntity:
            case ScopeDimension.Department:
            case ScopeDimension.CostCenter:
                if (entry.ReferenceId is null || entry.ReferenceId == Guid.Empty)
                {
                    throw new DomainValidationException(
                        "A legal entity, department or cost center scope requires a UUID reference.");
                }

                if (entry.ReferenceVersion is null or < 1)
                {
                    throw new DomainValidationException(
                        "A legal entity, department or cost center scope requires a positive version.");
                }

                break;
            default:
                throw new DomainValidationException("The decision scope dimension is invalid.");
        }
    }
}

public sealed record DecisionScopeEntry(ScopeDimension Dimension, Guid? ReferenceId, int? ReferenceVersion);

/// <summary>
/// Strict JSON converter: a descriptor is only accepted when its serialized form is exactly the
/// canonical <c>decision-scope/v1</c> JSON, so a non-canonical wire payload fails closed.
/// </summary>
public sealed class DecisionScopeDescriptorJsonConverter : JsonConverter<DecisionScopeDescriptor>
{
    public override DecisionScopeDescriptor Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return DecisionScopeDescriptor.Parse(document.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, DecisionScopeDescriptor value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToCanonicalJson());
}
