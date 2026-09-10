using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed record PolicyActor(string ActorType, Guid? ActorUserId);

public sealed record PolicyDraftDocument(
    Guid OrganizationId,
    string ScopesJson,
    string ContentJson,
    string ContentDigest);

public sealed record PolicyEvaluationCaller(
    Guid OrganizationId,
    string WorkloadIssuer,
    string WorkloadClientId,
    string Operation,
    string EvaluationKey,
    Guid PolicySetVersionId,
    string CorrelationReference)
{
    public string SubjectType { get; init; } = string.Empty;
    public string Cause { get; init; } = "INITIAL";
    public string? PreviousResultDigest { get; init; }
    public IReadOnlyList<string> ExceptionReferenceIds { get; init; } = [];
    public string? ExceptionTargetRequirementKey { get; init; }
    public int? ExceptionFrom { get; init; }
    public int? ExceptionTo { get; init; }
    public string? FactsDigest { get; init; }
    public string? ManifestDigest { get; init; }
    public string? InputCanonicalJson { get; init; }
}

public sealed class PolicyPersistenceService(ProcureToPayDbContext dbContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PolicySetVersionRecord> AppendVersionAsync(
        PolicySetVersion policy,
        DateTimeOffset createdAt,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        var nextSequence = (await dbContext.PolicySetVersions
            .Where(version => version.OrganizationId == policy.OrganizationId)
            .Select(version => (long?)version.Sequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        if (policy.Sequence != nextSequence)
        {
            throw new DomainConflictException("Policy version sequence is stale; create a new version from the current sequence.");
        }

        var content = PolicyCanonicalizer.CanonicalizePolicy(policy);
        var record = new PolicySetVersionRecord
        {
            Id = policy.Id,
            OrganizationId = policy.OrganizationId,
            Sequence = policy.Sequence,
            Status = (int)policy.Status,
            ScopesJson = JsonSerializer.Serialize(policy.Scopes.OrderBy(scope => scope), JsonOptions),
            ContentJson = content,
            ContentDigest = policy.ContentDigest ?? PolicyCanonicalizer.ComputePolicyDigest(policy),
            CreatedAt = createdAt
        };
        dbContext.PolicySetVersions.Add(record);
        AddAudit("POLICY_VERSION_CREATED", record.Id, null, (int)record.Sequence, record.ContentJson,
            actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<PolicySetVersionRecord> AppendDraftAsync(
        PolicyDraftDocument document,
        DateTimeOffset createdAt,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var sequence = (await dbContext.PolicySetVersions
            .Where(version => version.OrganizationId == document.OrganizationId)
            .Select(version => (long?)version.Sequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var record = new PolicySetVersionRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = document.OrganizationId,
            Sequence = sequence,
            Status = (int)PolicySetStatus.Draft,
            ScopesJson = document.ScopesJson,
            ContentJson = document.ContentJson,
            ContentDigest = document.ContentDigest,
            CreatedAt = createdAt
        };
        dbContext.PolicySetVersions.Add(record);
        AddAudit("POLICY_DRAFT_CREATED", record.Id, null, (int)record.Sequence,
            record.ContentJson, actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<(PolicySetVersionRecord Version, PolicyActivationRecord Activation)> PublishAndActivateAsync(
        Guid draftId,
        DateTimeOffset effectiveFrom,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default,
        Guid? organizationId = null)
    {
        if (effectiveFrom < DateTimeOffset.UtcNow)
        {
            throw new DomainValidationException("Policy activation cannot start in the past.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var draft = await dbContext.PolicySetVersions
            .SingleOrDefaultAsync(version => version.Id == draftId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy draft does not exist.");
        if (draft.Status != (int)PolicySetStatus.Draft)
        {
            throw new DomainConflictException("Only a draft policy can be published.");
        }
        if (organizationId.HasValue && draft.OrganizationId != organizationId.Value)
        {
            throw new DomainForbiddenException("The policy draft is outside the actor organization.");
        }
        ValidateDocument(new PolicyDraftDocument(draft.OrganizationId, draft.ScopesJson,
            draft.ContentJson, draft.ContentDigest));

        var openActivations = await dbContext.PolicyActivations
            .Where(activation => activation.OrganizationId == draft.OrganizationId)
            .Where(activation => !dbContext.PolicyRetirements.Any(retirement =>
                retirement.PolicyActivationId == activation.Id && retirement.EffectiveTo <= effectiveFrom))
            .ToArrayAsync(cancellationToken);
        if (openActivations.Any(activation => activation.EffectiveFrom >= effectiveFrom) || openActivations.Length > 1)
        {
            throw new DomainConflictException("Policy activation intervals overlap or are corrupt.");
        }

        var sequence = (await dbContext.PolicySetVersions
            .Where(version => version.OrganizationId == draft.OrganizationId)
            .Select(version => (long?)version.Sequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var published = new PolicySetVersionRecord
        {
            Id = Guid.NewGuid(), OrganizationId = draft.OrganizationId, Sequence = sequence,
            Status = (int)PolicySetStatus.Published, ScopesJson = draft.ScopesJson,
            ContentJson = draft.ContentJson, ContentDigest = draft.ContentDigest,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.PolicySetVersions.Add(published);

        foreach (var previous in openActivations)
        {
            dbContext.PolicyRetirements.Add(new PolicyRetirementRecord
            {
                Id = Guid.NewGuid(), PolicyActivationId = previous.Id, EffectiveTo = effectiveFrom,
                ActorType = actor.ActorType, ActorUserId = actor.ActorUserId, Reason = reason,
                OccurredAt = DateTimeOffset.UtcNow
            });
        }

        var activation = new PolicyActivationRecord
        {
            Id = Guid.NewGuid(), OrganizationId = draft.OrganizationId, PolicySetVersionId = published.Id,
            EffectiveFrom = effectiveFrom, ActorType = actor.ActorType, ActorUserId = actor.ActorUserId,
            Reason = reason, OccurredAt = DateTimeOffset.UtcNow
        };
        dbContext.PolicyActivations.Add(activation);
        AddAudit("POLICY_PUBLISHED", published.Id, draft.ContentJson, (int)published.Sequence,
            published.ContentJson, actor, reason, correlationReference);
        AddAudit("POLICY_ACTIVATED", activation.Id, null, null, null, actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (published, activation);
    }

    public async Task<PolicySetVersionRecord> PublishAsync(
        Guid draftId,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var draft = await dbContext.PolicySetVersions
            .SingleOrDefaultAsync(version => version.Id == draftId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy draft does not exist.");
        if (draft.Status != (int)PolicySetStatus.Draft)
        {
            throw new DomainConflictException("Only a draft policy can be published.");
        }
        ValidateDocument(new PolicyDraftDocument(draft.OrganizationId, draft.ScopesJson,
            draft.ContentJson, draft.ContentDigest));
        var sequence = (await dbContext.PolicySetVersions
            .Where(version => version.OrganizationId == draft.OrganizationId)
            .Select(version => (long?)version.Sequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var published = new PolicySetVersionRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = draft.OrganizationId,
            Sequence = sequence,
            Status = (int)PolicySetStatus.Published,
            ScopesJson = draft.ScopesJson,
            ContentJson = draft.ContentJson,
            ContentDigest = draft.ContentDigest,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.PolicySetVersions.Add(published);
        AddAudit("POLICY_PUBLISHED", published.Id, draft.ContentJson, (int)published.Sequence,
            published.ContentJson, actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    public async Task<PolicyActivationRecord> ActivateAsync(
        Guid organizationId,
        Guid policySetVersionId,
        DateTimeOffset effectiveFrom,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        if (effectiveFrom < DateTimeOffset.UtcNow)
        {
            throw new DomainValidationException("Policy activation cannot start in the past.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var version = await dbContext.PolicySetVersions
            .SingleOrDefaultAsync(candidate => candidate.Id == policySetVersionId && candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The policy version does not belong to the organization.");
        if (version.Status != (int)PolicySetStatus.Published)
        {
            throw new DomainConflictException("Only published policy versions can be activated.");
        }
        if (await dbContext.PolicyRetirements.AnyAsync(retirement =>
                dbContext.PolicyActivations.Any(activation =>
                    activation.Id == retirement.PolicyActivationId && activation.PolicySetVersionId == policySetVersionId),
                cancellationToken))
        {
            throw new DomainConflictException("A retired policy version cannot be reactivated.");
        }

        var active = await dbContext.PolicyActivations
            .Where(activation => activation.OrganizationId == organizationId)
            .Where(activation => !dbContext.PolicyRetirements.Any(retirement =>
                retirement.PolicyActivationId == activation.Id && retirement.EffectiveTo <= effectiveFrom))
            .AnyAsync(cancellationToken);
        if (active)
        {
            throw new DomainConflictException("An active policy already covers the requested activation time.");
        }

        var record = new PolicyActivationRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            PolicySetVersionId = policySetVersionId,
            EffectiveFrom = effectiveFrom,
            ActorType = actor.ActorType,
            ActorUserId = actor.ActorUserId,
            Reason = reason,
            OccurredAt = DateTimeOffset.UtcNow
        };
        dbContext.PolicyActivations.Add(record);
        AddAudit("POLICY_ACTIVATED", record.Id, null, null, null, actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<PolicyActivationRecord?> FindActiveAsync(
        Guid organizationId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var activations = await dbContext.PolicyActivations
            .AsNoTracking()
            .Include(activation => activation.PolicySetVersion)
            .Where(activation => activation.OrganizationId == organizationId && activation.EffectiveFrom <= at)
            .Where(activation => !dbContext.PolicyRetirements.Any(retirement =>
                retirement.PolicyActivationId == activation.Id && retirement.EffectiveTo <= at))
            .OrderByDescending(activation => activation.EffectiveFrom)
            .ThenByDescending(activation => activation.PolicySetVersion.Sequence)
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (activations.Length > 1)
        {
            throw new PolicyDependencyUnavailableException("Multiple policy activations are current.");
        }

        return activations.SingleOrDefault();
    }

    public async Task<PolicyRetirementRecord> RetireAsync(
        Guid activationId,
        DateTimeOffset effectiveTo,
        PolicyActor actor,
        string reason,
        string correlationReference,
        CancellationToken cancellationToken = default,
        Guid? organizationId = null)
    {
        if (effectiveTo < DateTimeOffset.UtcNow)
        {
            throw new DomainValidationException("Policy retirement cannot be backdated.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var activation = await dbContext.PolicyActivations
            .SingleOrDefaultAsync(candidate => candidate.Id == activationId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy activation does not exist.");
        if (organizationId.HasValue && activation.OrganizationId != organizationId.Value)
        {
            throw new DomainForbiddenException("The policy activation is outside the actor organization.");
        }
        if (effectiveTo <= activation.EffectiveFrom)
        {
            throw new DomainValidationException("A retirement must occur after activation.");
        }
        if (await dbContext.PolicyRetirements.AnyAsync(retirement => retirement.PolicyActivationId == activationId, cancellationToken))
        {
            throw new DomainConflictException("A policy activation can only be retired once.");
        }

        var record = new PolicyRetirementRecord
        {
            Id = Guid.NewGuid(),
            PolicyActivationId = activationId,
            EffectiveTo = effectiveTo,
            ActorType = actor.ActorType,
            ActorUserId = actor.ActorUserId,
            Reason = reason,
            OccurredAt = DateTimeOffset.UtcNow
        };
        dbContext.PolicyRetirements.Add(record);
        AddAudit("POLICY_RETIRED", activationId, null, null, null, actor, reason, correlationReference);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<PolicyEvaluationBundleRecord> AppendEvaluationAsync(
        PolicyEvaluationBundle bundle,
        PolicyEvaluationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        if (caller.PolicySetVersionId == Guid.Empty || bundle.Subject.Id == Guid.Empty)
        {
            throw new InvalidOperationException("Policy evaluations require a policy and subject reference.");
        }

        var fingerprint = ComputeFingerprint(caller, bundle);
        var existingByKey = await dbContext.PolicyEvaluationBundles
            .SingleOrDefaultAsync(evaluation =>
                evaluation.OrganizationId == caller.OrganizationId &&
                evaluation.WorkloadIssuer == caller.WorkloadIssuer &&
                evaluation.WorkloadClientId == caller.WorkloadClientId &&
                evaluation.Operation == caller.Operation &&
                evaluation.EvaluationKey == caller.EvaluationKey,
                cancellationToken);
        if (existingByKey is not null)
        {
            if (existingByKey.IdempotencyFingerprint == fingerprint)
            {
                return existingByKey;
            }

            throw new DomainConflictException("The evaluation key is already bound to a different request.");
        }

        var nextSequence = await dbContext.PolicyEvaluationBundles
            .CountAsync(evaluation => evaluation.OrganizationId == caller.OrganizationId &&
                                      evaluation.SubjectId == bundle.Subject.Id &&
                                      evaluation.SubjectVersion == bundle.Subject.Version,
                cancellationToken);
        var bundleJson = SerializeBundleWithMetadata(bundle, nextSequence + 1, caller);

        var record = new PolicyEvaluationBundleRecord
        {
            Id = bundle.Id,
            OrganizationId = caller.OrganizationId,
            EvaluationKey = caller.EvaluationKey,
            WorkloadIssuer = caller.WorkloadIssuer,
            WorkloadClientId = caller.WorkloadClientId,
            Operation = caller.Operation,
            EvaluationSequence = nextSequence + 1,
            SubjectId = bundle.Subject.Id,
            SubjectVersion = bundle.Subject.Version,
            PolicySetVersionId = caller.PolicySetVersionId,
            EvaluatedAt = bundle.EvaluatedAt,
            PolicyContentDigest = bundle.PolicyContentDigest,
            InputDigest = bundle.InputDigest,
            Result = bundle.Result.ToString().ToUpperInvariant(),
            ResultDigest = bundle.ResultDigest,
            BundleJson = bundleJson,
            IdempotencyFingerprint = fingerprint,
            PreviousBundleId = bundle.PreviousBundleId,
            CorrelationReference = caller.CorrelationReference
        };
        dbContext.PolicyEvaluationBundles.Add(record);
        AddAudit("POLICY_EVALUATED", record.Id, null, null, record.BundleJson,
            new PolicyActor("WORKLOAD", null), "Policy evaluation persisted", caller.CorrelationReference);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.Entry(record).State = EntityState.Detached;
            var duplicate = await dbContext.PolicyEvaluationBundles
                .SingleOrDefaultAsync(evaluation =>
                    evaluation.OrganizationId == caller.OrganizationId &&
                    evaluation.WorkloadIssuer == caller.WorkloadIssuer &&
                    evaluation.WorkloadClientId == caller.WorkloadClientId &&
                    evaluation.Operation == caller.Operation &&
                    evaluation.EvaluationKey == caller.EvaluationKey,
                    cancellationToken);
            if (duplicate is not null)
            {
                if (duplicate.IdempotencyFingerprint == fingerprint)
                {
                    return duplicate;
                }

                throw new DomainConflictException("The evaluation key is already bound to a different request.");
            }

            throw;
        }

        return record;
    }

    public Task<PolicyEvaluationBundleRecord?> FindEvaluationAsync(
        Guid evaluationId,
        CancellationToken cancellationToken = default) =>
        dbContext.PolicyEvaluationBundles.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == evaluationId, cancellationToken);

    public Task<PolicyEvaluationBundleRecord?> FindLatestEvaluationAsync(
        Guid organizationId,
        Guid subjectId,
        int subjectVersion,
        CancellationToken cancellationToken = default) =>
        dbContext.PolicyEvaluationBundles.AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.SubjectId == subjectId &&
                             record.SubjectVersion == subjectVersion)
            .OrderByDescending(record => record.EvaluationSequence)
            .ThenByDescending(record => record.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<IReadOnlyList<PolicyEvaluationBundleRecord>> FindByWorkloadEvaluationKeyAsync(
        string issuer,
        string clientId,
        string operation,
        string evaluationKey,
        CancellationToken cancellationToken = default) =>
        dbContext.PolicyEvaluationBundles.AsNoTracking()
            .Where(record => record.WorkloadIssuer == issuer &&
                             record.WorkloadClientId == clientId &&
                             record.Operation == operation &&
                             record.EvaluationKey == evaluationKey)
            .Take(2)
            .ToArrayAsync(cancellationToken)
            .ContinueWith(task => (IReadOnlyList<PolicyEvaluationBundleRecord>)task.Result,
                cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public Task<PolicyEvaluationBundleRecord?> FindByWorkloadEvaluationKeyAsync(
        Guid organizationId,
        string issuer,
        string clientId,
        string operation,
        string evaluationKey,
        CancellationToken cancellationToken = default) =>
        dbContext.PolicyEvaluationBundles.AsNoTracking().SingleOrDefaultAsync(record =>
            record.OrganizationId == organizationId &&
            record.WorkloadIssuer == issuer &&
            record.WorkloadClientId == clientId &&
            record.Operation == operation &&
            record.EvaluationKey == evaluationKey,
            cancellationToken);

    public async Task<PolicyEvaluationReservationRecord?> ReserveEvaluationKeyAsync(
        Guid organizationId,
        string issuer,
        string clientId,
        string operation,
        string evaluationKey,
        Guid subjectId,
        int subjectVersion,
        string idempotencyFingerprint,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 6000; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            if (await FindByWorkloadEvaluationKeyAsync(
                    organizationId, issuer, clientId, operation, evaluationKey, cancellationToken) is not null)
            {
                return null;
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);
            var reservation = await dbContext.PolicyEvaluationReservations.AsNoTracking().SingleOrDefaultAsync(record =>
                record.OrganizationId == organizationId &&
                record.WorkloadIssuer == issuer &&
                record.WorkloadClientId == clientId &&
                record.Operation == operation &&
                record.EvaluationKey == evaluationKey,
                cancellationToken);
            if (reservation is not null && reservation.ExpiresAt > now)
            {
                await transaction.RollbackAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }
            if (reservation is not null)
            {
                dbContext.PolicyEvaluationReservations.Remove(reservation);
            }

            var created = new PolicyEvaluationReservationRecord
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId,
                WorkloadIssuer = issuer, WorkloadClientId = clientId, Operation = operation,
                EvaluationKey = evaluationKey, SubjectId = subjectId, SubjectVersion = subjectVersion,
                IdempotencyFingerprint = idempotencyFingerprint, ReservedAt = now,
                ExpiresAt = now.AddMinutes(5)
            };
            dbContext.PolicyEvaluationReservations.Add(created);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return created;
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.Entry(created).State = EntityState.Detached;
            }
        }

        throw new PolicyDependencyUnavailableException("The evaluation key reservation timed out.");
    }

    public async Task ReleaseEvaluationKeyAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var reservation = await dbContext.PolicyEvaluationReservations
            .SingleOrDefaultAsync(record => record.Id == reservationId, cancellationToken);
        if (reservation is not null)
        {
            dbContext.PolicyEvaluationReservations.Remove(reservation);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
    public async Task<PolicyExceptionVerificationRecord> AppendExceptionVerificationAsync(
        Guid evaluationBundleId,
        QuotationWaiverRequest request,
        QuotationWaiverEvidence evidence,
        PolicyEvaluationBundle? reducedBundle = null,
        CancellationToken cancellationToken = default)
    {
        if (evaluationBundleId == Guid.Empty || string.IsNullOrWhiteSpace(request.TargetRequirementKey) ||
            string.IsNullOrWhiteSpace(evidence.VerifierReference))
        {
            throw new DomainValidationException("Quotation waiver persistence requires a bound evaluation and target.");
        }

        var workflowDecisionId = evidence.VerifierReference;
        var existing = await dbContext.Set<PolicyExceptionVerificationRecord>().SingleOrDefaultAsync(record =>
            record.WorkflowDecisionId == workflowDecisionId &&
            record.BaseBundleId == evaluationBundleId &&
            record.TargetRequirementKey == request.TargetRequirementKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Binding == request.Binding && existing.EvidenceDigest == evidence.EvidenceDigest &&
                existing.Nonce == request.Nonce)
            {
                return existing;
            }
            throw new DomainConflictException("The quotation waiver decision is already bound to another evidence.");
        }

        var record = new PolicyExceptionVerificationRecord
        {
            Id = Guid.NewGuid(),
            EvaluationBundleId = evaluationBundleId,
            BaseBundleId = evaluationBundleId,
            WorkflowDecisionId = workflowDecisionId,
            TargetRequirementKey = request.TargetRequirementKey,
            Binding = request.Binding,
            Nonce = request.Nonce,
            EvidenceDigest = evidence.EvidenceDigest,
            ApproverId = request.ApproverId,
            ExpiresAt = evidence.ExpiresAt,
            VerifierReference = evidence.VerifierReference,
            SnapshotJson = JsonSerializer.Serialize(new { request, evidence, reducedBundle }, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.Set<PolicyExceptionVerificationRecord>().Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(record).State = EntityState.Detached;
            var concurrent = await dbContext.Set<PolicyExceptionVerificationRecord>().SingleOrDefaultAsync(item =>
                item.WorkflowDecisionId == workflowDecisionId &&
                item.BaseBundleId == evaluationBundleId &&
                item.TargetRequirementKey == request.TargetRequirementKey,
                cancellationToken);
            if (concurrent is not null && concurrent.Binding == request.Binding &&
                concurrent.EvidenceDigest == evidence.EvidenceDigest)
            {
                return concurrent;
            }
            throw;
        }
        return record;
    }

    public async Task<PolicyEvaluationBundleRecord> AppendQuotationWaiverReevaluationAsync(
        PolicyEvaluationBundle baseBundle,
        PolicyEvaluationBundle reducedBundle,
        QuotationWaiverRequest request,
        QuotationWaiverEvidence evidence,
        Guid exceptionVerificationId,
        CancellationToken cancellationToken = default)
    {
        var baseRecord = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == baseBundle.Id, cancellationToken)
            ?? throw new DomainConflictException("The quotation waiver base evaluation is not persisted.");
        var evaluationKey = $"{baseRecord.EvaluationKey}:waiver:{evidence.EvidenceDigest[..16]}";
        if (evaluationKey.Length > 128)
        {
            evaluationKey = evaluationKey[..128];
        }
        var verifiedAt = DateTimeOffset.UtcNow;
        var exceptionVerification = new PolicyExceptionVerificationInput(
            "APPROVAL_WORKFLOW",
            "policy-exception-verifier/v1",
            evidence.VerifierReference,
            1,
            evidence.EvidenceDigest,
            baseRecord.Id,
            baseRecord.ResultDigest,
            baseRecord.PolicySetVersionId,
            reducedBundle.PolicyContentDigest,
            reducedBundle.Subject,
            reducedBundle.ManifestDigest ?? string.Empty,
            request.TargetRequirementKey!,
            request.From,
            request.To,
            request.OriginatorId,
            verifiedAt,
            request.Nonce,
            request.ApproverId,
            evidence.EvidenceDigest,
            true,
            "REQUEST",
            verifiedAt,
            evidence.ExpiresAt,
            request.Binding);
        var exceptionVerificationDigest = PolicyCanonicalizer.ComputeExceptionVerificationDigest(exceptionVerification);
        var inputCanonical = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            verifiedAt,
            "PURCHASE_REQUEST_WAIVER",
            reducedBundle.Subject,
            reducedBundle.PolicyContentDigest,
            reducedBundle.ActivationId,
            reducedBundle.FactsDigest,
            baseRecord.Id,
            baseRecord.ResultDigest,
            [exceptionVerificationDigest]);
        var inputDigest = PolicyCanonicalizer.Hash(inputCanonical);
        var resultDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            inputDigest,
            reducedBundle.ScopeEvaluations,
            reducedBundle.Controls,
            reducedBundle.Result,
            reducedBundle.Diff));
        var reevaluated = reducedBundle with
        {
            Id = Guid.NewGuid(),
            EvaluationKey = evaluationKey,
            EvaluatedAt = DateTimeOffset.UtcNow,
            InputDigest = inputDigest,
            InputCanonicalJson = inputCanonical,
            Operation = "PURCHASE_REQUEST_WAIVER",
            PreviousBundleId = baseRecord.Id,
            ResultDigest = resultDigest
        };
        return await AppendEvaluationAsync(
            reevaluated,
            new PolicyEvaluationCaller(
                baseRecord.OrganizationId,
                "QUOTATION_WAIVER",
                "WORKFLOW",
                "PURCHASE_REQUEST_WAIVER",
                evaluationKey,
                baseRecord.PolicySetVersionId,
                evidence.VerifierReference)
            {
                SubjectType = baseBundle.Operation,
                Cause = "QUOTATION_WAIVER",
                ExceptionReferenceIds = [exceptionVerificationId.ToString("D")],
                ExceptionTargetRequirementKey = request.TargetRequirementKey,
                ExceptionFrom = request.From,
                ExceptionTo = request.To,
                PreviousResultDigest = baseRecord.ResultDigest
            },
            cancellationToken);
    }

    private static void ValidateDocument(PolicyDraftDocument document)
    {
        if (document.OrganizationId == Guid.Empty || string.IsNullOrWhiteSpace(document.ScopesJson) ||
            string.IsNullOrWhiteSpace(document.ContentJson) ||
            document.ContentDigest is null || document.ContentDigest.Length != 64 ||
            !document.ContentDigest.All(Uri.IsHexDigit) ||
            !string.Equals(PolicyCanonicalizer.Hash(document.ContentJson), document.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("Policy content must contain a valid canonical SHA-256 digest.");
        }

        try
        {
            using var documentJson = JsonDocument.Parse(document.ContentJson);
            var root = documentJson.RootElement;
            var required = new[] { "canonicalization_version", "policy_schema_version", "organization_id", "scopes", "rules" };
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal)) ||
                required.Any(property => !root.TryGetProperty(property, out _)) ||
                root.GetProperty("canonicalization_version").GetString() != PolicyCanonicalizer.Version ||
                root.GetProperty("policy_schema_version").GetString() != "policy-schema/v1" ||
                root.GetProperty("organization_id").GetGuid() != document.OrganizationId ||
                root.GetProperty("scopes").GetArrayLength() == 0 ||
                root.GetProperty("rules").GetArrayLength() == 0 ||
                root.GetProperty("rules").GetArrayLength() > 2000 ||
                root.GetProperty("rules").EnumerateArray().Any(rule => !IsTypedRule(rule)))
            {
                throw new DomainValidationException("Policy content does not match the typed policy schema.");
            }

            var parsed = PolicyDocumentParser.Parse(
                document.ContentJson,
                Guid.NewGuid(),
                document.OrganizationId,
                1,
                PolicySetStatus.Draft,
                document.ContentDigest);
            if (!string.Equals(PolicyCanonicalizer.ComputePolicyDigest(parsed), document.ContentDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException("Policy digest does not match its typed canonical representation.");
            }
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException($"Policy content is not valid canonical JSON: {exception.Message}");
        }
    }

    private static bool IsTypedRule(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var allowed = new[] { "code", "effects", "fallback", "predicates", "revision", "scope" };
        if (rule.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)) ||
            !rule.TryGetProperty("code", out var code) || !Regex.IsMatch(code.GetString() ?? string.Empty, "^[A-Z][A-Z0-9_]{0,63}$") ||
            !rule.TryGetProperty("scope", out var scope) ||
            !new[] { "LINE", "REQUEST", "SOURCING_PO" }.Contains(scope.GetString(), StringComparer.Ordinal) ||
            !rule.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Array ||
            effects.GetArrayLength() is < 1 or > 16 || effects.EnumerateArray().Any(effect =>
                effect.ValueKind != JsonValueKind.Object ||
                !effect.TryGetProperty("requirement_key", out var key) ||
                !Regex.IsMatch(key.GetString() ?? string.Empty, "^[A-Z][A-Z0-9_]{0,63}$") ||
                !effect.TryGetProperty("type", out var type) ||
                !new[] { "ALLOW", "BLOCK", "REQUIRE_APPROVAL", "REQUIRE_BUDGET_CHECK", "REQUIRE_PROCUREMENT", "REQUIRE_QUOTATIONS", "REQUIRE_PO", "ALLOW_DIRECT_PURCHASE", "REQUIRE_SUPPORTING_DOCUMENT", "REQUIRE_ACTIVE_SUPPLIER" }.Contains(type.GetString(), StringComparer.Ordinal)))
        {
            return false;
        }
        return !rule.TryGetProperty("predicates", out var predicates) ||
            predicates.ValueKind == JsonValueKind.Array && predicates.GetArrayLength() <= 32;
    }

    private void AddAudit(
        string action,
        Guid targetId,
        string? beforeJson,
        int? newVersion,
        string? afterJson,
        PolicyActor actor,
        string reason,
        string correlationReference)
    {
        dbContext.AdministrativeAuditRecords.Add(new AdministrativeAuditRecord
        {
            Id = Guid.NewGuid(),
            ActorType = actor.ActorType,
            ActorUserId = actor.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
            Action = action,
            TargetType = "POLICY",
            TargetId = targetId,
            NewVersion = newVersion,
            ScopeJson = "[\"POLICY\"]",
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            CorrelationReference = correlationReference
        });
    }

    private static string SerializeBundle(PolicyEvaluationBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);

    private static string SerializeBundleWithMetadata(
        PolicyEvaluationBundle bundle,
        long sequence,
        PolicyEvaluationCaller caller)
    {
        var json = JsonNode.Parse(SerializeBundle(bundle))!.AsObject();
        json["evaluationSequence"] = sequence;
        json["operation"] = caller.Operation;
        json["subjectType"] = caller.SubjectType;
        json["factsDigest"] = caller.FactsDigest ?? bundle.FactsDigest;
        json["manifestDigest"] = caller.ManifestDigest ?? bundle.ManifestDigest;
        json["inputCanonicalJson"] = caller.InputCanonicalJson ?? bundle.InputCanonicalJson;
        json["requestSnapshotJson"] = bundle.RequestSnapshotJson;
        return json.ToJsonString(JsonOptions);
    }

    public static string ComputeCommandFingerprint(
        PolicyEvaluationCaller caller,
        PolicySubjectReference subject,
        Guid? previousBundleId = null)
    {
        var command = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cause"] = caller.Cause,
            ["exception_reference_ids"] = caller.ExceptionReferenceIds.Order(StringComparer.Ordinal).ToArray(),
            ["exception_target_requirement_key"] = caller.ExceptionTargetRequirementKey,
            ["exception_from"] = caller.ExceptionFrom,
            ["exception_to"] = caller.ExceptionTo,
            ["operation"] = caller.Operation,
            ["previous_bundle_id"] = previousBundleId?.ToString("D"),
            ["previous_result_digest"] = caller.PreviousResultDigest,
            ["subject_id"] = subject.Id.ToString("D"),
            ["subject_type"] = caller.SubjectType,
            ["subject_version"] = subject.Version
        };
        return PolicyCanonicalizer.Hash(JsonSerializer.Serialize(command));
    }

    private static string ComputeFingerprint(PolicyEvaluationCaller caller, PolicyEvaluationBundle bundle) =>
        ComputeCommandFingerprint(caller, bundle.Subject, bundle.PreviousBundleId);
}
