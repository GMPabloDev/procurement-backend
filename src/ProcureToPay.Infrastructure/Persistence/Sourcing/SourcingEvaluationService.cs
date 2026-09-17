using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>One immutable FX snapshot declared by the Buyer for an RFQ (REQ-08).</summary>
public sealed record RegisterFxSnapshotCommand(
    Guid OrganizationId,
    Guid RfqId,
    string SourceCurrency,
    decimal Rate,
    DateTimeOffset EffectiveAt,
    string SourceReference,
    SourcingAttachmentRef Attachment,
    string CommandKey);

/// <summary>One manual criterion score with its motive and confirmed evidence (REQ-07).</summary>
public sealed record RecordManualScoreCommand(
    Guid OrganizationId,
    Guid RfqId,
    Guid LineId,
    Guid SupplierId,
    SourcingEvaluationCriterion Criterion,
    decimal Score,
    string Justification,
    SourcingAttachmentRef Evidence,
    string CommandKey);

public sealed record EvaluateRfqCommand(Guid OrganizationId, Guid RfqId, string CommandKey);

/// <summary>Read projection of one persisted evaluation version (REQ-07, REQ-14).</summary>
public sealed record SourcingFxSnapshotView(
    Guid Id,
    int Version,
    string BaseCurrency,
    string SourceCurrency,
    decimal Rate,
    DateTimeOffset EffectiveAt,
    string SourceReference,
    string ContentDigest);

public sealed record SourcingManualScoreView(
    Guid Id,
    Guid RfqId,
    Guid LineId,
    int LineVersion,
    Guid SupplierId,
    int SupplierVersion,
    string Criterion,
    decimal Score,
    string Justification,
    string ContentDigest,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);

