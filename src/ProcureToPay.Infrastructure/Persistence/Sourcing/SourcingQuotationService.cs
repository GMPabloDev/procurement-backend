using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureToPay.Application.Abstractions.Files;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>One quoted line as offered by the supplier (REQ-03).</summary>
public sealed record QuotationLineDraft(
    Guid LineId,
    string Quantity,
    string UnitPrice,
    string Subtotal,
    string Taxes,
    string AdditionalCharges,
    string Discounts,
    string GrossTotal,
    string TechnicalResponse);

public sealed record RegisterQuotationCommand(
    Guid OrganizationId,
    Guid RfqId,
    Guid SupplierId,
    int SupplierVersion,
    string Currency,
    CommercialTerms Terms,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<QuotationLineDraft> Lines,
    IReadOnlyList<SourcingAttachmentRef> Attachments,
    string CommandKey);

public sealed record ReviewQuotationCommand(
    Guid OrganizationId,
    Guid QuotationId,
    int ExpectedVersion,
    QuotationReviewStatus Status,
    IReadOnlyList<QuotationReviewCode> Codes,
    string? Motive,
    string CommandKey);

/// <summary>Result of one quotation command: the version the command settled on (REQ-03).</summary>
public sealed record SourcingQuotationOutcome(
    Guid QuotationId,
    int Version,
    string Timeliness,
    string ReviewStatus,
    string ContentDigest,
    bool Replayed);

/// <summary>One quotation version ordered on the supplied quota, or a minimum-count deviation.</summary>
public sealed record SourcingSupplierQuote(
    Guid QuotationId,
    Guid SupplierId,
    int SupplierVersion,
    SourcingEntityRef SupplierRef,
    int Version,
    string Currency,
    CommercialTerms Terms,
    IReadOnlyList<QuotationLine> Lines,
    QuotationTimeliness Timeliness,
    string ContentDigest);

/// <summary>Projection of one quotation with its append-only versions (REQ-14).</summary>
public sealed record QuotationView(
    Guid QuotationId,
    Guid OrganizationId,
    Guid RfqId,
    Guid SupplierId,
    int CurrentVersion,
    IReadOnlyList<QuotationVersionView> Versions);

/// <summary>Staged or confirmed attachment as returned to the caller (REQ-03).</summary>
public sealed record SourcingAttachmentView(
    Guid FileId,
    int Version,
    string FileName,
    string ContentType,
    long Length,
    string Sha256,
    string State);

