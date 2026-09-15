using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Budget;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Idempotent consumer of <c>approval-result/v2|v3</c> and <c>approval-case-lifecycle/v1</c>
/// (REQ-09): it deduplicates by event and target, verifies organization, case, subject, version,
/// source and target against the persisted Purchase Request manifest, appends the result to the
/// inbox and projects per-line and aggregate status only for the current version. Late events of a
/// superseded case keep their history and never reopen it.
/// </summary>
public sealed class PurchaseRequestApprovalResultConsumer(
    ProcureToPayDbContext dbContext,
    Budget.BudgetReleaseService budgetReleases,
    ILogger<PurchaseRequestApprovalResultConsumer> logger,
    string contractVersion) : IApprovalResultConsumer
{
    public string ContractVersion { get; } = contractVersion;

    /// <summary>Results that end a target without approving it release its open reservation (SPEC 08 REQ-08).</summary>
    private static bool ReleasesReservation(string result) =>
        result is "REJECTED" or "CHANGES_REQUESTED" or "CANCELLED" or "SUPERSEDED";

    public async Task DeliverAsync(
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!string.Equals(delivery.ContractVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The delivered result does not match the consumer contract.");
        }

        ApprovalResultEvent eventValue;
        try
        {
            eventValue = ApprovalResultEvent.Parse(delivery);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                              InvalidOperationException or FormatException)
        {
            throw new DomainValidationException("The approval result payload is not readable.");
        }

        // The inbox is shared by every subject: an event of another domain is not ours to project.
        if (!string.Equals(eventValue.SubjectType, PurchaseRequestCodes.SubjectType, StringComparison.Ordinal))
        {
            return;
        }

        await VerifyBindingsAsync(eventValue, cancellationToken);
        // SPEC 08 REQ-08: a terminal negative result or a superseded case releases the reservation of
        // the target. The release runs before the inbox projection so a crash between them replays
        // both idempotently.
        if (ReleasesReservation(eventValue.Result))
        {
            await ReleaseBudgetAsync(eventValue, delivery, cancellationToken);
        }

        var existing = await dbContext.PurchaseRequestApprovalResults
            .AsNoTracking()
            .AnyAsync(
                record =>
                    record.EventId == eventValue.EventId &&
                    record.ContractVersion == eventValue.ContractVersion &&
                    record.SourceType == eventValue.SourceType &&
                    record.SourceId == eventValue.SourceId &&
                    record.LineId == eventValue.TargetId &&
                    record.LineVersion == eventValue.TargetVersion,
                cancellationToken);
        if (existing)
        {
            return;
        }

        dbContext.PurchaseRequestApprovalResults.Add(new PurchaseRequestApprovalResultRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = eventValue.OrganizationId,
            RequestId = eventValue.SubjectId,
            RequestVersion = eventValue.SubjectVersion,
            EventId = eventValue.EventId,
            ContractVersion = eventValue.ContractVersion,
            CaseId = eventValue.CaseId,
            SourceType = eventValue.SourceType,
            SourceId = eventValue.SourceId,
            SourceKey = eventValue.SourceKey,
            LineId = eventValue.TargetId,
            LineVersion = eventValue.TargetVersion,
            MaterialSnapshotDigest = eventValue.MaterialSnapshotDigest,
            Result = eventValue.Result,
            DecisionId = eventValue.DecisionId,
            OccurredAt = eventValue.OccurredAt,
            CreatedAt = DateTimeOffset.UtcNow
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // At-least-once delivery: a concurrent consumer may have stored the same event first.
            dbContext.ChangeTracker.Clear();
            var stored = await dbContext.PurchaseRequestApprovalResults
                .AsNoTracking()
                .AnyAsync(
                    record => record.EventId == eventValue.EventId &&
                              record.ContractVersion == eventValue.ContractVersion &&
                              record.SourceType == eventValue.SourceType &&
                              record.SourceId == eventValue.SourceId,
                    cancellationToken);
            if (stored)
            {
                return;
            }

            throw;
        }

        if (!eventValue.IsLifecycle)
        {
            await ProjectAsync(eventValue, cancellationToken);
        }

        logger.LogInformation(
            "Purchase request {RequestId} version {Version} consumed approval event {EventId} with result {Result}.",
            eventValue.SubjectId,
            eventValue.SubjectVersion,
            eventValue.EventId,
            eventValue.Result);
    }

    /// <summary>
    /// Organization, subject version, case and target must exist and belong together before the
    /// event can change a projection (REQ-09); anything else fails closed and is retried.
    /// </summary>
    private async Task VerifyBindingsAsync(
        ApprovalResultEvent eventValue,
        CancellationToken cancellationToken)
    {
        if (eventValue.EventId == Guid.Empty || eventValue.OrganizationId == Guid.Empty ||
            eventValue.SubjectId == Guid.Empty || eventValue.SubjectVersion < 1 ||
            eventValue.TargetId == Guid.Empty || eventValue.TargetVersion < 1)
        {
            throw new DomainValidationException("The approval result is missing its immutable identities.");
        }

        if (!string.Equals(eventValue.TargetType, PolicyApprovalTargets.TargetType, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The approval result target is not a purchase request line.");
        }

        var version = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == eventValue.SubjectId && record.Version == eventValue.SubjectVersion,
                cancellationToken)
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The purchase request version of the approval result does not exist.");
        if (version.OrganizationId != eventValue.OrganizationId)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The approval result belongs to another organization.");
        }

        var approvalCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == eventValue.CaseId, cancellationToken)
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The approval case of the result does not exist.");
        if (approvalCase.OrganizationId != eventValue.OrganizationId ||
            approvalCase.SubjectId != eventValue.SubjectId ||
            !string.Equals(approvalCase.SubjectType, PurchaseRequestCodes.SubjectType, StringComparison.Ordinal))
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The approval case of the result does not match the purchase request.");
        }

        if (eventValue.IsLifecycle)
        {
            if (eventValue.PreviousCaseId is null || eventValue.PreviousCaseId == Guid.Empty)
            {
                throw new DomainValidationException("A lifecycle event requires its previous case.");
            }

            var previous = await dbContext.ApprovalCases
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.Id == eventValue.PreviousCaseId, cancellationToken)
                ?? throw new PurchaseRequestDependencyUnavailableException(
                    "The previous approval case of the lifecycle event does not exist.");
            if (previous.SubjectId != eventValue.SubjectId)
            {
                throw new PurchaseRequestDependencyUnavailableException(
                    "The lifecycle event does not belong to the purchase request.");
            }

            return;
        }

        var line = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == eventValue.SubjectId &&
                          record.RequestVersion == eventValue.SubjectVersion &&
                          record.LineId == eventValue.TargetId &&
                          record.LineVersion == eventValue.TargetVersion,
                cancellationToken);
        if (line is null)
        {
            // A result outside the confirmed manifest can be a late event of another version; the
            // projection must not accept it, and the dispatcher will keep it out of the inbox.
            throw new PurchaseRequestDependencyUnavailableException(
                "The approval result target is not part of the confirmed purchase request manifest.");
        }

        if (eventValue.SourceType is not ("APPROVAL_REQUIREMENT" or "EXTERNAL_PREREQUISITE"))
        {
            throw new DomainValidationException("The approval result source is not an obligation.");
        }

        var sourceExists = eventValue.SourceType == "APPROVAL_REQUIREMENT"
            ? await dbContext.ApprovalRequirements.AsNoTracking().AnyAsync(
                record => record.Id == eventValue.SourceId && record.CaseId == eventValue.CaseId,
                cancellationToken)
            : await dbContext.ApprovalPrerequisites.AsNoTracking().AnyAsync(
                record => record.Id == eventValue.SourceId && record.CaseId == eventValue.CaseId,
                cancellationToken);
        if (!sourceExists)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The obligation of the approval result does not belong to the case.");
        }
    }

    /// <summary>
    /// Durable release of the open reservation of one terminal target (SPEC 08 REQ-08). It reuses
    /// the event identity as the release key, so a redelivery resolves to the same operation.
    /// </summary>
    private async Task ReleaseBudgetAsync(
        ApprovalResultEvent eventValue,
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken)
    {
        var trigger = eventValue.IsLifecycle
            ? BudgetReleaseTrigger.ApprovalSuperseded
            : BudgetReleaseTrigger.ApprovalResult;
        await budgetReleases.ReleaseAsync(
            new BudgetReleaseRequest(
                eventValue.OrganizationId,
                eventValue.CaseId,
                eventValue.SubjectId,
                eventValue.SubjectVersion,
                $"release-{eventValue.EventId:D}",
                BudgetReleaseService.ApprovalReleaseReason,
                trigger,
                new BudgetTriggerEvent(eventValue.ContractVersion, eventValue.EventId),
                [
                    new BudgetTarget(
                        eventValue.TargetId,
                        eventValue.TargetVersion,
                        eventValue.MaterialSnapshotDigest,
                        eventValue.TargetType)
                ]),
            BudgetActors.OwnerSystem,
            delivery.CorrelationReference,
            cancellationToken);
        logger.LogInformation(
            "Released the budget reservation of case {CaseId} target {TargetId} after {Result}.",
            eventValue.CaseId,
            eventValue.TargetId,
            eventValue.Result);
    }

    /// <summary>
    /// Per-line projection of the current case (REQ-09): every requirement covering the line must
    /// end APPROVED and every prerequisite SATISFIED; REJECTED and CHANGES_REQUESTED prevail and
    /// CANCELLED/SUPERSEDED never become an approval.
    /// </summary>
    private async Task ProjectAsync(ApprovalResultEvent eventValue, CancellationToken cancellationToken)
    {
        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == eventValue.SubjectId, cancellationToken)
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The purchase request of the approval result does not exist.");
        if (request.CurrentVersion != eventValue.SubjectVersion)
        {
            // A late result of a previous immutable version keeps its history only (REQ-09).
            return;
        }

        var attempt = await dbContext.PurchaseRequestSubmissionAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == eventValue.SubjectId &&
                          record.RequestVersion == eventValue.SubjectVersion,
                cancellationToken);
        if (attempt?.ApprovalCaseId != eventValue.CaseId)
        {
            // A late result of a superseded or cancelled case keeps its history only.
            return;
        }

        var requirements = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .Where(record => record.CaseId == eventValue.CaseId)
            .Select(record => new { record.Id, record.TargetsJson })
            .ToArrayAsync(cancellationToken);
        var prerequisites = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == eventValue.CaseId)
            .Select(record => new { record.Id, record.TargetsJson })
            .ToArrayAsync(cancellationToken);
        var lines = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(record => record.RequestId == eventValue.SubjectId &&
                             record.RequestVersion == eventValue.SubjectVersion)
            .Select(record => new { record.LineId, record.LineVersion })
            .ToArrayAsync(cancellationToken);
        var results = await dbContext.PurchaseRequestApprovalResults
            .AsNoTracking()
            .Where(record => record.CaseId == eventValue.CaseId)
            .ToArrayAsync(cancellationToken);

        var approvalCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleAsync(record => record.Id == eventValue.CaseId, cancellationToken);
        var caseCompleted = approvalCase.Status == (int)ApprovalCaseStatus.Completed;
        var statuses = new List<PurchaseRequestLineStatus>(lines.Length);
        foreach (var line in lines.OrderBy(entry => entry.LineId))
        {
            var coveringRequirements = requirements
                .Where(requirement => Covers(requirement.TargetsJson, line.LineId, line.LineVersion))
                .Select(requirement => requirement.Id)
                .ToArray();
            var coveringPrerequisites = prerequisites
                .Where(prerequisite => Covers(prerequisite.TargetsJson, line.LineId, line.LineVersion))
                .Select(prerequisite => prerequisite.Id)
                .ToArray();

            string? RequirementResult(Guid requirementId) => results
                .Where(record => record.SourceType == "APPROVAL_REQUIREMENT" &&
                                 record.SourceId == requirementId &&
                                 record.LineId == line.LineId &&
                                 record.LineVersion == line.LineVersion)
                .OrderByDescending(record => record.OccurredAt)
                .ThenByDescending(record => record.Id)
                .Select(record => record.Result)
                .FirstOrDefault();

            string? PrerequisiteResult(Guid prerequisiteId) => results
                .Where(record => record.SourceType == "EXTERNAL_PREREQUISITE" &&
                                 record.SourceId == prerequisiteId &&
                                 record.LineId == line.LineId &&
                                 record.LineVersion == line.LineVersion)
                .OrderByDescending(record => record.OccurredAt)
                .ThenByDescending(record => record.Id)
                .Select(record => record.Result)
                .FirstOrDefault();

            // A line without obligations of its own is only approved once the whole case
            // completed; an open case keeps it InApproval instead of vacuous approval (REQ-09).
            if (coveringRequirements.Length == 0 && coveringPrerequisites.Length == 0)
            {
                statuses.Add(caseCompleted ? PurchaseRequestLineStatus.Approved : PurchaseRequestLineStatus.InApproval);
                continue;
            }

            var requirementResults = coveringRequirements.Select(RequirementResult).ToArray();
            var prerequisiteResults = coveringPrerequisites.Select(PrerequisiteResult).ToArray();
            if (requirementResults.Any(result => result == "CHANGES_REQUESTED"))
            {
                statuses.Add(PurchaseRequestLineStatus.ChangesRequested);
                continue;
            }

            if (requirementResults.Any(result => result == "REJECTED"))
            {
                statuses.Add(PurchaseRequestLineStatus.Rejected);
                continue;
            }

            var satisfied = requirementResults.All(result => result == "APPROVED") &&
                            prerequisiteResults.All(result => result == "SATISFIED");
            statuses.Add(satisfied ? PurchaseRequestLineStatus.Approved : PurchaseRequestLineStatus.InApproval);
        }

        var projected = Aggregate(statuses);
        var tracked = await dbContext.PurchaseRequests
            .SingleAsync(record => record.Id == eventValue.SubjectId, cancellationToken);
        if (tracked.Status == (int)projected)
        {
            return;
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var before = tracked.Status;
        tracked.Status = (int)projected;
        tracked.UpdatedAt = occurredAt;
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = eventValue.OrganizationId,
            RequestId = eventValue.SubjectId,
            RequestVersion = eventValue.SubjectVersion,
            Action = ActionOf(projected),
            BeforeStatus = before,
            AfterStatus = (int)projected,
            ReasonCode = "APPROVAL_RESULT",
            Reason = "Approval result projection",
            CorrelationReference = eventValue.EventId.ToString("N")[..24],
            OccurredAt = occurredAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static PurchaseRequestStatus Aggregate(IReadOnlyList<PurchaseRequestLineStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return PurchaseRequestStatus.InApproval;
        }

        var approved = statuses.Count(status => status == PurchaseRequestLineStatus.Approved);
        var rejected = statuses.Count(status => status == PurchaseRequestLineStatus.Rejected);
        var changesRequested = statuses.Count(status => status == PurchaseRequestLineStatus.ChangesRequested);
        if (changesRequested > 0)
        {
            return PurchaseRequestStatus.ChangesRequested;
        }

        if (rejected == statuses.Count)
        {
            return PurchaseRequestStatus.Rejected;
        }

        if (approved == statuses.Count)
        {
            return PurchaseRequestStatus.Approved;
        }

        if (approved > 0)
        {
            return PurchaseRequestStatus.PartiallyApproved;
        }

        return PurchaseRequestStatus.InApproval;
    }

    private static string ActionOf(PurchaseRequestStatus status) => status switch
    {
        PurchaseRequestStatus.Approved => PurchaseRequestSubmissionCodes.ActionApproved,
        PurchaseRequestStatus.ChangesRequested => "CHANGES_REQUESTED",
        PurchaseRequestStatus.Rejected => "REJECTED",
        PurchaseRequestStatus.PartiallyApproved => "PARTIALLY_APPROVED",
        _ => PurchaseRequestSubmissionCodes.ActionInApproval
    };

    private static bool Covers(string targetsJson, Guid lineId, int lineVersion) =>
        ApprovalJsonPersistence.DeserializeTargets(targetsJson)
            .Any(target => target.Id == lineId && target.Version == lineVersion);

    /// <summary>Exact payload of a delivered result or lifecycle event (REQ-09, REQ-10).</summary>
    private sealed record ApprovalResultEvent(
        Guid EventId,
        string ContractVersion,
        Guid CaseId,
        Guid OrganizationId,
        string SubjectType,
        Guid SubjectId,
        int SubjectVersion,
        string TargetType,
        Guid TargetId,
        int TargetVersion,
        string MaterialSnapshotDigest,
        string SourceType,
        Guid SourceId,
        string? SourceKey,
        string Result,
        Guid? DecisionId,
        Guid? PreviousCaseId,
        DateTimeOffset OccurredAt)
    {
        public bool IsLifecycle =>
            string.Equals(ContractVersion, ApprovalEvolutionCodes.LifecycleContractVersion, StringComparison.Ordinal);

        public static ApprovalResultEvent Parse(ApprovalResultDelivery delivery)
        {
            using var document = JsonDocument.Parse(delivery.PayloadJson);
            var root = document.RootElement;
            var contract = root.GetProperty("contract_version").GetString() ?? string.Empty;
            if (!string.Equals(contract, delivery.ContractVersion, StringComparison.Ordinal))
            {
                throw new DomainValidationException("The payload contract version does not match its delivery.");
            }

            var target = root.GetProperty("target");
            string sourceType;
            Guid sourceId;
            string? sourceKey;
            if (root.TryGetProperty("result_source", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.Object)
            {
                sourceType = sourceElement.GetProperty("type").GetString() ?? string.Empty;
                sourceId = sourceElement.GetProperty("id").GetGuid();
                sourceKey = sourceElement.TryGetProperty("key", out var keyElement) &&
                            keyElement.ValueKind == JsonValueKind.String
                    ? keyElement.GetString()
                    : null;
            }
            else
            {
                // Lifecycle events carry the superseded case as their source (REQ-09).
                sourceType = ApprovalEntitySource.CodeOf(ApprovalEntitySourceType.ApprovalCase);
                sourceId = root.GetProperty("previous_case_id").GetGuid();
                sourceKey = null;
            }

            return new ApprovalResultEvent(
                root.GetProperty("event_id").GetGuid(),
                contract,
                root.GetProperty("case_id").GetGuid(),
                root.GetProperty("organization_id").GetGuid(),
                root.GetProperty("subject_type").GetString() ?? string.Empty,
                root.GetProperty("subject_id").GetGuid(),
                root.GetProperty("subject_version").GetInt32(),
                target.GetProperty("type").GetString() ?? string.Empty,
                target.GetProperty("id").GetGuid(),
                target.GetProperty("version").GetInt32(),
                target.GetProperty("material_snapshot_digest").GetString() ?? string.Empty,
                sourceType,
                sourceId,
                sourceKey,
                root.GetProperty("result").GetString() ?? string.Empty,
                ReadGuidOrNull(root, "decision_id"),
                ReadGuidOrNull(root, "previous_case_id"),
                DateTimeOffset.Parse(
                    root.GetProperty("occurred_at").GetString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        private static Guid? ReadGuidOrNull(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
            Guid.TryParse(element.GetString(), out var value)
                ? value
                : null;
    }
}
