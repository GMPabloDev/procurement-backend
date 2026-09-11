using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalSubmissionCommand(
    ApprovalWorkloadIdentity Workload,
    Guid OrganizationId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    string? ContractVersion,
    string SubmissionKey,
    Guid? RequesterId,
    Guid OriginatorId,
    string CorrelationReference);

public sealed record ApprovalSubmissionOutcome(Guid CaseId, string Status, int Version, bool Replayed);

/// <summary>
/// Idempotent, fail-closed submission ingestion (REQ-01): exactly-one adapter, allowlisted
/// workload, canonical fingerprint, reservation unique per workload scope and case creation
/// with its activation graph in one transaction.
/// </summary>
public sealed class ApprovalSubmissionService(
    ProcureToPayDbContext dbContext,
    IApprovalSubmissionAdapterRegistry adapters,
    ApprovalWorkloadAllowlist allowlist,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalSubmissionService> logger)
{
    public async Task<ApprovalSubmissionOutcome> SubmitAsync(
        ApprovalSubmissionCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        allowlist.EnsureAllowed(command.Workload);
        _ = ApprovalLimits.RequireKey(command.SubmissionKey, "submission_key");
        if (command.OrganizationId == Guid.Empty)
        {
            throw new DomainValidationException("A submission requires an organization.");
        }

        var adapter = adapters.ResolveExactlyOne(command.SubjectType, command.Operation, command.ContractVersion);
        var descriptor = adapter.Descriptor;
        var submission = await adapter.BuildAsync(
            new ApprovalSubmissionRequest(
                command.OrganizationId,
                command.SubjectType,
                command.SubjectId,
                command.SubjectVersion,
                command.Operation,
                descriptor.ContractVersion,
                command.SubmissionKey,
                command.RequesterId,
                command.OriginatorId,
                command.CorrelationReference),
            cancellationToken);
        ApprovalSubmissionRules.Validate(submission, descriptor, command.Workload);
        if (!string.Equals(submission.SubmissionKey, command.SubmissionKey, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The adapter changed the submission key.");
        }

        var fingerprint = ApprovalSubmissionRules.SubmissionFingerprint(submission, descriptor, command.Workload);
        var utcNow = occurredAt.ToUniversalTime();

        var reservation = await FindReservationAsync(command, cancellationToken);
        if (reservation is not null)
        {
            return await ReplayAsync(reservation, fingerprint, cancellationToken);
        }

        var approvalCase = ApprovalCase.Create(
            Guid.NewGuid(),
            submission,
            descriptor,
            command.Workload,
            fingerprint,
            utcNow,
            command.CorrelationReference);

        // Requirements activated by the graph are routed in the same transaction (REQ-04).
        await assignmentEngine.AssignUnassignedAsync(
            approvalCase, utcNow, command.CorrelationReference, cancellationToken);

        PersistNewCase(approvalCase, submission, command, fingerprint, utcNow);
        try
        {
            // The unique reservation index serializes concurrent identical submissions without a
            // long serializable transaction (REQ-10). SaveChanges is atomic on its own.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            var concurrent = await FindReservationAsync(command, cancellationToken);
            if (concurrent is null)
            {
                throw;
            }

            return await ReplayAsync(concurrent, fingerprint, cancellationToken);
        }

        logger.LogInformation(
            "Approval case {CaseId} submitted for {SubjectType}/{Operation} with {RequirementCount} requirements.",
            approvalCase.Id,
            submission.SubjectType,
            submission.Operation,
            approvalCase.Requirements.Count);

        ApprovalTelemetry.RecordSubmission("CREATED");

        return new ApprovalSubmissionOutcome(
            approvalCase.Id,
            approvalCase.Status.ToString().ToUpperInvariant(),
            approvalCase.Version,
            Replayed: false);
    }

    private async Task<ApprovalSubmissionOutcome> ReplayAsync(
        ApprovalSubmissionReservationRecord reservation,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(reservation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            ApprovalTelemetry.RecordConflict("SUBMIT");
            throw new DomainConflictException(
                "The submission key was already used with different content.");
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleAsync(record => record.Id == reservation.CaseId, cancellationToken);
        ApprovalTelemetry.RecordSubmission("REPLAYED");
        return new ApprovalSubmissionOutcome(
            caseRecord.Id,
            ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
            caseRecord.Version,
            Replayed: true);
    }

    private Task<ApprovalSubmissionReservationRecord?> FindReservationAsync(
        ApprovalSubmissionCommand command,
        CancellationToken cancellationToken) =>
        dbContext.ApprovalSubmissionReservations.SingleOrDefaultAsync(
            record =>
                record.OrganizationId == command.OrganizationId &&
                record.WorkloadIssuer == command.Workload.Issuer &&
                record.WorkloadClientId == command.Workload.ClientId &&
                record.SubjectType == command.SubjectType &&
                record.Operation == command.Operation &&
                record.SubmissionKey == command.SubmissionKey,
            cancellationToken);

    private void PersistNewCase(
        ApprovalCase approvalCase,
        ApprovalSubmission submission,
        ApprovalSubmissionCommand command,
        string fingerprint,
        DateTimeOffset occurredAt)
    {
        dbContext.ApprovalCases.Add(new ApprovalCaseRecord
        {
            Id = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            SubjectType = approvalCase.SubjectType,
            SubjectId = approvalCase.SubjectId,
            SubjectVersion = approvalCase.SubjectVersion,
            Operation = approvalCase.Operation,
            SourceSnapshotDigest = approvalCase.SourceSnapshotDigest,
            WorkloadIssuer = approvalCase.WorkloadIssuer,
            WorkloadClientId = approvalCase.WorkloadClientId,
            SubmissionKey = approvalCase.SubmissionKey,
            SubmissionFingerprint = approvalCase.SubmissionFingerprint,
            OriginatorId = approvalCase.OriginatorId,
            RequesterId = approvalCase.RequesterId,
            Status = (int)approvalCase.Status,
            Version = approvalCase.Version,
            CreatedAt = approvalCase.CreatedAt,
            CorrelationReference = approvalCase.CorrelationReference
        });

        foreach (var requirement in approvalCase.Requirements)
        {
            dbContext.ApprovalRequirements.Add(new ApprovalRequirementRecord
            {
                Id = requirement.Id,
                CaseId = approvalCase.Id,
                OrganizationId = approvalCase.OrganizationId,
                SourceRequirementKey = requirement.SourceRequirementKey,
                WorkflowRequirementKey = requirement.WorkflowRequirementKey,
                StageCode = requirement.StageCode,
                Role = (int)requirement.Role,
                AuthorityJson = ApprovalJsonPersistence.SerializeAuthority(requirement.Authority),
                DecisionScopeJson = requirement.DecisionScopeJson,
                ExcludedUserIdsJson = ApprovalJsonPersistence.SerializeGuids(requirement.ExcludedUserIds),
                TargetsJson = ApprovalJsonPersistence.SerializeTargets(requirement.Targets),
                DependenciesJson = ApprovalJsonPersistence.SerializeDependencies(requirement.Dependencies),
                Status = (int)requirement.Status,
                Version = requirement.Version
            });
        }

        foreach (var prerequisite in approvalCase.Prerequisites)
        {
            dbContext.ApprovalPrerequisites.Add(new ApprovalPrerequisiteRecord
            {
                Id = prerequisite.Id,
                CaseId = approvalCase.Id,
                OrganizationId = approvalCase.OrganizationId,
                Key = prerequisite.Key,
                OwnerAdapterId = prerequisite.OwnerAdapterId,
                OwnerAdapterVersion = prerequisite.OwnerAdapterVersion,
                SourceControlType = prerequisite.SourceControlType,
                SourceControlDigest = prerequisite.SourceControlDigest,
                ParametersJson = prerequisite.ParametersJson,
                TargetsJson = ApprovalJsonPersistence.SerializeTargets(prerequisite.Targets),
                Status = (int)prerequisite.Status,
                Version = prerequisite.Version
            });
        }

        foreach (var task in approvalCase.Tasks)
        {
            dbContext.ApprovalTasks.Add(new ApprovalTaskRecord
            {
                Id = task.Id,
                CaseId = approvalCase.Id,
                OrganizationId = approvalCase.OrganizationId,
                RequirementId = task.RequirementId,
                Status = (int)task.Status,
                CurrentAssigneeUserId = task.CurrentAssigneeUserId,
                Version = task.Version
            });
        }

        dbContext.ApprovalSubmissionReservations.Add(new ApprovalSubmissionReservationRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = approvalCase.OrganizationId,
            WorkloadIssuer = command.Workload.Issuer,
            WorkloadClientId = command.Workload.ClientId,
            SubjectType = submission.SubjectType,
            Operation = submission.Operation,
            SubmissionKey = submission.SubmissionKey,
            Fingerprint = fingerprint,
            CaseId = approvalCase.Id,
            CreatedAt = occurredAt
        });

        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
            approvalCase,
            "WORKLOAD",
            Guid.Empty,
            "CASE_SUBMITTED",
            nameof(ApprovalCase),
            approvalCase.Id,
            "[]",
            $"Submission {submission.Operation} for {submission.SubjectType}",
            null,
            null,
            occurredAt,
            command.CorrelationReference));
    }
}
