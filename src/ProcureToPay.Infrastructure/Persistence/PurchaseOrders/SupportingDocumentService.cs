using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>Command that stages one supporting document of a Purchase Request version (REQ-08).</summary>
public sealed record StageSupportingDocumentCommand(
    Guid OrganizationId,
    Guid RequestId,
    int ExpectedRequestVersion,
    Guid FileId,
    int FileVersion,
    string ContentType,
    string FileName,
    long Length,
    string? Sha256,
    string BusinessType,
    IReadOnlyList<OrderingEvidenceTargetRef> CoveredTargets,
    Guid ActorUserId);

/// <summary>Command that confirms one staged supporting document (REQ-08).</summary>
public sealed record ConfirmSupportingDocumentCommand(
    Guid OrganizationId,
    Guid DocumentId,
    int ExpectedVersion,
    Guid ActorUserId);

/// <summary>Command that registers the exactly-one processor of <c>supporting-document-owner/v1</c>.</summary>
public sealed record RegisterSupportingDocumentProcessorCommand(
    string AdapterId,
    string AdapterVersion,
    string ProcessorId,
    string WorkloadIssuer,
    string WorkloadClientId);

/// <summary>
/// Supporting documents of SPEC 11 REQ-08: staging computes and seals the SHA-256 of the stored bytes,
/// confirmation appends an immutable successor, and the download URL is derived on demand from the
/// deterministic object key so no location ever reaches a published document or a log.
/// </summary>
public sealed class SupportingDocumentService(
    ProcureToPayDbContext dbContext,
    IFileStorage storage)
{
    /// <summary>Contractual maximum lifetime of one download URL (REQ-11).</summary>
    public static readonly TimeSpan MaximumDownloadLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Stages one document root and uploads its bytes under a deterministic key (REQ-08).</summary>
    public async Task<ProcurementSupportingDocumentVersion> StageAsync(
        StageSupportingDocumentCommand command,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(content);
        var occurred = DateTimeOffset.UtcNow;
        if (command.Length is <= 0 or > PurchaseOrderCodes.MaxDocumentBytes)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A supporting document admits between 1 and {PurchaseOrderCodes.MaxDocumentBytes} bytes.");
        }

        var targets = (command.CoveredTargets ?? [])
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0)
        {
            throw new PurchaseOrderUnprocessableException("A supporting document requires a covered target.");
        }

        var requestVersion = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == command.RequestId &&
                          record.Version == command.ExpectedRequestVersion &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The document request version is not available.");
        var manifest = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(record => record.RequestId == command.RequestId &&
                             record.RequestVersion == command.ExpectedRequestVersion)
            .ToArrayAsync(cancellationToken);
        var lines = new List<PurchaseRequestLineVersionRecord>(targets.Length);
        foreach (var target in targets)
        {
            if (!manifest.Any(record => record.LineId == target.TargetId &&
                                        record.LineVersion == target.TargetVersion))
            {
                throw new PurchaseOrderUnprocessableException(
                    "A covered target does not belong to the document request version.");
            }

            lines.Add(await dbContext.PurchaseRequestLineVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.LineId == target.TargetId &&
                              record.LineVersion == target.TargetVersion &&
                              record.RequestId == command.RequestId,
                    cancellationToken));
        }

        var documents = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(record => record.OrganizationId == command.OrganizationId &&
                             record.RequestId == command.RequestId &&
                             record.RequestVersion == command.ExpectedRequestVersion &&
                             record.Version == 1)
            .CountAsync(cancellationToken);
        if (documents >= PurchaseOrderCodes.MaxDocumentsPerSubject)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A request version admits at most {PurchaseOrderCodes.MaxDocumentsPerSubject} documents.");
        }

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != command.Length)
        {
            throw new PurchaseOrderUnprocessableException(
                "The uploaded bytes do not match the declared length.");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (command.Sha256 is not null &&
            !string.Equals(sha256, PurchaseOrderCodes.Digest(command.Sha256, "File sha256"), StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The uploaded bytes do not match the declared SHA-256.");
        }

        var fileRef = new SupportingDocumentFileRef(
            command.ContentType,
            command.FileId,
            command.FileName,
            command.Length,
            sha256,
            command.FileVersion);
        var document = new ProcurementSupportingDocumentVersion(
            Guid.NewGuid(),
            version: 1,
            predecessorVersion: null,
            command.OrganizationId,
            new PurchaseOrderContentRef(
                command.RequestId, command.ExpectedRequestVersion, requestVersion.ContentDigest),
            targets
                .Select(target => new PurchaseOrderContentRef(target.TargetId, target.TargetVersion,
                    lines.Single(line => line.LineId == target.TargetId &&
                                         line.LineVersion == target.TargetVersion).ContentDigest))
                .ToArray(),
            command.BusinessType,
            fileRef,
            SupportingDocumentState.Staged,
            command.ActorUserId,
            occurred,
            confirmedAt: null);
        await storage.UploadAsync(
            new FileUploadRequest
            {
                ObjectKey = SupportingDocumentFileRef.ObjectKey(
                    command.OrganizationId, document.DocumentId, document.Version),
                FileName = fileRef.FileName,
                ContentType = fileRef.ContentType,
                Content = new MemoryStream(bytes, writable: false),
                Length = fileRef.Length
            },
            cancellationToken);
        dbContext.ProcurementSupportingDocuments.Add(Writers.Record(document));
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionDocumentStaged,
            PurchaseOrderCodes.TargetSupportingDocument,
            document.DocumentId,
            document.Version,
            ["business_type", "file_ref", "covered_targets"],
            document.Digest,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }

    /// <summary>
    /// Confirms one staged document: the successor seals the metadata and the SHA-256 of the stored
    /// bytes, and a confirmed root admits no further successor (REQ-08).
    /// </summary>
    public async Task<ProcurementSupportingDocumentVersion> ConfirmAsync(
        ConfirmSupportingDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var occurred = DateTimeOffset.UtcNow;
        var record = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .Where(candidate => candidate.Id == command.DocumentId &&
                                candidate.OrganizationId == command.OrganizationId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document is not visible.");
        if (record.State == (int)SupportingDocumentState.Confirmed)
        {
            return PurchaseOrderSerialization.ReadSupportingDocument(
                record.DocumentJson, record.Id, record.ActorUserId, record.OccurredAt, record.ConfirmedAt,
                record.ContentDigest);
        }

        if (command.ExpectedVersion != record.Version)
        {
            throw new DomainConflictException("The supporting document changed under the command.");
        }

        if (record.Version != 1)
        {
            throw new DomainConflictException(
                "A confirmed supporting document is never replaced; a correction creates another root.");
        }

        var staged = PurchaseOrderSerialization.ReadSupportingDocument(
            record.DocumentJson, record.Id, record.ActorUserId, record.OccurredAt, record.ConfirmedAt,
            record.ContentDigest);
        if (staged.State != SupportingDocumentState.Staged)
        {
            throw new DomainConflictException("Only a staged supporting document can be confirmed.");
        }

        var confirmed = staged.Confirm(command.ActorUserId, occurred);
        dbContext.ProcurementSupportingDocuments.Add(Writers.Record(confirmed));
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionDocumentConfirmed,
            PurchaseOrderCodes.TargetSupportingDocument,
            confirmed.DocumentId,
            confirmed.Version,
            ["state", "sha256", "coverage"],
            confirmed.Digest,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        return confirmed;
    }

    /// <summary>
    /// Temporary download URL of one confirmed document (REQ-11): it is derived from the stored
    /// metadata, lives at most fifteen minutes and is audited.
    /// </summary>
    public async Task<Uri> GenerateDownloadUrlAsync(
        Guid organizationId,
        Guid documentId,
        int version,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.ProcurementSupportingDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == documentId &&
                             candidate.Version == version &&
                             candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The supporting document is not visible.");
        var document = PurchaseOrderSerialization.ReadSupportingDocument(
            record.DocumentJson, record.Id, record.ActorUserId, record.OccurredAt, record.ConfirmedAt,
            record.ContentDigest);
        if (document.State != SupportingDocumentState.Confirmed)
        {
            throw new DomainConflictException("Only a confirmed supporting document can be downloaded.");
        }

        var occurred = DateTimeOffset.UtcNow;
        AddAudit(
            organizationId,
            actorUserId,
            PurchaseOrderCodes.ActionDocumentDownloaded,
            PurchaseOrderCodes.TargetSupportingDocument,
            documentId,
            version,
            ["download"],
            document.Digest,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        return storage.GenerateTemporaryDownloadUrl(
            SupportingDocumentFileRef.ObjectKey(organizationId, document.DocumentId, version),
            MaximumDownloadLifetime);
    }

    /// <summary>
    /// REQ-08: the registration of the processor is exactly one per adapter identity and must be
    /// bound to the owner workload the approval prerequisites already resolved.
    /// </summary>
    public async Task<SupportingDocumentProcessorRegistrationRecord> RegisterAsync(
        RegisterSupportingDocumentProcessorCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.AdapterId, PurchaseOrderCodes.SupportingDocumentOwnerAdapterId, StringComparison.Ordinal) ||
            !string.Equals(command.AdapterVersion, PurchaseOrderCodes.OwnerAdapterVersion, StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "Only the supporting-document-owner/v1 adapter accepts a processor registration.");
        }

        var processorId = PurchaseOrderCodes.Key(command.ProcessorId, "processor_id");
        var issuer = PurchaseOrderCodes.ContractId(command.WorkloadIssuer, "workload_issuer");
        var clientId = PurchaseOrderCodes.Key(command.WorkloadClientId, "workload_client_id");
        var mismatch = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .AnyAsync(
                record => record.OwnerAdapterId == command.AdapterId &&
                          record.OwnerAdapterVersion == command.AdapterVersion &&
                          (record.OwnerWorkloadIssuer != issuer || record.OwnerWorkloadClientId != clientId),
                cancellationToken);
        if (mismatch)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The registered processor workload does not match the resolved owner workload.");
        }

        var existing = await dbContext.SupportingDocumentProcessorRegistrations
            .SingleOrDefaultAsync(
                record => record.AdapterId == command.AdapterId &&
                          record.AdapterVersion == command.AdapterVersion,
                cancellationToken);
        if (existing is null)
        {
            existing = new SupportingDocumentProcessorRegistrationRecord
            {
                AdapterId = command.AdapterId,
                AdapterVersion = command.AdapterVersion,
                ProcessorId = processorId,
                WorkloadIssuer = issuer,
                WorkloadClientId = clientId
            };
            dbContext.SupportingDocumentProcessorRegistrations.Add(existing);
        }
        else
        {
            if (!string.Equals(existing.ProcessorId, processorId, StringComparison.Ordinal) ||
                !string.Equals(existing.WorkloadIssuer, issuer, StringComparison.Ordinal) ||
                !string.Equals(existing.WorkloadClientId, clientId, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "The supporting document processor is already registered with another identity.");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>The exactly-one registration of the owner adapter, or null when none exists (REQ-08).</summary>
    public async Task<SupportingDocumentProcessorRegistrationRecord?> ResolveRegistrationAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.SupportingDocumentProcessorRegistrations
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    private void AddAudit(
        Guid organizationId,
        Guid actorUserId,
        string action,
        string targetType,
        Guid targetId,
        int targetVersion,
        string[] changedFields,
        string correlation,
        DateTimeOffset occurredAt) =>
        dbContext.PurchaseOrderAuditRecords.Add(new PurchaseOrderAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetVersion = targetVersion,
            ChangedFieldsJson = System.Text.Json.JsonSerializer.Serialize(changedFields),
            CorrelationReference = correlation.Length <= 256 ? correlation : correlation[..256],
            OccurredAt = occurredAt
        });

    /// <summary>Single writer of one append-only supporting document row.</summary>
    internal static class Writers
    {
        internal static ProcurementSupportingDocumentRecord Record(
            ProcurementSupportingDocumentVersion document) =>
            new()
            {
                Id = document.DocumentId,
                Version = document.Version,
                OrganizationId = document.OrganizationId,
                RequestId = document.RequestRef.Id,
                RequestVersion = document.RequestRef.Version,
                CoveredTargetsJson = PurchaseOrderSerialization.Targets(document.CoveredTargets
                    .Select(target => new OrderingEvidenceTarget(target.Id, target.Version, target.ContentDigest))),
                FileRefJson = PurchaseOrderSerialization.FileRef(document.FileRef),
                BusinessType = document.BusinessType,
                State = (int)document.State,
                Sha256 = document.FileRef.Sha256,
                Length = document.FileRef.Length,
                ActorUserId = document.ActorUserId,
                OccurredAt = document.OccurredAt,
                ConfirmedAt = document.ConfirmedAt,
                DocumentJson = document.CanonicalDocument(),
                ContentDigest = document.Digest
            };
    }
}
