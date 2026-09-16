using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>Result of one governed catalogue save (REQ-06).</summary>
public sealed record ApprovedCatalogSaveOutcome(
    ApprovedCatalogVersionView Version,
    Guid? ProposalId,
    bool RequiresApproval);

/// <summary>
/// Governed lifecycle of the Approved Supplier Catalog (SPEC 09 REQ-06, REQ-07): append-only
/// versions behind a current pointer, a confirmed agreement attachment as the publication evidence
/// and the same segregation of duties as the Supplier Master.
/// </summary>
public sealed class ApprovedSupplierCatalogGovernanceService(
    ProcureToPayDbContext dbContext,
    SupplierPersistenceService persistence,
    ApprovalSubmissionService approvalSubmissions,
    IPurchaseRequestReferenceOwnerRegistry referenceOwners,
    // The agreement storage is resolved only when a file is actually staged, confirmed or
    // downloaded: building the catalogue service must never require the object storage, so a
    // deployment without it degrades readiness instead of failing unrelated requests (REQ-07).
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ApprovedSupplierCatalogGovernanceService> logger)
{
    public const string RequirementKey = "SUPPLIER_CATALOG_APPROVAL";
    public const string RequirementStage = "PROCUREMENT";
    public const string AgreementPrefix = "supplier-agreements";

    /// <summary>Trusted in-process workload of the catalogue domain (REQ-07).</summary>
    public static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", SupplierCodes.ApprovedSupplierFactOwnerId);

    private IFileStorage Storage => serviceProvider.GetRequiredService<IFileStorage>();

    public int MinimumAuthorityRank =>
        int.TryParse(configuration["Supplier:Governance:MinimumSupplierMasterRank"], out var rank) && rank >= 1
            ? rank
            : 1;

    /// <summary>
    /// Stages one agreement file (REQ-07): the server computes the SHA-256, refuses empty, oversized
    /// or disallowed uploads and stores the object under an opaque key. A staged attachment is never
    /// publishable until it is confirmed.
    /// </summary>
    public async Task<SupplierAgreementAttachmentView> StageAttachmentAsync(
        Guid organizationId,
        Guid supplierId,
        Guid actorUserId,
        string fileName,
        string contentType,
        long length,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedName = SupplierCodes.DisplayName(fileName, "File name", SupplierCodes.MaxAttachmentNameScalars);
        var normalizedType = SupplierCodes.ContentType(contentType);
        if (length is < 1 or > SupplierCodes.MaxAttachmentBytes)
        {
            throw new DomainValidationException(
                $"An agreement attachment must be between 1 byte and {SupplierCodes.MaxAttachmentBytes} bytes.");
        }

        var supplierExists = await dbContext.Suppliers
            .AsNoTracking()
            .AnyAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId,
                cancellationToken);
        if (!supplierExists)
        {
            throw new DomainNotFoundException("The supplier does not exist in this organization.");
        }

        var attachmentId = Guid.NewGuid();
        var objectKey = $"{AgreementPrefix}/{organizationId:D}/{supplierId:D}/{attachmentId:D}";
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != length)
        {
            throw new DomainValidationException("The uploaded agreement length does not match its content.");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        buffer.Position = 0;
        await Storage.UploadAsync(
            new FileUploadRequest
            {
                ObjectKey = objectKey,
                FileName = normalizedName,
                ContentType = normalizedType,
                Content = buffer,
                Length = length
            },
            cancellationToken);
        var occurredAt = DateTimeOffset.UtcNow;
        dbContext.SupplierAgreementAttachments.Add(new SupplierAgreementAttachmentRecord
        {
            Id = attachmentId,
            Version = 1,
            OrganizationId = organizationId,
            SupplierId = supplierId,
            ObjectKey = objectKey,
            FileName = normalizedName,
            ContentType = normalizedType,
            Length = length,
            Sha256 = sha256,
            State = (int)SupplierAgreementAttachmentState.Staged,
            CreatedByUserId = actorUserId,
            CreatedAt = occurredAt
        });
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionAttachmentStaged, Actor(actorUserId), null,
            ["attachment"], "attachment",
            $"attachment:{attachmentId:D}:staged",
            $"{{\"id\":\"{attachmentId:D}\",\"type\":\"{SupplierCodes.TargetAttachment}\",\"version\":1}}",
            occurredAt);
        await persistence.SaveAsync(cancellationToken);
        return new SupplierAgreementAttachmentView(attachmentId, 1, normalizedName, normalizedType, length, sha256);
    }

    /// <summary>Confirms a staged attachment after recomputing its length and digest (REQ-07).</summary>
    public async Task<SupplierAgreementAttachmentView> ConfirmAttachmentAsync(
        Guid organizationId,
        Guid attachmentId,
        Guid actorUserId,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SupplierAgreementAttachments
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The agreement attachment does not exist.");
        if (record.State == (int)SupplierAgreementAttachmentState.Confirmed)
        {
            return View(record);
        }

        if (!string.Equals(
                record.Sha256,
                SupplierCodes.Digest(sha256, "Attachment digest"),
                StringComparison.Ordinal))
        {
            throw new DomainValidationException("The uploaded agreement digest does not match the staged file.");
        }

        record.State = (int)SupplierAgreementAttachmentState.Confirmed;
        record.ConfirmedAt = DateTimeOffset.UtcNow;
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionAttachmentStaged, Actor(actorUserId), null,
            ["attachment"], "attachment",
            $"attachment:{attachmentId:D}:confirmed",
            $"{{\"id\":\"{attachmentId:D}\",\"type\":\"{SupplierCodes.TargetAttachment}\",\"version\":1}}",
            record.ConfirmedAt.Value);
        await persistence.SaveAsync(cancellationToken);
        return View(record);
    }

    /// <summary>
    /// Creates or revises the draft of one catalogue entry. Publishing always needs approval, so a
    /// save only appends the candidate and refreshes the open proposal (REQ-06, REQ-07).
    /// </summary>
    public async Task<ApprovedCatalogSaveOutcome> SaveAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid? catalogEntryId,
        int? expectedVersion,
        VersionedEntityRef supplierRef,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef? productRef,
        decimal negotiatedPrice,
        string currency,
        string unitCode,
        string externalContractReference,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovedCatalogEntryStatus status,
        Guid attachmentId,
        int attachmentVersion,
        string reason,
        string changeKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplierRef);
        ArgumentNullException.ThrowIfNull(spendCategoryRef);
        var normalizedReason = SupplierCodes.Reason(reason);
        _ = SupplierCodes.Key(changeKey, "Change key");
        if (catalogEntryId is not null && expectedVersion is null)
        {
            throw new DomainConflictException("Revising a catalogue entry requires its expected version.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var occurredAt = DateTimeOffset.UtcNow;
        var supplier = await RequireOperationalSupplierAsync(
            organizationId, supplierRef, cancellationToken);
        var category = await RequireActiveCategoryAsync(
            organizationId, supplierRef, spendCategoryRef, cancellationToken);
        var product = productRef is null
            ? null
            : await RequireActiveProductAsync(organizationId, productRef, cancellationToken);
        var attachment = await dbContext.SupplierAgreementAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.Version == attachmentVersion &&
                       row.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The agreement attachment does not exist.");
        if (attachment.State != (int)SupplierAgreementAttachmentState.Confirmed)
        {
            throw new DomainValidationException("Only a confirmed attachment can be published.");
        }

        var content = new ApprovedCatalogVersionContent(
            supplierRef,
            category,
            product,
            negotiatedPrice,
            currency,
            unitCode,
            externalContractReference,
            validFrom,
            validTo,
            status,
            View(attachment));

        ApprovedSupplierCatalogEntryRecord entry;
        if (catalogEntryId is null)
        {
            entry = new ApprovedSupplierCatalogEntryRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                SupplierId = supplierRef.Id,
                SpendCategoryCode = spendCategoryRef.Code,
                ProductId = productRef?.Id,
                CurrentVersion = 0
            };
            dbContext.ApprovedSupplierCatalogEntries.Add(entry);
        }
        else
        {
            entry = await dbContext.ApprovedSupplierCatalogEntries
                .SingleOrDefaultAsync(
                    row => row.Id == catalogEntryId && row.OrganizationId == organizationId,
                    cancellationToken)
                ?? throw new DomainNotFoundException("The catalogue entry does not exist.");
            await SupplierPersistenceService.AcquireSupplierLockAsync(
                dbContext, organizationId, entry.Id, cancellationToken);
            if (entry.CurrentVersion != expectedVersion)
            {
                throw new DomainConflictException("The catalogue entry was modified by another command.");
            }

            // R12 (revisión independiente): una revisión no puede trasladar la versión al selector de
            // otra raíz, ni publicar un intervalo ACTIVE que solape la versión vigente del mismo
            // selector (dos entradas ACTIVE efectivas dejarían el matching ambiguo).
            if (!string.Equals(entry.SpendCategoryCode, spendCategoryRef.Code, StringComparison.Ordinal) ||
                (entry.ProductId is null) != (productRef is null) ||
                (entry.ProductId is not null && entry.ProductId != productRef!.Id))
            {
                throw new DomainValidationException(
                    "A catalogue version must keep the exact selector of its root.");
            }

            if (entry.CurrentVersion > 0)
            {
                var current = await LoadVersionAsync(entry.Id, entry.CurrentVersion, cancellationToken);
                if (current.Content.Status == ApprovedCatalogEntryStatus.Active &&
                    status == ApprovedCatalogEntryStatus.Active &&
                    content.ValidFrom < current.Content.ValidTo &&
                    current.Content.ValidFrom < content.ValidTo)
                {
                    throw new DomainConflictException(
                        "An active catalogue version of that selector already covers the requested window.");
                }
            }
        }

        var existingProposal = await dbContext.SupplierChangeProposals
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId &&
                       row.SupplierId == entry.Id &&
                       (row.State == (int)SupplierProposalState.Draft ||
                        row.State == (int)SupplierProposalState.Pending),
                cancellationToken);
        if (existingProposal is not null && existingProposal.State == (int)SupplierProposalState.Pending)
        {
            throw new DomainConflictException(
                "A pending catalogue proposal must be decided before the entry can be edited again.");
        }

        var candidateVersion = entry.CurrentVersion + 1;
        var proposalId = existingProposal?.Id ?? Guid.NewGuid();
        var digest = SupplierCanonicalizer.ApprovedCatalogContentDigest(
            entry.Id, candidateVersion, supplierRef, category, product, negotiatedPrice, currency,
            SupplierCodes.Code(unitCode, "Unit code"), content.ExternalContractReference, content.ValidFrom,
            content.ValidTo, status, content.Attachment);
        var record = new ApprovedSupplierCatalogVersionRecord
        {
            CatalogEntryId = entry.Id,
            Version = candidateVersion,
            OrganizationId = organizationId,
            SupplierId = supplierRef.Id,
            SupplierVersion = supplierRef.Version,
            SpendCategoryJson = SupplierSerialization.CodeRef(category),
            ProductJson = SupplierSerialization.EntityRef(product),
            NegotiatedPrice = content.NegotiatedPrice,
            Currency = content.Currency,
            UnitCode = content.UnitCode,
            ExternalContractReference = content.ExternalContractReference,
            ValidFrom = content.ValidFrom,
            ValidTo = content.ValidTo,
            Status = (int)content.Status,
            AttachmentId = attachmentId,
            AttachmentVersion = attachmentVersion,
            ContentDigest = digest,
            PredecessorVersion = entry.CurrentVersion == 0 ? null : entry.CurrentVersion,
            ProposalId = proposalId,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = normalizedReason
        };
        dbContext.ApprovedSupplierCatalogVersions.Add(record);
        if (existingProposal is null)
        {
            dbContext.SupplierChangeProposals.Add(new SupplierChangeProposalRecord
            {
                Id = proposalId,
                OrganizationId = organizationId,
                SupplierId = entry.Id,
                BaseVersion = entry.CurrentVersion == 0 ? null : entry.CurrentVersion,
                CandidateVersion = candidateVersion,
                ChangeKind = (int)SupplierChangeKind.SensitiveUpdate,
                SensitiveFieldsJson = SupplierPersistenceService.WriteSensitiveFields(
                    ["negotiated_price", "external_contract_reference", "validity", "selector", "attachment"]),
                RequestedStatus = null,
                EditorUserId = actorUserId,
                State = (int)SupplierProposalState.Draft,
                ChangeKey = changeKey,
                Fingerprint = SupplierCanonicalizer.ChangeFingerprint(
                    entry.Id, organizationId, actorUserId, entry.CurrentVersion == 0 ? null : entry.CurrentVersion,
                    candidateVersion, "CATALOG", "ACTIVE", changeKey, digest, normalizedReason),
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt
            });
        }
        else
        {
            existingProposal.CandidateVersion = candidateVersion;
            existingProposal.SensitiveFieldsJson = SupplierPersistenceService.WriteSensitiveFields(
                ["negotiated_price", "external_contract_reference", "validity", "selector", "attachment"]);
            existingProposal.ChangeKey = changeKey;
            existingProposal.Fingerprint = SupplierCanonicalizer.ChangeFingerprint(
                entry.Id, organizationId, actorUserId, entry.CurrentVersion == 0 ? null : entry.CurrentVersion,
                candidateVersion, "CATALOG", "ACTIVE", changeKey, digest, normalizedReason);
            existingProposal.UpdatedAt = occurredAt;
        }

        persistence.AddAudit(
            organizationId, SupplierCodes.ActionCatalogSubmitted, Actor(actorUserId), null,
            ["negotiated_price", "attachment"], correlationReference,
            $"{changeKey}:candidate",
            $"{{\"id\":\"{entry.Id:D}\",\"type\":\"{SupplierCodes.TargetCatalogEntry}\",\"version\":{candidateVersion}}}",
            occurredAt);
        await persistence.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Catalogue entry {EntryId} version {Version} drafted by {Actor}.",
            entry.Id, candidateVersion, actorUserId);
        return new ApprovedCatalogSaveOutcome(ReadVersion(record, attachment), proposalId, RequiresApproval: true);
    }

    /// <summary>Submits the catalogue candidate to the same approver requirement as the master (REQ-07).</summary>
    public async Task<SupplierSubmitOutcome> SubmitAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid catalogEntryId,
        int candidateVersion,
        string submissionKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        _ = SupplierCodes.Key(submissionKey, "Submission key");
        var occurredAt = DateTimeOffset.UtcNow;
        Guid proposalId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await SupplierPersistenceService.AcquireSupplierLockAsync(
                dbContext, organizationId, catalogEntryId, cancellationToken);
            var proposal = await dbContext.SupplierChangeProposals
                .SingleOrDefaultAsync(
                    row => row.OrganizationId == organizationId &&
                           row.SupplierId == catalogEntryId &&
                           row.CandidateVersion == candidateVersion &&
                           (row.State == (int)SupplierProposalState.Draft ||
                            row.State == (int)SupplierProposalState.Pending),
                    cancellationToken)
                ?? throw new DomainNotFoundException("The catalogue change proposal does not exist.");
            if (proposal.State == (int)SupplierProposalState.Pending && proposal.ApprovalCaseId is not null)
            {
                return new SupplierSubmitOutcome(
                    proposal.Id, candidateVersion, proposal.ApprovalCaseId.Value, Replayed: true);
            }

            if (proposal.EditorUserId != actorUserId)
            {
                throw new DomainForbiddenException("Only the editor of the proposal can submit it.");
            }

            if (proposal.State == (int)SupplierProposalState.Pending &&
                proposal.SubmissionKey is not null &&
                !string.Equals(proposal.SubmissionKey, submissionKey, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A submission of this proposal is already in flight with another key.");
            }

            proposal.State = (int)SupplierProposalState.Pending;
            proposal.SubmissionKey = submissionKey;
            proposal.RequirementKey = RequirementKey;
            proposal.CaseContractVersion = SupplierCodes.GovernanceAdapterVersion;
            proposal.UpdatedAt = occurredAt;
            await persistence.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            proposalId = proposal.Id;
        }

        var outcome = await approvalSubmissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                Workload,
                organizationId,
                SupplierCodes.CatalogApprovalSubjectType,
                catalogEntryId,
                candidateVersion,
                SupplierCodes.CatalogApprovalOperation,
                SupplierCodes.GovernanceAdapterVersion,
                submissionKey,
                actorUserId,
                actorUserId,
                correlationReference),
            occurredAt,
            cancellationToken);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var proposal = await dbContext.SupplierChangeProposals
                .SingleAsync(row => row.Id == proposalId, cancellationToken);
            proposal.ApprovalCaseId = outcome.CaseId;
            proposal.UpdatedAt = DateTimeOffset.UtcNow;
            await persistence.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new SupplierSubmitOutcome(proposalId, candidateVersion, outcome.CaseId, outcome.Replayed);
    }

    /// <summary>Publishes one approved candidate: the pointer moves and the version becomes effective.</summary>
    public async Task ApplyApprovalAsync(
        Guid organizationId,
        Guid catalogEntryId,
        int candidateVersion,
        Guid proposalId,
        string result,
        Guid? decisionActorUserId,
        string causeJson,
        string effectKey,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var proposal = await dbContext.SupplierChangeProposals
            .SingleOrDefaultAsync(
                row => row.Id == proposalId && row.OrganizationId == organizationId &&
                       row.SupplierId == catalogEntryId,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException("The catalogue proposal does not exist.");
        if (proposal.CandidateVersion != candidateVersion)
        {
            throw new DomainConflictException("The approval result does not match the catalogue candidate.");
        }

        if (proposal.State is (int)SupplierProposalState.Approved or (int)SupplierProposalState.Rejected or
            (int)SupplierProposalState.ChangesRequested or (int)SupplierProposalState.Cancelled)
        {
            return;
        }

        var entry = await dbContext.ApprovedSupplierCatalogEntries
            .SingleAsync(row => row.Id == catalogEntryId, cancellationToken);
        if (!string.Equals(result, "APPROVED", StringComparison.Ordinal))
        {
            proposal.State = result == "CHANGES_REQUESTED"
                ? (int)SupplierProposalState.ChangesRequested
                : (int)SupplierProposalState.Rejected;
            proposal.UpdatedAt = occurredAt;
            persistence.AddAudit(
                organizationId, SupplierCodes.ActionRejected, Actor(decisionActorUserId ?? Guid.Empty), causeJson,
                ["catalogue"], "approval", effectKey,
                $"{{\"id\":\"{catalogEntryId:D}\",\"type\":\"{SupplierCodes.TargetCatalogEntry}\",\"version\":{candidateVersion}}}",
                occurredAt);
            await persistence.SaveAsync(cancellationToken);
            return;
        }

        candidateVersion = proposal.CandidateVersion
            ?? throw new SupplierDependencyUnavailableException("The catalogue proposal has no candidate.");
        entry.CurrentVersion = candidateVersion;
        proposal.State = (int)SupplierProposalState.Approved;
        proposal.UpdatedAt = occurredAt;
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionCatalogPublished, Actor(decisionActorUserId ?? Guid.Empty),
            causeJson, ["negotiated_price", "external_contract_reference", "validity"], "approval", effectKey,
            $"{{\"id\":\"{catalogEntryId:D}\",\"type\":\"{SupplierCodes.TargetCatalogEntry}\",\"version\":{candidateVersion}}}",
            occurredAt);
        await persistence.SaveAsync(cancellationToken);
        logger.LogInformation(
            "Catalogue entry {EntryId} published version {Version}.", catalogEntryId, candidateVersion);
    }

    /// <summary>Current version of every entry of one supplier and category, used by the fact owner (REQ-08).</summary>
    public async Task<IReadOnlyList<ApprovedCatalogVersionView>> ListCurrentVersionsAsync(
        Guid organizationId,
        Guid supplierId,
        string spendCategoryCode,
        CancellationToken cancellationToken = default)
    {
        var entryIds = await dbContext.ApprovedSupplierCatalogEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId &&
                            entry.SupplierId == supplierId &&
                            entry.SpendCategoryCode == spendCategoryCode &&
                            entry.CurrentVersion > 0)
            .Select(entry => new { entry.Id, entry.CurrentVersion })
            .ToArrayAsync(cancellationToken);
        var views = new List<ApprovedCatalogVersionView>(entryIds.Length);
        foreach (var entry in entryIds)
        {
            views.Add(await LoadVersionAsync(entry.Id, entry.CurrentVersion, cancellationToken));
        }

        return views;
    }

    /// <summary>Loads one exact catalogue version, reproducing its digest before exposing it (NFR-01).</summary>
    public async Task<ApprovedCatalogVersionView> LoadVersionAsync(
        Guid catalogEntryId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.ApprovedSupplierCatalogVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.CatalogEntryId == catalogEntryId && row.Version == version,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException("The catalogue version does not exist.");
        var attachment = await dbContext.SupplierAgreementAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == record.AttachmentId && row.Version == record.AttachmentVersion,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException(
                "The agreement attachment of the catalogue version is missing.");
        var view = ReadVersion(record, attachment);
        if (!string.Equals(ComputeDigest(view), view.ContentDigest, StringComparison.Ordinal))
        {
            throw new SupplierDependencyUnavailableException(
                "The stored catalogue version content digest is not reproducible.");
        }

        return view;
    }

    /// <summary>Effective entries of the organization at one instant, for the public projection (REQ-11).</summary>
    public async Task<IReadOnlyList<ApprovedSupplierReferenceView>> ListEffectiveAsync(
        Guid organizationId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var entries = await dbContext.ApprovedSupplierCatalogEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId && entry.CurrentVersion > 0)
            .Select(entry => new { entry.Id, entry.CurrentVersion })
            .ToArrayAsync(cancellationToken);
        var effective = new List<ApprovedSupplierReferenceView>(entries.Length);
        foreach (var entry in entries)
        {
            var version = await LoadVersionAsync(entry.Id, entry.CurrentVersion, cancellationToken);
            if (!string.Equals(version.Content.EffectiveStatus(at), "ACTIVE", StringComparison.Ordinal))
            {
                continue;
            }

            effective.Add(new ApprovedSupplierReferenceView(
                version.CatalogEntryId,
                version.Version,
                version.Content.SupplierRef,
                version.Content.SpendCategoryRef,
                version.Content.ProductRef,
                version.Content.NegotiatedPrice,
                version.Content.Currency,
                version.Content.UnitCode,
                version.Content.ExternalContractReference,
                version.Content.ValidFrom,
                version.Content.ValidTo,
                "ACTIVE"));
        }

        return effective;
    }

    /// <summary>Temporary, audited download URL of one confirmed attachment (REQ-07).</summary>
    public async Task<Uri> CreateDownloadUrlAsync(
        Guid organizationId,
        Guid attachmentId,
        Guid actorUserId,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SupplierAgreementAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.OrganizationId == organizationId &&
                       row.State == (int)SupplierAgreementAttachmentState.Confirmed,
                cancellationToken)
            ?? throw new DomainNotFoundException("The agreement attachment does not exist.");
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionAttachmentDownloaded, Actor(actorUserId), null,
            ["attachment"], correlationReference, $"attachment:{attachmentId:D}:download",
            $"{{\"id\":\"{attachmentId:D}\",\"type\":\"{SupplierCodes.TargetAttachment}\",\"version\":{record.Version}}}",
            DateTimeOffset.UtcNow);
        await persistence.SaveAsync(cancellationToken);
        return Storage.GenerateTemporaryDownloadUrl(record.ObjectKey);
    }

    internal static ApprovedCatalogVersionView ReadVersion(
        ApprovedSupplierCatalogVersionRecord record,
        SupplierAgreementAttachmentRecord attachment) => new(
        record.CatalogEntryId,
        record.Version,
        new ApprovedCatalogVersionContent(
            new VersionedEntityRef("SUPPLIER", record.SupplierId, record.SupplierVersion),
            SupplierSerialization.ReadCodeRef(record.SpendCategoryJson),
            SupplierSerialization.ReadEntityRef(record.ProductJson),
            record.NegotiatedPrice,
            record.Currency,
            record.UnitCode,
            record.ExternalContractReference,
            record.ValidFrom,
            record.ValidTo,
            (ApprovedCatalogEntryStatus)record.Status,
            new SupplierAgreementAttachmentView(
                attachment.Id,
                attachment.Version,
                attachment.FileName,
                attachment.ContentType,
                attachment.Length,
                attachment.Sha256)),
        record.ContentDigest,
        record.PredecessorVersion,
        record.ActorUserId,
        record.OccurredAt,
        record.Reason);

    private static string ComputeDigest(ApprovedCatalogVersionView view) =>
        SupplierCanonicalizer.ApprovedCatalogContentDigest(
            view.CatalogEntryId,
            view.Version,
            view.Content.SupplierRef,
            view.Content.SpendCategoryRef,
            view.Content.ProductRef,
            view.Content.NegotiatedPrice,
            view.Content.Currency,
            view.Content.UnitCode,
            view.Content.ExternalContractReference,
            view.Content.ValidFrom,
            view.Content.ValidTo,
            view.Content.Status,
            view.Content.Attachment);

    private async Task<SupplierVersionView> RequireOperationalSupplierAsync(
        Guid organizationId,
        VersionedEntityRef supplierRef,
        CancellationToken cancellationToken)
    {
        var root = await dbContext.Suppliers
            .AsNoTracking()
            .SingleOrDefaultAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierRef.Id,
                cancellationToken)
            ?? throw new DomainNotFoundException("The supplier does not exist in this organization.");
        if (root.OperationalVersion is null)
        {
            throw new DomainValidationException(
                "Only an approved supplier can be homologated.");
        }

        var view = await persistence.LoadVersionAsync(supplierRef.Id, root.OperationalVersion.Value, cancellationToken);
        if (view.Version != supplierRef.Version ||
            !SupplierStatusCodes.IsUsable(view.Content.Status))
        {
            throw new DomainValidationException(
                "The catalogue entry must reference the current operational supplier version.");
        }

        return view;
    }

    private async Task<VersionedCodeRef> RequireActiveCategoryAsync(
        Guid organizationId,
        VersionedEntityRef supplierRef,
        VersionedCodeRef spendCategoryRef,
        CancellationToken cancellationToken)
    {
        var root = await dbContext.Suppliers
            .AsNoTracking()
            .SingleOrDefaultAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierRef.Id,
                cancellationToken)
            ?? throw new DomainNotFoundException("The supplier does not exist in this organization.");
        var supplier = await persistence.LoadVersionAsync(
            supplierRef.Id,
            root.OperationalVersion
            ?? throw new DomainValidationException("Only an approved supplier can be homologated."),
            cancellationToken);
        var match = supplier.Content.CategoriesSupplied.SingleOrDefault(
            reference => string.Equals(reference.Code, spendCategoryRef.Code, StringComparison.Ordinal) &&
                         reference.Version == spendCategoryRef.Version &&
                         string.Equals(reference.Digest, spendCategoryRef.Digest, StringComparison.Ordinal));
        if (match is null)
        {
            throw new DomainValidationException(
                "The spend category must be a current category supplied by the approved supplier version.");
        }

        var owner = referenceOwners.ResolveExactlyOne(
            PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.SpendCategory);
        var verification = await owner.VerifyAsync(
            new PurchaseRequestVerificationRequest(
                PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                organizationId.ToString("D"),
                PurchaseRequestAttestedRef.ForCode(
                    PurchaseRequestReferenceType.SpendCategory, spendCategoryRef.Code, spendCategoryRef.Version,
                    spendCategoryRef.Digest),
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
        if (!verification.Active)
        {
            throw new DomainValidationException("The spend category reference is not usable.");
        }

        return spendCategoryRef;
    }

    private async Task<VersionedEntityRef> RequireActiveProductAsync(
        Guid organizationId,
        VersionedEntityRef productRef,
        CancellationToken cancellationToken)
    {
        // SPEC 09 REQ-06: a product-scoped entry requires the optional PRODUCT owner to confirm the
        // current version; without that owner the publication fails closed instead of guessing.
        var owner = referenceOwners.ResolveExactlyOne(
            PurchaseRequestAssertionType.ActiveInOrganization, PurchaseRequestReferenceType.Product);
        var verification = await owner.VerifyAsync(
            new PurchaseRequestVerificationRequest(
                PurchaseRequestCodes.Code(PurchaseRequestAssertionType.ActiveInOrganization),
                organizationId.ToString("D"),
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.Product, productRef.Id, productRef.Version),
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
        if (!verification.Active)
        {
            throw new DomainValidationException("The product reference is not usable.");
        }

        return productRef;
    }

    private static SupplierAgreementAttachmentView View(SupplierAgreementAttachmentRecord record) => new(
        record.Id, record.Version, record.FileName, record.ContentType, record.Length, record.Sha256);

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";
}
