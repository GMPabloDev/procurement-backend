using System.Collections.Immutable;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseRequests;

public static class PurchaseRequestAssertionCodes
{
    public const string StatusActive = "ACTIVE";
    public const string StatusInactive = "INACTIVE";
    public const string StatusNotFound = "NOT_FOUND";
    public const string RefKindEntity = "ENTITY";
    public const string RefKindCode = "CODE";
    public const string RefKindQuestionSchema = "QUESTION_SCHEMA";

    public static string RequireStatus(string? status) => IsKnownStatus(status)
        ? status!
        : throw new DomainValidationException("The reference verification status is invalid.");

    /// <summary>Closed status vocabulary of the owner answer (SPEC 07 REQ-07).</summary>
    public static bool IsKnownStatus(string? status) =>
        status is StatusActive or StatusInactive or StatusNotFound;
}

/// <summary>
/// Exact <c>AttestedRef</c> of the attestation contract: all six properties are present even when
/// they are <c>null</c>, and the kind decides which of them carry the identity.
/// </summary>
public sealed record PurchaseRequestAttestedRef
{
    private PurchaseRequestAttestedRef(
        PurchaseRequestReferenceKind kind,
        PurchaseRequestReferenceType type,
        Guid? id,
        int? version,
        string? code,
        string? digest)
    {
        Kind = kind;
        Type = type;
        Id = id;
        Version = version;
        Code = code;
        Digest = digest;
    }

    public PurchaseRequestReferenceKind Kind { get; }
    public PurchaseRequestReferenceType Type { get; }
    public Guid? Id { get; }
    public int? Version { get; }
    public string? Code { get; }
    public string? Digest { get; }

    public string KindCode => Kind switch
    {
        PurchaseRequestReferenceKind.Entity => PurchaseRequestAssertionCodes.RefKindEntity,
        PurchaseRequestReferenceKind.Code => PurchaseRequestAssertionCodes.RefKindCode,
        PurchaseRequestReferenceKind.QuestionSchema => PurchaseRequestAssertionCodes.RefKindQuestionSchema,
        _ => throw new DomainValidationException("The attested reference kind is invalid.")
    };

    public static PurchaseRequestAttestedRef ForEntity(PurchaseRequestReferenceType type, Guid id, int version)
    {
        if (id == Guid.Empty || version < 1)
        {
            throw new DomainValidationException("An entity assertion requires its identity and version.");
        }

        return new PurchaseRequestAttestedRef(PurchaseRequestReferenceKind.Entity, type, id, version, null, null);
    }

    public static PurchaseRequestAttestedRef ForCode(
        PurchaseRequestReferenceType type,
        string code,
        int version,
        string digest) =>
        new(
            PurchaseRequestReferenceKind.Code,
            type,
            null,
            RequireVersion(version),
            Policy.PolicyValue.NormalizeCode(code, "Attested code"),
            PurchaseRequestLineRef.RequireDigest(digest, "Attested code digest"));

    public static PurchaseRequestAttestedRef ForQuestionSchema(string code, int version, string digest) =>
        new(
            PurchaseRequestReferenceKind.QuestionSchema,
            PurchaseRequestReferenceType.RiskSchema,
            null,
            RequireVersion(version),
            Policy.PolicyValue.NormalizeCode(code, "Risk schema code"),
            PurchaseRequestLineRef.RequireDigest(digest, "Risk schema digest"));

    public static PurchaseRequestAttestedRef Restore(
        PurchaseRequestReferenceKind kind,
        PurchaseRequestReferenceType type,
        Guid? id,
        int? version,
        string? code,
        string? digest)
    {
        switch (kind)
        {
            case PurchaseRequestReferenceKind.Entity when id is Guid entityId && version is int entityVersion:
                return ForEntity(type, entityId, entityVersion);
            case PurchaseRequestReferenceKind.Code when code is not null && version is int codeVersion &&
                                                       digest is not null:
                return ForCode(type, code, codeVersion, digest);
            case PurchaseRequestReferenceKind.QuestionSchema when code is not null && version is int schemaVersion &&
                                                                digest is not null:
                return ForQuestionSchema(code, schemaVersion, digest);
            default:
                throw new DomainValidationException("The stored attested reference is incomplete.");
        }
    }

    private static int RequireVersion(int version) =>
        version < 1 ? throw new DomainValidationException("An assertion version must be positive.") : version;
}

