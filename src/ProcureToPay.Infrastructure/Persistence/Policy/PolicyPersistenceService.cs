using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    string CorrelationReference);

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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
    {
        var activation = await dbContext.PolicyActivations
            .SingleOrDefaultAsync(candidate => candidate.Id == activationId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy activation does not exist.");
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
        return record;
    }

    public async Task<PolicyEvaluationBundleRecord> AppendEvaluationAsync(
        PolicyEvaluationBundle bundle,
        PolicyEvaluationCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
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

        var record = new PolicyEvaluationBundleRecord
        {
            Id = bundle.Id,
            OrganizationId = caller.OrganizationId,
            EvaluationKey = caller.EvaluationKey,
            WorkloadIssuer = caller.WorkloadIssuer,
            WorkloadClientId = caller.WorkloadClientId,
            Operation = caller.Operation,
            SubjectId = bundle.Subject.Id,
            SubjectVersion = bundle.Subject.Version,
            PolicySetVersionId = caller.PolicySetVersionId,
            EvaluatedAt = bundle.EvaluatedAt,
            PolicyContentDigest = bundle.PolicyContentDigest,
            InputDigest = bundle.InputDigest,
            Result = bundle.Result.ToString().ToUpperInvariant(),
            ResultDigest = PolicyCanonicalizer.Hash(SerializeBundle(bundle)),
            BundleJson = SerializeBundle(bundle),
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
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(record).State = EntityState.Detached;
            var duplicate = await dbContext.PolicyEvaluationBundles
                .SingleOrDefaultAsync(evaluation => evaluation.IdempotencyFingerprint == fingerprint, cancellationToken);
            if (duplicate is not null)
            {
                return duplicate;
            }

            throw;
        }

        return record;
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
                root.GetProperty("rules").EnumerateArray().Any(rule => !IsTypedRule(rule)))
            {
                throw new DomainValidationException("Policy content does not match the typed policy schema.");
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

    private static string ComputeFingerprint(PolicyEvaluationCaller caller, PolicyEvaluationBundle bundle)
    {
        var value = string.Join("|", caller.OrganizationId, caller.WorkloadIssuer, caller.WorkloadClientId,
            caller.Operation, caller.EvaluationKey, bundle.Subject.Id, bundle.Subject.Version,
            bundle.PreviousBundleId?.ToString("D") ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