public sealed record SourcingEvaluationView(
    Guid EvaluationId,
    int Version,
    Guid RfqId,
    int RfqVersion,
    string BaseCurrency,
    string ContentDigest,
    string DocumentJson,
    IReadOnlyList<SourcingEntityRef> Recommendations,
    IReadOnlyList<SourcingContentRef> QuotationRefs,
    IReadOnlyList<Guid> CoveredLines,
    bool Consumed,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Weighted evaluation of an RFQ (SPEC 10 REQ-07, REQ-08, REQ-09). The service freezes the inputs
/// that the formulas consume (valid quotations, FX snapshots, manual scores, supplier performance),
/// appends exactly one version per command and refuses to replace a version a proposal consumed.
/// </summary>
public sealed class SourcingEvaluationService(ProcureToPayDbContext dbContext)
{
    private const string RegisterFxCommand = "REGISTER_FX_SNAPSHOT";
    private const string RecordManualCommand = "RECORD_MANUAL_SCORE";
    private const string EvaluateCommand = "EVALUATE_RFQ";

    public async Task<SourcingFxSnapshotView> RegisterFxSnapshotAsync(
        RegisterFxSnapshotCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var (rfq, current, _) = await RequireLiveRfqAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var evidence = await RequireConfirmedAttachmentAsync(
            command.OrganizationId, rfq.Id, command.Attachment, cancellationToken);
        var baseCurrency = await BaseCurrencyAsync(command.OrganizationId, cancellationToken);
        var snapshot = new SourcingFxSnapshot(
            Guid.NewGuid(),
            1,
            baseCurrency,
            command.SourceCurrency,
            command.Rate,
            command.EffectiveAt,
            command.SourceReference,
            evidence);
        var fingerprint = SourcingCommandFingerprints.Transition(
            RegisterFxCommand, command.OrganizationId, actorUserId, command.RfqId, current.Version,
            snapshot.Digest, command.EffectiveAt, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, RegisterFxCommand, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null)
            {
                throw new DomainConflictException("The command key was reused with a different FX snapshot.");
            }

            return await GetFxSnapshotAsync(
                command.OrganizationId, replay.ResultId.Value, cancellationToken);
        }

        dbContext.SourcingFxSnapshots.Add(new SourcingFxSnapshotRecord
        {
            Id = snapshot.Id,
            Version = snapshot.Version,
            OrganizationId = command.OrganizationId,
            RfqId = command.RfqId,
            BaseCurrency = snapshot.BaseCurrency,
            SourceCurrency = snapshot.SourceCurrency,
            Rate = snapshot.Rate,
            EffectiveAt = snapshot.EffectiveAt,
            SourceReference = snapshot.SourceReference,
            AttachmentJson = SourcingSerialization.Attachments([snapshot.Attachment]),
            DocumentJson = FxDocument(snapshot),
            ContentDigest = snapshot.Digest,
            ActorUserId = actorUserId,
            OccurredAt = now
        });
        AddCommand(
            command.OrganizationId, actorUserId, RegisterFxCommand, command.CommandKey, fingerprint,
            snapshot.Id, 1, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionFxRegistered, actorUserId,
            ["fx"], correlationReference, $"fx:{snapshot.Id:D}:registered",
            Target("SourcingFxSnapshot", snapshot.Id, 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetFxSnapshotAsync(command.OrganizationId, snapshot.Id, cancellationToken);
    }

    public async Task<SourcingManualScoreView> RecordManualScoreAsync(
        RecordManualScoreCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var (_, current, _) = await RequireLiveRfqAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var line = SourcingSerialization.ReadRfqLines(current.LinesJson)
            .SingleOrDefault(candidate => candidate.LineRef.Id == command.LineId)
            ?? throw new DomainConflictException("A manual score requires a line of the RFQ.");
        var evidence = await RequireConfirmedAttachmentAsync(
            command.OrganizationId, rfqId: current.RfqId, command.Evidence, cancellationToken);
        var supplier = await RequireValidQuoteAsync(
            command.OrganizationId, command.RfqId, command.LineId, command.SupplierId, cancellationToken);
        var input = new ManualCriterionInput(
            command.Criterion, line.LineRef, supplier, command.Score, command.Justification, evidence);
        var fingerprint = SourcingCommandFingerprints.Transition(
            RecordManualCommand, command.OrganizationId, actorUserId, command.RfqId, current.Version,
            input.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), null, command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, RecordManualCommand, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null)
            {
                throw new DomainConflictException("The command key was reused with a different manual score.");
            }

            return await GetManualScoreAsync(
                command.OrganizationId, replay.ResultId.Value, cancellationToken);
        }

        var id = Guid.NewGuid();
        dbContext.SourcingManualScores.Add(new SourcingManualScoreRecord
        {
            Id = id,
            OrganizationId = command.OrganizationId,
            RfqId = command.RfqId,
            LineId = input.LineRef.Id,
            LineVersion = input.LineRef.Version,
            SupplierId = input.SupplierRef.Id,
            SupplierVersion = input.SupplierRef.Version,
            Criterion = (int)input.Criterion,
            Score = input.Score,
            Justification = input.Justification,
            EvidenceJson = SourcingSerialization.Attachments([input.Evidence]),
            ContentDigest = ManualScoreDigest(input),
            ActorUserId = actorUserId,
            OccurredAt = now
        });
        AddCommand(
            command.OrganizationId, actorUserId, RecordManualCommand, command.CommandKey, fingerprint,
            id, current.Version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionManualScoreRecorded, actorUserId,
            ["score"], correlationReference,
            $"manual-score:{id:D}",
            Target("SourcingManualScore", id, 1), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetManualScoreAsync(command.OrganizationId, id, cancellationToken);
    }

    /// <summary>
    /// Appends one evaluation version from the frozen facts of the RFQ. A material change after a
    /// proposal consumed the current version is refused: the caller must supersede the proposal with
    /// a new one instead of rewriting the evaluation it used (REQ-07, REQ-08).
    /// </summary>
    public async Task<SourcingEvaluationView> EvaluateAsync(
        EvaluateRfqCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var (rfq, current, _) = await RequireLiveRfqAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var baseCurrency = await BaseCurrencyAsync(command.OrganizationId, cancellationToken);
        var fingerprint = SourcingCommandFingerprints.Transition(
            EvaluateCommand, command.OrganizationId, actorUserId, command.RfqId, current.Version, null, null,
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, EvaluateCommand, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different evaluation.");
            }

            return await GetEvaluationAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
        }

        var lines = SourcingSerialization.ReadRfqLines(current.LinesJson);
        var quotations = new SourcingQuotationQueries(dbContext);
        var quotes = new List<EvaluationQuote>();
        foreach (var line in lines)
        {
            foreach (var quote in await quotations.ListValidAsync(
                         command.OrganizationId, command.RfqId, line.LineRef.Id, cancellationToken))
            {
                quotes.Add(new EvaluationQuote(
                    new SourcingContentRef(quote.QuotationId, quote.Version, quote.ContentDigest),
                    quote.SupplierRef,
                    quote.Currency,
                    quote.Terms,
                    quote.Lines));
            }
        }

        var performance = await SupplierPerformanceAsync(quotes, cancellationToken);
        var fx = await FxSnapshotsAsync(command.OrganizationId, command.RfqId, baseCurrency, cancellationToken);
        var manual = await ManualInputsAsync(command.OrganizationId, command.RfqId, lines, cancellationToken);
        var evaluation = SourcingEvaluationEngine.Evaluate(
            command.OrganizationId,
            new SourcingContentRef(current.RfqId, current.Version, current.ContentDigest),
            baseCurrency,
            SourcingSerialization.ReadWeights(current.WeightsJson),
            lines,
            quotes,
            performance,
            fx,
            manual);

        var root = await dbContext.SourcingEvaluations
            .SingleOrDefaultAsync(
                record => record.RfqId == command.RfqId && record.OrganizationId == command.OrganizationId,
                cancellationToken);
        if (root is null)
        {
            root = new SourcingEvaluationRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                RfqId = command.RfqId,
                CurrentVersion = 0
            };
            dbContext.SourcingEvaluations.Add(root);
        }
        else if (root.ConsumedVersion == root.CurrentVersion)
        {
            throw new DomainConflictException(
                "The current evaluation was consumed by a proposal; a new proposal version is required.");
        }

        var version = root.CurrentVersion + 1;
        var document = evaluation.CanonicalDocument();
        SourcingCodes.RequireCanonicalDocument(document);
        var digest = PolicyCanonicalizer.Hash(document);
        dbContext.QuoteEvaluationVersions.Add(new QuoteEvaluationVersionRecord
        {
            EvaluationId = root.Id,
            Version = version,
            OrganizationId = command.OrganizationId,
            RfqId = current.RfqId,
            RfqVersion = current.Version,
            RfqContentDigest = current.ContentDigest,
            BaseCurrency = baseCurrency,
            DocumentJson = document,
            ContentDigest = digest,
            RecommendationsJson = SourcingSerialization.EntityRefs(evaluation.Recommendations),
            QuotationRefsJson = SourcingSerialization.ContentRefs(evaluation.QuotationRefs),
            FxSnapshotRefsJson = SourcingSerialization.FxRefs(evaluation.FxSnapshots),
            ActorUserId = actorUserId,
            OccurredAt = now
        });
        root.CurrentVersion = version;
        AddCommand(
            command.OrganizationId, actorUserId, EvaluateCommand, command.CommandKey, fingerprint,
            root.Id, version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionEvaluationCreated, actorUserId,
            ["evaluation"], correlationReference, $"evaluation:{root.Id:D}:v{version}",
            Target("SourcingEvaluation", root.Id, version), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetEvaluationAsync(command.OrganizationId, root.Id, version, cancellationToken);
    }

    /// <summary>Marks one evaluation version as consumed by a proposal; it can never be rewritten (REQ-07).</summary>
    public async Task ConsumeAsync(
        Guid organizationId,
        Guid evaluationId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var root = await dbContext.SourcingEvaluations
            .SingleOrDefaultAsync(
                record => record.Id == evaluationId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The evaluation does not exist in this organization.");
        var row = await dbContext.QuoteEvaluationVersions
            .SingleOrDefaultAsync(
                record => record.EvaluationId == evaluationId && record.Version == version,
                cancellationToken)
            ?? throw new DomainNotFoundException("The evaluation version does not exist.");
        if (row.ConsumedAt is not null)
        {
            return;
        }

        row.ConsumedAt = DateTimeOffset.UtcNow;
        root.ConsumedVersion = version;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SourcingEvaluationView> GetEvaluationAsync(
        Guid organizationId,
        Guid evaluationId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.QuoteEvaluationVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.EvaluationId == evaluationId && record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The evaluation version does not exist.");
        return Read(row);
    }

    private async Task<SourcingEvaluationView> GetStoredEvaluationAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        var root = await dbContext.SourcingEvaluations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RfqId == rfqId && record.OrganizationId == organizationId,
                cancellationToken);
        if (root is null || root.CurrentVersion == 0)
        {
            // No evaluation exists yet: the command only recorded an input, so a minimal projection
            // keeps the API answer useful without inventing an evaluation (REQ-07).
            return new SourcingEvaluationView(
                Guid.Empty, 0, rfqId, 0, await BaseCurrencyAsync(organizationId, cancellationToken),
                string.Empty, string.Empty, [], [], [], false, Guid.Empty, DateTimeOffset.MinValue);
        }

        return await GetEvaluationAsync(organizationId, root.Id, root.CurrentVersion, cancellationToken);
    }

    public async Task<SourcingEvaluationView> GetCurrentEvaluationAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var root = await dbContext.SourcingEvaluations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RfqId == rfqId && record.OrganizationId == organizationId,
                cancellationToken);
        return root is null || root.CurrentVersion == 0
            ? await GetStoredEvaluationAsync(organizationId, rfqId, cancellationToken)
            : await GetEvaluationAsync(organizationId, root.Id, root.CurrentVersion, cancellationToken);
    }

    private async Task<SourcingFxSnapshotView> GetFxSnapshotAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.SourcingFxSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == id && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The FX snapshot does not exist.");
        if (!string.Equals(
                PolicyCanonicalizer.Hash(row.DocumentJson), row.ContentDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored FX snapshot is corrupted.");
        }

        return new SourcingFxSnapshotView(
            row.Id, row.Version, row.BaseCurrency, row.SourceCurrency, row.Rate, row.EffectiveAt,
            row.SourceReference, row.ContentDigest);
    }

    private async Task<SourcingManualScoreView> GetManualScoreAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.SourcingManualScores
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == id && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The manual score does not exist.");
        return new SourcingManualScoreView(
            row.Id, row.RfqId, row.LineId, row.LineVersion, row.SupplierId, row.SupplierVersion,
            SourcingStateCodes.Criterion((SourcingEvaluationCriterion)row.Criterion), row.Score,
            row.Justification, row.ContentDigest, row.ActorUserId, row.OccurredAt);
    }

    private static string ManualScoreDigest(ManualCriterionInput input) =>
        PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["attachment_ref"] = SourcingCanonicalizer.Attachment(input.Evidence),
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["contract_version"] = ManualCriterionInput.ContractVersion,
                ["criterion"] = SourcingStateCodes.Criterion(input.Criterion),
                ["justification"] = input.Justification,
                ["line_ref"] = SourcingCanonicalizer.ContentRef(input.LineRef),
                ["score"] = SourcingCodes.Decimal(input.Score),
                ["supplier_ref"] = SourcingCanonicalizer.EntityRef(input.SupplierRef)
            }));

    private static SourcingEvaluationView Read(QuoteEvaluationVersionRecord row)
    {
        // The stored bytes must hash back to the recorded digest: a tampered document is refused.
        if (!string.Equals(
                PolicyCanonicalizer.Hash(row.DocumentJson), row.ContentDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored evaluation is corrupted.");
        }

        var covered = JsonDocument.Parse(row.DocumentJson).RootElement
            .GetProperty("criteria")
            .EnumerateArray()
            .Select(element => element.GetProperty("line_ref").GetProperty("id").GetGuid())
            .Distinct()
            .ToArray();
        return new SourcingEvaluationView(
            row.EvaluationId,
            row.Version,
            row.RfqId,
            row.RfqVersion,
            row.BaseCurrency,
            row.ContentDigest,
            row.DocumentJson,
            SourcingSerialization.ReadEntityRefs(row.RecommendationsJson),
            SourcingSerialization.ReadContentRefs(row.QuotationRefsJson),
            covered,
            row.ConsumedAt is not null,
            row.ActorUserId,
            row.OccurredAt);
    }

    private async Task<(RfqRecord Rfq, RfqVersionRecord Current, SourcingProcessRecord Process)>
        RequireLiveRfqAsync(Guid organizationId, Guid rfqId, CancellationToken cancellationToken)
    {
        var rfq = await dbContext.Rfqs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == rfqId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The RFQ does not exist in this organization.");
        var current = await dbContext.RfqVersions
            .AsNoTracking()
            .SingleAsync(record => record.RfqId == rfqId && record.Version == rfq.CurrentVersion, cancellationToken);
        if ((RfqStatus)current.Status is RfqStatus.Draft or RfqStatus.Cancelled)
        {
            throw new DomainConflictException("This RFQ cannot be evaluated.");
        }

        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == rfq.ProcessId, cancellationToken)
            ?? throw new SourcingDependencyUnavailableException("The RFQ has no sourcing process.");
        if (process.State == (int)SourcingProcessState.Cancelled)
        {
            throw new DomainConflictException("A cancelled sourcing process cannot be evaluated.");
        }

        return (rfq, current, process);
    }

    private async Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await dbContext.Organizations
            .AsNoTracking()
            .Where(record => record.Id == organizationId)
            .Select(record => record.BaseCurrency)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new SourcingDependencyUnavailableException("The organization base currency is unavailable.");

    private async Task<SourcingAttachmentRef> RequireConfirmedAttachmentAsync(
        Guid organizationId,
        Guid rfqId,
        SourcingAttachmentRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var record = await dbContext.SourcingAttachments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == reference.FileId && row.Version == reference.Version &&
                       row.OrganizationId == organizationId && row.RfqId == rfqId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The referenced evidence does not exist in this RFQ.");
        if (record.State != (int)SourcingAttachmentState.Confirmed ||
            !string.Equals(record.Sha256, reference.Sha256, StringComparison.Ordinal) ||
            record.Length != reference.Length ||
            !string.Equals(record.ContentType, reference.ContentType, StringComparison.Ordinal) ||
            !string.Equals(record.FileName, reference.FileName, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The evidence reference does not match the confirmed attachment.");
        }

        return new SourcingAttachmentRef(
            record.ContentType, record.Id, record.FileName, record.Length, record.Sha256, record.Version);
    }

    private async Task<SourcingEntityRef> RequireValidQuoteAsync(
        Guid organizationId,
        Guid rfqId,
        Guid lineId,
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var quotations = new SourcingQuotationQueries(dbContext);
        var quote = (await quotations.ListValidAsync(organizationId, rfqId, lineId, cancellationToken))
            .SingleOrDefault(candidate => candidate.SupplierId == supplierId)
            ?? throw new DomainConflictException(
                "A manual score requires a current ON_TIME+VALID quotation of that supplier on that line.");
        return quote.SupplierRef;
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>> SupplierPerformanceAsync(
        IReadOnlyList<EvaluationQuote> quotes,
        CancellationToken cancellationToken)
    {
        var performance = new Dictionary<Guid, decimal>();
        foreach (var supplier in quotes.Select(quote => quote.SupplierRef).DistinctBy(reference => reference.Id))
        {
            var content = await dbContext.SupplierVersions
                .AsNoTracking()
                .Where(record => record.SupplierId == supplier.Id && record.Version == supplier.Version)
                .Select(record => record.PerformanceJson)
                .SingleOrDefaultAsync(cancellationToken);
            if (content is null)
            {
                continue;
            }

            var score = ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierSerialization
                .ReadPerformance(content);
            if (score is not null)
            {
                performance[supplier.Id] = score.Score;
            }
        }

        return performance;
    }

    private async Task<IReadOnlyDictionary<string, SourcingFxSnapshot>> FxSnapshotsAsync(
        Guid organizationId,
        Guid rfqId,
        string baseCurrency,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SourcingFxSnapshots
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.EffectiveAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var snapshots = new Dictionary<string, SourcingFxSnapshot>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var attachment = SourcingSerialization.ReadAttachments(row.AttachmentJson).Single();
            var snapshot = new SourcingFxSnapshot(
                row.Id, row.Version, row.BaseCurrency, row.SourceCurrency, row.Rate, row.EffectiveAt,
                row.SourceReference, attachment);
            if (!string.Equals(snapshot.Digest, row.ContentDigest, StringComparison.Ordinal))
            {
                throw new SourcingDependencyUnavailableException("The stored FX snapshot is corrupted.");
            }

            if (!string.Equals(snapshot.BaseCurrency, baseCurrency, StringComparison.Ordinal))
            {
                // A snapshot of another base currency never enters an evaluation of this organization.
                throw new SourcingDependencyUnavailableException(
                    "A stored FX snapshot declares a different base currency.");
            }

            // The newest snapshot of the pair wins; the older ones stay for past evaluations.
            snapshots[snapshot.SourceCurrency] = snapshot;
        }
        return snapshots;
    }

    private async Task<IReadOnlyList<ManualCriterionInput>> ManualInputsAsync(
        Guid organizationId,
        Guid rfqId,
        IReadOnlyList<RfqLine> lines,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SourcingManualScores
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.OccurredAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var latest = new Dictionary<(Guid LineId, Guid SupplierId, int Criterion), SourcingManualScoreRecord>();
        foreach (var row in rows)
        {
            latest[(row.LineId, row.SupplierId, row.Criterion)] = row;
        }

        return latest.Values
            .Select(row =>
            {
                var line = lines.SingleOrDefault(candidate => candidate.LineRef.Id == row.LineId)
                    ?? throw new SourcingDependencyUnavailableException(
                        "A manual score references a line outside its RFQ.");
                if (line.LineRef.Version != row.LineVersion)
                {
                    throw new DomainConflictException(
                        "A manual score references a stale line version; record it again.");
                }

                return new ManualCriterionInput(
                    (SourcingEvaluationCriterion)row.Criterion,
                    line.LineRef,
                    new SourcingEntityRef(row.SupplierId, row.SupplierVersion),
                    row.Score,
                    row.Justification,
                    SourcingSerialization.ReadAttachments(row.EvidenceJson).Single());
            })
            .ToImmutableArray();
    }

    private static string FxDocument(SourcingFxSnapshot snapshot) =>
        PolicyCanonicalizer.SerializeCanonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attachment_ref"] = SourcingCanonicalizer.Attachment(snapshot.Attachment),
            ["base_currency"] = snapshot.BaseCurrency,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = SourcingFxSnapshot.ContractVersion,
            ["effective_at"] = SourcingCodes.FormatUtc(snapshot.EffectiveAt),
            ["rate"] = SourcingCodes.Decimal(snapshot.Rate),
            ["source_currency"] = snapshot.SourceCurrency,
            ["source_reference"] = snapshot.SourceReference
        });

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
            ChangedFieldsJson = JsonSerializer.Serialize(
                changedFields.Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal)),
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = targetJson
        });

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";

    private static string Target(string type, Guid id, int version) =>
        $"{{\"id\":\"{id:D}\",\"type\":\"{type}\",\"version\":{version}}}";

}
