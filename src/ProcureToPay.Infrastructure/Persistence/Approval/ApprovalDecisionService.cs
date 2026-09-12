using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalDecisionCommand(
    Guid TaskId,
    ApprovalDecisionAction Action,
    string Reason,
    string DecisionKey,
    int ExpectedTaskVersion,
    Guid ActorUserId,
    string CorrelationReference);

public sealed record ApprovalDecisionOutcome(
    Guid CaseId,
    Guid RequirementId,
    Guid TaskId,
    Guid DecisionId,
    string Action,
    string RequirementStatus,
    string TaskStatus,
    string CaseStatus,
    int CaseVersion,
    bool Replayed,
    IReadOnlyList<Guid> OutboxEventIds);

/// <summary>
/// Authorized, immutable and idempotent decisions (REQ-06, REQ-08, NFR-01, NFR-02): only the
/// current assignee of a pending task decides, authority is re-resolved inside the transaction
/// with the server clock, the fingerprint is reproducible from the stored preimage versions, and
/// the root audit, its automatic effects, the decision, its targets, the assignment release and
/// the <c>approval-result/v2</c> events commit together. A replay returns the artifacts of the
/// original decision, not the current state (NFR-01).
/// </summary>
public sealed class ApprovalDecisionService(
    ProcureToPayDbContext dbContext,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalDecisionService> logger)
{
    public async Task<ApprovalDecisionOutcome> DecideAsync(
        ApprovalDecisionCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A decision requires an authenticated actor.");
        }

        var utcNow = occurredAt.ToUniversalTime();
        var reason = ApprovalLimits.RequireReason(command.Reason, "Decision reason");
        var decisionKey = ApprovalLimits.RequireKey(command.DecisionKey, "decision_key");
        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var taskRecord = await dbContext.ApprovalTasks
            .SingleOrDefaultAsync(record => record.Id == command.TaskId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval task is not visible.");
        var caseRecord = await dbContext.ApprovalCases
            .SingleOrDefaultAsync(record => record.Id == taskRecord.CaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var requirementRecords = await dbContext.ApprovalRequirements
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        var prerequisiteRecords = await dbContext.ApprovalPrerequisites
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        var taskRecords = await dbContext.ApprovalTasks
            .Where(record => record.CaseId == caseRecord.Id)
            .ToArrayAsync(cancellationToken);
        var requirementRecord = requirementRecords.Single(record => record.Id == taskRecord.RequirementId);
        var approvalCase = ApprovalCaseHydrator.Hydrate(
            caseRecord, requirementRecords, prerequisiteRecords, taskRecords);
        var requirement = approvalCase.RequireRequirement(requirementRecord.Id);
        var task = approvalCase.RequireTask(taskRecord.Id);

        // Replay is resolved before any state check so an identical retry returns the original
        // decision even when the task is already terminal (REQ-06).
        var existing = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == caseRecord.OrganizationId &&
                          record.ActorUserId == command.ActorUserId &&
                          record.DecisionKey == decisionKey,
                cancellationToken);
        if (existing is not null)
        {
            // Recomputed with the STORED preimage versions, so the digest is reproducible.
            var replayedFingerprint = ApprovalFingerprints.DecisionFingerprint(
                caseRecord.Id,
                requirement.Id,
                task.Id,
                existing.RequirementVersion,
                existing.TaskVersion,
                requirement.Targets,
                command.Action,
                reason,
                command.ActorUserId);
            if (!string.Equals(replayedFingerprint, existing.Fingerprint, StringComparison.Ordinal))
            {
                ApprovalTelemetry.RecordConflict("DECIDE");
                throw new DomainConflictException("The decision key was already used with different content.");
            }

            // NFR-01: a replay returns the original artifacts, not the state the case advanced to.
            var replayedEvents = await dbContext.ApprovalOutboxEvents
                .AsNoTracking()
                .Where(record => record.OrganizationId == caseRecord.OrganizationId &&
                                 record.SourceCommandId == existing.Id)
                .OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.Id)
                .Select(record => record.Id)
                .ToArrayAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ApprovalTelemetry.RecordDecision(command.Action.ToString(), "REPLAYED");
            return new ApprovalDecisionOutcome(
                caseRecord.Id,
                requirement.Id,
                task.Id,
                existing.Id,
                ((ApprovalDecisionAction)existing.Action).ToString().ToUpperInvariant(),
                ((ApprovalRequirementStatus)existing.RequirementStatusAfter).ToString().ToUpperInvariant(),
                ((ApprovalTaskStatus)existing.TaskStatusAfter).ToString().ToUpperInvariant(),
                ((ApprovalCaseStatus)existing.CaseStatusAfter).ToString().ToUpperInvariant(),
                existing.CaseVersionAfter,
                Replayed: true,
                replayedEvents);
        }

        if (task.Status != ApprovalTaskStatus.Pending)
        {
            throw new DomainConflictException("The approval task is not pending.");
        }

        if (task.CurrentAssigneeUserId != command.ActorUserId)
        {
            throw new DomainForbiddenException("Only the current assignee can decide this approval task.");
        }

        if (taskRecord.Version != command.ExpectedTaskVersion)
        {
            ApprovalTelemetry.RecordConflict("DECIDE");
            throw new DomainConflictException("The approval task was modified by another request.");
        }

        // Authority is revalidated for the actor with the requirement inputs and the server clock.
        var candidate = await assignmentEngine.FindEligibleCandidateAsync(
                approvalCase, requirement, command.ActorUserId, utcNow, cancellationToken)
            ?? throw new DomainForbiddenException(
                "The assignee is no longer eligible to decide this approval requirement.");
        var eligibilityJson = ApprovalEligibilityEvidence.Canonicalize(candidate.Evidence);
        var authorityDigest = ApprovalFingerprints.AuthorityEvidenceDigest(eligibilityJson);

        var decisionId = Guid.NewGuid();
        var fingerprint = ApprovalFingerprints.DecisionFingerprint(
            caseRecord.Id,
            requirement.Id,
            task.Id,
            requirement.Version,
            task.Version,
            requirement.Targets,
            command.Action,
            reason,
            command.ActorUserId);
        var decisionDigest = ApprovalFingerprints.WorkflowDecisionDigest(
            decisionId,
            1,
            caseRecord.Id,
            caseRecord.SubjectType,
            caseRecord.SubjectId,
            caseRecord.SubjectVersion,
            caseRecord.SourceSnapshotDigest,
            requirement.WorkflowRequirementKey,
            DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson).Digest(),
            requirement.Targets,
            command.Action,
            ApprovalDecisionOrigin.Human,
            command.ActorUserId,
            utcNow,
            reason,
            authorityDigest,
            requirement.ExcludedUserIds);

        // Domain construction validates the evidence before anything is written.
        var decision = ApprovalDecision.Create(
            decisionId,
            caseRecord.Id,
            caseRecord.OrganizationId,
            requirement.Id,
            task.Id,
            command.Action,
            ApprovalDecisionOrigin.Human,
            command.ActorUserId,
            reason,
            utcNow,
            decisionKey,
            fingerprint,
            decisionDigest,
            authorityDigest,
            eligibilityJson,
            requirement.Targets,
            correlation);

        var result = ResultCode(command.Action);

        // The command root audit exists before its automatic effects reference it (REQ-08).
        var rootAudit = ApprovalEvidence.Root(
            approvalCase,
            ApprovalAuditActor.User(command.ActorUserId),
            $"DECISION_{result}",
            nameof(ApprovalDecision),
            decision.Id,
            requirement.DecisionScopeJson,
            reason,
            null,
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                ("action", ApprovalCanonicalJson.String(command.Action)),
                ("actor_user_id", ApprovalCanonicalJson.String(command.ActorUserId)),
                ("decision_digest", ApprovalCanonicalJson.String(decisionDigest)),
                ("task_id", ApprovalCanonicalJson.String(task.Id)))),
            utcNow,
            correlation,
            requirement.Id);
        dbContext.ApprovalAuditEntries.Add(rootAudit);

        dbContext.ApprovalDecisions.Add(new ApprovalDecisionRecord
        {
            Id = decision.Id,
            CaseId = decision.CaseId,
            OrganizationId = decision.OrganizationId,
            RequirementId = decision.RequirementId,
            TaskId = decision.TaskId,
            Action = (int)decision.Action,
            Origin = (int)decision.Origin,
            ActorUserId = decision.ActorUserId,
            Reason = decision.Reason,
            DecidedAt = decision.DecidedAt,
            DecisionKey = decision.DecisionKey,
            Fingerprint = decision.Fingerprint,
            DecisionDigest = decision.DecisionDigest,
            AuthorityEvidenceDigest = decision.AuthorityEvidenceDigest,
            EligibilityEvidenceJson = decision.EligibilityEvidenceJson,
            RequirementVersion = requirement.Version,
            TaskVersion = task.Version,
            CorrelationReference = decision.CorrelationReference,
            Version = decision.Version
        });

        foreach (var target in decision.Targets)
        {
            dbContext.ApprovalDecisionTargets.Add(new ApprovalDecisionTargetRecord
            {
                Id = Guid.NewGuid(),
                DecisionId = decision.Id,
                CaseId = decision.CaseId,
                OrganizationId = decision.OrganizationId,
                RequirementId = decision.RequirementId,
                TargetType = target.Type,
                TargetId = target.Id,
                TargetVersion = target.Version,
                MaterialSnapshotDigest = target.MaterialSnapshotDigest
            });
        }

        // The decided task releases its current assignment so the load is freed (REQ-04).
        var currentAssignment = await dbContext.ApprovalAssignments
            .SingleOrDefaultAsync(
                record => record.TaskId == task.Id && record.ReleasedAt == null, cancellationToken);
        if (currentAssignment is not null)
        {
            currentAssignment.ReleasedAt = utcNow;
        }

        approvalCase.ApplyDecision(task, command.Action);
        var resultSource = approvalCase.SourceOf(requirement);
        var outboxIds = new List<Guid>();
        if (command.Action == ApprovalDecisionAction.Approve)
        {
            // An approval enables its dependent edges; they are routed in this transaction and the
            // engine materializes every activation/assignment effect of the root audit (REQ-04, REQ-08).
            await assignmentEngine.AssignUnassignedAsync(
                approvalCase, utcNow, correlation, rootAudit.Id, cancellationToken);
        }
        else
        {
            // A rejection propagates CANCELLED to linked descendants: each one keeps its own audit
            // effect and its own event per target (REQ-08, DEC-07).
            foreach (var effect in approvalCase.DrainAutomaticEffects())
            {
                var (scopeJson, requirementId) = EffectScope(approvalCase, effect);
                dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Effect(
                    approvalCase, rootAudit.Id, effect, scopeJson, utcNow, correlation, requirementId));
                if (effect.Action == "REQUIREMENT_CANCELLED")
                {
                    var cancelled = approvalCase.RequireRequirement(effect.TargetId);
                    foreach (var target in cancelled.Targets)
                    {
                        outboxIds.Add(AddOutbox(
                            approvalCase, approvalCase.SourceOf(cancelled), target, "CANCELLED", null, null,
                            decision.Id, utcNow, correlation));
                    }
                }
            }
        }

        foreach (var target in requirement.Targets)
        {
            outboxIds.Add(AddOutbox(
                approvalCase, resultSource, target, result, decision.Id, decisionDigest, decision.Id,
                utcNow, correlation));
        }

        ApprovalStateSync.Apply(approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);
        var decisionRecord = dbContext.ApprovalDecisions.Local.Single(record => record.Id == decision.Id);
        decisionRecord.RequirementStatusAfter = (int)requirement.Status;
        decisionRecord.TaskStatusAfter = (int)task.Status;
        decisionRecord.CaseStatusAfter = (int)approvalCase.Status;
        decisionRecord.CaseVersionAfter = approvalCase.Version;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Approval task {TaskId} of case {CaseId} decided {Result} by {ActorUserId}.",
            task.Id,
            caseRecord.Id,
            result,
            command.ActorUserId);

        ApprovalTelemetry.RecordDecision(command.Action.ToString(), "ACCEPTED");
        return new ApprovalDecisionOutcome(
            caseRecord.Id,
            requirement.Id,
            task.Id,
            decision.Id,
            command.Action.ToString().ToUpperInvariant(),
            requirement.Status.ToString().ToUpperInvariant(),
            task.Status.ToString().ToUpperInvariant(),
            approvalCase.Status.ToString().ToUpperInvariant(),
            approvalCase.Version,
            Replayed: false,
            outboxIds);
    }

    private Guid AddOutbox(
        ApprovalCase approvalCase,
        ApprovalEntitySource source,
        ApprovalTarget target,
        string result,
        Guid? decisionId,
        string? decisionDigest,
        Guid sourceCommandId,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        var outbox = ApprovalEvidence.Outbox(
            approvalCase,
            source,
            target,
            result,
            decisionId,
            decisionDigest,
            sourceCommandId,
            occurredAt,
            correlationReference);
        dbContext.ApprovalOutboxEvents.Add(outbox);
        return outbox.Id;
    }

    private static (string ScopeJson, Guid? RequirementId) EffectScope(
        ApprovalCase approvalCase,
        ApprovalAutomaticEffect effect) =>
        effect.Source.Type == ApprovalEntitySourceType.ApprovalRequirement
            ? (approvalCase.RequireRequirement(effect.Source.Id).DecisionScopeJson, effect.Source.Id)
            : ("[]", null);

    private static string ResultCode(ApprovalDecisionAction action) => action switch
    {
        ApprovalDecisionAction.Approve => "APPROVED",
        ApprovalDecisionAction.Reject => "REJECTED",
        ApprovalDecisionAction.RequestChanges => "CHANGES_REQUESTED",
        _ => throw new DomainValidationException("The approval action is invalid.")
    };
}
