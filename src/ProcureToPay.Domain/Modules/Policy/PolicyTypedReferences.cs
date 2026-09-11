using System.Text.RegularExpressions;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

/// <summary>Stable reference to a versioned code in an authoritative catalog.</summary>
public sealed record VersionedCodeRef
{
    public VersionedCodeRef(string catalog, string code, int version, string digest)
    {
        Catalog = PolicyValue.NormalizeCode(catalog, "Catalog");
        Code = PolicyValue.NormalizeCode(code, "Code");
        if (version < 1)
        {
            throw new DomainValidationException("Versioned code references require a positive version.");
        }
        if (string.IsNullOrWhiteSpace(digest) ||
            !Regex.IsMatch(digest, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
        {
            throw new DomainValidationException("Versioned code references require a SHA-256 digest.");
        }
        Version = version;
        Digest = digest.ToLowerInvariant();
    }

    public string Catalog { get; }
    public string Code { get; }
    public int Version { get; }
    public string Digest { get; }
}

/// <summary>Stable reference to an entity snapshot owned by another domain.</summary>
public sealed record VersionedEntityRef
{
    public VersionedEntityRef(string entityType, Guid id, int version)
    {
        EntityType = PolicyValue.NormalizeCode(entityType, "Entity type");
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("Versioned entity references require an id and positive version.");
        }
        Id = id;
        Version = version;
    }

    public string EntityType { get; }
    public Guid Id { get; }
    public int Version { get; }
}

public enum TypedAnswerValueKind
{
    Boolean = 1,
    EnumCode = 2
}

/// <summary>Typed answer to a versioned risk/compliance question.</summary>
public sealed record TypedAnswerRef
{
    public TypedAnswerRef(string questionCode, int schemaVersion, TypedAnswerValueKind valueKind, string value)
    {
        QuestionCode = PolicyValue.NormalizeCode(questionCode, "Question code");
        if (schemaVersion < 1 || string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException("Typed answers require a schema version and value.");
        }
        if (valueKind == TypedAnswerValueKind.Boolean &&
            !bool.TryParse(value, out _))
        {
            throw new DomainValidationException("Boolean typed answers must contain true or false.");
        }
        if (valueKind == TypedAnswerValueKind.EnumCode)
        {
            value = PolicyValue.NormalizeCode(value, "Enum answer");
        }
        SchemaVersion = schemaVersion;
        ValueKind = valueKind;
        Value = value;
    }

    public string QuestionCode { get; }
    public int SchemaVersion { get; }
    public TypedAnswerValueKind ValueKind { get; }
    public string Value { get; }
}
