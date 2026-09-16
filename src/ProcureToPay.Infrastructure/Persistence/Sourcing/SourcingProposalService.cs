using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>Command that builds the proposal of one selected supplier of an RFQ (REQ-10).</summary>
public sealed record BuildProposalCommand(
    Guid OrganizationId,
    Guid RfqId,
    Guid SupplierId,
    string CommandKey);

/// <summary>Read projection of one proposal version (REQ-10, REQ-14).</summary>
public sealed record SourcingProposalView(
    Guid ProposalId,
    int Version,
    Guid RfqId,
    Guid RequestId,
    int RequestVersion,
    SourcingSelectionBasis SelectionBasis,
    SourcingEntityRef SupplierRef,
    string ContentDigest,
    string DocumentJson,
    string ManifestDigest,
    string ManifestJson,
    IReadOnlyList<Guid> SelectedLines,
    Guid? EvaluationId,
    int? EvaluationVersion,
    Guid? WaiverFactsId,
    string State,
    Guid? ApprovalCaseId,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Builds the <c>SOURCING_PO</c> proposal of one supplier (SPEC 10 REQ-10): the selected lines of the
/// current evaluation, the award candidate derived from the selected quotations, the request bundle it
/// was evaluated against and the completeness manifest that Policy and Approval verify.
/// </summary>
public sealed class SourcingProposalService(ProcureToPayDbContext dbContext)
{
    private const string BuildCommand = "BUILD_PROPOSAL";

    public async Task<SourcingProposalView> BuildAsync(
        BuildProposalCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var (rfq, current, process) = await RequireLiveRfqAsync(
            command.OrganizationId, command.RfqId, cancellationToken);
        var evaluation = await new SourcingEvaluationService(dbContext)
            .GetCurrentEvaluationAsync(command.OrganizationId, command.RfqId, cancellationToken);
        if (evaluation.Version == 0)
        {
            throw new DomainConflictException("A proposal requires a current evaluation of the RFQ.");
        }

        var selections = await new SourcingSelectionService(dbContext)
            .ListSelectionsAsync(command.OrganizationId, command.RfqId, cancellationToken);
        var selectedForSupplier = selections
            .Where(view => view.Selection.SupplierRef.Id == command.SupplierId)
            .ToArray();
        if (selectedForSupplier.Length == 0)
        {
            throw new DomainConflictException("The supplier has no selected line in this RFQ.");
        }

        var terms = new List<(SourcingSelectionView View, CommercialTerms Terms, string Currency)>();
        foreach (var view in selectedForSupplier)
        {
            var quotation = await dbContext.QuotationVersions
                .AsNoTracking()
                .SingleAsync(
                    record => record.QuotationId == view.Selection.QuotationRef.Id &&
                              record.Version == view.Selection.QuotationRef.Version,
                    cancellationToken);
            terms.Add((view, SourcingSerialization.ReadTerms(quotation.TermsJson), quotation.Currency));
        }

        var distinctTerms = terms.Select(entry => entry.Terms).Distinct().ToArray();
        var distinctCurrency = terms.Select(entry => entry.Currency).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctTerms.Length != 1 || distinctCurrency.Length != 1)
        {
            throw new DomainConflictException(
                "Selected lines of different terms or currency must be proposed separately.");
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            BuildCommand,
            command.OrganizationId,
            actorUserId,
            command.RfqId,
            current.Version,
            // Version-independent preimage: the proposal identity is the evaluation it consumes, the
            // supplier and the exact selected lines, so a replay returns its version (NFR-03).
            $"{command.SupplierId:D}:{evaluation.EvaluationId:D}:{evaluation.Version}:{evaluation.ContentDigest}:" +
            string.Join(
                ",",
                selectedForSupplier
                    .Select(view => view.Selection.LineRef.Id)
                    .OrderBy(id => id)
                    .Select(id => id.ToString("D"))),
            null,
            command.CommandKey);
        var replay = await FindCommandAsync(
            command.OrganizationId, actorUserId, BuildCommand, command.CommandKey, cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                replay.ResultId is null || replay.ResultVersion is null)
            {
                throw new DomainConflictException("The command key was reused with a different proposal.");
            }

            return await GetProposalAsync(
                command.OrganizationId, replay.ResultId.Value, replay.ResultVersion.Value, cancellationToken);
        }

        var baseCurrency = await BaseCurrencyAsync(command.OrganizationId, cancellationToken);
        var lines = new List<AwardLine>(selectedForSupplier.Length);
        foreach (var entry in terms.OrderBy(entry => entry.View.Selection.LineRef.Id))
        {
            lines.Add(await AwardLineAsync(
                entry.View, evaluation, baseCurrency, cancellationToken));
        }

        var supplierRef = terms[0].View.Selection.SupplierRef;
        var awardCandidate = new AwardCandidate(
            supplierRef,
            distinctCurrency[0],
            baseCurrency,
            distinctTerms[0],
            lines);
        var requestBundle = await RequestBundleRefAsync(
            command.OrganizationId, process.RequestId, process.RequestVersion, cancellationToken);
        var requestManifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == process.RequestId &&
                          record.RequestVersion == process.RequestVersion,
                cancellationToken)
            ?? throw new DomainConflictException(
                "The purchase request version has no confirmed completeness manifest.");
        var waiver = await dbContext.SourcingWaiverFacts
            .AsNoTracking()
            .Where(record => record.OrganizationId == command.OrganizationId &&
                             record.RfqId == command.RfqId &&
                             record.ApprovalCaseId != null)
            .OrderByDescending(record => record.OccurredAt)
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var root = await dbContext.SourcingProposals
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.RfqId == command.RfqId &&
                          record.SupplierId == command.SupplierId,
                cancellationToken);
        var version = (root?.CurrentVersion ?? 0) + 1;
        if (root is null)
        {
            root = new SourcingProposalRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                ProcessId = rfq.ProcessId,
                RfqId = command.RfqId,
                SupplierId = command.SupplierId,
                CurrentVersion = 0
            };
            dbContext.SourcingProposals.Add(root);
        }

        var selectedLines = lines.Select(line => line.LineRef).ToArray();
        var proposal = new SourcingProposalVersion(
            version,
            version == 1 ? null : version - 1,
            command.OrganizationId,
            new SourcingContentRef(process.RequestId, process.RequestVersion, requestManifest.RequestContentDigest),
            requestBundle,
            SourcingSelectionBasis.Rfq,
            supplierRef,
            selectedLines,
            new SourcingContentRef(evaluation.EvaluationId, evaluation.Version, evaluation.ContentDigest),
            terms.Select(entry => entry.View.Selection.QuotationRef).ToArray(),
            [],
            waiver,
            distinctTerms[0],
            awardCandidate);
        var document = proposal.CanonicalDocument();
        var digest = PolicyCanonicalizer.Hash(document);
        var manifest = new SourcingCompletenessManifest(
            SourcingCodes.PolicyFactProviderId,
            SourcingCodes.PolicyFactProviderContractVersion,
            process.RequestId,
            process.RequestVersion,
            requestManifest.PolicyManifestDigest,
            requestBundle.ResultDigest,
            requestBundle.Id,
            root.Id,
            version,
            selectedLines,
            evaluation.ContentDigest,
            SourcingCanonicalizer.RefSetDigest(terms.Select(entry => entry.View.Selection.QuotationRef)),
            SourcingCatalogSnapshotSet.ComputeDigest(command.OrganizationId, root.Id, version, []),
            awardCandidate.ComputeDigest(),
            PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(SourcingCanonicalizer.Terms(distinctTerms[0]))));
        dbContext.SourcingProposalVersions.Add(new SourcingProposalVersionRecord
        {
            ProposalId = root.Id,
            Version = version,
            OrganizationId = command.OrganizationId,
            ProcessId = rfq.ProcessId,
            RfqId = command.RfqId,
            RequestId = process.RequestId,
            RequestVersion = process.RequestVersion,
            RequestRefJson = SourcingSerialization.ContentRefs([proposal.RequestRef]),
            RequestBundleRefJson = JsonSerializer.Serialize(
                PolicyCanonicalizer.SerializeCanonical(
                    SourcingProposalVersion.EvaluationRefDocument(requestBundle))),
            SelectionBasis = (int)SourcingSelectionBasis.Rfq,
            SupplierId = command.SupplierId,
            SupplierVersion = awardCandidate.SupplierRef.Version,
            EvaluationId = evaluation.EvaluationId,
            EvaluationVersion = evaluation.Version,
            EvaluationContentDigest = evaluation.ContentDigest,
            WaiverFactsId = waiver,
            LineIdsJson = SourcingSerialization.ContentRefs(selectedLines),
            TermsJson = SourcingSerialization.Terms(distinctTerms[0]),
            DocumentJson = document,
            ContentDigest = digest,
            ManifestJson = manifest.CanonicalDocument(),
            ManifestDigest = manifest.ComputeDigest(),
            State = (int)SourcingProposalState.Draft,
            CommandKey = command.CommandKey,
            ActorUserId = actorUserId,
            OccurredAt = now
        });
        root.CurrentVersion = version;
        AddCommand(
            command.OrganizationId, actorUserId, BuildCommand, command.CommandKey, fingerprint, root.Id,
            version, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionProposalBuilt, actorUserId,
            ["proposal"], correlationReference, $"proposal:{root.Id:D}:v{version}",
            Target(SourcingCodes.TargetProposal, root.Id, version),
            now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // REQ-07/REQ-10: the evaluation a proposal consumed can no longer be replaced; a material
        // change forces a new proposal version instead of a silent re-score.
        await new SourcingEvaluationService(dbContext).ConsumeAsync(
            command.OrganizationId, evaluation.EvaluationId, evaluation.Version, cancellationToken);
        return await GetProposalAsync(command.OrganizationId, root.Id, version, cancellationToken);
    }

    public async Task<SourcingProposalView> GetProposalAsync(
        Guid organizationId,
        Guid proposalId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.SourcingProposalVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ProposalId == proposalId && record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The proposal version does not exist.");
        return Read(row);
    }

    public async Task<IReadOnlyList<SourcingProposalView>> ListProposalsAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var roots = await dbContext.SourcingProposals
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.SupplierId)
            .ToArrayAsync(cancellationToken);
        var views = new List<SourcingProposalView>(roots.Length);
        foreach (var root in roots)
        {
            views.Add(await GetProposalAsync(organizationId, root.Id, root.CurrentVersion, cancellationToken));
        }

        return views;
    }

    private static SourcingProposalView Read(SourcingProposalVersionRecord row)
    {
        if (!string.Equals(
                PolicyCanonicalizer.Hash(row.DocumentJson), row.ContentDigest, StringComparison.Ordinal) ||
            !string.Equals(
                PolicyCanonicalizer.Hash(row.ManifestJson), row.ManifestDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored proposal is corrupted.");
        }

        var document = JsonDocument.Parse(row.DocumentJson).RootElement;
        var lines = document.GetProperty("selected_lines").EnumerateArray()
            .Select(element => element.GetProperty("id").GetGuid())
            .ToArray();
        return new SourcingProposalView(
            row.ProposalId,
            row.Version,
            row.RfqId,
            row.RequestId,
            row.RequestVersion,
            (SourcingSelectionBasis)row.SelectionBasis,
            new SourcingEntityRef(row.SupplierId, row.SupplierVersion),
            row.ContentDigest,
            row.DocumentJson,
            row.ManifestDigest,
            row.ManifestJson,
            lines,
            row.EvaluationId,
            row.EvaluationVersion,
            row.WaiverFactsId,
            SourcingStateCodes.ProposalState((SourcingProposalState)row.State),
            row.ApprovalCaseId,
            row.ActorUserId,
            row.OccurredAt);
    }

    /// <summary>
    /// REQ-12: on the RFQ route the award line repeats the selected quotation line exactly, and its
    /// base amount is the normalized gross amount the frozen evaluation already computed.
    /// </summary>
    private async Task<AwardLine> AwardLineAsync(
        SourcingSelectionView selection,
        SourcingEvaluationView evaluation,
        string baseCurrency,
        CancellationToken cancellationToken)
    {
        var quotation = await dbContext.QuotationVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.QuotationId == selection.Selection.QuotationRef.Id &&
                          record.Version == selection.Selection.QuotationRef.Version,
                cancellationToken);
        var quoted = SourcingSerialization.ReadQuotationLines(quotation.LinesJson)
            .SingleOrDefault(line => line.LineRef.Id == selection.Selection.LineRef.Id)
            ?? throw new SourcingDependencyUnavailableException(
                "The selected quotation does not cover its selected line.");
        var lineResult = SourcingEvaluationDocument.ReadLineResult(
            evaluation.DocumentJson, selection.Selection.LineRef.Id);
        var score = lineResult.QuotationScores.SingleOrDefault(candidate =>
            candidate.SupplierRef.Id == selection.Selection.SupplierRef.Id)
            ?? throw new SourcingDependencyUnavailableException(
                "The evaluation has no score for the selected supplier and line.");
        var fx = SourcingEvaluationDocument.ReadFxRef(
            evaluation.DocumentJson, quotation.Currency, baseCurrency);
        return new AwardLine(
            selection.Selection.LineRef,
            quoted.QuantityValue,
            quoted.UnitCode,
            quoted.UnitPriceValue,
            quotation.Currency,
            quoted.GrossTotalValue,
            baseCurrency,
            score.NormalizedGrossAmount,
            fx);
    }

    private async Task<SourcingPolicyEvaluationRef> RequestBundleRefAsync(
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var bundle = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.SubjectId == requestId &&
                record.SubjectVersion == requestVersion)
            .OrderByDescending(record => record.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new DomainConflictException(
            "The purchase request version has no persisted policy evaluation to bind.");
        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleAsync(
                record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                cancellationToken);
        return new SourcingPolicyEvaluationRef(
            bundle.EvaluationSequence,
            bundle.Id,
            bundle.InputDigest,
            bundle.InputDigest,
            manifest.PolicyManifestDigest,
            bundle.PolicyContentDigest,
            bundle.ResultDigest);
    }

    private async Task<string> BaseCurrencyAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await dbContext.Organizations
            .AsNoTracking()
            .Where(record => record.Id == organizationId)
            .Select(record => record.BaseCurrency)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new SourcingDependencyUnavailableException("The organization base currency is unavailable.");

    private async Task<(RfqRecord Rfq, RfqVersionRecord Current, SourcingProcessRecord Process)> RequireLiveRfqAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken)
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
            throw new DomainConflictException("This RFQ cannot build a proposal.");
        }

        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == rfq.ProcessId, cancellationToken)
            ?? throw new SourcingDependencyUnavailableException("The RFQ has no sourcing process.");
        if (process.State == (int)SourcingProcessState.Cancelled)
        {
            throw new DomainConflictException("A cancelled sourcing process cannot build a proposal.");
        }

        return (rfq, current, process);
    }

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

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";

    private static string Target(string type, Guid id, int version) =>
        $"{{\"id\":\"{id:D}\",\"type\":\"{type}\",\"version\":{version}}}";

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
}
