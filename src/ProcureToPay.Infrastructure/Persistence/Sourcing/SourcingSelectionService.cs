using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>One command that selects the supplier of one line (REQ-09).</summary>
public sealed record SelectLineCommand(
    Guid OrganizationId,
    Guid RfqId,
    Guid LineId,
    Guid SupplierId,
    int? ExpectedSelectionVersion,
    string? DeviationJustification,
    string CommandKey);

/// <summary>Current selection of one line as read from the contract (REQ-09, REQ-14).</summary>
public sealed record SourcingSelectionView(
    Guid SelectionId,
    int Version,
    Guid RfqId,
    SourcingSelection Selection,
    Guid? EvaluationId,
    int? EvaluationVersion,
    string? EvaluationContentDigest,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Human selection of the supplier of each line (SPEC 10 REQ-09). The recommended set comes from the
/// frozen evaluation, the deviation justification is enforced server-side and a selected line is
/// never chosen twice; the partition of the selected lines tells the proposal builder which lines may
/// share one proposal and which must be split.
/// </summary>
public sealed class SourcingSelectionService(ProcureToPayDbContext dbContext)
{
    private const string SelectCommand = "SELECT_LINE";

    public async Task<SourcingSelectionView> SelectAsync(
        SelectLineCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var (rfq, current, _) = await RequireLiveRfqAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var line = SourcingSerialization.ReadRfqLines(current.LinesJson)
            .SingleOrDefault(candidate => candidate.LineRef.Id == command.LineId)
            ?? throw new DomainConflictException("A selection requires a line of the RFQ.");
        var evaluation = await CurrentEvaluationAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var lineResult = SourcingEvaluationDocument.ReadLineResult(evaluation.DocumentJson, command.LineId);
        var quote = await RequireEligibleQuoteAsync(
            command.OrganizationId, command.RfqId, command.LineId, command.SupplierId, cancellationToken);
        var supplierRef = quote.SupplierRef;
        var selection = SourcingSelection.Create(lineResult, supplierRef, command.DeviationJustification);
        await RequireRequestSupplierAsync(command, cancellationToken);

        var fingerprint = SourcingCommandFingerprints.Transition(
            SelectCommand, command.OrganizationId, actorUserId, command.RfqId, current.Version,
            $"{command.LineId:D}:{command.SupplierId:D}:{selection.DeviationJustification}",
            null,
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, SelectCommand, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different selection.");
            }

            return await GetSelectionAsync(
                command.OrganizationId, replay.ResultId.Value, cancellationToken);
        }

        var root = await dbContext.SourcingSelections
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.RfqId == command.RfqId &&
                          record.LineId == command.LineId,
                cancellationToken);
        if (root is null)
        {
            if (command.ExpectedSelectionVersion is not null)
            {
                throw new DomainConflictException("The line has no selection to revise.");
            }

            root = new SourcingSelectionRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                ProcessId = rfq.ProcessId,
                RfqId = command.RfqId,
                LineId = command.LineId,
                CurrentVersion = 0
            };
            dbContext.SourcingSelections.Add(root);
        }
        else if (root.CurrentVersion != command.ExpectedSelectionVersion)
        {
            throw new DomainConflictException("The selection version is stale.");
        }

        var version = root.CurrentVersion + 1;
        dbContext.SourcingSelectionVersions.Add(new SourcingSelectionVersionRecord
        {
            SelectionId = root.Id,
            Version = version,
            OrganizationId = command.OrganizationId,
            ProcessId = root.ProcessId,
            RfqId = command.RfqId,
            LineId = line.LineRef.Id,
            LineVersion = line.LineRef.Version,
            LineContentDigest = line.LineRef.ContentDigest,
            SupplierId = selection.SupplierRef.Id,
            SupplierVersion = selection.SupplierRef.Version,
            QuotationId = selection.QuotationRef.Id,
            QuotationVersion = selection.QuotationRef.Version,
            QuotationContentDigest = selection.QuotationRef.ContentDigest,
            EvaluationId = evaluation.EvaluationId,
            EvaluationVersion = evaluation.Version,
            EvaluationContentDigest = evaluation.ContentDigest,
            Recommended = selection.Recommended,
            DeviationJustification = selection.DeviationJustification,
            ActorUserId = actorUserId,
            OccurredAt = now
        });
        root.CurrentVersion = version;
        AddCommand(
            command.OrganizationId, actorUserId, SelectCommand, command.CommandKey, fingerprint, root.Id,
            version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionLineSelected, actorUserId,
            selection.Recommended ? ["selection"] : ["selection", "deviation_justification"],
            correlationReference,
            $"selection:{root.Id:D}:v{version}",
            $"{{\"id\":\"{root.Id:D}\",\"type\":\"{SourcingCodes.TargetSelection}\",\"version\":{version}}}",
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetSelectionAsync(command.OrganizationId, root.Id, cancellationToken);
    }

    public async Task<SourcingSelectionView> GetSelectionAsync(
        Guid organizationId,
        Guid selectionId,
        CancellationToken cancellationToken = default)
    {
        var root = await dbContext.SourcingSelections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == selectionId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The selection does not exist in this organization.");
        var row = await dbContext.SourcingSelectionVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.SelectionId == selectionId && record.Version == root.CurrentVersion,
                cancellationToken);
        return Read(root, row);
    }

    public async Task<IReadOnlyList<SourcingSelectionView>> ListSelectionsAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var roots = await dbContext.SourcingSelections
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.LineId)
            .ToArrayAsync(cancellationToken);
        var views = new List<SourcingSelectionView>(roots.Length);
        foreach (var root in roots)
        {
            var row = await dbContext.SourcingSelectionVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.SelectionId == root.Id && record.Version == root.CurrentVersion,
                    cancellationToken);
            views.Add(Read(root, row));
        }

        return views;
    }

    /// <summary>
    /// Partition of the current selections into proposal drafts (REQ-09): same supplier, currency and
    /// compatible terms may share one proposal, any difference splits the set.
    /// </summary>
    public async Task<IReadOnlyList<SourcingProposalDraft>> PartitionAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var selections = await ListSelectionsAsync(organizationId, rfqId, cancellationToken);
        if (selections.Count == 0)
        {
            return [];
        }

        var candidates = new List<SelectionCandidate>(selections.Count);
        foreach (var view in selections)
        {
            var quotation = await dbContext.QuotationVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.QuotationId == view.Selection.QuotationRef.Id &&
                              record.Version == view.Selection.QuotationRef.Version,
                    cancellationToken);
            candidates.Add(new SelectionCandidate(
                view.Selection,
                quotation.Currency,
                SourcingSerialization.ReadTerms(quotation.TermsJson)));
        }

        return SourcingSelectionPartition.Partition(candidates);
    }

    /// <summary>
    /// REQ-09: when the Purchase Request line already declared a supplier, the selection must match it.
    /// </summary>
    private async Task RequireRequestSupplierAsync(
        SelectLineCommand command,
        CancellationToken cancellationToken)
    {
        var declared = await dbContext.PurchaseRequestLineVersions
            .AsNoTracking()
            .Where(record => record.LineId == command.LineId)
            .OrderByDescending(record => record.LineVersion)
            .Select(record => record.SupplierJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(declared))
        {
            return;
        }

        Guid supplierId;
        try
        {
            using var document = JsonDocument.Parse(declared);
            supplierId = document.RootElement.GetProperty("id").GetGuid();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SourcingDependencyUnavailableException("The request supplier reference is corrupted.");
        }

        if (supplierId != command.SupplierId)
        {
            throw new DomainConflictException(
                "The selection must match the supplier already declared by the purchase request line.");
        }
    }

    /// <summary>
    /// REQ-09/REQ-11: only a supplier with a current <c>ON_TIME+VALID</c> quotation on that line is
    /// eligible, so a stale or invalidated answer can never be selected.
    /// </summary>
    private async Task<SourcingSupplierQuote> RequireEligibleQuoteAsync(
        Guid organizationId,
        Guid rfqId,
        Guid lineId,
        Guid supplierId,
        CancellationToken cancellationToken) =>
        (await new SourcingQuotationQueries(dbContext).ListValidAsync(
            organizationId, rfqId, lineId, cancellationToken))
        .SingleOrDefault(quote => quote.SupplierId == supplierId)
        ?? throw new DomainConflictException(
            "The selected supplier has no current ON_TIME+VALID quotation on that line.");

    private async Task<SourcingEvaluationView> CurrentEvaluationAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken)
    {
        var evaluation = await new SourcingEvaluationService(dbContext)
            .GetCurrentEvaluationAsync(organizationId, rfqId, cancellationToken);
        if (evaluation.Version == 0)
        {
            throw new DomainConflictException("A selection requires a current evaluation of the RFQ.");
        }

        return evaluation;
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
            throw new DomainConflictException("This RFQ cannot be selected.");
        }

        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == rfq.ProcessId, cancellationToken)
            ?? throw new SourcingDependencyUnavailableException("The RFQ has no sourcing process.");
        if (process.State == (int)SourcingProcessState.Cancelled)
        {
            throw new DomainConflictException("A cancelled sourcing process cannot select a supplier.");
        }

        return (rfq, current, process);
    }

    private static SourcingSelectionView Read(
        SourcingSelectionRecord root,
        SourcingSelectionVersionRecord row) =>
        new(
            root.Id,
            row.Version,
            root.RfqId,
            SourcingSelection.Stored(
                new SourcingContentRef(row.LineId, row.LineVersion, row.LineContentDigest),
                new SourcingContentRef(row.QuotationId, row.QuotationVersion, row.QuotationContentDigest),
                new SourcingEntityRef(row.SupplierId, row.SupplierVersion),
                row.Recommended,
                row.DeviationJustification),
            row.EvaluationId,
            row.EvaluationVersion,
            row.EvaluationContentDigest,
            row.ActorUserId,
            row.OccurredAt);

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
            ActorJson = $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{actorUserId:D}\"," +
                        "\"workload_client_id\":null,\"workload_issuer\":null}}",
            ChangedFieldsJson = JsonSerializer.Serialize(
                changedFields.Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal)),
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = targetJson
        });
}
