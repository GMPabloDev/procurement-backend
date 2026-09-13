using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>Copies aggregate state back to the tracked persistence records.</summary>
public static class ApprovalStateSync
{
    public static void Case(ApprovalCaseRecord record, ApprovalCase approvalCase)
    {
        record.Status = (int)approvalCase.Status;
        record.Version = approvalCase.Version;
        record.CancelledAt = approvalCase.CancelledAt;
        record.CancellationReason = approvalCase.CancellationReason;
    }

    public static void Requirement(ApprovalRequirementRecord record, ApprovalRequirement requirement)
    {
        record.Status = (int)requirement.Status;
        record.Version = requirement.Version;
    }

    public static void Task(ApprovalTaskRecord record, ApprovalTask task)
    {
        record.Status = (int)task.Status;
        record.Version = task.Version;
        record.CurrentAssigneeUserId = task.CurrentAssigneeUserId;
    }

    public static void Prerequisite(ApprovalPrerequisiteRecord record, ExternalPrerequisite prerequisite)
    {
        record.Status = (int)prerequisite.Status;
        record.Version = prerequisite.Version;
        record.SignalKey = prerequisite.SignalKey;
        record.SignalFingerprint = prerequisite.SignalFingerprint;
        record.ResolvedAt = prerequisite.ResolvedAt;
    }

    public static void Apply(
        ApprovalCase approvalCase,
        ApprovalCaseRecord caseRecord,
        IReadOnlyCollection<ApprovalRequirementRecord> requirementRecords,
        IReadOnlyCollection<ApprovalTaskRecord> taskRecords,
        IReadOnlyCollection<ApprovalPrerequisiteRecord> prerequisiteRecords)
    {
        Case(caseRecord, approvalCase);
        foreach (var requirement in approvalCase.Requirements)
        {
            Requirement(requirementRecords.Single(record => record.Id == requirement.Id), requirement);
        }

        foreach (var task in approvalCase.Tasks)
        {
            Task(taskRecords.Single(record => record.Id == task.Id), task);
        }

        foreach (var prerequisite in approvalCase.Prerequisites)
        {
            Prerequisite(prerequisiteRecords.Single(record => record.Id == prerequisite.Id), prerequisite);
        }
    }
}

public sealed record ApprovalSignalCommand(
    ApprovalWorkloadIdentity Workload,
    bool Satisfied,
    string SignalKey,
    int ExpectedVersion,
    string? EvidenceReference,
    string? EvidenceDigest,
    string CorrelationReference);

public sealed record ApprovalWorkloadOutcome(
    Guid CaseId,
    string Status,
    int Version,
    bool Replayed,
    IReadOnlyList<Guid> OutboxEventIds);

