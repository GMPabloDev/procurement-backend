using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>Command that publishes the award of an approved proposal (REQ-12).</summary>
public sealed record PublishAwardCommand(
    Guid OrganizationId,
    Guid ProposalId,
    int ProposalVersion,
    int ExpectedProcessVersion,
    int? ExpectedAwardVersion,
    string AwardKey,
    string Reason);

/// <summary>Read projection of one award version (REQ-12, REQ-14).</summary>
public sealed record SourcingAwardView(
    Guid AwardId,
    int Version,
    Guid ProcessId,
    int ProcessVersion,
    Guid ProposalId,
    int ProposalVersion,
    Guid RequestId,
    int RequestVersion,
    SourcingEntityRef SupplierRef,
    SourcingSelectionBasis SelectionBasis,
    string ContentDigest,
    string DocumentJson,
    IReadOnlyList<Guid> AwardedLines,
    bool Superseded,
    Guid ActorUserId,
    DateTimeOffset PublishedAt);

/// <summary>
/// Verifier of the exact <c>award-consumption/v1</c> contract (REQ-14). It validates the award
/// reference, the exact line set, the takeover and the current supplier eligibility, and it rebuilds
/// the amounts, the catalogue snapshots and the FX snapshots it references. It never creates or
/// reserves a Purchase Order and never returns a partial snapshot.
/// </summary>
public interface IAwardConsumptionVerifier
{
    string VerifierId { get; }

    string ContractVersion { get; }

    Task<AwardConsumptionResponse> VerifyAsync(
        AwardConsumptionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Publication and consumption of sourcing awards (SPEC 10 REQ-11, REQ-12, REQ-14). Publishing is a
/// compare-and-swap under the takeover fence: the award, the process version, the per-line uniqueness
/// pointer and the outbox event are committed together or not at all.
/// </summary>
public sealed class SourcingAwardService(ProcureToPayDbContext dbContext) : IAwardConsumptionVerifier
{
    private const string PublishCommand = "PUBLISH_AWARD";

    public string VerifierId => SourcingCodes.AwardConsumptionVerifierId;

    public string ContractVersion => SourcingCodes.AwardConsumptionContract;

    public async Task<SourcingAwardView> PublishAsync(
        PublishAwardCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var awardKey = SourcingCodes.Key(command.AwardKey, "award_key");
        var reason = SourcingCodes.Reason(command.Reason);
        var proposal = await dbContext.SourcingProposalVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ProposalId == command.ProposalId &&
                          record.Version == command.ProposalVersion &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The proposal version does not exist.");
        var content = SourcingProposalDocument.Read(proposal.DocumentJson, proposal.ContentDigest);
        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == proposal.ProcessId &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new SourcingDependencyUnavailableException("The proposal has no sourcing process.");

        // Idempotent publication: the same award key returns the version it produced (NFR-03).
        var replay = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.ActorUserId == actorUserId &&
                          record.AwardKey == awardKey,
                cancellationToken);
        if (replay is not null)
        {
            return Read(replay);
        }

        if (process.Version != command.ExpectedProcessVersion)
        {
            throw new DomainConflictException("The sourcing process version is stale.");
        }

        if (process.State is not ((int)SourcingProcessState.Active) and not ((int)SourcingProcessState.Awarded))
        {
            throw new DomainConflictException("Only an active or awarded sourcing process can publish an award.");
        }

        var takeover = await dbContext.SourcingTakeovers
            .AsNoTracking()
            .AnyAsync(
                record => record.ProcessId == process.Id && record.ReleasedAt == null,
                cancellationToken);
        if (!takeover)
        {
            throw new DomainConflictException("The award requires a live sourcing takeover.");
        }

        // REQ-11: the proposal only becomes an award after its approval case completed.
        var approvalCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == command.OrganizationId &&
                record.SubjectType == SourcingCodes.ApprovalSubjectType &&
                record.SubjectId == proposal.ProposalId &&
                record.SubjectVersion == proposal.Version)
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException("The proposal has no approval case to publish.");
        if (approvalCase.Status != (int)ApprovalCaseStatus.Completed)
        {
            throw new DomainConflictException("The proposal approval case is not completed.");
        }

        // REQ-11: eligibility is rechecked server-side before publishing; a blocked or stale supplier
        // never receives an award.
        var supplierVersion = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(record => record.SupplierId == proposal.SupplierId && record.Version == proposal.SupplierVersion)
            .Select(record => new { record.Status })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AwardNotEligibleException("The awarded supplier version does not exist.");
        if (supplierVersion.Status != (int)SupplierOperationalStatus.Active)
        {
            throw new AwardNotEligibleException("The awarded supplier is not active.");
        }

        var currentSupplierVersion = await dbContext.Suppliers
            .AsNoTracking()
            .Where(record => record.Id == proposal.SupplierId)
            .Select(record => record.OperationalVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (currentSupplierVersion != proposal.SupplierVersion)
        {
            throw new AwardNotEligibleException("The awarded supplier version is no longer current.");
        }

        // REQ-12: one award lineage per sourcing process. A correction (same supplier or a supplier
        // change) increments the same root and references its predecessor; the CAS below serializes
        // every publication of the process inside one transaction.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var award = await dbContext.SourcingAwards
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.ProcessId == process.Id,
                cancellationToken);
        var version = (award?.CurrentVersion ?? 0) + 1;
        if (award is null)
        {
            if (command.ExpectedAwardVersion is not null)
            {
                throw new DomainConflictException("The sourcing process has no award to revise.");
            }
        }
        else if (command.ExpectedAwardVersion != award.CurrentVersion)
        {
            throw new DomainConflictException("The award version is stale.");
        }

        if (award is null)
        {
            award = new SourcingAwardRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                ProcessId = process.Id,
                RfqId = proposal.RfqId,
                SupplierId = proposal.SupplierId,
                CurrentVersion = 0
            };
            dbContext.SourcingAwards.Add(award);
        }
        else
        {
            // The lineage may correct the supplier: the root follows its current version.
            award.SupplierId = proposal.SupplierId;
        }

