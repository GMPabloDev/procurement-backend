using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalSupersessionCommand(
    ApprovalWorkloadIdentity Workload,
    Guid PreviousCaseId,
    int ExpectedPreviousCaseVersion,
    string SupersessionKey,
    string SubmissionKey,
    string? ContractVersion,
    IReadOnlyList<ApprovalSupersessionMapping> TargetMapping,
    string CorrelationReference,
    /// <summary>SPEC 06 REQ-08 variable-cardinality delta; null selects the historical v2 mapping.</summary>
    IReadOnlyList<ApprovalSupersessionDeltaEntry>? TargetDelta = null,
    /// <summary>
    /// Previous target identities whose materiality proof the owning adapter verified equal to
    /// their replacement. Carry-forward never happens for an identity outside this set.
    /// </summary>
    IReadOnlySet<string>? VerifiedMaterialityIdentities = null,
    /// <summary>
    /// Subject version of the successor when the owning subject can skip versions (SPEC 06
    /// REQ-02/REQ-06); null keeps the strict previous+1 progression of SPEC 04.
    /// </summary>
    int? NewSubjectVersion = null);

public sealed record ApprovalSupersessionOutcome(
    Guid CaseId,
    Guid PreviousCaseId,
    string Status,
    int Version,
    bool Replayed,
    int CarryForwardCount,
    IReadOnlyList<Guid> OutboxEventIds);