/// <summary>
/// Workload-driven transitions: prerequisite signals and owner cancellation (REQ-03, REQ-07).
/// A signal requires the exact persisted owner identity <c>issuer + client_id</c>; every
/// transition writes its root audit, the SYSTEM effects it caused and one outbox event per target
/// of the entity that really transitioned, all in the same transaction (REQ-08).
/// </summary>
public sealed class ApprovalWorkflowService(
    ProcureToPayDbContext dbContext,
    ApprovalWorkloadAllowlist allowlist,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalWorkflowService> logger)
{
    public async Task<ApprovalWorkloadOutcome> SignalAsync(
        Guid prerequisiteId,
        ApprovalSignalCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        allowlist.EnsureAllowed(command.Workload);
        var signalKey = ApprovalLimits.RequireKey(command.SignalKey, "signal_key");
        var correlationReference = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var evidenceDigest = command.EvidenceDigest is null
            ? null
            : ApprovalLimits.RequireSha256(command.EvidenceDigest, "signal evidence digest");
        var utcNow = occurredAt.ToUniversalTime();

        // The organization lock is acquired before any row read so signals share one order with
        // decisions and submissions (lock, then rows) and never deadlock against them (REQ-04).
        var organizationId = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.Id == prerequisiteId)
            .Select(record => (Guid?)record.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The external prerequisite is not visible.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await ApprovalAssignmentLock.AcquireAsync(dbContext, organizationId, cancellationToken);

        var prerequisiteAnywhere = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == prerequisiteId, cancellationToken)
            ?? throw new DomainNotFoundException("The external prerequisite is not visible.");
        var caseId = prerequisiteAnywhere.CaseId;
        var caseRecord = await dbContext.ApprovalCases
            .SingleOrDefaultAsync(record => record.Id == caseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var prerequisiteRecords = await dbContext.ApprovalPrerequisites
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var prerequisiteRecord = prerequisiteRecords.SingleOrDefault(record => record.Id == prerequisiteId)
            ?? throw new DomainNotFoundException("The external prerequisite is not visible.");
        var ownerWorkload = new ApprovalWorkloadIdentity(
            prerequisiteRecord.OwnerWorkloadIssuer, prerequisiteRecord.OwnerWorkloadClientId);
        // Exact issuer + client_id match: sharing only one of them or the adapter id is not enough
        // and returns 403 (REQ-03, DEC-11).
        EnsureOwner(command.Workload, ownerWorkload);

        var existingSignal = await dbContext.ApprovalPrerequisiteSignals
            .SingleOrDefaultAsync(
                record => record.OrganizationId == caseRecord.OrganizationId &&
                          record.PrerequisiteId == prerequisiteId &&
                          record.SignalKey == signalKey,
                cancellationToken);

        // Expected version comes from the command, not from the current row: a replay must
        // recompute the same fingerprint after the prerequisite was already signalled.
        var fingerprint = ApprovalFingerprints.SignalFingerprint(
            prerequisiteId,
            command.ExpectedVersion,
            ownerWorkload,
            command.Satisfied,
            command.EvidenceReference,
            evidenceDigest,
            signalKey);

        if (existingSignal is not null)
        {
            if (!string.Equals(existingSignal.SignalFingerprint, fingerprint, StringComparison.Ordinal))
            {
                ApprovalTelemetry.RecordConflict("SIGNAL");
                throw new DomainConflictException("The signal key was already used with different content.");
            }

            var replayedEvents = await OutboxIdsForCommandAsync(
                caseRecord.OrganizationId, existingSignal.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ApprovalWorkloadOutcome(
                caseRecord.Id,
                ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
                caseRecord.Version,
                Replayed: true,
                replayedEvents);
        }

        var requirementRecords = await dbContext.ApprovalRequirements
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var taskRecords = await dbContext.ApprovalTasks
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var approvalCase = ApprovalCaseHydrator.Hydrate(
            caseRecord, requirementRecords, prerequisiteRecords, taskRecords);
        var prerequisite = approvalCase.RequirePrerequisite(prerequisiteId);

        var rootAudit = ApprovalEvidence.Root(
            approvalCase,
            ApprovalAuditActor.Workload(command.Workload),
            command.Satisfied ? "PREREQUISITE_SATISFIED" : "PREREQUISITE_FAILED",
            "PREREQUISITE",
            prerequisiteId,
            "[]",
            $"Signal {signalKey}",
            null,
            null,
            utcNow,
            correlationReference);
        dbContext.ApprovalAuditEntries.Add(rootAudit);

        approvalCase.ApplyPrerequisiteSignal(
            prerequisite, command.Satisfied, signalKey, fingerprint, utcNow);

        // A satisfied prerequisite activates its dependents; they are routed in the same
        // transaction so no requirement is left unrouted after the signal (REQ-04).
        await assignmentEngine.AssignUnassignedAsync(
            approvalCase, utcNow, correlationReference, rootAudit.Id, cancellationToken);

        ApprovalStateSync.Apply(approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);

        var signalRecordId = Guid.NewGuid();
        dbContext.ApprovalPrerequisiteSignals.Add(new ApprovalPrerequisiteSignalRecord
        {
            Id = signalRecordId,
            OrganizationId = caseRecord.OrganizationId,
            CaseId = caseId,
            PrerequisiteId = prerequisiteId,
            SignalKey = signalKey,
            SignalFingerprint = fingerprint,
            Satisfied = command.Satisfied,
            OccurredAt = utcNow,
            ActorType = "WORKLOAD",
            ActorId = Guid.Empty,
            EvidenceReference = command.EvidenceReference,
            EvidenceDigest = evidenceDigest,
            CorrelationReference = correlationReference
        });

        var resultSource = approvalCase.SourceOf(prerequisite);
        var outboxIds = new List<Guid>();
        foreach (var target in prerequisite.Targets)
        {
            var outbox = ApprovalEvidence.Outbox(
                approvalCase,
                resultSource,
                target,
                command.Satisfied ? "SATISFIED" : "FAILED",
                null,
                null,
                signalRecordId,
                utcNow,
                correlationReference);
            dbContext.ApprovalOutboxEvents.Add(outbox);
            outboxIds.Add(outbox.Id);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Prerequisite {PrerequisiteId} of case {CaseId} signalled {Result}.",
            prerequisiteId,
            caseId,
            command.Satisfied ? "SATISFIED" : "FAILED");

        return new ApprovalWorkloadOutcome(
            caseId,
            approvalCase.Status.ToString().ToUpperInvariant(),
            approvalCase.Version,
            Replayed: false,
            outboxIds);
    }

    public async Task<ApprovalWorkloadOutcome> CancelAsync(
        Guid caseId,
        ApprovalWorkloadIdentity workload,
        string reason,
        int expectedVersion,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        allowlist.EnsureAllowed(workload);
        var cancellationReason = ApprovalLimits.RequireReason(reason);
        var correlation = ApprovalLimits.RequireCorrelation(correlationReference);
        var utcNow = occurredAt.ToUniversalTime();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var caseRecord = await dbContext.ApprovalCases
            .SingleOrDefaultAsync(record => record.Id == caseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        if (!string.Equals(caseRecord.WorkloadIssuer, workload.Issuer, StringComparison.Ordinal) ||
            !string.Equals(caseRecord.WorkloadClientId, workload.ClientId, StringComparison.Ordinal))
        {
            throw new DomainForbiddenException("Only the owning workload can cancel an approval case.");
        }

        if (caseRecord.Version != expectedVersion)
        {
            throw new DomainConflictException("The approval case was modified by another request.");
        }

        var requirementRecords = await dbContext.ApprovalRequirements
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var prerequisiteRecords = await dbContext.ApprovalPrerequisites
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var taskRecords = await dbContext.ApprovalTasks
            .Where(record => record.CaseId == caseId)
            .ToArrayAsync(cancellationToken);
        var approvalCase = ApprovalCaseHydrator.Hydrate(
            caseRecord, requirementRecords, prerequisiteRecords, taskRecords);

        var rootAudit = ApprovalEvidence.Root(
            approvalCase,
            ApprovalAuditActor.Workload(workload),
            "CASE_CANCELLED",
            nameof(ApprovalCase),
            caseId,
            "[]",
            cancellationReason,
            null,
            null,
            utcNow,
            correlation);
        dbContext.ApprovalAuditEntries.Add(rootAudit);

        approvalCase.Cancel(cancellationReason, utcNow);
        ApprovalStateSync.Apply(approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);

        // Only entities that really transitioned publish CANCELLED; terminal entities stay intact
        // and prerequisites are cancelled only while WAITING (REQ-07, REQ-08, DEC-07).
        var outboxIds = new List<Guid>();
        foreach (var effect in approvalCase.DrainAutomaticEffects())
        {
            var (scopeJson, requirementId) = EffectScope(approvalCase, effect);
            dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Effect(
                approvalCase, rootAudit.Id, effect, scopeJson, utcNow, correlation, requirementId));
            if (effect.Action == "REQUIREMENT_CANCELLED")
            {
                var requirement = approvalCase.RequireRequirement(effect.TargetId);
                AddCancellationEvents(approvalCase, approvalCase.SourceOf(requirement), requirement.Targets, utcNow, correlation, outboxIds);
            }
            else if (effect.Action == "PREREQUISITE_CANCELLED")
            {
                var prerequisite = approvalCase.RequirePrerequisite(effect.TargetId);
                AddCancellationEvents(approvalCase, approvalCase.SourceOf(prerequisite), prerequisite.Targets, utcNow, correlation, outboxIds);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Approval case {CaseId} cancelled by its owning workload.", caseId);

        return new ApprovalWorkloadOutcome(
            caseId,
            approvalCase.Status.ToString().ToUpperInvariant(),
            approvalCase.Version,
            Replayed: false,
            outboxIds);
    }

    private void AddCancellationEvents(
        ApprovalCase approvalCase,
        ApprovalEntitySource source,
        IEnumerable<ApprovalTarget> targets,
        DateTimeOffset occurredAt,
        string correlationReference,
        List<Guid> outboxIds)
    {
        foreach (var target in targets)
        {
            var outbox = ApprovalEvidence.Outbox(
                approvalCase,
                source,
                target,
                "CANCELLED",
                null,
                null,
                sourceCommandId: null,
                occurredAt,
                correlationReference);
            dbContext.ApprovalOutboxEvents.Add(outbox);
            outboxIds.Add(outbox.Id);
        }
    }

    private static (string ScopeJson, Guid? RequirementId) EffectScope(
        ApprovalCase approvalCase,
        ApprovalAutomaticEffect effect) =>
        effect.Source.Type == ApprovalEntitySourceType.ApprovalRequirement
            ? (approvalCase.RequireRequirement(effect.Source.Id).DecisionScopeJson, effect.Source.Id)
            : ("[]", null);

    private async Task<IReadOnlyList<Guid>> OutboxIdsForCommandAsync(
        Guid organizationId,
        Guid sourceCommandId,
        CancellationToken cancellationToken) =>
        await dbContext.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.SourceCommandId == sourceCommandId)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);

    private static void EnsureOwner(ApprovalWorkloadIdentity workload, ApprovalWorkloadIdentity owner)
    {
        if (!string.Equals(workload.Issuer, owner.Issuer, StringComparison.Ordinal) ||
            !string.Equals(workload.ClientId, owner.ClientId, StringComparison.Ordinal))
        {
            throw new DomainForbiddenException(
                "Only the workload that owns the prerequisite can signal it.");
        }
    }
}
