using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Canonical JSON writer for <c>approval-canonical-json/v2</c> (SPEC 03 REQ-08):
/// UTF-8 without BOM or whitespace, known properties always present, object keys
/// ordered ordinal, strings normalized to NFC, UUID lowercase D, UTC timestamps with
/// seven decimals, uppercase enums, invariant decimals and sets sorted by canonical
/// bytes with duplicates rejected.
/// </summary>
public static class ApprovalCanonicalJson
{
    public const string CanonicalizationVersion = "approval-canonical-json/v2";

    /// <summary>
    /// Canonicalization version of the SPEC 06 supersession delta (REQ-08). The writer rules are
    /// those of <c>approval-canonical-json/v2</c>; the version changes because the supersession
    /// preimage is widened by <c>approval-supersession-delta/v1</c>, so historical v2 fingerprints
    /// stay byte-identical and verifiable.
    /// </summary>
    public const string CanonicalizationVersionV3 = "approval-canonical-json/v3";

    public static CanonicalValue String(string value) => new CanonicalString(
        (value ?? throw new DomainValidationException("Canonical strings cannot be null."))
        .Normalize(NormalizationForm.FormC));

    public static CanonicalValue String(Guid value) => new CanonicalString(value.ToString("D").ToLowerInvariant());

    public static CanonicalValue String(DateTimeOffset value) =>
        new CanonicalString(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));

    public static CanonicalValue String(Enum value) => new CanonicalString(value.ToString().ToUpperInvariant());

    public static CanonicalValue StringOrNull(string? value) => value is null ? Null() : String(value);

    public static CanonicalValue StringOrNull(Guid? value) => value is null ? Null() : String(value.Value);

    public static CanonicalValue StringOrNull(DateTimeOffset? value) => value is null ? Null() : String(value.Value);

    public static CanonicalValue Number(int value) => new CanonicalNumber(value.ToString(CultureInfo.InvariantCulture));

    public static CanonicalValue Number(long value) => new CanonicalNumber(value.ToString(CultureInfo.InvariantCulture));

    public static CanonicalValue Number(decimal value) => new CanonicalNumber(FormatDecimal(value));

    public static CanonicalValue NumberOrNull(int? value) => value is null ? Null() : Number(value.Value);

    public static CanonicalValue NumberOrNull(decimal? value) => value is null ? Null() : Number(value.Value);

    public static CanonicalValue Bool(bool value) => new CanonicalBoolean(value);

    public static CanonicalValue Null() => CanonicalNull.Instance;

    public static CanonicalValue Array(params CanonicalValue[] values) => new CanonicalArray(values ?? []);

    public static CanonicalValue Array(IEnumerable<CanonicalValue> values) => new CanonicalArray((values ?? []).ToArray());

    /// <summary>Ordered, duplicate-free set sorted by canonical bytes.</summary>
    public static CanonicalValue Set(IEnumerable<CanonicalValue> values)
    {
        var materialized = (values ?? throw new DomainValidationException("Set values are required.")).ToArray();
        var encoded = materialized
            .Select(value => (Value: value, Bytes: Encoding.UTF8.GetBytes(Serialize(value))))
            .OrderBy(entry => entry.Bytes, ByteArrayComparer.Instance)
            .ToArray();
        for (var index = 1; index < encoded.Length; index++)
        {
            if (ByteArrayComparer.Instance.Compare(encoded[index - 1].Bytes, encoded[index].Bytes) == 0)
            {
                throw new DomainConflictException("Canonical sets cannot contain duplicates.");
            }
        }

        return new CanonicalArray(encoded.Select(entry => entry.Value).ToArray(), IsSet: true);
    }

    public static CanonicalValue Set(IEnumerable<string> values) =>
        Set((values ?? []).Select(value => String(value)));

    public static CanonicalValue Object(params (string Key, CanonicalValue Value)[] properties) =>
        Object((IEnumerable<(string, CanonicalValue)>)properties);

    public static CanonicalValue Object(IEnumerable<(string Key, CanonicalValue Value)> properties)
    {
        var materialized = (properties ?? throw new DomainValidationException("Canonical objects need properties."))
            .Select(property => (
                Key: (property.Key ?? throw new DomainValidationException("Canonical property names are required."))
                    .Normalize(NormalizationForm.FormC),
                property.Value))
            .ToArray();
        var duplicate = materialized
            .GroupBy(property => property.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainConflictException($"Canonical objects cannot repeat the property '{duplicate.Key}'.");
        }

        var ordered = materialized
            .OrderBy(property => Encoding.UTF8.GetBytes(property.Key), ByteArrayComparer.Instance)
            .ToArray();
        return new CanonicalObject(ordered);
    }

    public static string Serialize(CanonicalValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        Write(value, builder);
        return builder.ToString();
    }

    public static string Digest(CanonicalValue value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value)))).ToLowerInvariant();

    public static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static void Write(CanonicalValue value, StringBuilder builder)
    {
        switch (value)
        {
            case CanonicalNull:
                builder.Append("null");
                break;
            case CanonicalString text:
                WriteString(text.Value, builder);
                break;
            case CanonicalNumber number:
                builder.Append(number.Value);
                break;
            case CanonicalBoolean boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case CanonicalArray array:
                builder.Append('[');
                for (var index = 0; index < array.Values.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array.Values[index], builder);
                }

                builder.Append(']');
                break;
            case CanonicalObject canonicalObject:
                builder.Append('{');
                for (var index = 0; index < canonicalObject.Properties.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(canonicalObject.Properties[index].Key, builder);
                    builder.Append(':');
                    Write(canonicalObject.Properties[index].Value, builder);
                }

                builder.Append('}');
                break;
            default:
                throw new DomainValidationException("Unsupported canonical value.");
        }
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}

public abstract record CanonicalValue;

public sealed record CanonicalNull : CanonicalValue
{
    public static readonly CanonicalNull Instance = new();

    private CanonicalNull()
    {
    }
}

public sealed record CanonicalString(string Value) : CanonicalValue;

public sealed record CanonicalNumber(string Value) : CanonicalValue;

public sealed record CanonicalBoolean(bool Value) : CanonicalValue;

public sealed record CanonicalArray(IReadOnlyList<CanonicalValue> Values, bool IsSet = false) : CanonicalValue;

public sealed record CanonicalObject(IReadOnlyList<(string Key, CanonicalValue Value)> Properties) : CanonicalValue;