/// <summary>Signed download of one confirmed attachment; the object key never leaves the server (REQ-14).</summary>
public sealed record SourcingAttachmentDownload(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Quotation responses and their evidence (SPEC 10 REQ-03, REQ-04). Every answer of one supplier is
/// an append-only successor, timeliness is decided server-side against the deadline in force when
/// the answer was received and only the current <c>ON_TIME+VALID</c> version counts per line.
/// </summary>
public sealed class SourcingQuotationService(
    ProcureToPayDbContext dbContext,
    IServiceProvider serviceProvider,
    IConfiguration configuration)
{
    public const string AttachmentPrefix = "sourcing-quotes";
    public const int MaxTemporaryUrlMinutes = 15;

    private IFileStorage Storage => serviceProvider.GetRequiredService<IFileStorage>();

    /// <summary>
    /// Stages one quotation file (REQ-03): the server computes the SHA-256, refuses empty, oversized
    /// or disallowed uploads and stores the object under an opaque key.
    /// </summary>
    public async Task<SourcingAttachmentView> StageAttachmentAsync(
        Guid organizationId,
        Guid rfqId,
        Guid actorUserId,
        string fileName,
        string contentType,
        long length,
        Stream content,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedName = SourcingCodes.FileName(fileName);
        var normalizedType = SourcingCodes.ContentType(contentType);
        if (length is < 1 or > SourcingCodes.MaxAttachmentBytes)
        {
            throw new DomainValidationException(
                $"A quotation attachment must be between 1 byte and {SourcingCodes.MaxAttachmentBytes} bytes.");
        }

        var rfq = await dbContext.Rfqs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == rfqId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        var current = await dbContext.RfqVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.RfqId == rfq.Id && record.Version == rfq.CurrentVersion,
                cancellationToken);
        if ((RfqStatus)current.Status is RfqStatus.Draft or RfqStatus.Cancelled)
        {
            throw new DomainConflictException("This RFQ cannot receive quotation evidence.");
        }

        var attachmentId = Guid.NewGuid();
        var objectKey = $"{AttachmentPrefix}/{organizationId:D}/{rfqId:D}/{attachmentId:D}";
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != length)
        {
            throw new DomainValidationException("The uploaded attachment length does not match its content.");
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
        dbContext.SourcingAttachments.Add(new SourcingAttachmentRecord
        {
            Id = attachmentId,
            Version = 1,
            OrganizationId = organizationId,
            RfqId = rfqId,
            ObjectKey = objectKey,
            FileName = normalizedName,
            ContentType = normalizedType,
            Length = length,
            Sha256 = sha256,
            State = (int)SourcingAttachmentState.Staged,
            CreatedByUserId = actorUserId,
            CreatedAt = now
        });
        AddAudit(
            organizationId, SourcingCodes.ActionAttachmentStaged, actorUserId, null,
            ["attachment"], $"attachment:{attachmentId:D}:staged", $"attachment:{attachmentId:D}:staged",
            Target(SourcingCodes.TargetAttachment, attachmentId, 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingAttachmentView(attachmentId, 1, normalizedName, normalizedType, length, sha256, "STAGED");
    }

    /// <summary>
    /// Confirms a staged attachment after matching the computed digest; the row becomes immutable
    /// and only then may a quotation reference it (REQ-03, NFR-02).
    /// </summary>
    public async Task<SourcingAttachmentView> ConfirmAttachmentAsync(
        Guid organizationId,
        Guid attachmentId,
        int version,
        string sha256,
        Guid actorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SourcingAttachments
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.Version == version && row.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation attachment does not exist.");
        if (record.State == (int)SourcingAttachmentState.Confirmed)
        {
            return View(record);
        }

        if (!string.Equals(
                record.Sha256,
                SourcingCodes.Digest(sha256, "Attachment digest"),
                StringComparison.Ordinal))
        {
            throw new DomainValidationException("The uploaded attachment digest does not match the staged file.");
        }

        record.State = (int)SourcingAttachmentState.Confirmed;
        record.ConfirmedAt = now;
        AddAudit(
            organizationId, SourcingCodes.ActionAttachmentConfirmed, actorUserId, null,
            ["attachment"], $"attachment:{attachmentId:D}:confirmed", $"attachment:{attachmentId:D}:confirmed",
            Target(SourcingCodes.TargetAttachment, attachmentId, version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return View(record);
    }

    /// <summary>
    /// Registers one supplier answer. A first answer creates the quotation root; every later answer
    /// appends a successor that keeps the previous versions consultable (REQ-03).
    /// </summary>
    public async Task<SourcingQuotationOutcome> RegisterQuotationAsync(
        RegisterQuotationCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        if (command.SupplierId == Guid.Empty || command.SupplierVersion < 1)
        {
            throw new DomainValidationException("A quotation requires its supplier reference.");
        }

        var rfq = await dbContext.Rfqs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.RfqId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        var current = await dbContext.RfqVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.RfqId == rfq.Id && record.Version == rfq.CurrentVersion,
                cancellationToken);
        if ((RfqStatus)current.Status is RfqStatus.Draft or RfqStatus.Cancelled)
        {
            throw new DomainConflictException("This RFQ cannot receive quotations.");
        }

        var receivedAt = command.ReceivedAt.ToUniversalTime();
        var fingerprint = SourcingCommandFingerprints.Quotation(
            command.OrganizationId,
            actorUserId,
            command.RfqId,
            command.SupplierId,
            await CurrentQuotationVersionAsync(command.OrganizationId, command.RfqId, command.SupplierId, cancellationToken),
            command.Currency,
            command.Terms,
            receivedAt,
            await MaterializeLinesAsync(current, command, cancellationToken),
            command.Attachments ?? [],
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.RegisterQuotation,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different quotation.");
            }

            var stored = await RequireQuotationVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingQuotationOutcome(
                stored.QuotationId, stored.Version, SourcingStateCodes.Timeliness((QuotationTimeliness)stored.Timeliness),
                SourcingSerialization.ReadReview(stored.ReviewJson).StatusCode, stored.ContentDigest, Replayed: true);
        }

        if (receivedAt > now)
        {
            // The declared receipt instant is a fact of the supplier; the server clock is registered_at.
            throw new DomainValidationException("A quotation cannot be received in the future.");
        }

        await RequireSupplierVersionAsync(command, cancellationToken);
        var lines = await MaterializeLinesAsync(current, command, cancellationToken);
        var attachments = await RequireConfirmedAttachmentsAsync(command, cancellationToken);
        var currency = SourcingCodes.Currency(command.Currency, "Quotation currency");
        if (!string.Equals(currency, current.Currency, StringComparison.Ordinal))
        {
            throw new DomainConflictException("A quotation must use the currency frozen by its RFQ.");
        }

        var root = await dbContext.Quotations
            .SingleOrDefaultAsync(
                record =>
                    record.OrganizationId == command.OrganizationId &&
                    record.RfqId == command.RfqId &&
                    record.SupplierId == command.SupplierId,
                cancellationToken);
        if (root is null)
        {
            var quotations = await dbContext.Quotations
                .AsNoTracking()
                .CountAsync(record => record.RfqId == command.RfqId, cancellationToken);
            if (quotations >= SourcingCodes.MaxQuotationsPerRfq)
            {
                throw new DomainConflictException(
                    $"An RFQ admits at most {SourcingCodes.MaxQuotationsPerRfq} quotations.");
            }

            root = new QuotationRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                RfqId = command.RfqId,
                SupplierId = command.SupplierId,
                CurrentVersion = 0
            };
            dbContext.Quotations.Add(root);
        }

        var version = root.CurrentVersion + 1;
        if (version > SourcingCodes.MaxQuotationVersions)
        {
            throw new DomainConflictException(
                $"A quotation admits at most {SourcingCodes.MaxQuotationVersions} versions.");
        }

        var predecessor = root.CurrentVersion == 0 ? (int?)null : root.CurrentVersion;
        var timeliness = await ResolveTimelinessAsync(rfq.Id, receivedAt, cancellationToken);
        var review = QuotationReview.Pending();
        var content = new QuotationVersionContent(
            command.OrganizationId,
            new SourcingContentRef(current.RfqId, current.Version, current.ContentDigest),
            new SourcingEntityRef(command.SupplierId, command.SupplierVersion),
            currency,
            command.Terms,
            timeliness,
            receivedAt,
            now,
            lines,
            attachments,
            review);
        var digest = SourcingCanonicalizer.QuotationContentDigest(
            version, predecessor, content.OrganizationId, content.RfqRef, content.SupplierRef,
            content.Currency, content.Terms, content.Timeliness, content.ReceivedAt, content.RegisteredAt,
            content.Lines, content.Attachments, content.Review);
        AddVersion(root, version, predecessor, content, review, digest, actorUserId, now, reason: null);
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.RegisterQuotation,
            command.CommandKey, fingerprint, root.Id, version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionQuotationRegistered, actorUserId, null,
            ["quotation", "timeliness"], correlationReference,
            $"quotation:{root.Id:D}:v{version}:registered",
            Target(SourcingCodes.TargetQuotation, root.Id, version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingQuotationOutcome(
            root.Id, version, SourcingStateCodes.Timeliness(timeliness), "PENDING", digest, Replayed: false);
    }

    /// <summary>
    /// Reviews the current version with an expected version. <c>VALID</c> is refused server-side
    /// while evidence, lines, reconciliation or criterion fields are missing (REQ-04).
    /// </summary>
    public async Task<SourcingQuotationOutcome> ReviewQuotationAsync(
        ReviewQuotationCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var root = await dbContext.Quotations
            .SingleOrDefaultAsync(
                record => record.Id == command.QuotationId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation does not exist in this organization.");
        if (root.CurrentVersion != command.ExpectedVersion)
        {
            throw new DomainConflictException("The quotation version is stale.");
        }

        if (command.Status == QuotationReviewStatus.Pending)
        {
            throw new DomainValidationException("A review cannot return a quotation to PENDING.");
        }

        var current = await dbContext.QuotationVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.QuotationId == root.Id && record.Version == root.CurrentVersion,
                cancellationToken);
        var fingerprint = SourcingCommandFingerprints.QuotationReviewCommand(
            command.OrganizationId, actorUserId, command.QuotationId, command.ExpectedVersion,
            command.Status, command.Codes ?? [], command.Motive, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.ReviewQuotation,
            command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different review.");
            }

            var stored = await RequireQuotationVersionAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
            return new SourcingQuotationOutcome(
                stored.QuotationId, stored.Version, SourcingStateCodes.Timeliness((QuotationTimeliness)stored.Timeliness),
                SourcingSerialization.ReadReview(stored.ReviewJson).StatusCode, stored.ContentDigest, Replayed: true);
        }

        var previousReview = SourcingSerialization.ReadReview(current.ReviewJson);
        if (previousReview.Status == QuotationReviewStatus.Withdrawn)
        {
            throw new DomainConflictException("A withdrawn quotation is terminal.");
        }

        var review = QuotationReview.Decided(
            command.Status, command.Codes ?? [], command.Motive, actorUserId, now);
        if (command.Status == QuotationReviewStatus.Valid)
        {
            await RequireReviewableAsync(current, cancellationToken);
        }

        var lines = SourcingSerialization.ReadQuotationLines(current.LinesJson);
        var attachments = SourcingSerialization.ReadAttachments(current.AttachmentsJson);
        var content = new QuotationVersionContent(
            current.OrganizationId,
            new SourcingContentRef(current.RfqId, current.RfqVersion, current.RfqContentDigest),
            new SourcingEntityRef(current.SupplierId, current.SupplierVersion),
            current.Currency,
            SourcingSerialization.ReadTerms(current.TermsJson),
            (QuotationTimeliness)current.Timeliness,
            current.ReceivedAt,
            current.RegisteredAt,
            lines,
            attachments,
            review);
        var version = root.CurrentVersion + 1;
        var digest = SourcingCanonicalizer.QuotationContentDigest(
            version, root.CurrentVersion, content.OrganizationId, content.RfqRef, content.SupplierRef,
            content.Currency, content.Terms, content.Timeliness, content.ReceivedAt, content.RegisteredAt,
            content.Lines, content.Attachments, content.Review);
        AddVersion(root, version, root.CurrentVersion, content, review, digest, actorUserId, now, reason: null);
        AddCommand(
            command.OrganizationId, actorUserId, SourcingCommandFingerprints.ReviewQuotation,
            command.CommandKey, fingerprint, root.Id, version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionQuotationReviewed, actorUserId, null,
            ["review"], correlationReference,
            $"quotation:{root.Id:D}:v{version}:reviewed",
            Target(SourcingCodes.TargetQuotation, root.Id, version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SourcingQuotationOutcome(
            root.Id, version, SourcingStateCodes.Timeliness((QuotationTimeliness)current.Timeliness),
            review.StatusCode, digest, Replayed: false);
    }

    /// <summary>
    /// Withdraws the current answer by appending a <c>WITHDRAWN</c> successor with a mandatory
    /// motive; earlier versions stay consultable and never count (REQ-03, REQ-04).
    /// </summary>
    public async Task<SourcingQuotationOutcome> WithdrawQuotationAsync(
        ReviewQuotationCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await ReviewQuotationAsync(
            command with { Status = QuotationReviewStatus.Withdrawn }, actorUserId, correlationReference, now,
            cancellationToken);

    public async Task<QuotationView> GetQuotationAsync(
        Guid organizationId,
        Guid quotationId,
        CancellationToken cancellationToken = default)
    {
        var root = await dbContext.Quotations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == quotationId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation does not exist in this organization.");
        var versions = await dbContext.QuotationVersions
            .AsNoTracking()
            .Where(record => record.QuotationId == quotationId)
            .OrderBy(record => record.Version)
            .ToArrayAsync(cancellationToken);
        return new QuotationView(
            root.Id,
            root.OrganizationId,
            root.RfqId,
            root.SupplierId,
            root.CurrentVersion,
            versions.Select(ReadQuotationVersion).ToArray());
    }

    public async Task<IReadOnlyList<QuotationView>> ListQuotationsAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var roots = await dbContext.Quotations
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.SupplierId)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var views = new List<QuotationView>(roots.Length);
        foreach (var id in roots)
        {
            views.Add(await GetQuotationAsync(organizationId, id, cancellationToken));
        }

        return views;
    }

    /// <summary>
    /// Valid quotations per line of one RFQ (REQ-04): only the current version of a quotation counts,
    /// only when it is <c>ON_TIME+VALID</c> and covers that line, and every distinct supplier counts
    /// at most once per line. The query never aggregates across lines.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountValidQuotationsAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var currentVersions = await (
            from quotation in dbContext.Quotations.AsNoTracking()
            join version in dbContext.QuotationVersions.AsNoTracking()
                on new { Id = quotation.Id, Version = quotation.CurrentVersion }
                equals new { Id = version.QuotationId, version.Version }
            where quotation.OrganizationId == organizationId && quotation.RfqId == rfqId
            select new
            {
                quotation.Id,
                quotation.SupplierId,
                version.Timeliness,
                version.ReviewStatus
            }).ToArrayAsync(cancellationToken);
        var valid = currentVersions
            .Where(version => version.Timeliness == (int)QuotationTimeliness.OnTime &&
                              version.ReviewStatus == (int)QuotationReviewStatus.Valid)
            .ToArray();
        if (valid.Length == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var validIds = valid.Select(version => version.Id).ToArray();
        var suppliers = valid.ToDictionary(version => version.Id, version => version.SupplierId);
        var scopes = await dbContext.QuotationLineScopes
            .AsNoTracking()
            .Where(scope => validIds.Contains(scope.QuotationId) &&
                            scope.Version == dbContext.Quotations
                                .Where(quotation => quotation.Id == scope.QuotationId)
                                .Select(quotation => quotation.CurrentVersion)
                                .First())
            .Select(scope => new { scope.QuotationId, scope.LineId })
            .ToArrayAsync(cancellationToken);
        return scopes
            .GroupBy(scope => scope.LineId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(scope => suppliers[scope.QuotationId]).Distinct().Count());
    }

    /// <summary>Ordered valid quotes of one RFQ and line, for evaluation and recommendation (REQ-07).</summary>
    public async Task<IReadOnlyList<SourcingSupplierQuote>> ListValidQuotesAsync(
        Guid organizationId,
        Guid rfqId,
        Guid lineId,
        CancellationToken cancellationToken = default)
    {
        var currentVersions = await (
            from quotation in dbContext.Quotations.AsNoTracking()
            join version in dbContext.QuotationVersions.AsNoTracking()
                on new { Id = quotation.Id, Version = quotation.CurrentVersion }
                equals new { Id = version.QuotationId, version.Version }
            where quotation.OrganizationId == organizationId &&
                  quotation.RfqId == rfqId &&
                  version.Timeliness == (int)QuotationTimeliness.OnTime &&
                  version.ReviewStatus == (int)QuotationReviewStatus.Valid
            select version).ToArrayAsync(cancellationToken);
        if (currentVersions.Length == 0)
        {
            return [];
        }

        var quotes = new List<SourcingSupplierQuote>(currentVersions.Length);
        foreach (var version in currentVersions)
        {
            var lines = SourcingSerialization.ReadQuotationLines(version.LinesJson);
            if (!lines.Any(line => line.LineRef.Id == lineId))
            {
                continue;
            }

            quotes.Add(new SourcingSupplierQuote(
                version.QuotationId,
                version.SupplierId,
                version.SupplierVersion,
                new SourcingEntityRef(version.SupplierId, version.SupplierVersion),
                version.Version,
                version.Currency,
                SourcingSerialization.ReadTerms(version.TermsJson),
                lines,
                (QuotationTimeliness)version.Timeliness,
                version.ContentDigest));
        }

        return quotes;
    }

    /// <summary>Signed, audited download of one confirmed attachment (REQ-14).</summary>
    public async Task<SourcingAttachmentDownload> DownloadAttachmentAsync(
        Guid organizationId,
        Guid attachmentId,
        int version,
        Guid actorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SourcingAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.Version == version && row.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The quotation attachment does not exist.");
        RequireTemporaryUrlPolicy();
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = SourcingCodes.ActionAttachmentDownloaded,
            ActorJson = Actor(actorUserId),
            ChangedFieldsJson = "[]",
            CorrelationReference = $"attachment:{attachmentId:D}:download",
            EffectKey = $"attachment:{attachmentId:D}:v{version}:downloaded:{Guid.NewGuid():D}",
            OccurredAt = now,
            TargetJson = Target(SourcingCodes.TargetAttachment, attachmentId, version)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        var lifetime = Convert.ToInt32(configuration["Storage:S3:TemporaryUrlLifetimeMinutes"] ?? "15");
        return new SourcingAttachmentDownload(
            Storage.GenerateTemporaryDownloadUrl(record.ObjectKey),
            now.AddMinutes(lifetime));
    }

    public async Task<SourcingAttachmentView> GetAttachmentAsync(
        Guid organizationId,
        Guid attachmentId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.SourcingAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == attachmentId && row.Version == version && row.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The quotation attachment does not exist.");
        return View(record);
    }

    /// <summary>
    /// Deadline in force when the answer was received: the last RFQ version confirmed at or before
    /// <paramref name="receivedAt"/>. A later extension has a later instant, so it never reclassifies
    /// an already received answer (REQ-04, DEC-02).
    /// </summary>
    private async Task<QuotationTimeliness> ResolveTimelinessAsync(
        Guid rfqId,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var versions = await dbContext.RfqVersions
            .AsNoTracking()
            .Where(record => record.RfqId == rfqId)
            .OrderBy(record => record.Version)
            .Select(record => new { record.Version, record.OccurredAt, record.ResponseDeadline })
            .ToArrayAsync(cancellationToken);
        var inForce = versions
            .Where(version => version.OccurredAt <= receivedAt)
            .OrderByDescending(version => version.Version)
            .FirstOrDefault() ?? versions[0];
        return receivedAt <= inForce.ResponseDeadline ? QuotationTimeliness.OnTime : QuotationTimeliness.Late;
    }

    private async Task<IReadOnlyList<QuotationLine>> MaterializeLinesAsync(
        RfqVersionRecord rfqVersion,
        RegisterQuotationCommand command,
        CancellationToken cancellationToken)
    {
        var drafts = (command.Lines ?? []).ToImmutableArray();
        if (drafts.Length == 0)
        {
            throw new DomainValidationException("A quotation requires at least one line.");
        }

        if (drafts.Select(draft => draft.LineId).Distinct().Count() != drafts.Length)
        {
            throw new DomainConflictException("A quotation cannot quote the same line twice.");
        }

        var rfqLines = SourcingSerialization.ReadRfqLines(rfqVersion.LinesJson)
            .ToDictionary(line => line.LineRef.Id);
        var lines = ImmutableArray.CreateBuilder<QuotationLine>(drafts.Length);
        foreach (var draft in drafts)
        {
            if (!rfqLines.TryGetValue(draft.LineId, out var rfqLine))
            {
                throw new DomainConflictException("Every quoted line must belong to the RFQ.");
            }

            var line = new QuotationLine(
                draft.AdditionalCharges,
                draft.Discounts,
                draft.GrossTotal,
                rfqLine.LineRef,
                draft.Quantity,
                draft.Subtotal,
                draft.Taxes,
                draft.TechnicalResponse,
                rfqLine.UnitCode,
                draft.UnitPrice);
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private async Task<IReadOnlyList<SourcingAttachmentRef>> RequireConfirmedAttachmentsAsync(
        RegisterQuotationCommand command,
        CancellationToken cancellationToken)
    {
        var requested = (command.Attachments ?? []).ToImmutableArray();
        if (requested.Length == 0)
        {
            throw new DomainValidationException("A quotation requires at least one confirmed attachment.");
        }

        if (requested.Select(attachment => attachment.FileId).Distinct().Count() != requested.Length)
        {
            throw new DomainConflictException("A quotation cannot reference the same attachment twice.");
        }

        var records = await dbContext.SourcingAttachments
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == command.OrganizationId &&
                record.RfqId == command.RfqId)
            .ToArrayAsync(cancellationToken);
        var attachments = ImmutableArray.CreateBuilder<SourcingAttachmentRef>(requested.Length);
        foreach (var reference in requested)
        {
            var record = records.SingleOrDefault(candidate =>
                candidate.Id == reference.FileId && candidate.Version == reference.Version);
            if (record is null || record.State != (int)SourcingAttachmentState.Confirmed)
            {
                throw new DomainValidationException("Only a confirmed attachment of this RFQ can be referenced.");
            }

            if (!string.Equals(record.Sha256, reference.Sha256, StringComparison.Ordinal) ||
                record.Length != reference.Length ||
                !string.Equals(record.ContentType, reference.ContentType, StringComparison.Ordinal) ||
                !string.Equals(record.FileName, reference.FileName, StringComparison.Ordinal))
            {
                throw new DomainValidationException("The attachment reference does not match the stored evidence.");
            }

            attachments.Add(new SourcingAttachmentRef(
                record.ContentType, record.Id, record.FileName, record.Length, record.Sha256, record.Version));
        }

        return attachments.ToArray();
    }

    private async Task RequireSupplierVersionAsync(
        RegisterQuotationCommand command,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.Suppliers
            .AsNoTracking()
            .AnyAsync(
                supplier => supplier.Id == command.SupplierId &&
                            supplier.OrganizationId == command.OrganizationId,
                cancellationToken);
        var version = exists && await dbContext.SupplierVersions
            .AsNoTracking()
            .AnyAsync(
                record => record.SupplierId == command.SupplierId && record.Version == command.SupplierVersion,
                cancellationToken);
        if (!exists || !version)
        {
            throw new SourcingDependencyUnavailableException(
                "The referenced supplier version is unavailable for this quotation.");
        }
    }

    /// <summary>
    /// Server-side gate of <c>VALID</c> (REQ-04): confirmed evidence, covered lines, monetary
    /// reconciliation and the fields required by the frozen criteria must exist.
    /// </summary>
    private async Task RequireReviewableAsync(
        QuotationVersionRecord version,
        CancellationToken cancellationToken)
    {
        var lines = SourcingSerialization.ReadQuotationLines(version.LinesJson);
        if (lines.Count == 0)
        {
            throw new DomainValidationException("A quotation without lines cannot be valid.");
        }

        var attachments = SourcingSerialization.ReadAttachments(version.AttachmentsJson);
        if (attachments.Count == 0)
        {
            throw new DomainValidationException("A quotation without confirmed evidence cannot be valid.");
        }

        var confirmed = await dbContext.SourcingAttachments
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == version.OrganizationId &&
                record.RfqId == version.RfqId &&
                record.State == (int)SourcingAttachmentState.Confirmed)
            .Select(record => new { record.Id, record.Version, record.Sha256, record.Length })
            .ToArrayAsync(cancellationToken);
        foreach (var attachment in attachments)
        {
            var record = confirmed.SingleOrDefault(candidate =>
                candidate.Id == attachment.FileId && candidate.Version == attachment.Version);
            if (record is null || !string.Equals(record.Sha256, attachment.Sha256, StringComparison.Ordinal) ||
                record.Length != attachment.Length)
            {
                throw new DomainValidationException("Every attachment must stay confirmed and unmodified.");
            }
        }

        if (lines.Any(line => string.IsNullOrWhiteSpace(line.TechnicalResponse)))
        {
            throw new DomainValidationException("Every quoted line requires its technical response.");
        }
    }

    private void AddVersion(
        QuotationRecord root,
        int version,
        int? predecessor,
        QuotationVersionContent content,
        QuotationReview review,
        string digest,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        string? reason)
    {
        dbContext.QuotationVersions.Add(new QuotationVersionRecord
        {
            QuotationId = root.Id,
            Version = version,
            OrganizationId = content.OrganizationId,
            RfqId = content.RfqRef.Id,
            RfqVersion = content.RfqRef.Version,
            RfqContentDigest = content.RfqRef.ContentDigest,
            SupplierId = content.SupplierRef.Id,
            SupplierVersion = content.SupplierRef.Version,
            Currency = content.Currency,
            TermsJson = SourcingSerialization.Terms(content.Terms),
            LinesJson = SourcingSerialization.QuotationLines(content.Lines),
            AttachmentsJson = SourcingSerialization.Attachments(content.Attachments),
            ReviewJson = SourcingSerialization.Review(review),
            ReviewStatus = (int)review.Status,
            Timeliness = (int)content.Timeliness,
            ReceivedAt = content.ReceivedAt,
            RegisteredAt = content.RegisteredAt,
            PredecessorVersion = predecessor,
            ContentDigest = digest,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            Reason = reason
        });
        foreach (var line in content.Lines)
        {
            dbContext.QuotationLineScopes.Add(new QuotationLineScopeRecord
            {
                QuotationId = root.Id,
                Version = version,
                LineId = line.LineRef.Id
            });
        }

        root.CurrentVersion = version;
    }

    private async Task<int?> CurrentQuotationVersionAsync(
        Guid organizationId,
        Guid rfqId,
        Guid supplierId,
        CancellationToken cancellationToken) =>
        await dbContext.Quotations
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.RfqId == rfqId &&
                record.SupplierId == supplierId)
            .Select(record => (int?)record.CurrentVersion)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<QuotationVersionRecord> RequireQuotationVersionAsync(
        Guid organizationId,
        Guid quotationId,
        int version,
        CancellationToken cancellationToken) =>
        await dbContext.QuotationVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.QuotationId == quotationId && record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The quotation version does not exist.");

    private async Task<SourcingCommandRecord?> FindCommandAsync(
        Guid organizationId,
        Guid actorUserId,
        string commandType,
        string commandKey,
        CancellationToken cancellationToken) =>
        await dbContext.SourcingCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record =>
                    record.OrganizationId == organizationId &&
                    record.ActorUserId == actorUserId &&
                    record.CommandType == commandType &&
                    record.CommandKey == commandKey,
                cancellationToken);

    private void AddCommand(
        Guid organizationId,
        Guid actorUserId,
        string commandType,
        string commandKey,
        string fingerprint,
        Guid resultId,
        int resultVersion,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingCommands.Add(new SourcingCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            CommandType = commandType,
            CommandKey = commandKey,
            Fingerprint = fingerprint,
            ResultId = resultId,
            ResultVersion = resultVersion,
            CreatedAt = occurredAt
        });

    private void AddAudit(
        Guid organizationId,
        string action,
        Guid actorUserId,
        string? causeJson,
        IEnumerable<string> changedFields,
        string correlationReference,
        string effectKey,
        string targetJson,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = action,
            ActorJson = Actor(actorUserId),
            CauseJson = causeJson,
            ChangedFieldsJson = System.Text.Json.JsonSerializer.Serialize(
                (changedFields ?? []).Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal)),
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = targetJson
        });

    private void RequireTemporaryUrlPolicy()
    {
        if (!int.TryParse(configuration["Storage:S3:TemporaryUrlLifetimeMinutes"], out var minutes) ||
            minutes is <= 0 or > MaxTemporaryUrlMinutes)
        {
            // Fail closed: evidence never leaves through a link that lives too long (REQ-14).
            throw new SourcingDependencyUnavailableException(
                $"Storage:S3:TemporaryUrlLifetimeMinutes must be between 1 and {MaxTemporaryUrlMinutes}.");
        }
    }

    private static QuotationVersionView ReadQuotationVersion(QuotationVersionRecord record) => new(
        record.QuotationId,
        record.Version,
        new QuotationVersionContent(
            record.OrganizationId,
            new SourcingContentRef(record.RfqId, record.RfqVersion, record.RfqContentDigest),
            new SourcingEntityRef(record.SupplierId, record.SupplierVersion),
            record.Currency,
            SourcingSerialization.ReadTerms(record.TermsJson),
            (QuotationTimeliness)record.Timeliness,
            record.ReceivedAt,
            record.RegisteredAt,
            SourcingSerialization.ReadQuotationLines(record.LinesJson),
            SourcingSerialization.ReadAttachments(record.AttachmentsJson),
            SourcingSerialization.ReadReview(record.ReviewJson)),
        record.ContentDigest,
        record.PredecessorVersion,
        record.ActorUserId,
        record.OccurredAt,
        record.Reason ?? string.Empty);

    private static SourcingAttachmentView View(SourcingAttachmentRecord record) => new(
        record.Id,
        record.Version,
        record.FileName,
        record.ContentType,
        record.Length,
        record.Sha256,
        record.State == (int)SourcingAttachmentState.Confirmed ? "CONFIRMED" : "STAGED");

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";

    private static string Target(string type, Guid id, int version) =>
        $"{{\"id\":\"{id:D}\",\"type\":\"{type}\",\"version\":{version}}}";
}