/// <summary>
/// Full-case supersession of an immutable new version (SPEC 04 REQ-04, DEC-06): the mapping is
/// bijective and complete, the previous case and every non-terminal node become terminal
/// <c>SUPERSEDED</c> in one transaction, prior terminal decisions stay immutable, and the new
/// graph continues only through strict canonical carry-forward or new tasks.
/// </summary>
public sealed class ApprovalSupersessionService(
    ProcureToPayDbContext dbContext,
    ApprovalWorkloadAllowlist allowlist,
    // pi-lens-ignore: lsp:CS0246
    IApprovalSubmissionAdapterRegistry adapters,
    IApprovalOwnerWorkloadRegistry ownerWorkloads,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalSupersessionService> logger)
{
    public async Task<ApprovalSupersessionOutcome> SupersedeAsync(
        ApprovalSupersessionCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        allowlist.EnsureAllowed(command.Workload);
        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var supersessionKey = ApprovalLimits.RequireKey(command.SupersessionKey, "supersession_key");
        var submissionKey = ApprovalLimits.RequireKey(command.SubmissionKey, "submission_key");
        var mapping = command.TargetMapping?.ToArray() ?? [];
        var delta = command.TargetDelta?.ToArray();
        if (delta is not null)
        {
            ApprovalSupersessionDeltaRules.Validate(delta);
        }
        else if (mapping.Length == 0)
        {
            throw new DomainValidationException("A supersession needs at least one target mapping.");
        }

        var utcNow = occurredAt.ToUniversalTime();
        var previousRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == command.PreviousCaseId, cancellationToken)
            ?? throw new DomainNotFoundException("The previous approval case is not visible.");
        EnsureOwner(previousRecord, command.Workload);

        var adapter = adapters.ResolveExactlyOne(
            previousRecord.SubjectType, previousRecord.Operation, command.ContractVersion);
        var descriptor = adapter.Descriptor;
        if (delta is not null && !descriptor.SupersessionDeltaSupported)
        {
            throw new DomainValidationException(
                "The resolved adapter does not support the variable-cardinality supersession delta.");
        }

        if (delta is null && descriptor.SupersessionDeltaSupported)
        {
            throw new DomainValidationException(
                "The resolved adapter requires approval-supersession-delta/v1 for this supersession.");
        }
        var successorVersion = command.NewSubjectVersion ?? previousRecord.SubjectVersion + 1;
        if (successorVersion <= previousRecord.SubjectVersion)
        {
            throw new DomainValidationException(
                "A supersession requires a successor subject version after the previous one.");
        }

        var submission = await adapter.BuildAsync(
            new ApprovalSubmissionRequest(
                previousRecord.OrganizationId,
                previousRecord.SubjectType,
                previousRecord.SubjectId,
                successorVersion,
                previousRecord.Operation,
                descriptor.ContractVersion,
                submissionKey,
                previousRecord.RequesterId,
                previousRecord.OriginatorId,
                correlation,
                previousRecord.Id),
            cancellationToken);
        ApprovalSubmissionRules.Validate(submission, descriptor, command.Workload);
        if (!string.Equals(submission.SubmissionKey, submissionKey, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The adapter changed the submission key.");
        }

        if (submission.OrganizationId != previousRecord.OrganizationId ||
            !string.Equals(submission.SubjectType, previousRecord.SubjectType, StringComparison.Ordinal) ||
            submission.SubjectId != previousRecord.SubjectId)
        {
            throw new DomainValidationException("A supersession keeps the organization and subject identity.");
        }

        if (submission.SubjectVersion <= previousRecord.SubjectVersion)
        {
            throw new DomainConflictException("A supersession requires a new immutable subject version.");
        }

        var prerequisiteOwners = new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal);
        foreach (var prerequisite in submission.Prerequisites)
        {
            prerequisiteOwners[prerequisite.Key] = ownerWorkloads.ResolveExactlyOne(
                prerequisite.OwnerAdapterId, prerequisite.OwnerAdapterVersion);
        }

        var submissionFingerprint = ApprovalSubmissionRules.SubmissionFingerprint(
            submission, descriptor, command.Workload);
        var fingerprint = delta is null
            ? ApprovalFingerprints.SupersessionFingerprint(
                submissionFingerprint,
                command.Workload,
                previousRecord.Id,
                command.ExpectedPreviousCaseVersion,
                supersessionKey,
                mapping)
            : ApprovalFingerprints.SupersessionFingerprintV3(
                submissionFingerprint,
                command.Workload,
                previousRecord.Id,
                command.ExpectedPreviousCaseVersion,
                supersessionKey,
                delta);

        var existing = await FindSupersessionAsync(
            previousRecord.OrganizationId, command.Workload, supersessionKey, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(existing, fingerprint, cancellationToken);
        }

        // A first command for this key still requires the case at its expected version and state
        // (REQ-04); the replay above already resolved the idempotent path.
        if (previousRecord.Version != command.ExpectedPreviousCaseVersion)
        {
            ApprovalTelemetry.RecordConflict("SUPERSEDE");
            throw new DomainConflictException("The approval case was modified by another request.");
        }

        if (previousRecord.Status == (int)ApprovalCaseStatus.Superseded)
        {
            ApprovalTelemetry.RecordConflict("SUPERSEDE");
            throw new DomainConflictException("The approval case was already superseded.");
        }

        if (previousRecord.Status == (int)ApprovalCaseStatus.Cancelled)
        {
            throw new DomainConflictException("A cancelled approval case cannot be superseded.");
        }

        // The bijection is validated against the persisted previous graph and the declared new
        // graph before anything is written (REQ-04, CA-04).
        var previousRequirementRecords = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .Where(record => record.CaseId == previousRecord.Id)
            .ToArrayAsync(cancellationToken);
        var previousPrerequisiteRecords = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == previousRecord.Id)
            .ToArrayAsync(cancellationToken);
        if (delta is null)
        {
            ValidateMapping(
                mapping,
                previousRequirementRecords,
                previousPrerequisiteRecords,
                submission);
        }
        else
        {
            ValidateDelta(
                delta,
                previousRequirementRecords,
                previousPrerequisiteRecords,
                submission);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            var caseRecord = await dbContext.ApprovalCases
                .SingleOrDefaultAsync(record => record.Id == previousRecord.Id, cancellationToken)
                ?? throw new DomainNotFoundException("The previous approval case is not visible.");
            if (caseRecord.Version != command.ExpectedPreviousCaseVersion ||
                caseRecord.Status == (int)ApprovalCaseStatus.Superseded)
            {
                ApprovalTelemetry.RecordConflict("SUPERSEDE");
                throw new DomainConflictException("The approval case was modified by another request.");
            }

            var requirementRecords = await dbContext.ApprovalRequirements
                .Where(record => record.CaseId == caseRecord.Id)
                .ToArrayAsync(cancellationToken);
            var prerequisiteRecords = await dbContext.ApprovalPrerequisites
                .Where(record => record.CaseId == caseRecord.Id)
                .ToArrayAsync(cancellationToken);
            var taskRecords = await dbContext.ApprovalTasks
                .Where(record => record.CaseId == caseRecord.Id)
                .ToArrayAsync(cancellationToken);
            var previousCase = ApprovalCaseHydrator.Hydrate(
                caseRecord, requirementRecords, prerequisiteRecords, taskRecords);

            var newCaseId = Guid.NewGuid();
            var newCase = ApprovalCase.Create(
                newCaseId,
                submission,
                descriptor,
                command.Workload,
                prerequisiteOwners,
                submissionFingerprint,
                utcNow,
                correlation);

            var carryForwards = await ApplyCarryForwardsAsync(
                newCase,
                previousCase,
                delta is null
                    ? LegacyRetainedResolver(mapping)
                    : DeltaRetainedResolver(delta, command.VerifiedMaterialityIdentities),
                utcNow,
                correlation,
                cancellationToken);

            previousCase.Supersede(utcNow);
            var rootAudit = ApprovalEvidence.Root(
                previousCase,
                // pi-lens-ignore: lsp:CS0103
                ApprovalAuditActor.Workload(command.Workload),
                "CASE_SUPERSEDED",
                nameof(ApprovalCase),
                previousCase.Id,
                "[]",
                "Supersession by a new immutable version.",
                ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                    ("status", ApprovalCanonicalJson.String(((ApprovalCaseStatus)previousRecord.Status).ToString().ToUpperInvariant())))),
                ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                    ("status", ApprovalCanonicalJson.String("SUPERSEDED")))),
                utcNow,
                correlation);
            dbContext.ApprovalAuditEntries.Add(rootAudit);

            var outboxIds = new List<Guid>();
            WritePreviousEffects(previousCase, rootAudit.Id, utcNow, correlation);
            foreach (var plan in carryForwards)
            {
                outboxIds.AddRange(WriteCarryForward(
                    newCase, plan, rootAudit.Id, utcNow, correlation));
            }

            await assignmentEngine.AssignUnassignedAsync(
                newCase, utcNow, correlation, rootAudit.Id, cancellationToken);

            PersistNewCase(newCase, previousRecord, command, submissionFingerprint, utcNow);
            dbContext.CaseSupersessions.Add(new CaseSupersessionRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = previousRecord.OrganizationId,
                PreviousCaseId = previousRecord.Id,
                PreviousCaseVersion = command.ExpectedPreviousCaseVersion,
                NewCaseId = newCase.Id,
                SupersessionKey = supersessionKey,
                Fingerprint = fingerprint,
                WorkloadIssuer = command.Workload.Issuer,
                WorkloadClientId = command.Workload.ClientId,
                TargetMappingJson = delta is null
                    ? ApprovalJsonPersistence.SerializeSupersessionMapping(mapping)
                    : ApprovalJsonPersistence.SerializeSupersessionDelta(delta),
                CreatedAt = utcNow
            });

            foreach (var target in PreviousTargets(requirementRecords, prerequisiteRecords))
            {
                var lifecycle = ApprovalEvidence.Lifecycle(
                    newCase, previousRecord.Id, target, utcNow, correlation);
                lifecycle.SourceCommandId = rootAudit.Id;
                dbContext.ApprovalOutboxEvents.Add(lifecycle);
                outboxIds.Add(lifecycle.Id);
            }

            ApprovalStateSync.Apply(previousCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);
            await ReleasePreviousAssignmentsAsync(previousRecord.Id, utcNow, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Approval case {PreviousCaseId} was superseded by case {NewCaseId} with {CarryForwardCount} carry-forward decisions.",
                previousRecord.Id,
                newCase.Id,
                carryForwards.Count);
            ApprovalTelemetry.RecordSupersession("SUPERSEDED");
            ApprovalTelemetry.RecordCarryForward(carryForwards.Count);
            return new ApprovalSupersessionOutcome(
                newCase.Id,
                previousRecord.Id,
                newCase.Status.ToString().ToUpperInvariant(),
                newCase.Version,
                Replayed: false,
                carryForwards.Count,
                outboxIds);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await FindSupersessionAsync(
                previousRecord.OrganizationId, command.Workload, supersessionKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return await ReplayAsync(winner, fingerprint, cancellationToken);
        }
    }

    private async Task<ApprovalSupersessionOutcome> ReplayAsync(
        CaseSupersessionRecord record,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            ApprovalTelemetry.RecordConflict("SUPERSEDE");
            throw new DomainConflictException(
                "The supersession key was already used with different content.");
        }

        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == record.NewCaseId, cancellationToken);
        var carryForwardCount = await dbContext.DecisionCarryForwardEntries
            .AsNoTracking()
            .CountAsync(candidate => candidate.NewCaseId == record.NewCaseId, cancellationToken);
        var outboxIds = await dbContext.ApprovalOutboxEvents
            .AsNoTracking()
            .Where(candidate => candidate.CaseId == record.NewCaseId && candidate.SourceCommandId != null)
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => candidate.Id)
            .ToArrayAsync(cancellationToken);
        ApprovalTelemetry.RecordSupersession("REPLAYED");
        return new ApprovalSupersessionOutcome(
            caseRecord.Id,
            record.PreviousCaseId,
            ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
            caseRecord.Version,
            Replayed: true,
            carryForwardCount,
            outboxIds);
    }

    private sealed record CarryForwardPlan(
        ApprovalRequirement NewRequirement,
        ApprovalRequirement PreviousRequirement,
        ApprovalDecisionRecord SourceDecision,
        DecisionAuthorityEvidenceRecord Evidence,
        ApprovalSupersessionMapping CanonicalMapping,
        string SourceContractDigest,
        string NewContractDigest,
        string ProofDigest,
        string DecisionDigest,
        Guid DecisionId);

    /// <summary>
    /// Strict canonical equality (REQ-05, DEC-04; SPEC 06 REQ-08): organization, subject, mapped
    /// target identity, the whole requirement descriptor and a still-valid shared evidence are
    /// required. Retained coverage only preserves an approval when its owning adapter verified the
    /// materiality proof; anything else keeps a human task. No heuristic ever preserves an approval.
    /// </summary>
    private async Task<List<CarryForwardPlan>> ApplyCarryForwardsAsync(
        ApprovalCase newCase,
        ApprovalCase previousCase,
        Func<ApprovalTarget, ApprovalSupersessionDeltaEntry?> resolveRetained,
        DateTimeOffset occurredAt,
        string correlation,
        CancellationToken cancellationToken)
    {
        var plans = new List<CarryForwardPlan>();
        var previousRequirements = previousCase.Requirements
            .Select(requirement => (
                Requirement: requirement,
                Identities: requirement.Targets
                    .Select(target => target.CanonicalIdentity)
                    .ToHashSet(StringComparer.Ordinal)))
            .ToArray();
        var decisions = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .Where(record => record.CaseId == previousCase.Id && record.Action == (int)ApprovalDecisionAction.Approve)
            .OrderByDescending(record => record.DecidedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var decisionByRequirement = decisions
            .GroupBy(record => record.RequirementId)
            .ToDictionary(group => group.Key, group => group.First());
        var evidenceIds = decisions.Select(record => record.EvidenceId).Distinct().ToArray();
        var evidenceById = (await dbContext.DecisionAuthorityEvidences
                .AsNoTracking()
                .Where(record => evidenceIds.Contains(record.Id))
                .ToArrayAsync(cancellationToken))
            .ToDictionary(record => record.Id);
        // Policy exception requirements never carry forward (SPEC 05 REQ-05, CA-08): a waiver is
        // bound to one evaluation and needs its own case and decision, so it is mapped as a new
        // requirement that must be decided again.
        var exceptionRequirementIds = (await dbContext.ApprovalPolicyExceptionRequests
                .AsNoTracking()
                .Where(record => record.CaseId == previousCase.Id)
                .Select(record => record.RequirementId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();

        foreach (var newRequirement in newCase.Requirements
                     .OrderBy(requirement => requirement.WorkflowRequirementKey, StringComparer.Ordinal))
        {
            var mappedIdentities = new HashSet<string>(StringComparer.Ordinal);
            var matches = true;
            foreach (var target in newRequirement.Targets)
            {
                var entry = resolveRetained(target);
                if (entry is null)
                {
                    matches = false;
                    break;
                }

                mappedIdentities.Add(entry.PreviousIdentity);
            }

            if (!matches)
            {
                continue;
            }

            var previous = previousRequirements.FirstOrDefault(candidate =>
                candidate.Identities.SetEquals(mappedIdentities));
            if (previous.Requirement is null ||
                exceptionRequirementIds.Contains(previous.Requirement.Id) ||
                !decisionByRequirement.TryGetValue(previous.Requirement.Id, out var sourceDecision) ||
                !evidenceById.TryGetValue(sourceDecision.EvidenceId, out var evidence) ||
                evidence.Status != ApprovalEvolutionCodes.EvidenceValid)
            {
                continue;
            }

            var sourceDigest = ApprovalFingerprints.RequirementContractDigest(previous.Requirement);
            var newDigest = ApprovalFingerprints.RequirementContractDigest(newRequirement);
            if (!string.Equals(sourceDigest, newDigest, StringComparison.Ordinal))
            {
                continue;
            }

            // The proof pins one canonical target pair; the decision digest still covers the whole
            // mapped target set, so equality remains complete.
            var canonicalTarget = newRequirement.Targets
                .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
                .First();
            var canonicalEntry = resolveRetained(canonicalTarget)
                ?? throw new DomainValidationException("A carry-forward needs its canonical retained entry.");
            var canonicalMapping = ApprovalSupersessionMapping.Create(
                canonicalEntry.Previous!,
                canonicalEntry.Replacement!,
                canonicalEntry.MaterialitySchemaVersion,
                canonicalEntry.MaterialityDigest);
            var decisionId = Guid.NewGuid();
            var rootHumanDecisionId = sourceDecision.RootHumanDecisionId ?? sourceDecision.Id;
            var proofDigest = ApprovalFingerprints.CarryForwardProof(
                newCase.Id,
                newDigest,
                newRequirement.WorkflowRequirementKey,
                canonicalTarget,
                rootHumanDecisionId,
                sourceDecision.DecisionDigest,
                sourceDecision.Id,
                evidence.Id,
                evidence.Version,
                sourceDigest,
                canonicalMapping.Previous);
            var decisionDigest = ApprovalFingerprints.CarryForwardDecisionDigest(
                decisionId,
                1,
                newCase.Id,
                newCase.SubjectType,
                newCase.SubjectId,
                newCase.SubjectVersion,
                newCase.SourceSnapshotDigest,
                newRequirement.WorkflowRequirementKey,
                DecisionScopeDescriptor.Parse(newRequirement.DecisionScopeJson).Digest(),
                newRequirement.Targets,
                occurredAt,
                evidence.Digest,
                proofDigest,
                evidence.Id,
                evidence.Version,
                sourceDecision.Id,
                rootHumanDecisionId,
                newRequirement.ExcludedUserIds);
            plans.Add(new CarryForwardPlan(
                newRequirement,
                previous.Requirement,
                sourceDecision,
                evidence,
                canonicalMapping,
                sourceDigest,
                newDigest,
                proofDigest,
                decisionDigest,
                decisionId));
        }

        _ = correlation;
        return plans;
    }

    private IReadOnlyList<Guid> WriteCarryForward(
        ApprovalCase newCase,
        CarryForwardPlan plan,
        Guid rootAuditId,
        DateTimeOffset occurredAt,
        string correlation)
    {
        var sourceDecision = plan.SourceDecision;
        var evidence = plan.Evidence;
        var rootHumanDecisionId = sourceDecision.RootHumanDecisionId ?? sourceDecision.Id;
        var decision = ApprovalDecision.CreateCarryForward(
            plan.DecisionId,
            newCase.Id,
            newCase.OrganizationId,
            plan.NewRequirement.Id,
            ApprovalFingerprints.CarryForwardReason,
            occurredAt,
            plan.DecisionDigest,
            evidence.Digest,
            evidence.EvidenceJson,
            evidence.Id,
            evidence.Version,
            sourceDecision.Id,
            rootHumanDecisionId,
            plan.NewRequirement.Targets,
            correlation);
        newCase.ApplyCarryForward(plan.NewRequirement);

        dbContext.ApprovalDecisions.Add(new ApprovalDecisionRecord
        {
            Id = decision.Id,
            CaseId = decision.CaseId,
            OrganizationId = decision.OrganizationId,
            RequirementId = decision.RequirementId,
            TaskId = null,
            Action = (int)decision.Action,
            Origin = (int)decision.Origin,
            ActorType = "SYSTEM",
            ActorUserId = null,
            Reason = decision.Reason,
            DecidedAt = decision.DecidedAt,
            DecisionKey = null,
            Fingerprint = null,
            DecisionDigest = decision.DecisionDigest,
            AuthorityEvidenceDigest = decision.AuthorityEvidenceDigest,
            EligibilityEvidenceJson = decision.EligibilityEvidenceJson,
            EvidenceId = decision.EvidenceId,
            EvidenceVersion = decision.EvidenceVersion,
            SourceDecisionId = decision.SourceDecisionId,
            RootHumanDecisionId = decision.RootHumanDecisionId,
            RequirementVersion = null,
            TaskVersion = null,
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

        dbContext.DecisionCarryForwardEntries.Add(new DecisionCarryForwardEntryRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = newCase.OrganizationId,
            SourceDecisionId = decision.SourceDecisionId!.Value,
            RootHumanDecisionId = decision.RootHumanDecisionId!.Value,
            NewDecisionId = decision.Id,
            EvidenceId = decision.EvidenceId,
            EvidenceVersion = decision.EvidenceVersion,
            NewCaseId = newCase.Id,
            NewRequirementId = decision.RequirementId,
            NewRequirementKey = plan.NewRequirement.WorkflowRequirementKey,
            SourceRequirementContractDigest = plan.SourceContractDigest,
            NewRequirementContractDigest = plan.NewContractDigest,
            ProofDigest = plan.ProofDigest,
            SourceTargetJson = ApprovalJsonPersistence.SerializeTarget(plan.CanonicalMapping.Previous),
            NewTargetJson = ApprovalJsonPersistence.SerializeTarget(plan.CanonicalMapping.Replacement),
            TargetMappingJson = ApprovalJsonPersistence.SerializeSupersessionMapping([plan.CanonicalMapping]),
            CreatedAt = occurredAt
        });

        // The derived decision is a SYSTEM effect of the supersession root audit (REQ-05, REQ-08).
        var causedBy = ApprovalCausalLink.Approval(rootAuditId);
        var effectSource = newCase.SourceOf(plan.NewRequirement);
        var effectKey = ApprovalFingerprints.AutomaticEffectKey(
            "DECISION_CARRY_FORWARD",
            plan.NewRequirement.Version - 1,
            plan.NewRequirement.Version,
            newCase.Id,
            causedBy,
            effectSource,
            newCase.OrganizationId,
            "DECISION",
            decision.Id);
        dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Audit(
            newCase,
            ApprovalAuditActor.System(),
            causedBy,
            effectKey,
            "DECISION_CARRY_FORWARD",
            "DECISION",
            decision.Id,
            plan.NewRequirement.DecisionScopeJson,
            ApprovalFingerprints.CarryForwardReason,
            null,
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
                ("decision_digest", ApprovalCanonicalJson.String(decision.DecisionDigest)),
                ("origin", ApprovalCanonicalJson.String("CARRY_FORWARD")),
                ("root_human_decision_id", ApprovalCanonicalJson.String(rootHumanDecisionId)),
                ("source_decision_id", ApprovalCanonicalJson.String(sourceDecision.Id)))),
            occurredAt,
            correlation,
            plan.NewRequirement.Id));

        var outboxIds = new List<Guid>();
        foreach (var target in decision.Targets)
        {
            var outbox = ApprovalEvidence.CarryForwardOutbox(
                newCase,
                effectSource,
                target,
                decision.Id,
                decision.DecisionDigest,
                occurredAt,
                correlation);
            dbContext.ApprovalOutboxEvents.Add(outbox);
            outboxIds.Add(outbox.Id);
        }

        return outboxIds;
    }

    private void WritePreviousEffects(
        ApprovalCase previousCase,
        Guid rootAuditId,
        DateTimeOffset occurredAt,
        string correlation)
    {
        foreach (var effect in previousCase.DrainAutomaticEffects())
        {
            var (scopeJson, requirementId) = effect.Source.Type == ApprovalEntitySourceType.ApprovalRequirement
                ? (previousCase.RequireRequirement(effect.Source.Id).DecisionScopeJson, effect.Source.Id)
                : ("[]", (Guid?)null);
            dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Effect(
                previousCase, rootAuditId, effect, scopeJson, occurredAt, correlation, requirementId));
        }
    }

    private void PersistNewCase(
        ApprovalCase newCase,
        ApprovalCaseRecord previousRecord,
        ApprovalSupersessionCommand command,
        string submissionFingerprint,
        DateTimeOffset occurredAt)
    {
        dbContext.ApprovalCases.Add(new ApprovalCaseRecord
        {
            Id = newCase.Id,
            OrganizationId = newCase.OrganizationId,
            SubjectType = newCase.SubjectType,
            SubjectId = newCase.SubjectId,
            SubjectVersion = newCase.SubjectVersion,
            Operation = newCase.Operation,
            SourceSnapshotDigest = newCase.SourceSnapshotDigest,
            WorkloadIssuer = newCase.WorkloadIssuer,
            WorkloadClientId = newCase.WorkloadClientId,
            SubmissionKey = newCase.SubmissionKey,
            SubmissionFingerprint = newCase.SubmissionFingerprint,
            OriginatorId = newCase.OriginatorId,
            RequesterId = newCase.RequesterId,
            Status = (int)newCase.Status,
            Version = newCase.Version,
            CreatedAt = newCase.CreatedAt,
            CorrelationReference = newCase.CorrelationReference
        });

        foreach (var requirement in newCase.Requirements)
        {
            dbContext.ApprovalRequirements.Add(new ApprovalRequirementRecord
            {
                Id = requirement.Id,
                CaseId = newCase.Id,
                OrganizationId = newCase.OrganizationId,
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

        foreach (var prerequisite in newCase.Prerequisites)
        {
            dbContext.ApprovalPrerequisites.Add(new ApprovalPrerequisiteRecord
            {
                Id = prerequisite.Id,
                CaseId = newCase.Id,
                OrganizationId = newCase.OrganizationId,
                Key = prerequisite.Key,
                OwnerAdapterId = prerequisite.OwnerAdapterId,
                OwnerAdapterVersion = prerequisite.OwnerAdapterVersion,
                OwnerWorkloadIssuer = prerequisite.OwnerWorkloadIssuer,
                OwnerWorkloadClientId = prerequisite.OwnerWorkloadClientId,
                SourceControlType = prerequisite.SourceControlType,
                SourceControlDigest = prerequisite.SourceControlDigest,
                ParametersJson = prerequisite.ParametersJson,
                TargetsJson = ApprovalJsonPersistence.SerializeTargets(prerequisite.Targets),
                Status = (int)prerequisite.Status,
                Version = prerequisite.Version
            });
        }

        foreach (var task in newCase.Tasks)
        {
            dbContext.ApprovalTasks.Add(new ApprovalTaskRecord
            {
                Id = task.Id,
                CaseId = newCase.Id,
                OrganizationId = newCase.OrganizationId,
                RequirementId = task.RequirementId,
                Status = (int)task.Status,
                CurrentAssigneeUserId = task.CurrentAssigneeUserId,
                Version = task.Version
            });
        }

        // The new version keeps the submission-key identity surface of its ingestion (REQ-04).
        dbContext.ApprovalSubmissionReservations.Add(new ApprovalSubmissionReservationRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = newCase.OrganizationId,
            WorkloadIssuer = command.Workload.Issuer,
            WorkloadClientId = command.Workload.ClientId,
            SubjectType = newCase.SubjectType,
            Operation = newCase.Operation,
            SubmissionKey = newCase.SubmissionKey,
            Fingerprint = submissionFingerprint,
            CaseId = newCase.Id,
            CreatedAt = occurredAt
        });
        _ = previousRecord;
    }

    private async Task ReleasePreviousAssignmentsAsync(
        Guid previousCaseId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var open = await dbContext.ApprovalAssignments
            .Where(record => record.CaseId == previousCaseId && record.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        foreach (var assignment in open)
        {
            assignment.ReleasedAt = occurredAt;
        }
    }

    private Task<CaseSupersessionRecord?> FindSupersessionAsync(
        Guid organizationId,
        ApprovalWorkloadIdentity workload,
        string supersessionKey,
        CancellationToken cancellationToken) =>
        dbContext.CaseSupersessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.WorkloadIssuer == workload.Issuer &&
                          record.WorkloadClientId == workload.ClientId &&
                          record.SupersessionKey == supersessionKey,
                cancellationToken);

    private static void ValidateMapping(
        IReadOnlyList<ApprovalSupersessionMapping> mapping,
        IReadOnlyCollection<ApprovalRequirementRecord> previousRequirementRecords,
        IReadOnlyCollection<ApprovalPrerequisiteRecord> previousPrerequisiteRecords,
        ApprovalSubmission submission)
    {
        var previousTargets = PreviousTargets(previousRequirementRecords, previousPrerequisiteRecords);
        var newTargets = NewTargets(submission);
        var previousIdentities = previousTargets.Select(target => target.CanonicalIdentity).ToArray();
        var newIdentities = newTargets.Select(target => target.CanonicalIdentity).ToArray();
        var mappedPrevious = mapping.Select(entry => entry.PreviousIdentity).ToArray();
        var mappedReplacement = mapping.Select(entry => entry.ReplacementIdentity).ToArray();
        if (mappedPrevious.Length != mappedPrevious.Distinct(StringComparer.Ordinal).Count() ||
            mappedReplacement.Length != mappedReplacement.Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("A supersession mapping must be bijective.");
        }

        if (mappedPrevious.Length != previousIdentities.Length ||
            !mappedPrevious.OrderBy(identity => identity, StringComparer.Ordinal)
                .SequenceEqual(previousIdentities.OrderBy(identity => identity, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new DomainConflictException(
                "The supersession mapping must cover every previous target exactly once.");
        }

        if (mappedReplacement.Length != newIdentities.Length ||
            !mappedReplacement.OrderBy(identity => identity, StringComparer.Ordinal)
                .SequenceEqual(newIdentities.OrderBy(identity => identity, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new DomainConflictException(
                "The supersession mapping must declare exactly the targets of the new version.");
        }

        // A grouped requirement is indivisible: its targets are recreated inside exactly one
        // requirement of the new version and no new requirement merges two previous groups
        // (REQ-04, CA-04). Splitting or merging would change the approval partition silently.
        var previousGroups = previousRequirementRecords
            .Select(record => Identities(ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson)))
            .ToArray();
        var newGroups = submission.Requirements
            .Select(requirement => Identities(requirement.Targets))
            .ToArray();
        var byPrevious = mapping.ToDictionary(
            entry => entry.PreviousIdentity, entry => entry.ReplacementIdentity, StringComparer.Ordinal);
        var byReplacement = mapping.ToDictionary(
            entry => entry.ReplacementIdentity, entry => entry.PreviousIdentity, StringComparer.Ordinal);
        foreach (var group in previousGroups)
        {
            var replacements = group.Select(identity => byPrevious[identity]).ToHashSet(StringComparer.Ordinal);
            if (newGroups.Count(candidate => replacements.IsSubsetOf(candidate)) != 1)
            {
                throw new DomainConflictException(
                    "A grouped previous requirement cannot be split or merged in the new version.");
            }
        }

        foreach (var group in newGroups)
        {
            var sources = group.Select(identity => byReplacement[identity]).ToHashSet(StringComparer.Ordinal);
            if (previousGroups.Count(candidate => sources.IsSubsetOf(candidate)) != 1)
            {
                throw new DomainConflictException(
                    "A grouped requirement of the new version cannot be split or merged from the previous one.");
            }
        }
    }

    /// <summary>
    /// Coverage of the closed delta against both persisted manifests (SPEC 06 REQ-08): every
    /// previous target appears once as <c>RETAINED|REMOVED</c>, every target of the new submission
    /// once as <c>RETAINED|ADDED</c>, additions never reuse an existing stable identity, and a
    /// grouped requirement is neither split nor merged.
    /// </summary>
    private static void ValidateDelta(
        IReadOnlyList<ApprovalSupersessionDeltaEntry> delta,
        IReadOnlyCollection<ApprovalRequirementRecord> previousRequirementRecords,
        IReadOnlyCollection<ApprovalPrerequisiteRecord> previousPrerequisiteRecords,
        ApprovalSubmission submission)
    {
        var previousTargets = PreviousTargets(previousRequirementRecords, previousPrerequisiteRecords);
        var newTargets = NewTargets(submission);
        var previousIdentities = previousTargets.Select(target => target.CanonicalIdentity).ToArray();
        var newIdentities = newTargets.Select(target => target.CanonicalIdentity).ToArray();
        var declaredPrevious = delta
            .Where(entry => entry.Previous is not null)
            .Select(entry => entry.PreviousIdentity)
            .ToArray();
        var declaredReplacement = delta
            .Where(entry => entry.Replacement is not null)
            .Select(entry => entry.ReplacementIdentity)
            .ToArray();
        if (!declaredPrevious.OrderBy(identity => identity, StringComparer.Ordinal)
                .SequenceEqual(previousIdentities.OrderBy(identity => identity, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new DomainConflictException(
                "The supersession delta must cover every previous target exactly once.");
        }

        if (!declaredReplacement.OrderBy(identity => identity, StringComparer.Ordinal)
                .SequenceEqual(newIdentities.OrderBy(identity => identity, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new DomainConflictException(
                "The supersession delta must declare exactly the targets of the new version.");
        }

        var previousStable = previousTargets
            .Select(ApprovalSupersessionDeltaEntry.StableIdentity)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var added in delta.Where(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Added))
        {
            if (previousStable.Contains(ApprovalSupersessionDeltaEntry.StableIdentity(added.Replacement!)))
            {
                throw new DomainConflictException(
                    "An added target cannot reuse the stable identity of a previous target.");
            }
        }

        // A grouped requirement stays indivisible (REQ-04, CA-04): splitting or merging its
        // covered set silently changes the approval partition, so it fails closed.
        var previousGroups = previousRequirementRecords
            .Select(record => Identities(ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson)))
            .ToArray();
        var newGroups = submission.Requirements
            .Select(requirement => Identities(requirement.Targets))
            .ToArray();
        var previousToReplacement = delta
            .Where(entry => entry.Previous is not null && entry.Replacement is not null)
            .ToDictionary(entry => entry.PreviousIdentity, entry => entry.ReplacementIdentity, StringComparer.Ordinal);
        var replacementToPrevious = delta
            .Where(entry => entry.Previous is not null && entry.Replacement is not null)
            .ToDictionary(entry => entry.ReplacementIdentity, entry => entry.PreviousIdentity, StringComparer.Ordinal);
        foreach (var group in previousGroups)
        {
            var replacements = group
                .Where(previousToReplacement.ContainsKey)
                .Select(identity => previousToReplacement[identity])
                .ToHashSet(StringComparer.Ordinal);
            if (replacements.Count != 0 && newGroups.Count(candidate => replacements.IsSubsetOf(candidate)) != 1)
            {
                throw new DomainConflictException(
                    "A grouped previous requirement cannot be split or merged in the new version.");
            }
        }

        foreach (var group in newGroups)
        {
            var sources = group
                .Where(replacementToPrevious.ContainsKey)
                .Select(identity => replacementToPrevious[identity])
                .ToHashSet(StringComparer.Ordinal);
            if (sources.Count != 0 && previousGroups.Count(candidate => sources.IsSubsetOf(candidate)) != 1)
            {
                static string Short(string identity)
                {
                    var parts = identity.Split(':');
                    return parts.Length >= 3 ? $"{parts[1][..8]}:{parts[2]}" : identity;
                }

                throw new DomainConflictException(
                    "A grouped requirement of the new version cannot be split or merged from the previous one. " +
                    $"group=[{string.Join(",", group.Select(Short).OrderBy(value => value, StringComparer.Ordinal))}], " +
                    $"sources=[{string.Join(",", sources.Select(Short).OrderBy(value => value, StringComparer.Ordinal))}], " +
                    $"previous=[{string.Join("|", previousGroups.Select(candidate => string.Join(",", candidate.Select(Short).OrderBy(value => value, StringComparer.Ordinal))))}], " +
                    $"new=[{string.Join("|", newGroups.Select(candidate => string.Join(",", candidate.Select(Short).OrderBy(value => value, StringComparer.Ordinal))))}].");
            }
        }
    }

    /// <summary>
    /// SPEC 04 REQ-05 legacy mapping: exact target identity equality is required, so the immutable
    /// target itself is the materiality proof.
    /// </summary>
    private static Func<ApprovalTarget, ApprovalSupersessionDeltaEntry?> LegacyRetainedResolver(
        IReadOnlyList<ApprovalSupersessionMapping> mapping)
    {
        var byReplacement = mapping.ToDictionary(entry => entry.ReplacementIdentity, StringComparer.Ordinal);
        return target =>
        {
            if (!byReplacement.TryGetValue(target.CanonicalIdentity, out var entry) ||
                !string.Equals(entry.PreviousIdentity, entry.ReplacementIdentity, StringComparison.Ordinal))
            {
                return null;
            }

            return ApprovalSupersessionDeltaEntry.CreateRetained(
                entry.Previous, entry.Replacement, entry.MaterialitySchemaVersion, entry.MaterialityDigest);
        };
    }

    /// <summary>
    /// SPEC 06 REQ-08 retained coverage whose materiality proof the owning adapter verified;
    /// target version/digest may differ, while an unverified or ambiguous pair keeps its task.
    /// </summary>
    private static Func<ApprovalTarget, ApprovalSupersessionDeltaEntry?> DeltaRetainedResolver(
        IReadOnlyList<ApprovalSupersessionDeltaEntry> delta,
        IReadOnlySet<string>? verifiedMaterialityIdentities)
    {
        var retained = delta
            .Where(entry => entry.ChangeKind == ApprovalSupersessionChangeKind.Retained)
            .ToDictionary(entry => entry.ReplacementIdentity, StringComparer.Ordinal);
        return target => retained.TryGetValue(target.CanonicalIdentity, out var entry) &&
                         entry.Previous is not null &&
                         verifiedMaterialityIdentities is not null &&
                         verifiedMaterialityIdentities.Contains(entry.PreviousIdentity)
            ? entry
            : null;
    }

    private static HashSet<string> Identities(IEnumerable<ApprovalTarget> targets) =>
        targets.Select(target => target.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<ApprovalTarget> PreviousTargets(
        IEnumerable<ApprovalRequirementRecord> requirementRecords,
        IEnumerable<ApprovalPrerequisiteRecord> prerequisiteRecords) =>
        requirementRecords
            .SelectMany(record => ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson))
            .Concat(prerequisiteRecords.SelectMany(record =>
                ApprovalJsonPersistence.DeserializeTargets(record.TargetsJson)))
            .GroupBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<ApprovalTarget> NewTargets(ApprovalSubmission submission) =>
        submission.Requirements
            .SelectMany(requirement => requirement.Targets)
            .Concat(submission.Prerequisites.SelectMany(prerequisite => prerequisite.Targets))
            .GroupBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();

    private static void EnsureOwner(ApprovalCaseRecord record, ApprovalWorkloadIdentity workload)
    {
        if (!string.Equals(record.WorkloadIssuer, workload.Issuer, StringComparison.Ordinal) ||
            !string.Equals(record.WorkloadClientId, workload.ClientId, StringComparison.Ordinal))
        {
            throw new DomainForbiddenException("Only the owning workload can supersede an approval case.");
        }
    }
}
