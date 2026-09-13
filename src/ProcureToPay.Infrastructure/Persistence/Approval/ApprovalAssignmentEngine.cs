using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Serializes selection, load reservation and assignment per organization (REQ-04). The lock is
/// held by the caller's transaction, so a submission, a signal, a decision and a reconciliation
/// cannot choose the same candidate from the same stale load.
/// </summary>
public static class ApprovalAssignmentLock
{
    public static async Task AcquireAsync(
        ProcureToPayDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The assignment lock requires an open transaction that owns the critical section.");
        }

        var resource = $"approval:assign:{organizationId:D}";
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await dbContext.Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000",
            [resource, result],
            cancellationToken);
        if (result.Value is not int code || code < 0)
        {
            throw new ApprovalDependencyUnavailableException(
                "The approval assignment critical section could not be acquired.");
        }
    }
}

public enum ApprovalAssignmentOutcomeKind
{
    Assigned,
    Reassigned,
    Unassigned,
    Unchanged
}

public sealed record ApprovalAssignmentOutcome(
    Guid RequirementId,
    Guid TaskId,
    ApprovalAssignmentOutcomeKind Kind,
    Guid? AssigneeUserId,
    int Load);

/// <summary>
/// Direct eligible candidate plus the real delegation that routed it, if any (SPEC 04 REQ-02).
/// The delegation never grants authority: the candidate is still a direct result of the SPEC 01
/// resolver and the delegator is removed from the effective set.
/// </summary>
public sealed record EffectiveCandidate(EligibleCandidate Candidate, Guid? DelegationId, int? DelegationVersion);