/// <summary>
/// One durable owner answer kept by the attestation (REQ-04). Identical assertions are
/// deduplicated before persisting; a missing cardinality forbids the manifest.
/// </summary>
public sealed record PurchaseRequestReferenceAssertion
{
    public PurchaseRequestReferenceAssertion(
        PurchaseRequestAssertionType assertionType,
        string ownerContractVersion,
        string ownerId,
        PurchaseRequestAttestedRef sourceRef,
        string status,
        PurchaseRequestAttestedRef? targetRef)
    {
        if (assertionType == PurchaseRequestAssertionType.CostCenterOwnedByDepartment && targetRef is null)
        {
            throw new DomainValidationException("The cost center ownership assertion requires its target department.");
        }

        if (assertionType != PurchaseRequestAssertionType.CostCenterOwnedByDepartment && targetRef is not null)
        {
            throw new DomainValidationException("Only the cost center ownership assertion carries a target reference.");
        }

        AssertionType = assertionType;
        OwnerContractVersion = RequireOwnerContractVersion(ownerContractVersion);
        OwnerId = RequireOwnerId(ownerId);
        SourceRef = sourceRef ?? throw new DomainValidationException("An assertion requires its source reference.");
        Status = PurchaseRequestAssertionCodes.RequireStatus(status);
        TargetRef = targetRef;
    }

    public PurchaseRequestAssertionType AssertionType { get; }
    public string OwnerContractVersion { get; }
    public string OwnerId { get; }
    public PurchaseRequestAttestedRef SourceRef { get; }
    public string Status { get; }
    public PurchaseRequestAttestedRef? TargetRef { get; }

    /// <summary>Identity of the assertion slot: its type plus the asserted source reference.</summary>
    public string SlotIdentity => $"{PurchaseRequestCodes.Code(AssertionType)}|{DescribeReference(SourceRef)}";

    /// <summary>Stable textual identity of one asserted reference, used for slot deduplication.</summary>
    public static string DescribeReference(PurchaseRequestAttestedRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Kind switch
        {
            PurchaseRequestReferenceKind.Entity =>
                $"{PurchaseRequestCodes.Code(reference.Type)}:{reference.Id:D}:{reference.Version}",
            PurchaseRequestReferenceKind.Code =>
                $"{PurchaseRequestCodes.Code(reference.Type)}:{reference.Code}:{reference.Version}:{reference.Digest}",
            PurchaseRequestReferenceKind.QuestionSchema =>
                $"{PurchaseRequestCodes.Code(reference.Type)}:{reference.Code}:{reference.Version}:{reference.Digest}",
            _ => throw new DomainValidationException("The attested reference kind is invalid.")
        };
    }

    internal static string RequireOwnerContractVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException(
                "An owner contract version must contain 1-64 characters without whitespace.");
        }

        return value;
    }

    internal static string RequireOwnerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException("An owner id must contain 1-64 characters without whitespace.");
        }

        return value;
    }
}

