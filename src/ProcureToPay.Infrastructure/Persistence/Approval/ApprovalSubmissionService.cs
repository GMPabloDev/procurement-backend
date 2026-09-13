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
/// Idempotent, fail-closed submission ingestion (REQ-01, REQ-03): exactly-one adapter, allowlisted
/// workload, exact-one owner workload for every prerequisite resolved before any write, canonical
/// fingerprint, reservation unique per workload scope and one transaction that serializes
/// selection, load reservation and assignment (REQ-04) while the submission root audit and the
/// automatic activation/assignment effects commit with the case graph (REQ-08).
/// </summary>
public sealed class ApprovalSubmissionService(
    ProcureToPayDbContext dbContext,
    IApprovalSubmissionAdapterRegistry adapters,
    // pi-lens-ignore: lsp:CS0246
    IApprovalOwnerWorkloadRegistry ownerWorkloads,
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
        // pi-lens-ignore: lsp:CS0117
        var correlationReference = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
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
                correlationReference),
            cancellationToken);
        return await SubmitPreparedAsync(command, descriptor, submission, occurredAt, afterPersist: null, cancellationToken);
    }

    /// <summary>
    /// Ingests a submission already built by a trusted in-process command (SPEC 05 REQ-05). The
    /// optional <paramref name="afterPersist"/> hook runs inside the same transaction, after the
    /// case graph is materialized and before it is committed, so an extension row cannot exist
    /// without its case and vice versa.
    /// </summary>
    public async Task<ApprovalSubmissionOutcome> SubmitPreparedAsync(
        ApprovalSubmissionCommand command,
        ApprovalAdapterDescriptor descriptor,
        ApprovalSubmission submission,
        DateTimeOffset occurredAt,
        Action<ApprovalCase>? afterPersist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(submission);
        allowlist.EnsureAllowed(command.Workload);
        _ = ApprovalLimits.RequireKey(command.SubmissionKey, "submission_key");
        // pi-lens-ignore: lsp:CS0117
        var correlationReference = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        if (command.OrganizationId == Guid.Empty)
        {
            throw new DomainValidationException("A submission requires an organization.");
        }

        ApprovalSubmissionRules.Validate(submission, descriptor, command.Workload);
        if (!string.Equals(submission.SubmissionKey, command.SubmissionKey, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The adapter changed the submission key.");
        }

        var fingerprint = ApprovalSubmissionRules.SubmissionFingerprint(submission, descriptor, command.Workload);
        var utcNow = occurredAt.ToUniversalTime();

        // Owner adapter/version resolves exact-one to an allowlisted workload identity before the
        // case exists; absence, ambiguity or a non-allowlisted match fails closed (REQ-03, DEC-11).
        var prerequisiteOwners = new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal);
        foreach (var prerequisite in submission.Prerequisites)
        {
            prerequisiteOwners[prerequisite.Key] = ownerWorkloads.ResolveExactlyOne(
                prerequisite.OwnerAdapterId, prerequisite.OwnerAdapterVersion);
        }

        var reservation = await FindReservationAsync(command, cancellationToken);
        if (reservation is not null)
        {
            return await ReplayAsync(reservation, fingerprint, cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var concurrent = await FindReservationAsync(command, cancellationToken);
        if (concurrent is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ReplayAsync(concurrent, fingerprint, cancellationToken);
        }

        var approvalCase = ApprovalCase.Create(
            Guid.NewGuid(),
            submission,
            descriptor,
            command.Workload,
            prerequisiteOwners,
            fingerprint,
            utcNow,
            correlationReference);

        // The root audit of the command exists before its automatic effects reference it (REQ-08).
        var rootAudit = ApprovalEvidence.Root(
            approvalCase,
            // pi-lens-ignore: lsp:CS0103
            ApprovalAuditActor.Workload(command.Workload),
            "CASE_SUBMITTED",
            nameof(ApprovalCase),
            approvalCase.Id,
            "[]",
            $"Submission {submission.Operation} for {submission.SubjectType}",
            null,
            null,
            utcNow,
            correlationReference);
        dbContext.ApprovalAuditEntries.Add(rootAudit);

        try
        {
            // Requirements activated by the graph are routed before the rows are materialized, so the
            // persisted task snapshot already carries the chosen assignee and load (REQ-04) and the
            // automatic effects commit with the case graph and the root audit (REQ-08).
            await assignmentEngine.AssignUnassignedAsync(
                approvalCase, utcNow, correlationReference, rootAudit.Id, cancellationToken);
            PersistNewCase(approvalCase, submission, command, fingerprint, utcNow);
            afterPersist?.Invoke(approvalCase);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await FindReservationAsync(command, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return await ReplayAsync(winner, fingerprint, cancellationToken);
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
                ActionsJson = ApprovalJsonPersistence.SerializeActions(requirement.Actions),
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
                // pi-lens-ignore: lsp:CS1061
                OwnerWorkloadIssuer = prerequisite.OwnerWorkloadIssuer,
                // pi-lens-ignore: lsp:CS1061
                OwnerWorkloadClientId = prerequisite.OwnerWorkloadClientId,
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
    }
}
