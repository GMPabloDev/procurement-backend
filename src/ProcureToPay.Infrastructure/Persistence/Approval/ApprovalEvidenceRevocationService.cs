using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalEvidenceRevocationCommand(
    EvidenceRevocationActorType ActorType,
    Guid ActorUserId,
    ApprovalWorkloadIdentity? Workload,
    Guid OrganizationId,
    Guid EvidenceId,
    int ExpectedEvidenceVersion,
    string RevocationKey,
    EvidenceRevocationReasonCode ReasonCode,
    string Reason,
    string CorrelationReference);

public sealed record ApprovalEvidenceRevocationOutcome(
    Guid RevocationId,
    Guid EvidenceId,
    string EvidenceStatus,
    int EvidenceVersion,
    bool Replayed);

/// <summary>
/// Irreversible, idempotent evidence revocation (SPEC 04 REQ-06): only the owning workload can
/// invalidate the binding, ADMIN only as incident containment with a reason, and the transaction
/// cuts future verification and carry-forward without touching the historical decision.
/// </summary>
public sealed class ApprovalEvidenceRevocationService(
    ProcureToPayDbContext dbContext,
    ILogger<ApprovalEvidenceRevocationService> logger)
{
    public async Task<ApprovalEvidenceRevocationOutcome> RevokeAsync(
        ApprovalEvidenceRevocationCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var key = ApprovalLimits.RequireKey(command.RevocationKey, "revocation_key");
        var reason = ApprovalLimits.RequireReason(command.Reason, "Revocation reason");
        var utcNow = occurredAt.ToUniversalTime();
        if (command.ExpectedEvidenceVersion < 1)
        {
            throw new DomainValidationException("A revocation requires the expected evidence version.");
        }

        if (command.ActorType == EvidenceRevocationActorType.Admin &&
            command.ReasonCode != EvidenceRevocationReasonCode.IncidentContainment)
        {
            throw new DomainForbiddenException(
                "ADMIN can only revoke evidence as incident containment.");
        }

        if (command.ActorType == EvidenceRevocationActorType.Workload &&
            command.ReasonCode != EvidenceRevocationReasonCode.OwnerInvalidation)
        {
            throw new DomainValidationException(
                "The owning workload revokes because the subject or binding is no longer valid.");
        }

        var actorWorkload = command.ActorType == EvidenceRevocationActorType.Workload
            ? command.Workload ?? throw new DomainValidationException("A workload revocation requires its identity.")
            : null;
        var actorUserId = command.ActorType == EvidenceRevocationActorType.Admin
            ? command.ActorUserId == Guid.Empty
                ? throw new DomainValidationException("An ADMIN revocation requires the acting user.")
                : command.ActorUserId
            : (Guid?)null;

        var evidenceRecord = await dbContext.DecisionAuthorityEvidences
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == command.EvidenceId, cancellationToken)
            ?? throw new DomainNotFoundException("The decision authority evidence is not visible.");
        if (command.OrganizationId != Guid.Empty && evidenceRecord.OrganizationId != command.OrganizationId)
        {
            throw new DomainNotFoundException("The decision authority evidence is not visible.");
        }
        var decisionRecord = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == evidenceRecord.RootHumanDecisionId, cancellationToken)
            ?? throw new DomainNotFoundException("The root human decision of the evidence is not visible.");

        if (command.ActorType == EvidenceRevocationActorType.Workload)
        {
            var owns = await dbContext.ApprovalCases
                .AsNoTracking()
                .AnyAsync(
                    record => record.Id == decisionRecord.CaseId &&
                              record.WorkloadIssuer == actorWorkload!.Issuer &&
                              record.WorkloadClientId == actorWorkload.ClientId,
                    cancellationToken);
            if (!owns)
            {
                throw new DomainForbiddenException(
                    "Only the workload that owns a linked case can revoke this evidence.");
            }
        }

        var fingerprint = ApprovalFingerprints.RevocationFingerprint(
            command.ActorType,
            actorUserId,
            actorWorkload,
            evidenceRecord.Id,
            command.ExpectedEvidenceVersion,
            evidenceRecord.OrganizationId,
            reason,
            key);
        var existing = await dbContext.DecisionEvidenceRevocations
            .AsNoTracking()
            .Where(record => record.EvidenceId == command.EvidenceId && record.RevocationKey == key)
            .Where(record => command.OrganizationId == Guid.Empty || record.OrganizationId == command.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        if (evidenceRecord.Version != command.ExpectedEvidenceVersion)
        {
            ApprovalTelemetry.RecordConflict("REVOKE_EVIDENCE");
            throw new DomainConflictException("The evidence was modified by another request.");
        }

        if (evidenceRecord.Status == ApprovalEvolutionCodes.EvidenceRevoked)
        {
            throw new DomainConflictException("The decision authority evidence is already revoked.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var tracked = await dbContext.DecisionAuthorityEvidences
                .SingleOrDefaultAsync(record => record.Id == command.EvidenceId, cancellationToken)
                ?? throw new DomainNotFoundException("The decision authority evidence is not visible.");
            var evidence = DecisionAuthorityEvidence.Restore(
                tracked.Id,
                tracked.OrganizationId,
                tracked.RootHumanDecisionId,
                tracked.Digest,
                tracked.EvidenceJson,
                tracked.CreatedAt,
                tracked.Version,
                ApprovalEvolutionCodes.ParseStatus(tracked.Status),
                tracked.RevokedAt);
            evidence.Revoke(utcNow);
            tracked.Status = ApprovalEvolutionCodes.Code(evidence.Status);
            tracked.Version = evidence.Version;
            tracked.RevokedAt = evidence.RevokedAt;

            var revocationId = Guid.NewGuid();
            dbContext.DecisionEvidenceRevocations.Add(new DecisionEvidenceRevocationRecord
            {
                Id = revocationId,
                OrganizationId = tracked.OrganizationId,
                CaseId = decisionRecord.CaseId,
                EvidenceId = tracked.Id,
                EvidenceVersion = tracked.Version,
                DecisionId = tracked.RootHumanDecisionId,
                RevocationKey = key,
                Fingerprint = fingerprint,
                ActorType = ApprovalEvolutionCodes.Code(command.ActorType),
                ActorUserId = actorUserId,
                ActorWorkloadIssuer = actorWorkload?.Issuer,
                ActorWorkloadClientId = actorWorkload?.ClientId,
                ReasonCode = ApprovalEvolutionCodes.Code(command.ReasonCode),
                Reason = reason,
                CreatedAt = utcNow
            });

            var actor = command.ActorType == EvidenceRevocationActorType.Admin
                ? ApprovalAuditActor.User(actorUserId!.Value)
                : ApprovalAuditActor.Workload(actorWorkload!);
            var caseRecord = await dbContext.ApprovalCases
                .AsNoTracking()
                .SingleAsync(record => record.Id == decisionRecord.CaseId, cancellationToken);
            var requirementScope = await dbContext.ApprovalRequirements
                .AsNoTracking()
                .Where(record => record.Id == decisionRecord.RequirementId)
                .Select(record => record.DecisionScopeJson)
                .SingleOrDefaultAsync(cancellationToken) ?? "[]";
            dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.RootForOrganization(
                tracked.OrganizationId,
                actor,
                Guid.NewGuid(),
                "EVIDENCE_REVOKED",
                "EVIDENCE",
                tracked.Id,
                requirementScope,
                reason,
                utcNow,
                correlation));
            dbContext.ApprovalOutboxEvents.Add(ApprovalEvidence.EvidenceRevoked(
                tracked.OrganizationId,
                decisionRecord.CaseId,
                tracked.Id,
                tracked.Digest,
                tracked.Version,
                tracked.RootHumanDecisionId,
                revocationId,
                ApprovalEvolutionCodes.Code(command.ActorType),
                ApprovalEvolutionCodes.Code(command.ReasonCode),
                utcNow,
                correlation));
            _ = caseRecord;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Decision authority evidence {EvidenceId} of case {CaseId} was revoked.",
                tracked.Id,
                decisionRecord.CaseId);
            ApprovalTelemetry.RecordEvidenceRevocation("REVOKED");
            return new ApprovalEvidenceRevocationOutcome(
                revocationId,
                tracked.Id,
                ApprovalEvolutionCodes.EvidenceRevoked,
                tracked.Version,
                Replayed: false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.DecisionEvidenceRevocations
                .AsNoTracking()
                .Where(record => record.EvidenceId == command.EvidenceId && record.RevocationKey == key)
                .Where(record => command.OrganizationId == Guid.Empty || record.OrganizationId == command.OrganizationId)
                .SingleOrDefaultAsync(cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return Replay(winner, fingerprint);
        }
    }

    private static ApprovalEvidenceRevocationOutcome Replay(
        DecisionEvidenceRevocationRecord record,
        string fingerprint)
    {
        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            ApprovalTelemetry.RecordConflict("REVOKE_EVIDENCE");
            throw new DomainConflictException(
                "The revocation key was already used with different content.");
        }

        return new ApprovalEvidenceRevocationOutcome(
            record.Id,
            record.EvidenceId,
            ApprovalEvolutionCodes.EvidenceRevoked,
            record.EvidenceVersion,
            Replayed: true);
    }
}