        // REQ-12: a correction supersedes the current award of the same process (same supplier or a
        // supplier change), while the first publication finds every selected line free. The takeover
        // is never released: only the current pointer of each line moves.
        var selectedLineIds = content.SelectedLines.Select(line => line.Id).ToArray();
        var currentLines = await dbContext.SourcingCurrentAwardLines
            .Where(row => selectedLineIds.Contains(row.LineId))
            .ToArrayAsync(cancellationToken);
        var processAwardVersions = await dbContext.SourcingAwardVersions
            .Where(row => row.OrganizationId == command.OrganizationId && row.ProcessId == process.Id)
            .ToArrayAsync(cancellationToken);
        if (process.State == (int)SourcingProcessState.Active)
        {
            if (currentLines.Length != 0)
            {
                throw new DomainConflictException("Another current award already owns one of the selected lines.");
            }
        }
        else if (currentLines.Length != content.SelectedLines.Count)
        {
            throw new DomainConflictException(
                "A successor award must supersede the current award of every selected line.");
        }

        foreach (var current in currentLines)
        {
            _ = processAwardVersions.SingleOrDefault(
                row => row.AwardId == current.AwardId && row.Version == current.AwardVersion)
                ?? throw new DomainConflictException(
                    "The selected lines are owned by an award of another sourcing process.");
        }

        // A displaced version is only marked superseded when the successor covers every one of its
        // lines; a partially covered version stays current for the lines it still owns.
        foreach (var row in processAwardVersions)
        {
            var ownsCurrentLine = currentLines.Any(
                current => current.AwardId == row.AwardId && current.AwardVersion == row.Version);
            if (!ownsCurrentLine)
            {
                continue;
            }

            var rowLines = SourcingSerialization.ReadContentRefs(row.LinesJson)
                .Select(reference => reference.Id)
                .ToHashSet();
            if (rowLines.All(lineId => selectedLineIds.Contains(lineId)))
            {
                row.Superseded = true;
            }
        }

