using System.Collections.Immutable;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary><c>file_ref={content_type,file_id,file_name,length,sha256,version}</c> of REQ-08.</summary>
public sealed record SupportingDocumentFileRef
{
    public SupportingDocumentFileRef(
        string contentType,
        Guid fileId,
        string fileName,
        long length,
        string sha256,
        int version)
    {
        ContentType = PurchaseOrderCodes.ContentType(contentType);
        FileId = fileId == Guid.Empty
            ? throw new DomainValidationException("A file reference requires its identity.")
            : fileId;
        FileName = PurchaseOrderCodes.FileName(fileName);
        if (length <= 0 || length > PurchaseOrderCodes.MaxDocumentBytes)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A supporting document admits between 1 and {PurchaseOrderCodes.MaxDocumentBytes} bytes.");
        }

        Length = length;
        Sha256 = PurchaseOrderCodes.Digest(sha256, "File sha256");
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A file reference requires a positive version.");
    }

    public string ContentType { get; }
    public Guid FileId { get; }
    public string FileName { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public int Version { get; }

    /// <summary>Identity of the exact uploaded bytes: the same bytes count once per target (REQ-08).</summary>
    public string ByteIdentity => $"{Sha256}:{Length}";

    /// <summary>
    /// Object key of the stored bytes. It is derived from the server-owned document identity, never
    /// from a caller value, so a second request can never address (and overwrite) the bytes of an
    /// already confirmed document (REQ-08, NFR-01).
    /// </summary>
    public static string ObjectKey(Guid organizationId, Guid documentId, int version) =>
        $"purchase-orders/{organizationId:D}/supporting-documents/{documentId:D}/{version}";
}

/// <summary>
/// One supporting document version (<c>procurement-supporting-document/v1</c>, REQ-08). Append-only:
/// a confirmation seals a successor version, and a confirmed root admits no further successor.
/// </summary>
public sealed record ProcurementSupportingDocumentVersion
{
    public const string ContractVersion = PurchaseOrderCodes.SupportingDocumentContract;

    public ProcurementSupportingDocumentVersion(
        Guid documentId,
        int version,
        int? predecessorVersion,
        Guid organizationId,
        PurchaseOrderContentRef requestRef,
        IReadOnlyList<PurchaseOrderContentRef> coveredTargets,
        string businessType,
        SupportingDocumentFileRef fileRef,
        SupportingDocumentState state,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        DateTimeOffset? confirmedAt)
    {
        DocumentId = documentId == Guid.Empty
            ? throw new DomainValidationException("A supporting document requires its identity.")
            : documentId;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A supporting document version must be positive.");
        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first supporting document version has no predecessor.");
        }

        PredecessorVersion = predecessorVersion;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("A supporting document requires its organization.")
            : organizationId;
        RequestRef = requestRef ?? throw new DomainValidationException("A supporting document requires its request.");
        var targets = (coveredTargets ?? [])
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (targets.Length == 0)
        {
            throw new DomainValidationException("A supporting document requires at least one covered target.");
        }

