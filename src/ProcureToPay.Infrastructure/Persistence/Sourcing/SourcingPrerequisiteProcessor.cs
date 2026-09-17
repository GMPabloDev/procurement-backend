using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Sourcing;
using ApprovalTargetView = ProcureToPay.Domain.Modules.Sourcing.ApprovalTargetView;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>Outcome of one processed sourcing prerequisite (REQ-13).</summary>
public sealed record SourcingPrerequisiteOutcome(
    Guid AttemptId,
    Guid PrerequisiteId,
    string OwnerAdapterId,
    string State,
    string? SignalResult,
    bool Satisfied);

/// <summary>Durable owner identity of one processor instance, used as the lease holder (REQ-13).</summary>
public sealed class SourcingProcessorIdentity
{
    public SourcingProcessorIdentity()
    {
        var candidate = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        Owner = candidate.Length <= 120 ? candidate : candidate[^120..];
    }

    public string Owner { get; }
}

/// <summary>
/// Real processors of the two PROCUREMENT-stage owners (SPEC 10 REQ-13). Both recalculate their
/// evidence from SQL, claim one durable attempt under a lease with a fencing token and signal only
/// <c>SATISFIED</c>: an insufficient trace or a technical error leaves the prerequisite <c>WAITING</c>
/// and is retried instead of producing an Approval result.
/// </summary>
public sealed class SourcingPrerequisiteProcessor(
    ProcureToPayDbContext dbContext,
    ApprovalWorkflowService workflow,
    IApprovalOwnerWorkloadRegistry ownerWorkloads,
    SourcingProcessorIdentity instanceIdentity,
    ILogger<SourcingPrerequisiteProcessor> logger)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(10);

    private const string Pending = "PENDING";
    private const string Processing = "PROCESSING";
    private const string Checked = "CHECKED";
    private const string Signalling = "SIGNALLING";
    private const string Completed = "COMPLETED";
    private const string Abandoned = "ABANDONED";

    private static readonly string[] OwnedAdapters =
    [
        SourcingCodes.QuotationStatusOwnerAdapterId,
        SourcingCodes.ProcurementStageOwnerAdapterId
    ];

    private readonly SemaphoreSlim leaseGate = new(1, 1);

    /// <summary>
    /// True when this deployment is the registered owner of that prerequisite: exactly one
    /// registration row exists, its processor identity matches this module and the workload Approval
    /// persisted for the owner resolves to the same client (REQ-13).
    /// </summary>
    public async Task<bool> IsRegisteredAsync(
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.SourcingPrerequisiteProcessorRegistrations
            .AsNoTracking()
            .Where(row => row.AdapterId == adapterId &&
                          row.AdapterVersion == SourcingCodes.OwnerAdapterVersion)
            .ToArrayAsync(cancellationToken);
        if (rows.Length != 1 ||
            !string.Equals(rows[0].ProcessorId, ProcessorId(adapterId), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var workload = ownerWorkloads.ResolveExactlyOne(adapterId, SourcingCodes.OwnerAdapterVersion);
            return string.Equals(workload.ClientId, ProcessorId(adapterId), StringComparison.Ordinal) &&
                   string.Equals(workload.Issuer, rows[0].WorkloadIssuer, StringComparison.Ordinal) &&
                   string.Equals(workload.ClientId, rows[0].WorkloadClientId, StringComparison.Ordinal);
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>Readiness of both owners: zero or two registrations degrade instead of claiming.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapterId in OwnedAdapters)
        {
            if (!await IsRegisteredAsync(adapterId, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Claims every processable attempt of both owners and advances it once (REQ-13).</summary>
    public async Task<IReadOnlyList<SourcingPrerequisiteOutcome>> ProcessDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var ready = false;
        foreach (var adapterId in OwnedAdapters)
        {
            if (await IsRegisteredAsync(adapterId, cancellationToken))
            {
                ready = true;
            }
        }

        if (!ready)
        {
            return [];
        }

        var waiting = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => OwnedAdapters.Contains(record.OwnerAdapterId) &&
                             record.OwnerAdapterVersion == SourcingCodes.OwnerAdapterVersion &&
                             (record.Status == (int)PrerequisiteStatus.Waiting ||
                              record.Status == (int)PrerequisiteStatus.Satisfied ||
                              record.Status == (int)PrerequisiteStatus.Failed))
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var recoverable = await dbContext.SourcingOwnerAttempts
            .AsNoTracking()
            .Where(record => record.State != Completed && record.State != Abandoned)
            .Select(record => record.PrerequisiteId)
            .ToArrayAsync(cancellationToken);
        var outcomes = new List<SourcingPrerequisiteOutcome>();
        foreach (var prerequisiteId in waiting.Union(recoverable).OrderBy(id => id))
        {
            var outcome = await ProcessOneAsync(prerequisiteId, now, cancellationToken);
            if (outcome is not null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>Processes one prerequisite end to end; null when another live lease owns it.</summary>
    public async Task<SourcingPrerequisiteOutcome?> ProcessOneAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var attempt = await FindOrCreateAttemptAsync(prerequisiteId, now, cancellationToken);
        if (attempt is null)
        {
            return null;
        }

        if (!await IsRegisteredAsync(attempt.OwnerAdapterId, cancellationToken))
        {
            return null;
        }

        if (!await ClaimAsync(attempt, now, cancellationToken))
        {
            return null;
        }

        await using var heartbeat = new LeaseHeartbeat(
            dbContext.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The sourcing processor has no configured connection."),
            attempt.Id,
            instanceIdentity.Owner,
            attempt.FencingToken,
            LeaseRenewalInterval,
            LeaseDuration,
            leaseGate);
        heartbeat.Start();
        return await ProcessClaimedAsync(attempt, now, cancellationToken);
    }

    /// <summary>
    /// Cancels the local attempts of one pre-award process without signalling Approval (REQ-13): the
    /// prerequisites of the Purchase Request stay WAITING for another process.
    /// </summary>
    public async Task<int> AbandonAttemptsAsync(
        Guid processId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // An attempt may predate the RFQ resolution, so the process, its RFQ and its cases (the
        // Purchase Request case and the proposals of that process) all identify its attempts.
        var rfqId = await dbContext.Rfqs
            .AsNoTracking()
            .Where(record => record.ProcessId == processId)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var caseIds = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record => record.OrganizationId == dbContext.SourcingProcesses
                    .Where(process => process.Id == processId)
                    .Select(process => process.OrganizationId)
                    .First())
            .Where(record => record.SubjectType == "PURCHASE_REQUEST" &&
                             dbContext.SourcingProcesses
                                 .Where(process => process.Id == processId)
                                 .Select(process => process.RequestId)
                                 .Contains(record.SubjectId) &&
                             record.SubjectVersion == dbContext.SourcingProcesses
                                 .Where(process => process.Id == processId)
                                 .Select(process => process.RequestVersion)
                                 .First())
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var proposalCases = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record => record.SubjectType == SourcingCodes.ApprovalSubjectType &&
                             dbContext.SourcingProposalVersions
                                 .Where(proposal => proposal.ProcessId == processId)
                                 .Select(proposal => proposal.ProposalId)
                                 .Contains(record.SubjectId))
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var attempts = await dbContext.SourcingOwnerAttempts
            .Where(record => (record.ProcessId == processId ||
                              (rfqId != null && record.RfqId == rfqId) ||
                              caseIds.Contains(record.CaseId) ||
                              proposalCases.Contains(record.CaseId)) &&
                             record.State != Completed &&
                             record.State != Abandoned)
            .ToArrayAsync(cancellationToken);
        foreach (var attempt in attempts)
        {
            attempt.State = Abandoned;
            attempt.AbandonedAt = now;
            attempt.LeaseOwner = null;
            attempt.LeaseExpiresAt = null;
            attempt.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return attempts.Length;
    }

    private async Task<SourcingPrerequisiteOutcome?> ProcessClaimedAsync(
        SourcingOwnerAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleAsync(record => record.Id == attempt.PrerequisiteId, cancellationToken);
        ApprovalWorkloadIdentity ownerWorkload;
        try
        {
            ownerWorkload = ownerWorkloads.ResolveExactlyOne(
                prerequisite.OwnerAdapterId, prerequisite.OwnerAdapterVersion);
        }
        catch (DomainException exception)
        {
            // Zero or two registered workloads for the owner: nothing is claimable (REQ-13).
            logger.LogWarning(
                exception,
                "The owner workload of {PrerequisiteId} is ambiguous; the attempt stays retryable.",
                attempt.PrerequisiteId);
            await RetryAsync(attempt, "SOURCING_OWNER_AMBIGUOUS", now, cancellationToken);
            return null;
        }

        if (!string.Equals(ownerWorkload.Issuer, prerequisite.OwnerWorkloadIssuer, StringComparison.Ordinal) ||
            !string.Equals(ownerWorkload.ClientId, prerequisite.OwnerWorkloadClientId, StringComparison.Ordinal))
        {
            // The workload Approval persisted for this prerequisite is not the one this module owns.
            await RetryAsync(attempt, "SOURCING_OWNER_MISMATCH", now, cancellationToken);
            return null;
        }
        var targets = ApprovalJsonPersistence.DeserializeTargets(prerequisite.TargetsJson)
            .Select(target => new ApprovalTargetView(
                target.Type, target.Id, target.Version, target.MaterialSnapshotDigest))
            .ToArray();
        var parametersDigest = Sha256(prerequisite.ParametersJson);
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleAsync(record => record.Id == prerequisite.CaseId, cancellationToken);

        // A crash after a confirmed signal finalizes the recorded signal with its own key instead of
        // signalling twice (REQ-13, NFR-03).
        if (prerequisite.Status == (int)PrerequisiteStatus.Satisfied && attempt.State != Completed)
        {
            var recorded = await dbContext.ApprovalPrerequisiteSignals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.PrerequisiteId == attempt.PrerequisiteId &&
                              record.SignalKey == attempt.SignalKey,
                    cancellationToken);
            if (recorded is not null)
            {
                if (!await CompleteAsync(attempt, "SATISFIED", recorded.EvidenceDigest, now, cancellationToken))
                {
                    return null;
                }

                return new SourcingPrerequisiteOutcome(
                    attempt.Id, attempt.PrerequisiteId, attempt.OwnerAdapterId, Completed, "SATISFIED", true);
            }
        }

        if (attempt.State == Completed)
        {
            return new SourcingPrerequisiteOutcome(
                attempt.Id, attempt.PrerequisiteId, attempt.OwnerAdapterId, Completed, attempt.SignalResult,
                string.Equals(attempt.SignalResult, "SATISFIED", StringComparison.Ordinal));
        }

        if (!await SaveAttemptAsync(attempt, current => current.State = Processing, cancellationToken))
        {
            return null;
        }

        string? evidenceDigest = null;
        var satisfied = false;
        try
        {
            switch (attempt.OwnerAdapterId)
            {
                case SourcingCodes.QuotationStatusOwnerAdapterId:
                    var quotationEvidence = await CheckQuotationsAsync(
                        attempt, prerequisite, caseRecord, targets, parametersDigest, cancellationToken);
                    satisfied = quotationEvidence is not null;
                    evidenceDigest = quotationEvidence?.Digest;
                    if (quotationEvidence is not null)
                    {
                        dbContext.SourcingOwnerEvidence.Add(Evidence(attempt, quotationEvidence,
                            QuotationStatusEvidence.ContractVersion, now));
                    }

                    break;
                case SourcingCodes.ProcurementStageOwnerAdapterId:
                    var procurementEvidence = await CheckProcurementAsync(
                        attempt, prerequisite, caseRecord, targets, parametersDigest, cancellationToken);
                    satisfied = procurementEvidence is not null;
                    evidenceDigest = procurementEvidence?.Digest;
                    if (procurementEvidence is not null)
                    {
                        dbContext.SourcingOwnerEvidence.Add(Evidence(attempt, procurementEvidence,
                            ProcurementStageEvidence.ContractVersion, now));
                    }

                    break;
                default:
                    throw new DomainValidationException("The prerequisite is not owned by the sourcing domain.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Insufficiency is not a technical error, but any unavailable dependency stays retryable:
            // the prerequisite remains WAITING and is never marked FAILED by the owner (REQ-13).
            logger.LogWarning(
                exception,
                "The sourcing prerequisite {PrerequisiteId} could not be checked and stays retryable.",
                attempt.PrerequisiteId);
            await RetryAsync(attempt, "SOURCING_CHECK_UNAVAILABLE", now, cancellationToken);
            return null;
        }

        if (!satisfied || evidenceDigest is null)
        {
            // The trace does not meet its minimum: no signal, no FAILED, retry later.
            await RetryAsync(attempt, "SOURCING_MINIMUM_NOT_MET", now, cancellationToken);
            return new SourcingPrerequisiteOutcome(
                attempt.Id, attempt.PrerequisiteId, attempt.OwnerAdapterId, Pending, null, false);
        }

        if (!await SaveAttemptAsync(
                attempt,
                current =>
                {
                    current.State = Checked;
                    current.SignalResult = "SATISFIED";
                    current.EvidenceDigest = evidenceDigest;
                    current.NextAttemptAt = now;
                },
                cancellationToken))
        {
            return null;
        }

        if (!await SaveAttemptAsync(attempt, current => current.State = Signalling, cancellationToken))
        {
            return null;
        }

        var signalKey = attempt.SignalKey;
        try
        {
            _ = await workflow.SignalAsync(
                attempt.PrerequisiteId,
                new ApprovalSignalCommand(
                    ownerWorkload,
                    true,
                    signalKey,
                    prerequisite.Version,
                    $"sourcing://{attempt.Id:D}",
                    evidenceDigest,
                    signalKey),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "The sourcing prerequisite {PrerequisiteId} could not be signalled and stays retryable.",
                attempt.PrerequisiteId);
            await RetryAsync(attempt, "SOURCING_SIGNAL_UNAVAILABLE", now, cancellationToken);
            return null;
        }

        if (!await CompleteAsync(attempt, "SATISFIED", evidenceDigest, now, cancellationToken))
        {
            return null;
        }

        logger.LogInformation(
            "The sourcing prerequisite {PrerequisiteId} of {Owner} was signalled SATISFIED.",
            attempt.PrerequisiteId,
            attempt.OwnerAdapterId);
        return new SourcingPrerequisiteOutcome(
            attempt.Id, attempt.PrerequisiteId, attempt.OwnerAdapterId, Completed, "SATISFIED", true);
    }

    /// <summary>
    /// REQ-13: the quotation owner recalculates the valid count of every target with the same query
    /// the evaluation uses. The requirement is met with the published minimum or with an approved
    /// waiver whose recalculated facts still match; a zero count never satisfies anything (REQ-05).
    /// </summary>
    private async Task<QuotationStatusEvidence?> CheckQuotationsAsync(
        SourcingOwnerAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        ApprovalCaseRecord caseRecord,
        IReadOnlyList<ApprovalTargetView> targets,
        string parametersDigest,
        CancellationToken cancellationToken)
    {
        var parameters = QuotationPrerequisiteParameters.Parse(prerequisite.ParametersJson);
        var rfqId = await ResolveRfqAsync(attempt, caseRecord, cancellationToken);
        if (rfqId is null)
        {
            return null;
        }

        var queries = new SourcingQuotationQueries(dbContext);
        var counts = await queries.CountValidAsync(caseRecord.OrganizationId, rfqId.Value, cancellationToken);
        var countsByTarget = targets.ToDictionary(
            target => target.Id,
            target => counts.TryGetValue(target.Id, out var count) ? count : 0);

        var effectiveMinimum = await EffectiveMinimumAsync(
            attempt, prerequisite, caseRecord, countsByTarget, parameters, cancellationToken);
        if (effectiveMinimum is null)
        {
            return null;
        }

        var quotations = new List<SourcingContentRef>();
        foreach (var target in targets)
        {
            foreach (var quote in await queries.ListValidAsync(
                         caseRecord.OrganizationId, rfqId.Value, target.Id, cancellationToken))
            {
                quotations.Add(new SourcingContentRef(quote.QuotationId, quote.Version, quote.ContentDigest));
            }
        }

        return new QuotationStatusEvidence(
            attempt.Id,
            attempt.PrerequisiteId,
            caseRecord.OrganizationId,
            parametersDigest,
            attempt.SignalKey,
            DateTimeOffset.UtcNow,
            countsByTarget,
            effectiveMinimum.Value.Minimum,
            quotations.DistinctBy(reference => (reference.Id, reference.Version)).ToArray(),
            targets,
            effectiveMinimum.Value.WaiverVerificationDigest);
    }

    /// <summary>
    /// Effective minimum of one quotation requirement: the published minimum, or the reduction of the
    /// newest approved waiver whose facts still describe the current trace (REQ-05, REQ-13).
    /// </summary>
    private async Task<(int Minimum, string? WaiverVerificationDigest)?> EffectiveMinimumAsync(
        SourcingOwnerAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        ApprovalCaseRecord caseRecord,
        IReadOnlyDictionary<Guid, int> countsByTarget,
        QuotationPrerequisiteParameters parameters,
        CancellationToken cancellationToken)
    {
        var lowest = countsByTarget.Values.DefaultIfEmpty(0).Min();
        if (lowest <= 0)
        {
            return null;
        }

        if (lowest >= parameters.MinimumQuotations)
        {
            return (parameters.MinimumQuotations, null);
        }

        var waiver = await dbContext.SourcingWaiverFacts
            .AsNoTracking()
            .Where(record => record.OrganizationId == caseRecord.OrganizationId &&
                             record.PrerequisiteId == prerequisite.Id &&
                             record.ApprovalCaseId != null)
            .OrderByDescending(record => record.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (waiver is null || lowest < waiver.Floor || lowest < waiver.To)
        {
            return null;
        }

        var waiverCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == waiver.ApprovalCaseId, cancellationToken);
        if (waiverCase is null || waiverCase.Status != (int)ApprovalCaseStatus.Completed)
        {
            return null;
        }

        // The facts must still describe the trace: the counts they recorded are rechecked here, so a
        // withdrawn or invalidated answer invalidates the waiver too (REQ-05). A tampered document
        // never reduces the minimum either: it must rehash to its recorded digest.
        var facts = SourcingSerialization.ReadWaiverFacts(waiver.DocumentJson);
        if (!string.Equals(facts.ComputeDigest(), waiver.ContentDigest, StringComparison.Ordinal))
        {
            return null;
        }

        if (facts.Targets.Any(target =>
                !countsByTarget.TryGetValue(target.LineRef.Id, out var count) ||
                count < target.ValidQuotations ||
                count < waiver.To))
        {
            return null;
        }

        return (waiver.To, waiver.ContentDigest);
    }

    /// <summary>
    /// REQ-13: the procurement owner distinguishes the persisted subject. On a proposal case it
    /// satisfies when the current proposal, its evaluation and its manifest still match its targets;
    /// on a Purchase Request case only when every target is covered by a current published award whose
    /// proposal Policy and Approval completed.
    /// </summary>
    private async Task<ProcurementStageEvidence?> CheckProcurementAsync(
        SourcingOwnerAttemptRecord attempt,
        ApprovalPrerequisiteRecord prerequisite,
        ApprovalCaseRecord caseRecord,
        IReadOnlyList<ApprovalTargetView> targets,
        string parametersDigest,
        CancellationToken cancellationToken)
    {
        var awards = new List<SourcingContentRef>();
        var bundles = new List<SourcingContentRef>();
        if (string.Equals(caseRecord.SubjectType, SourcingCodes.ApprovalSubjectType, StringComparison.Ordinal))
        {
            var proposal = await dbContext.SourcingProposalVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.ProposalId == caseRecord.SubjectId &&
                              record.Version == caseRecord.SubjectVersion,
                    cancellationToken);
            if (proposal is null || proposal.ManifestDigest is null)
            {
                return null;
            }

            var root = await dbContext.SourcingProposals
                .AsNoTracking()
                .SingleAsync(record => record.Id == proposal.ProposalId, cancellationToken);
            if (root.CurrentVersion != proposal.Version)
            {
                return null;
            }

            var lines = SourcingSerialization.ReadContentRefs(proposal.LineIdsJson);
            if (!lines.Select(line => line.Id).ToHashSet().SetEquals(targets.Select(target => target.Id)))
            {
                return null;
            }

            bundles.Add(new SourcingContentRef(proposal.ProposalId, proposal.Version, proposal.ContentDigest));
        }
        else if (string.Equals(caseRecord.SubjectType, "PURCHASE_REQUEST", StringComparison.Ordinal))
        {
            foreach (var target in targets)
            {
                var current = await dbContext.SourcingCurrentAwardLines
                    .AsNoTracking()
                    .SingleOrDefaultAsync(record => record.LineId == target.Id, cancellationToken);
                if (current is null)
                {
                    return null;
                }

                var awardRow = await dbContext.SourcingAwardVersions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        record => record.AwardId == current.AwardId && record.Version == current.AwardVersion,
                        cancellationToken);
                if (awardRow is null || awardRow.Superseded ||
                    awardRow.RequestId != caseRecord.SubjectId ||
                    awardRow.RequestVersion != caseRecord.SubjectVersion)
                {
                    return null;
                }

                var root = await dbContext.SourcingAwards
                    .AsNoTracking()
                    .SingleAsync(record => record.Id == awardRow.AwardId, cancellationToken);
                if (root.CurrentVersion != awardRow.Version)
                {
                    return null;
                }

                var proposalCase = await dbContext.ApprovalCases
                    .AsNoTracking()
                    .Where(record => record.SubjectType == SourcingCodes.ApprovalSubjectType &&
                                     record.SubjectId == awardRow.ProposalId &&
                                     record.SubjectVersion == awardRow.ProposalVersion)
                    .OrderByDescending(record => record.Version)
                    .FirstOrDefaultAsync(cancellationToken);
                if (proposalCase is null || proposalCase.Status != (int)ApprovalCaseStatus.Completed)
                {
                    return null;
                }

                awards.Add(new SourcingContentRef(awardRow.AwardId, awardRow.Version, awardRow.ContentDigest));
                var proposalRow = await dbContext.SourcingProposalVersions
                    .AsNoTracking()
                    .SingleAsync(
                        record => record.ProposalId == awardRow.ProposalId &&
                                  record.Version == awardRow.ProposalVersion,
                        cancellationToken);
                bundles.Add(new SourcingContentRef(
                    proposalRow.ProposalId, proposalRow.Version, proposalRow.ContentDigest));
            }
        }
        else
        {
            return null;
        }

        return new ProcurementStageEvidence(
            attempt.Id,
            attempt.PrerequisiteId,
            caseRecord.OrganizationId,
            parametersDigest,
            attempt.SignalKey,
            DateTimeOffset.UtcNow,
            awards,
            bundles,
            targets);
    }

    /// <summary>
    /// RFQ whose trace the quotation requirement measures: the live process of the request for a
    /// Purchase Request case, or the process of the proposal for a proposal case (REQ-13).
    /// </summary>
    private async Task<Guid?> ResolveRfqAsync(
        SourcingOwnerAttemptRecord attempt,
        ApprovalCaseRecord caseRecord,
        CancellationToken cancellationToken)
    {
        if (attempt.RfqId is Guid known)
        {
            return known;
        }

        Guid? processId = attempt.ProcessId;
        if (processId is null)
        {
            processId = string.Equals(caseRecord.SubjectType, SourcingCodes.ApprovalSubjectType, StringComparison.Ordinal)
                ? await dbContext.SourcingProposalVersions
                    .AsNoTracking()
                    .Where(record => record.ProposalId == caseRecord.SubjectId &&
                                     record.Version == caseRecord.SubjectVersion)
                    .Select(record => (Guid?)record.ProcessId)
                    .SingleOrDefaultAsync(cancellationToken)
                : await dbContext.SourcingTakeovers
                    .AsNoTracking()
                    .Where(record => record.RequestId == caseRecord.SubjectId && record.ReleasedAt == null)
                    .Select(record => (Guid?)record.ProcessId)
                    .FirstOrDefaultAsync(cancellationToken);
        }

        if (processId is null)
        {
            return null;
        }

        var rfqId = await dbContext.Rfqs
            .AsNoTracking()
            .Where(record => record.ProcessId == processId)
            .Select(record => (Guid?)record.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (rfqId is not null && (attempt.ProcessId is null || attempt.RfqId != rfqId))
        {
            attempt.ProcessId = processId;
            _ = await SaveAttemptAsync(
                attempt,
                current =>
                {
                    current.ProcessId = processId;
                    current.RfqId = rfqId;
                },
                cancellationToken);
        }

        return rfqId;
    }

    private async Task<SourcingOwnerAttemptRecord?> FindOrCreateAttemptAsync(
        Guid prerequisiteId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.SourcingOwnerAttempts
            .SingleOrDefaultAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var prerequisite = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken);
        if (prerequisite is null ||
            !OwnedAdapters.Contains(prerequisite.OwnerAdapterId) ||
            !string.Equals(
                prerequisite.OwnerAdapterVersion, SourcingCodes.OwnerAdapterVersion, StringComparison.Ordinal))
        {
            return null;
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisite.CaseId, cancellationToken);
        if (caseRecord is null)
        {
            return null;
        }

        var attempt = new SourcingOwnerAttemptRecord
        {
            Id = Guid.NewGuid(),
            PrerequisiteId = prerequisiteId,
            OrganizationId = caseRecord.OrganizationId,
            CaseId = prerequisite.CaseId,
            SubjectType = caseRecord.SubjectType,
            OwnerAdapterId = prerequisite.OwnerAdapterId,
            OwnerAdapterVersion = prerequisite.OwnerAdapterVersion,
            TargetsJson = prerequisite.TargetsJson,
            State = Pending,
            CheckKey = $"{prerequisite.OwnerAdapterId}:{prerequisiteId:D}:check",
            SignalKey = $"{prerequisite.OwnerAdapterId}:{prerequisiteId:D}:signal",
            DueAt = caseRecord.CreatedAt,
            NextAttemptAt = caseRecord.CreatedAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SourcingOwnerAttempts.Add(attempt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.SourcingOwnerAttempts
                .SingleAsync(record => record.PrerequisiteId == prerequisiteId, cancellationToken);
        }

        return attempt;
    }

    private async Task<bool> ClaimAsync(
        SourcingOwnerAttemptRecord attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (attempt.State is Completed or Abandoned ||
            attempt.NextAttemptAt > now ||
            attempt.LeaseExpiresAt is DateTimeOffset until && until > now)
        {
            return false;
        }

        attempt.LeaseOwner = instanceIdentity.Owner;
        attempt.LeaseExpiresAt = now + LeaseDuration;
        attempt.FencingToken += 1;
        attempt.Attempts += 1;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    private async Task<bool> SaveAttemptAsync(
        SourcingOwnerAttemptRecord attempt,
        Action<SourcingOwnerAttemptRecord> mutate,
        CancellationToken cancellationToken)
    {
        var expectedToken = attempt.FencingToken;
        var owner = instanceIdentity.Owner;
        await leaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = dbContext.Entry(attempt);
            if (entry.State == EntityState.Detached)
            {
                dbContext.Attach(attempt);
            }

            await entry.ReloadAsync(cancellationToken);
            if (!string.Equals(attempt.LeaseOwner, owner, StringComparison.Ordinal) ||
                attempt.FencingToken != expectedToken ||
                attempt.LeaseExpiresAt is not DateTimeOffset until ||
                until <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            mutate(attempt);
            attempt.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        finally
        {
            leaseGate.Release();
        }
    }

    private async Task RetryAsync(
        SourcingOwnerAttemptRecord attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _ = await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Pending;
                current.LastErrorCode = errorCode;
                current.NextAttemptAt = now + TimeSpan.FromSeconds(5);
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
            },
            cancellationToken);

    private async Task<bool> CompleteAsync(
        SourcingOwnerAttemptRecord attempt,
        string signalResult,
        string? evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SaveAttemptAsync(
            attempt,
            current =>
            {
                current.State = Completed;
                current.SignalResult = signalResult;
                current.EvidenceDigest ??= evidenceDigest;
                current.CompletedAt = now;
                current.LeaseOwner = null;
                current.LeaseExpiresAt = null;
            },
            cancellationToken);

    private static SourcingOwnerEvidenceRecord Evidence(
        SourcingOwnerAttemptRecord attempt,
        QuotationStatusEvidence evidence,
        string contractVersion,
        DateTimeOffset now)
    {
        var document = evidence.CanonicalDocument();
        SourcingCodes.RequireCanonicalDocument(document);
        return new SourcingOwnerEvidenceRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = attempt.OrganizationId,
            PrerequisiteId = attempt.PrerequisiteId,
            AttemptId = attempt.Id,
            ContractVersion = contractVersion,
            Result = "SATISFIED",
            SignalKey = attempt.SignalKey,
            DocumentJson = document,
            ContentDigest = evidence.Digest,
            ActorUserId = attempt.Id,
            OccurredAt = now
        };
    }

    private static SourcingOwnerEvidenceRecord Evidence(
        SourcingOwnerAttemptRecord attempt,
        ProcurementStageEvidence evidence,
        string contractVersion,
        DateTimeOffset now)
    {
        var document = evidence.CanonicalDocument();
        SourcingCodes.RequireCanonicalDocument(document);
        return new SourcingOwnerEvidenceRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = attempt.OrganizationId,
            PrerequisiteId = attempt.PrerequisiteId,
            AttemptId = attempt.Id,
            ContractVersion = contractVersion,
            Result = "SATISFIED",
            SignalKey = attempt.SignalKey,
            DocumentJson = document,
            ContentDigest = evidence.Digest,
            ActorUserId = attempt.Id,
            OccurredAt = now
        };
    }

    /// <summary>Stable processor id registered for one owner, which is also its workload client.</summary>
    private static string ProcessorId(string adapterId) => adapterId switch
    {
        SourcingCodes.QuotationStatusOwnerAdapterId => SourcingCodes.QuotationStatusProcessorId,
        SourcingCodes.ProcurementStageOwnerAdapterId => SourcingCodes.ProcurementStageProcessorId,
        _ => throw new DomainValidationException("The owner is not a sourcing owner.")
    };

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Independent lease heartbeat (REQ-13): a dedicated connection renews the lease every ten seconds
    /// so a blocked signal never lets the 30-second lease expire under a live worker.
    /// </summary>
    private sealed class LeaseHeartbeat(
        string connectionString,
        Guid attemptId,
        string owner,
        long fencingToken,
        TimeSpan interval,
        TimeSpan duration,
        SemaphoreSlim gate) : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private Task? loop;

        public void Start() => loop = Task.Run(RunAsync);

        private async Task RunAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(interval, cancellation.Token);
                    await gate.WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellation.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SET XACT_ABORT ON; BEGIN TRANSACTION; " +
                        "DECLARE @seen int; " +
                        "SELECT @seen = 1 FROM [Sourcing].[SourcingOwnerAttempts] " +
                        "WITH (UPDLOCK, ROWLOCK) WHERE [Id] = @id; " +
                        "UPDATE [Sourcing].[SourcingOwnerAttempts] " +
                        "SET [LeaseExpiresAt] = DATEADD(SECOND, @seconds, SYSUTCDATETIME()) " +
                        "WHERE [Id] = @id AND [LeaseOwner] = @owner AND [FencingToken] = @token " +
                        "AND [LeaseExpiresAt] > SYSUTCDATETIME(); " +
                        "SET @affected = @@ROWCOUNT; COMMIT TRANSACTION;";
                    command.Parameters.AddWithValue("@seconds", (int)duration.TotalSeconds);
                    command.Parameters.AddWithValue("@id", attemptId);
                    command.Parameters.AddWithValue("@owner", owner);
                    command.Parameters.AddWithValue("@token", fencingToken);
                    var affectedParameter = command.Parameters.Add("@affected", SqlDbType.Int);
                    affectedParameter.Direction = ParameterDirection.Output;
                    await command.ExecuteNonQueryAsync(cancellation.Token);
                    if (affectedParameter.Value is not int affected || affected == 0)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A transient renewal failure never kills the attempt: the next tick retries and
                    // every effect stays fenced.
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop;
                }
                catch (OperationCanceledException)
                {
                    // The heartbeat stops with the attempt.
                }
            }

            cancellation.Dispose();
        }
    }
}