/// <summary>
/// Immutable snapshot of the owner answers of one presented request version (REQ-04). It never
/// turns Purchase Requests into the owner of those catalogs: it only freezes what owners answered.
/// </summary>
public sealed record PurchaseRequestReferenceAttestation
{
    public PurchaseRequestReferenceAttestation(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        DateTimeOffset attestedAt,
        IEnumerable<PurchaseRequestReferenceAssertion> assertions,
        string digest)
    {
        if (requestId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DomainValidationException("An attestation requires its request and organization.");
        }

        if (requestVersion < 1)
        {
            throw new DomainValidationException("An attestation requires the request version.");
        }

        var materialized = assertions?.ToImmutableArray() ??
            throw new DomainValidationException("An attestation requires its assertions.");
        var duplicate = materialized
            .GroupBy(assertion => assertion.SlotIdentity, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainConflictException("An attestation cannot answer the same assertion slot twice.");
        }

        RequestId = requestId;
        RequestVersion = requestVersion;
        OrganizationId = organizationId;
        AttestedAt = attestedAt.ToUniversalTime();
        Assertions = materialized
            .OrderBy(assertion => assertion.SlotIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        Digest = PurchaseRequestLineRef.RequireDigest(digest, "Reference attestation digest");
    }

    public Guid RequestId { get; }
    public int RequestVersion { get; }
    public Guid OrganizationId { get; }
    public DateTimeOffset AttestedAt { get; }
    public IReadOnlyList<PurchaseRequestReferenceAssertion> Assertions { get; }
    public string Digest { get; }

    /// <summary>Freezes the owner answers and derives the attestation digest from them (REQ-04).</summary>
    public static PurchaseRequestReferenceAttestation Create(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        DateTimeOffset attestedAt,
        IEnumerable<PurchaseRequestReferenceAssertion> assertions)
    {
        var materialized = (assertions ?? []).ToArray();
        return new PurchaseRequestReferenceAttestation(
            requestId,
            requestVersion,
            organizationId,
            attestedAt,
            materialized,
            PurchaseRequestCanonicalizer.ReferenceAttestationDigest(
                requestId, requestVersion, organizationId, attestedAt, materialized));
    }

    public static string RequireOwnerContractVersion(string? value) =>
        PurchaseRequestReferenceAssertion.RequireOwnerContractVersion(value);
}

/// <summary>
/// Exact <c>purchase-request-reference-verification/v1</c> request sent to exactly one owner.
/// </summary>
public sealed record PurchaseRequestVerificationRequest(
    string AssertionType,
    string OrganizationId,
    PurchaseRequestAttestedRef SourceRef,
    PurchaseRequestAttestedRef? TargetRef,
    DateTimeOffset VerifiedAt);

/// <summary>Exact verification response the owner must echo without adding fields.</summary>
public sealed record PurchaseRequestVerificationResponse(
    bool Active,
    string AssertionType,
    string OrganizationId,
    string OwnerContractVersion,
    string OwnerId,
    PurchaseRequestAttestedRef SourceRef,
    string Status,
    PurchaseRequestAttestedRef? TargetRef,
    DateTimeOffset VerifiedAt);

/// <summary>
/// <c>PurchaseRequestCompletenessManifest</c> (REQ-04): exactly one reference per line of the
/// presented version plus the digests that let Policy and later consumers reproduce the proof.
/// </summary>
public sealed record PurchaseRequestCompletenessManifest
{
    public PurchaseRequestCompletenessManifest(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        string requestContentDigest,
        string referenceAttestationDigest,
        string policyManifestDigest,
        string domainAttestationDigest,
        IEnumerable<PurchaseRequestLineRef> lines)
    {
        if (requestId == Guid.Empty || organizationId == Guid.Empty || requestVersion < 1)
        {
            throw new DomainValidationException("A completeness manifest requires its request identity.");
        }

        var materialized = (lines ?? []).ToImmutableArray();
        if (materialized.Length == 0)
        {
            throw new DomainValidationException("A completeness manifest requires at least one line.");
        }

        if (materialized.Select(line => line.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() !=
            materialized.Length)
        {
            throw new DomainConflictException("A completeness manifest cannot repeat a line version.");
        }

        RequestId = requestId;
        RequestVersion = requestVersion;
        OrganizationId = organizationId;
        RequestContentDigest = PurchaseRequestLineRef.RequireDigest(requestContentDigest, "Request content digest");
        ReferenceAttestationDigest = PurchaseRequestLineRef.RequireDigest(
            referenceAttestationDigest, "Reference attestation digest");
        PolicyManifestDigest = PurchaseRequestLineRef.RequireDigest(
            policyManifestDigest, "Policy manifest digest");
        DomainAttestationDigest = PurchaseRequestLineRef.RequireDigest(
            domainAttestationDigest, "Domain attestation digest");
        Lines = materialized
            .OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public Guid RequestId { get; }
    public int RequestVersion { get; }
    public Guid OrganizationId { get; }
    public string RequestContentDigest { get; }
    public string ReferenceAttestationDigest { get; }
    public string PolicyManifestDigest { get; }
    public string DomainAttestationDigest { get; }
    public IReadOnlyList<PurchaseRequestLineRef> Lines { get; }

    /// <summary>
    /// SPEC 09 REQ-09: new attestations freeze the supplier fact snapshot set and publish the v2
    /// contract. Rows written before this spec keep the v1 value, so their digests stay verifiable.
    /// </summary>
    public string ContractVersion { get; init; } = PurchaseRequestCodes.ManifestVersion;

    /// <summary>Digest of the frozen supplier fact snapshot set; null for a v1 manifest.</summary>
    public string? SupplierFactSnapshotsDigest { get; init; }
}