        // REQ-01/REQ-12: the process leaves ACTIVE exactly once; a supersession keeps AWARDED and
        // advances its version, so only one branch ever affects the row and the loser re-reads.
        var applied = await dbContext.SourcingProcesses
            .Where(record =>
                record.Id == process.Id &&
                (record.State == (int)SourcingProcessState.Active ||
                 record.State == (int)SourcingProcessState.Awarded) &&
                record.Version == command.ExpectedProcessVersion)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(record => record.State, (int)SourcingProcessState.Awarded)
                    .SetProperty(record => record.Version, command.ExpectedProcessVersion + 1)
                    .SetProperty(record => record.UpdatedAt, now),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The sourcing process changed concurrently.");
        }

        var awardedProcessVersion = command.ExpectedProcessVersion + 1;
        var record = new SourcingAwardVersion(
            version,
            version == 1 ? null : version - 1,
            command.OrganizationId,
            actorUserId,
            now,
            new SourcingEntityRef(process.Id, awardedProcessVersion),
            new SourcingContentRef(proposal.ProposalId, proposal.Version, proposal.ContentDigest),
            content.RequestRef,
            content.SelectionBasis,
            content.SupplierRef,
            content.EvaluationRef,
            content.QuotationRefs,
            content.CatalogSnapshots,
            content.WaiverRef,
            [
                new SourcingPolicyApprovalRef(
                    await RequireProposalEvaluationRefAsync(
                        command.OrganizationId, proposal.ProposalId, proposal.Version, cancellationToken),
                    approvalCase.Id,
                    approvalCase.Version,
                    approvalCase.SourceSnapshotDigest)
            ],
            content.Terms,
            content.AwardCandidate,
            reason);
        var document = record.CanonicalDocument();
        SourcingCodes.RequireCanonicalDocument(document);
        dbContext.SourcingAwardVersions.Add(new SourcingAwardVersionRecord
        {
            AwardId = award.Id,
            Version = version,
            OrganizationId = command.OrganizationId,
            ProcessId = process.Id,
            RfqId = proposal.RfqId,
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            RequestId = proposal.RequestId,
            RequestVersion = proposal.RequestVersion,
            SupplierId = proposal.SupplierId,
            SupplierVersion = proposal.SupplierVersion,
            SelectionBasis = (int)content.SelectionBasis,
            EvaluationId = proposal.EvaluationId,
            EvaluationVersion = proposal.EvaluationVersion,
            EvaluationContentDigest = proposal.EvaluationContentDigest,
            WaiverFactsId = proposal.WaiverFactsId,
            LinesJson = SourcingSerialization.ContentRefs(content.SelectedLines),
            DocumentJson = document,
            ContentDigest = record.ComputeDigest(),
            AwardKey = awardKey,
            PredecessorVersion = version == 1 ? null : version - 1,
            ActorUserId = actorUserId,
            PublishedAt = now,
            Reason = reason
        });
        foreach (var line in content.SelectedLines)
        {
            var existing = await dbContext.SourcingCurrentAwardLines
                .SingleOrDefaultAsync(
                    row => row.LineId == line.Id,
                    cancellationToken);
            if (existing is null)
            {
                dbContext.SourcingCurrentAwardLines.Add(new SourcingCurrentAwardLineRecord
                {
                    LineId = line.Id,
                    LineVersion = line.Version,
                    AwardId = award.Id,
                    AwardVersion = version,
                    OrganizationId = command.OrganizationId
                });
            }
            else
            {
                // The line pointer moves to the published successor; the superseded version was
                // already marked above and stays append-only (REQ-12).
                existing.AwardId = award.Id;
                existing.AwardVersion = version;
            }
        }

        award.CurrentVersion = version;
        AddOutbox(
            command.OrganizationId, award.Id, record, now);
        AddAudit(
            command.OrganizationId, SourcingCodes.ActionAwardPublished, actorUserId, correlationReference,
            $"award:{award.Id:D}:v{version}", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAwardAsync(command.OrganizationId, award.Id, version, cancellationToken);
    }

    public async Task<SourcingAwardView> GetAwardAsync(
        Guid organizationId,
        Guid awardId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AwardId == awardId && record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award version does not exist.");
        return Read(row);
    }

    public async Task<SourcingAwardView> GetCurrentAwardAsync(
        Guid organizationId,
        Guid rfqId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var award = await dbContext.SourcingAwards
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.RfqId == rfqId &&
                          record.SupplierId == supplierId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award does not exist.");
        return await GetAwardAsync(organizationId, award.Id, award.CurrentVersion, cancellationToken);
    }

    public async Task<IReadOnlyList<SourcingAwardView>> ListAwardsAsync(
        Guid organizationId,
        Guid rfqId,
        CancellationToken cancellationToken = default)
    {
        var awards = await dbContext.SourcingAwards
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.RfqId == rfqId)
            .OrderBy(record => record.SupplierId)
            .ToArrayAsync(cancellationToken);
        var views = new List<SourcingAwardView>(awards.Length);
        foreach (var award in awards)
        {
            views.Add(await GetAwardAsync(organizationId, award.Id, award.CurrentVersion, cancellationToken));
        }

        return views;
    }

    /// <summary>
    /// Exact <c>award-consumption/v1</c> verification (REQ-14). Only a current award of a live
    /// takeover whose supplier is still eligible produces a response, and the line set must match
    /// exactly: a stale reference is a conflict and never a partial snapshot.
    /// </summary>
    public async Task<AwardConsumptionResponse> VerifyAsync(
        AwardConsumptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var award = await dbContext.SourcingAwards
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == request.AwardId && record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award is not visible.");
        var row = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AwardId == award.Id && record.Version == request.AwardVersion,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award version is not visible.");
        if (!string.Equals(row.ContentDigest, SourcingCodes.Digest(request.AwardContentDigest, "award digest"),
                StringComparison.Ordinal))
        {
            throw new DomainConflictException("The award digest does not match the published version.");
        }

        // The request must still belong to the takeover and the published line set, exactly.
        var takeover = await dbContext.SourcingTakeovers
            .AsNoTracking()
            .AnyAsync(
                record => record.ProcessId == row.ProcessId &&
                          record.RequestId == request.RequestId &&
                          record.RequestVersion == request.RequestVersion &&
                          record.ReleasedAt == null,
                cancellationToken);
        if (!takeover)
        {
            throw new DomainConflictException("The request is no longer bound to the awarded takeover.");
        }

        var content = SourcingAwardDocument.Read(row.DocumentJson, row.ContentDigest);
        var expected = content.AwardCandidate.Lines
            .Select(line => (line.LineRef.Id, line.LineRef.Version))
            .OrderBy(line => line.Id)
            .ToArray();
        var covered = request.CoveredLines
            .Select(line => (line.Id, line.Version))
            .OrderBy(line => line.Id)
            .ToArray();
        if (expected.Length != covered.Length || !expected.SequenceEqual(covered))
        {
            throw new DomainConflictException("The requested lines do not match the published award.");
        }

        // REQ-12: currency is decided by the per-line index, not by the root version alone, so a
        // partially superseded version remains consumable only for the lines it still owns.
        foreach (var line in expected)
        {
            var current = await dbContext.SourcingCurrentAwardLines
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.LineId == line.Id, cancellationToken);
            if (current is null || current.AwardId != row.AwardId || current.AwardVersion != row.Version)
            {
                throw new DomainConflictException("The award version is not current for the requested lines.");
            }
        }

        var eligible = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(record => record.SupplierId == row.SupplierId && record.Version == row.SupplierVersion)
            .Select(record => record.Status)
            .SingleOrDefaultAsync(cancellationToken);
        var currentSupplierVersion = await dbContext.Suppliers
            .AsNoTracking()
            .Where(record => record.Id == row.SupplierId)
            .Select(record => record.OperationalVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (eligible != (int)SupplierOperationalStatus.Active ||
            currentSupplierVersion != row.SupplierVersion)
        {
            throw new AwardNotEligibleException("The awarded supplier is no longer eligible.");
        }

        var fxSnapshots = new List<SourcingPolicyEvaluationRef>();
        foreach (var line in content.AwardCandidate.Lines.Where(line => line.FxSnapshotRef is not null))
        {
            var snapshot = await dbContext.SourcingFxSnapshots
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.Id == line.FxSnapshotRef!.Id &&
                              record.Version == line.FxSnapshotRef.Version &&
                              record.OrganizationId == request.OrganizationId,
                    cancellationToken)
                ?? throw new SourcingDependencyUnavailableException("A referenced FX snapshot is unavailable.");
            var attachment = SourcingSerialization.ReadAttachments(snapshot.AttachmentJson).Single();
            var domain = new SourcingFxSnapshot(
                snapshot.Id, snapshot.Version, snapshot.BaseCurrency, snapshot.SourceCurrency, snapshot.Rate,
                snapshot.EffectiveAt, snapshot.SourceReference, attachment);
            if (!string.Equals(domain.Digest, snapshot.ContentDigest, StringComparison.Ordinal))
            {
                throw new SourcingDependencyUnavailableException("A stored FX snapshot is corrupted.");
            }

            fxSnapshots.Add(new SourcingPolicyEvaluationRef(
                1, snapshot.Id, domain.Digest, domain.Digest, domain.Digest, domain.Digest, domain.Digest));
        }

        AddAudit(
            request.OrganizationId, SourcingCodes.ActionAwardConsumed, row.ActorUserId,
            $"award:{row.AwardId:D}:consumed", request.WorkloadClientId, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AwardConsumptionResponse(
            new SourcingContentRef(row.AwardId, row.Version, row.ContentDigest),
            new SourcingContentRef(row.ProposalId, row.ProposalVersion, content.ProposalDigest),
            content.RequestRef,
            content.SupplierRef,
            content.AwardCandidate.Lines,
            content.AwardCandidate.Lines.Select(line => new SourcedRef(line.LineRef.Id, line.LineRef.Version))
                .ToImmutableArray(),
            content.AwardCandidate.SourceCurrency,
            content.AwardCandidate.SourceAmount,
            content.AwardCandidate.BaseCurrency,
            content.AwardCandidate.BaseAmount,
            content.CatalogSnapshots,
            SourcingCatalogSnapshotSet.ComputeDigest(
                request.OrganizationId, row.ProposalId, row.ProposalVersion, content.CatalogSnapshots),
            fxSnapshots,
            content.PolicyBundleRef,
            content.Terms,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <c>policy-evaluation-ref/v1</c> of the sourcing evaluation the approval carried (REQ-12). It is
    /// rebuilt from the persisted bundle, so its sequence and five digests are real published values
    /// and never an invented version.
    /// </summary>
    private async Task<SourcingPolicyEvaluationRef> RequireProposalEvaluationRefAsync(
        Guid organizationId,
        Guid proposalId,
        int proposalVersion,
        CancellationToken cancellationToken)
    {
        var bundle = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.SubjectId == proposalId &&
                record.SubjectVersion == proposalVersion &&
                record.Operation == "SOURCING_PO")
            .OrderByDescending(record => record.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException(
                "The proposal has no persisted sourcing policy evaluation to reference.");
        var rehydrated = PolicyEvaluationBundleRehydrator.FromJson(bundle.BundleJson);
        return new SourcingPolicyEvaluationRef(
            bundle.EvaluationSequence,
            bundle.Id,
            rehydrated.FactsDigest ?? bundle.InputDigest,
            bundle.InputDigest,
            rehydrated.ManifestDigest ?? bundle.ResultDigest,
            bundle.PolicyContentDigest,
            bundle.ResultDigest);
    }

    private void AddOutbox(
        Guid organizationId,
        Guid awardId,
        SourcingAwardVersion award,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingOutbox.Add(new SourcingOutboxRecord
        {
            EventId = Guid.NewGuid(),
            OrganizationId = organizationId,
            ContractVersion = SourcingCodes.AwardChangedContract,
            PayloadJson = new JsonObject
            {
                ["award_content_digest"] = award.ComputeDigest(),
                ["award_id"] = awardId.ToString("D"),
                ["award_version"] = award.Version,
                ["contract_version"] = SourcingCodes.AwardChangedContract,
                ["occurred_at"] = SourcingCodes.FormatUtc(occurredAt),
                ["organization_id"] = organizationId.ToString("D"),
                ["proposal_id"] = award.ProposalRef.Id.ToString("D"),
                ["proposal_version"] = award.ProposalRef.Version,
                ["supplier_ref"] = new JsonObject
                {
                    ["id"] = award.SupplierRef.Id.ToString("D"),
                    ["version"] = award.SupplierRef.Version
                }
            }.ToJsonString(),
            OccurredAt = occurredAt
        });

    private void AddAudit(
        Guid organizationId,
        string action,
        Guid actorUserId,
        string correlationReference,
        string effectKey,
        DateTimeOffset occurredAt) =>
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Action = action,
            ActorJson = $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{actorUserId:D}\"," +
                        "\"workload_client_id\":null,\"workload_issuer\":null}}",
            ChangedFieldsJson = "[\"award\"]",
            CorrelationReference = correlationReference,
            EffectKey = effectKey,
            OccurredAt = occurredAt,
            TargetJson = $"{{\"id\":\"{effectKey}\",\"type\":\"{SourcingCodes.TargetAward}\",\"version\":1}}"
        });

    private static SourcingAwardView Read(SourcingAwardVersionRecord row)
    {
        if (!string.Equals(
                PolicyCanonicalizer.Hash(row.DocumentJson), row.ContentDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored award is corrupted.");
        }

        var lines = JsonDocument.Parse(row.LinesJson).RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetGuid())
            .ToArray();
        var content = SourcingAwardDocument.Read(row.DocumentJson, row.ContentDigest);
        return new SourcingAwardView(
            row.AwardId,
            row.Version,
            row.ProcessId,
            content.ProcessRef.Version,
            row.ProposalId,
            row.ProposalVersion,
            row.RequestId,
            row.RequestVersion,
            content.SupplierRef,
            (SourcingSelectionBasis)row.SelectionBasis,
            row.ContentDigest,
            row.DocumentJson,
            lines,
            row.Superseded,
            row.ActorUserId,
            row.PublishedAt);
    }
}
