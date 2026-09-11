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
/// Every transition writes audit and one outbox event per target in the same transaction.
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
        var evidenceDigest = command.EvidenceDigest is null
            ? null
            : ApprovalLimits.RequireSha256(command.EvidenceDigest, "signal evidence digest");
        var utcNow = occurredAt.ToUniversalTime();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

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
        EnsureOwner(command.Workload, prerequisiteRecord.OwnerAdapterId);

        var exitingSignal = await dbContext.ApprovalPrerequisiteSignals
            .SingleOrDefaultAsync(
                record => record.OrganizationId == caseRecord.OrganizationId &&
                          record.PrerequisiteId == prerequisiteId &&
                          record.SignalKey == signalKey,
                cancellationToken);

        var prerequisiteForFingerprint = ApprovalCaseHydrator.Hydrate(
                caseRecord,
                [],
                prerequisiteRecords,
                [])
            .RequirePrerequisite(prerequisiteId);
        // Expected version comes from the command, not from the current row: a replay must
        // recompute the same fingerprint after the prerequisite was already signalled.
        var fingerprint = ApprovalFingerprints.SignalFingerprint(
            prerequisiteId,
            command.ExpectedVersion,
            command.Workload,
            command.Satisfied,
            command.EvidenceReference,
            evidenceDigest,
            signalKey);

        if (exitingSignal is not null)
        {
            if (!string.Equals(exitingSignal.SignalFingerprint, fingerprint, StringComparison.Ordinal))
            {
                ApprovalTelemetry.RecordConflict("SIGNAL");
                throw new DomainConflictException("The signal key was already used with different content.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new ApprovalWorkloadOutcome(
                caseRecord.Id,
                ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
                caseRecord.Version,
                Replayed: true,
                []);
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

        approvalCase.ApplyPrerequisiteSignal(prerequisite, command.Satisfied, signalKey, fingerprint, utcNow);

        // A satisfied prerequisite activates its dependents; they are routed in the same
        // transaction so no requirement is left unrouted after the signal (REQ-04).
        await assignmentEngine.AssignUnassignedAsync(
            approvalCase, utcNow, command.CorrelationReference, cancellationToken);

        ApprovalStateSync.Apply(approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);

        dbContext.ApprovalPrerequisiteSignals.Add(new ApprovalPrerequisiteSignalRecord
        {
            Id = Guid.NewGuid(),
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
            CorrelationReference = command.CorrelationReference
        });

        var outboxIds = new List<Guid>();
        foreach (var target in prerequisite.Targets)
        {
            var outbox = ApprovalEvidence.Outbox(
                approvalCase,
                null,
                target,
                command.Satisfied ? "SATISFIED" : "FAILED",
                null,
                null,
                utcNow,
                command.CorrelationReference);
            dbContext.ApprovalOutboxEvents.Add(outbox);
            outboxIds.Add(outbox.Id);
        }

        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
            approvalCase,
            "WORKLOAD",
            Guid.Empty,
            command.Satisfied ? "PREREQUISITE_SATISFIED" : "PREREQUISITE_FAILED",
            nameof(ExternalPrerequisite),
            prerequisiteId,
            "[]",
            $"Signal {signalKey}",
            null,
            null,
            utcNow,
            command.CorrelationReference));

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

        approvalCase.Cancel(cancellationReason, utcNow);
        ApprovalStateSync.Apply(approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);

        var outboxIds = new List<Guid>();
        foreach (var requirement in approvalCase.Requirements)
        {
            foreach (var target in requirement.Targets)
            {
                var outbox = ApprovalEvidence.Outbox(
                    approvalCase,
                    requirement,
                    target,
                    "CANCELLED",
                    null,
                    null,
                    utcNow,
                    correlationReference);
                dbContext.ApprovalOutboxEvents.Add(outbox);
                outboxIds.Add(outbox.Id);
            }
        }

        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
            approvalCase,
            "WORKLOAD",
            Guid.Empty,
            "CASE_CANCELLED",
            nameof(ApprovalCase),
            caseId,
            "[]",
            cancellationReason,
            null,
            null,
            utcNow,
            correlationReference));

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

    private static void EnsureOwner(ApprovalWorkloadIdentity workload, string ownerAdapterId)
    {
        if (!string.Equals(workload.ClientId, ownerAdapterId, StringComparison.Ordinal))
        {
            throw new DomainForbiddenException(
                "Only the workload that owns the prerequisite can signal it.");
        }
    }
}