        if (targets.Select(target => target.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() != targets.Length)
        {
            throw new DomainConflictException("A supporting document cannot repeat a covered target.");
        }

        CoveredTargets = targets;
        BusinessType = PurchaseOrderCodes.DocumentType(businessType);
        FileRef = fileRef ?? throw new DomainValidationException("A supporting document requires its file.");
        State = state;
        if (actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A supporting document requires its actor.");
        }

        ActorUserId = actorUserId;
        OccurredAt = occurredAt.ToUniversalTime();
        ConfirmedAt = confirmedAt?.ToUniversalTime();
        if (State == SupportingDocumentState.Confirmed != (ConfirmedAt is not null))
        {
            throw new DomainValidationException("Only a confirmed supporting document carries its instant.");
        }

        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid DocumentId { get; }
    public int Version { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public IReadOnlyList<PurchaseOrderContentRef> CoveredTargets { get; }
    public string BusinessType { get; }
    public SupportingDocumentFileRef FileRef { get; }
    public SupportingDocumentState State { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset? ConfirmedAt { get; }

    /// <summary><c>supporting_document_digest</c> of the published preimage (REQ-08).</summary>
    public string Digest { get; }

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.SupportingDocumentDocument(this);

    /// <summary>The confirmed successor of a staged document; the bytes of the root stay sealed.</summary>
    public ProcurementSupportingDocumentVersion Confirm(Guid actorUserId, DateTimeOffset occurredAt) =>
        new(
            DocumentId,
            Version + 1,
            Version,
            OrganizationId,
            RequestRef,
            CoveredTargets,
            BusinessType,
            FileRef,
            SupportingDocumentState.Confirmed,
            actorUserId,
            occurredAt,
            occurredAt);
}

/// <summary>
/// Parameters of one <c>supporting-document-owner/v1</c> prerequisite (REQ-08): the union of admitted
/// business types and the minimum number of unique documents each target must reach.
/// </summary>
public sealed record SupportingDocumentOwnerParameters(
    IReadOnlyList<string> DocumentTypes,
    int MinimumCount)
{
    public IReadOnlyList<string> DocumentTypes { get; } = (DocumentTypes ?? [])
        .Select(PurchaseOrderCodes.DocumentType)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(type => type, StringComparer.Ordinal)
        .ToImmutableArray();

    public int MinimumCount { get; } = MinimumCount >= 1
        ? MinimumCount
        : throw new DomainValidationException("The supporting document minimum count must be positive.");

    /// <summary>
    /// Parses the canonical parameters of the prerequisite. Only the two documented properties are
    /// read, and a missing file type or a non positive minimum fails closed (REQ-08).
    /// </summary>
    public static SupportingDocumentOwnerParameters Parse(string parametersJson)
    {
        using var document = JsonDocument.Parse(parametersJson);
        var root = document.RootElement;
        var types = root.TryGetProperty("document_types", out var typeNode) &&
                    typeNode.ValueKind == JsonValueKind.Array
            ? typeNode.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
        var minimum = root.TryGetProperty("minimum_count", out var countNode) &&
                      countNode.TryGetInt32(out var count)
            ? count
            : 0;
        var parameters = new SupportingDocumentOwnerParameters(types, minimum);
        if (parameters.DocumentTypes.Count == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The supporting document prerequisite admits no business type.");
        }

        return parameters;
    }
}

/// <summary>One counted document reference of the evidence (<c>supporting-document-evidence/v1</c>).</summary>
public sealed record SupportingDocumentEvidenceRef(
    PurchaseOrderContentRef DocumentRef,
    PurchaseOrderContentRef TargetRef,
    string Sha256,
    long Length)
{
    public string Sha256 { get; } = PurchaseOrderCodes.Digest(Sha256, "File sha256");

    public string ByteIdentity => $"{Sha256}:{Length}";
}

/// <summary>
/// Reproducible evidence that every target of one supporting document prerequisite reached the
/// minimum count (<c>supporting-document-evidence/v1</c>, REQ-08). It never carries an attachment.
/// </summary>
public sealed record SupportingDocumentEvidence
{
    public const string ContractVersion = PurchaseOrderCodes.SupportingDocumentEvidenceContract;

    public SupportingDocumentEvidence(
        Guid attemptId,
        Guid organizationId,
        Guid prerequisiteId,
        string parametersDigest,
        string signalKey,
        IReadOnlyList<PurchaseOrderContentRef> targets,
        IReadOnlyList<SupportingDocumentEvidenceRef> documentRefs,
        DateTimeOffset checkedAt)
    {
        AttemptId = attemptId == Guid.Empty
            ? throw new DomainValidationException("The evidence requires its attempt.")
            : attemptId;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("The evidence requires its organization.")
            : organizationId;
        PrerequisiteId = prerequisiteId == Guid.Empty
            ? throw new DomainValidationException("The evidence requires its prerequisite.")
            : prerequisiteId;
        ParametersDigest = PurchaseOrderCodes.Digest(parametersDigest, "parameters digest");
        SignalKey = PurchaseOrderCodes.Key(signalKey, "signal_key");
        var targetSet = (targets ?? [])
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (targetSet.Length == 0)
        {
            throw new DomainValidationException("The evidence requires at least one target.");
        }

        Targets = targetSet;
        var references = (documentRefs ?? [])
            .OrderBy(reference => reference.TargetRef.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(reference => reference.ByteIdentity, StringComparer.Ordinal)
            .ThenBy(reference => reference.DocumentRef.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        // REQ-08: exactly one reference per (target, sha256, length); duplicate bytes never count twice.
        var duplicates = references
            .GroupBy(reference => $"{reference.TargetRef.CanonicalIdentity}|{reference.ByteIdentity}",
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new DomainConflictException("The evidence repeats a document reference of one target.");
        }

        if (references.Any(reference =>
                !targetSet.Any(target => string.Equals(
                    target.CanonicalIdentity, reference.TargetRef.CanonicalIdentity, StringComparison.Ordinal))))
        {
            throw new DomainValidationException("A document reference covers a target outside the evidence set.");
        }

        DocumentRefs = references;
        CheckedAt = checkedAt.ToUniversalTime();
        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid AttemptId { get; }
    public Guid OrganizationId { get; }
    public Guid PrerequisiteId { get; }
    public string ParametersDigest { get; }
    public string SignalKey { get; }
    public IReadOnlyList<PurchaseOrderContentRef> Targets { get; }
    public IReadOnlyList<SupportingDocumentEvidenceRef> DocumentRefs { get; }
    public DateTimeOffset CheckedAt { get; }

    /// <summary>Published result; only <c>SATISFIED</c> is ever emitted (REQ-08).</summary>
    public string Result => "SATISFIED";

    /// <summary><c>supporting_document_evidence_digest</c> of the published preimage (REQ-08).</summary>
    public string Digest { get; }

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.SupportingDocumentEvidenceDocument(this);

    /// <summary>Count of unique bytes of one target, as the published map states it.</summary>
    public IReadOnlyDictionary<string, int> CountsByTarget() =>
        Targets.ToDictionary(
            target => target.CanonicalIdentity,
            target => DocumentRefs.Count(reference =>
                string.Equals(reference.TargetRef.CanonicalIdentity, target.CanonicalIdentity,
                    StringComparison.Ordinal)),
            StringComparer.Ordinal);
}