/// <summary>
/// Deterministic routing of activation and reconciliation (REQ-04, REQ-05, NFR-01, NFR-03):
/// converts the stored decision scope, asks the SPEC 01 resolver, applies the effective-set
/// transformation of SPEC 04 REQ-02, excludes active reserved-role holders (DEC-08), picks the
/// lowest current load with a canonical-UUID tie-break, and never falls back when no candidate
/// exists. Selection, load reservation and assignment stay inside the caller's transaction;
/// every automatic change is written as a SYSTEM effect of the root audit that caused it (REQ-08).
/// </summary>
public sealed class ApprovalAssignmentEngine(
    ProcureToPayDbContext dbContext,
    IOrganizationEligibilityService eligibility,
    ApprovalScopeResolver scopeResolver)
{
    private readonly Dictionary<(Guid OrganizationId, int Role, DateTimeOffset EvaluatedAt), IReadOnlyList<ApprovalDelegationCoverage>> delegationCache = [];

    /// <summary>Assigns every activated requirement that has no assignee yet (REQ-04).</summary>
    public async Task<IReadOnlyList<ApprovalAssignmentOutcome>> AssignUnassignedAsync(
        ApprovalCase approvalCase,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid rootAuditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvalCase);
        var utcNow = occurredAt.ToUniversalTime();
        await ApprovalAssignmentLock.AcquireAsync(dbContext, approvalCase.OrganizationId, cancellationToken);
        var loads = await CurrentLoadsAsync(approvalCase.OrganizationId, cancellationToken);
        var outcomes = new List<ApprovalAssignmentOutcome>();

        foreach (var requirement in Ordered(approvalCase.Requirements)
                     .Where(item => item.Status == ApprovalRequirementStatus.Unassigned))
        {
            var task = approvalCase.TaskFor(requirement.Id);
            var requirementScope = await ResolveRequirementScopeAsync(approvalCase, requirement, cancellationToken);
            var candidates = await ResolveCandidatesAsync(
                approvalCase, requirement, requirementScope, utcNow, cancellationToken);
            var chosen = Select(candidates, loads);
            if (chosen is null)
            {
                ApprovalTelemetry.RecordUnassigned();
                // No candidate: the single current task stays UNASSIGNED and the case BLOCKED,
                // with no fallback (REQ-04). No transition happened, so no effect is recorded.
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unassigned, null, 0));
                continue;
            }

            var load = LoadOf(loads, chosen.Candidate.User.Id);
            var evidence = ApprovalEligibilityEvidence.Canonicalize(chosen.Candidate.Evidence);
            approvalCase.AssignTask(
                task, chosen.Candidate.User.Id, utcNow, load, evidence, ApprovalAssignmentCause.Initial.ToString());
            loads[chosen.Candidate.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Initial, utcNow));
            ApprovalTelemetry.RecordAssignment("ASSIGNED");
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Assigned, chosen.Candidate.User.Id, load));
        }

        WriteEffects(approvalCase, rootAuditId, utcNow, correlationReference);
        return outcomes;
    }

    /// <summary>
    /// Idempotent reconciliation (REQ-04, NFR-03): a PENDING requirement whose current
    /// assignee stopped being eligible is reassigned to the new best candidate, or released
    /// back to UNASSIGNED when nobody is eligible. A decided task never changes. A delegation
    /// run passes its coverage so only the requirements it governs are re-evaluated (SPEC 04
    /// REQ-03).
    /// </summary>
    public async Task<IReadOnlyList<ApprovalAssignmentOutcome>> ReconcilePendingAsync(
        ApprovalCase approvalCase,
        IEnumerable<ApprovalAssignmentRecord> assignmentRecords,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid rootAuditId,
        CancellationToken cancellationToken = default,
        ApprovalDelegationCoverage? coverageFilter = null)
    {
        ArgumentNullException.ThrowIfNull(approvalCase);
        var utcNow = occurredAt.ToUniversalTime();
        await ApprovalAssignmentLock.AcquireAsync(dbContext, approvalCase.OrganizationId, cancellationToken);
        var loads = await CurrentLoadsAsync(approvalCase.OrganizationId, cancellationToken);
        var current = assignmentRecords
            .Where(record => record.ReleasedAt is null)
            .ToDictionary(record => record.TaskId);
        var outcomes = new List<ApprovalAssignmentOutcome>();

        foreach (var requirement in Ordered(approvalCase.Requirements)
                     .Where(item => item.Status == ApprovalRequirementStatus.Pending))
        {
            var task = approvalCase.TaskFor(requirement.Id);
            var assignee = task.CurrentAssigneeUserId;
            if (assignee is null)
            {
                continue;
            }

            var requirementScope = await ResolveRequirementScopeAsync(approvalCase, requirement, cancellationToken);
            if (coverageFilter is not null &&
                (requirement.Role != coverageFilter.Role || !coverageFilter.Scope.Covers(requirementScope)))
            {
                continue;
            }

            var candidates = await ResolveCandidatesAsync(
                approvalCase, requirement, requirementScope, utcNow, cancellationToken);
            var stillEligible = candidates
                .FirstOrDefault(candidate => candidate.Candidate.User.Id == assignee.Value);
            if (stillEligible is not null)
            {
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unchanged, assignee, 0));
                continue;
            }

            // The previous assignee is no longer a candidate: release the held load first.
            loads[assignee.Value] = Math.Max(0, LoadOf(loads, assignee.Value) - 1);
            var previous = current.GetValueOrDefault(task.Id);
            if (previous is not null)
            {
                previous.ReleasedAt = utcNow;
            }

            var chosen = Select(candidates, loads);
            if (chosen is null)
            {
                ApprovalTelemetry.RecordUnassigned();
                approvalCase.UnassignTask(task, utcNow, ApprovalAssignmentCause.Unassigned.ToString());
                outcomes.Add(new ApprovalAssignmentOutcome(
                    requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Unassigned, null, 0));
                continue;
            }

            var load = LoadOf(loads, chosen.Candidate.User.Id);
            approvalCase.ReassignTask(task, chosen.Candidate.User.Id);
            loads[chosen.Candidate.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Reassigned, utcNow));
            ApprovalTelemetry.RecordAssignment("REASSIGNED");
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Reassigned, chosen.Candidate.User.Id, load));
        }

        WriteEffects(approvalCase, rootAuditId, utcNow, correlationReference);
        return outcomes;
    }

    /// <summary>
    /// True when the pending assignee is still an effective candidate (REQ-06, SPEC 04 REQ-02);
    /// the applied delegation travels with the candidate so the decision digest fixes its real
    /// id and version.
    /// </summary>
    public async Task<EffectiveCandidate?> FindEligibleCandidateAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        Guid actorUserId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        var requirementScope = await ResolveRequirementScopeAsync(approvalCase, requirement, cancellationToken);
        var candidates = await ResolveCandidatesAsync(
            approvalCase, requirement, requirementScope, evaluatedAt.ToUniversalTime(), cancellationToken);
        return candidates.FirstOrDefault(candidate => candidate.Candidate.User.Id == actorUserId);
    }

    /// <summary>Resolves a requirement's stored scope against the SPEC 01 catalog (REQ-02).</summary>
    public async Task<AuthorizationScopeSet> ResolveRequirementScopeAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        var descriptor = DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson);
        return await scopeResolver.ResolveAsync(descriptor, approvalCase.OrganizationId, cancellationToken);
    }

    /// <summary>
    /// Materializes every automatic effect recorded by the aggregate since the last drain as an
    /// immutable SYSTEM audit linked to the root audit of the command that caused it (REQ-08).
    /// </summary>
    private void WriteEffects(
        ApprovalCase approvalCase,
        Guid rootAuditId,
        DateTimeOffset occurredAt,
        string correlationReference)
    {
        foreach (var effect in approvalCase.DrainAutomaticEffects())
        {
            var (scopeJson, requirementId) = EffectScope(approvalCase, effect);
            dbContext.ApprovalAuditEntries.Add(ApprovalEvidence.Effect(
                approvalCase, rootAuditId, effect, scopeJson, occurredAt, correlationReference, requirementId));
        }
    }

    private static (string ScopeJson, Guid? RequirementId) EffectScope(
        ApprovalCase approvalCase,
        ApprovalAutomaticEffect effect) =>
        effect.Source.Type == ApprovalEntitySourceType.ApprovalRequirement
            ? (approvalCase.RequireRequirement(effect.Source.Id).DecisionScopeJson, effect.Source.Id)
            : ("[]", null);

    /// <summary>
    /// Effective candidate set (SPEC 04 REQ-02, DEC-01): delegates never receive authority, so a
    /// covered delegation only removes the delegator from the direct result and the delegatee is
    /// routed exclusively when it also appears by merit in that original result.
    /// </summary>
    private async Task<IReadOnlyList<EffectiveCandidate>> ResolveCandidatesAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        AuthorizationScopeSet requirementScope,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var direct = await eligibility.ResolveAsync(
            EligibilityRequest.Create(
                requirement.Role, requirementScope, requirement.Authority, evaluatedAt, requirement.ExcludedUserIds),
            cancellationToken);
        if (direct.Count == 0)
        {
            return [];
        }

        var applicable = (await ApplicableDelegationsAsync(
                approvalCase.OrganizationId, requirement.Role, evaluatedAt, cancellationToken))
            .Where(coverage => coverage.Scope.Covers(requirementScope))
            .ToArray();
        var directIds = direct.Select(candidate => candidate.User.Id).ToHashSet();
        var removedDelegators = applicable.Select(coverage => coverage.DelegatorUserId).ToHashSet();
        var effective = direct
            .Where(candidate => !removedDelegators.Contains(candidate.User.Id))
            .Where(candidate => directIds.Contains(candidate.User.Id))
            .ToArray();

        // DEC-08 / REQ-05: an active, already effective local ADMIN or AUDITOR assignment excludes
        // the user from candidacy, assignment and new decisions regardless of scope or accumulated
        // business roles and grants. The source of truth is queried at every evaluation.
        var reserved = await ReservedRoleHoldersAsync(
            approvalCase.OrganizationId,
            effective.Select(candidate => candidate.User.Id).Distinct().ToArray(),
            cancellationToken);
        var routed = reserved.Count == 0
            ? effective
            : effective.Where(candidate => !reserved.Contains(candidate.User.Id)).ToArray();

        return routed
            .Select(candidate => new EffectiveCandidate(
                candidate, DelegationOf(candidate, applicable), DelegationVersionOf(candidate, applicable)))
            .ToArray();
    }

    private async Task<IReadOnlyList<ApprovalDelegationCoverage>> ApplicableDelegationsAsync(
        Guid organizationId,
        SystemRole role,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var key = (organizationId, (int)role, evaluatedAt);
        if (delegationCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var active = ApprovalDelegationCodes.StatusActive;
        var records = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.Role == (int)role &&
                record.Status == active &&
                record.ValidFrom <= evaluatedAt &&
                record.ValidTo > evaluatedAt)
            .OrderBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        var coverages = new List<ApprovalDelegationCoverage>(records.Length);
        foreach (var record in records)
        {
            var descriptor = DecisionScopeDescriptor.Parse(record.DecisionScopeJson);
            var scope = await scopeResolver.ResolveAsync(descriptor, organizationId, cancellationToken);
            coverages.Add(new ApprovalDelegationCoverage(
                record.Id, record.Version, record.DelegatorUserId, record.DelegateeUserId, role, scope));
        }

        delegationCache[key] = coverages;
        return coverages;
    }

    private static Guid? DelegationOf(
        EligibleCandidate candidate,
        IReadOnlyList<ApprovalDelegationCoverage> applicable) =>
        applicable
            .Where(coverage => coverage.DelegateeUserId == candidate.User.Id)
            .Select(coverage => (Guid?)coverage.DelegationId)
            .FirstOrDefault();

    private static int? DelegationVersionOf(
        EligibleCandidate candidate,
        IReadOnlyList<ApprovalDelegationCoverage> applicable) =>
        applicable
            .Where(coverage => coverage.DelegateeUserId == candidate.User.Id)
            .Select(coverage => (int?)coverage.DelegationVersion)
            .FirstOrDefault();

    private async Task<IReadOnlySet<Guid>> ReservedRoleHoldersAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var holders = await dbContext.RoleAssignments
            .AsNoTracking()
            .Where(assignment =>
                userIds.Contains(assignment.UserProfileId) &&
                assignment.Status == (int)AssignmentStatus.Active &&
                (assignment.Role == (int)SystemRole.Admin || assignment.Role == (int)SystemRole.Auditor) &&
                dbContext.UserProfiles.Any(profile =>
                    profile.Id == assignment.UserProfileId && profile.OrganizationId == organizationId))
            .Select(assignment => assignment.UserProfileId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        return holders.ToHashSet();
    }

    /// <summary>Lowest current PENDING load, tie-broken by canonical ascending UUID (DEC-02).</summary>
    private static EffectiveCandidate? Select(
        IReadOnlyList<EffectiveCandidate> candidates,
        IReadOnlyDictionary<Guid, int> loads) =>
        candidates
            .GroupBy(candidate => candidate.Candidate.User.Id)
            .Select(group => group.OrderBy(candidate => candidate.Candidate.RoleAssignment.Id).First())
            .OrderBy(candidate => ApprovalRoutingPolicy.LoadOf(loads, candidate.Candidate.User.Id))
            .ThenBy(
                candidate => ApprovalRoutingPolicy.CanonicalUserId(candidate.Candidate.User.Id),
                StringComparer.Ordinal)
            .FirstOrDefault();

    private static int LoadOf(IReadOnlyDictionary<Guid, int> loads, Guid userId) =>
        ApprovalRoutingPolicy.LoadOf(loads, userId);

    private static IEnumerable<ApprovalRequirement> Ordered(IEnumerable<ApprovalRequirement> requirements) =>
        requirements.OrderBy(requirement => requirement.WorkflowRequirementKey, StringComparer.Ordinal);

    private async Task<Dictionary<Guid, int>> CurrentLoadsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.ApprovalTasks
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.Status == (int)ApprovalTaskStatus.Pending &&
                record.CurrentAssigneeUserId != null)
            .GroupBy(record => record.CurrentAssigneeUserId!.Value)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        return pending.ToDictionary(entry => entry.UserId, entry => entry.Count);
    }

    private static ApprovalAssignmentRecord Assignment(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        ApprovalTask task,
        EffectiveCandidate candidate,
        int load,
        ApprovalAssignmentCause cause,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            CaseId = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            TaskId = task.Id,
            AssigneeUserId = candidate.Candidate.User.Id,
            AssignedAt = occurredAt,
            Load = load,
            Cause = cause.ToString(),
            EligibilityEvidenceJson = ApprovalEligibilityEvidence.Canonicalize(candidate.Candidate.Evidence),
            DelegationId = candidate.DelegationId,
            DelegationVersion = candidate.DelegationVersion
        };
}
