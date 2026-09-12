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
/// Deterministic routing of activation and reconciliation (REQ-04, REQ-05, NFR-01, NFR-03):
/// converts the stored decision scope, asks the SPEC 01 resolver, excludes active reserved-role
/// holders (DEC-08), picks the lowest current load with a canonical-UUID tie-break, and never
/// falls back when no candidate exists. Selection, load reservation and assignment stay inside
/// the caller's transaction; every automatic change is written as a SYSTEM effect of the root
/// audit that caused it (REQ-08).
/// </summary>
public sealed class ApprovalAssignmentEngine(
    ProcureToPayDbContext dbContext,
    IOrganizationEligibilityService eligibility,
    ApprovalScopeResolver scopeResolver)
{
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
            var candidates = await ResolveCandidatesAsync(approvalCase, requirement, utcNow, cancellationToken);
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

            var load = LoadOf(loads, chosen.User.Id);
            var evidence = ApprovalEligibilityEvidence.Canonicalize(chosen.Evidence);
            approvalCase.AssignTask(
                task, chosen.User.Id, utcNow, load, evidence, ApprovalAssignmentCause.Initial.ToString());
            loads[chosen.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Initial, utcNow));
            ApprovalTelemetry.RecordAssignment("ASSIGNED");
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Assigned, chosen.User.Id, load));
        }

        WriteEffects(approvalCase, rootAuditId, utcNow, correlationReference);
        return outcomes;
    }

    /// <summary>
    /// Idempotent reconciliation (REQ-04, NFR-03): a PENDING requirement whose current
    /// assignee stopped being eligible is reassigned to the new best candidate, or released
    /// back to UNASSIGNED when nobody is eligible. A decided task never changes.
    /// </summary>
    public async Task<IReadOnlyList<ApprovalAssignmentOutcome>> ReconcilePendingAsync(
        ApprovalCase approvalCase,
        IEnumerable<ApprovalAssignmentRecord> assignmentRecords,
        DateTimeOffset occurredAt,
        string correlationReference,
        Guid rootAuditId,
        CancellationToken cancellationToken = default)
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

            var candidates = await ResolveCandidatesAsync(approvalCase, requirement, utcNow, cancellationToken);
            var stillEligible = candidates
                .FirstOrDefault(candidate => candidate.User.Id == assignee.Value);
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

            var load = LoadOf(loads, chosen.User.Id);
            approvalCase.ReassignTask(task, chosen.User.Id);
            loads[chosen.User.Id] = load + 1;
            dbContext.ApprovalAssignments.Add(Assignment(
                approvalCase, requirement, task, chosen, load, ApprovalAssignmentCause.Reassigned, utcNow));
            ApprovalTelemetry.RecordAssignment("REASSIGNED");
            outcomes.Add(new ApprovalAssignmentOutcome(
                requirement.Id, task.Id, ApprovalAssignmentOutcomeKind.Reassigned, chosen.User.Id, load));
        }

        WriteEffects(approvalCase, rootAuditId, utcNow, correlationReference);
        return outcomes;
    }

    /// <summary>True when the pending assignee is still an eligible candidate (REQ-06).</summary>
    public async Task<EligibleCandidate?> FindEligibleCandidateAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        Guid actorUserId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default)
    {
        var candidates = await ResolveCandidatesAsync(
            approvalCase, requirement, evaluatedAt.ToUniversalTime(), cancellationToken);
        return candidates.FirstOrDefault(candidate => candidate.User.Id == actorUserId);
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

    private async Task<IReadOnlyList<EligibleCandidate>> ResolveCandidatesAsync(
        ApprovalCase approvalCase,
        ApprovalRequirement requirement,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var descriptor = DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson);
        var scope = await scopeResolver.ResolveAsync(descriptor, approvalCase.OrganizationId, cancellationToken);
        var candidates = await eligibility.ResolveAsync(
            EligibilityRequest.Create(
                requirement.Role, scope, requirement.Authority, evaluatedAt, requirement.ExcludedUserIds),
            cancellationToken);
        if (candidates.Count == 0)
        {
            return candidates;
        }

        // DEC-08 / REQ-05: an active, already effective local ADMIN or AUDITOR assignment excludes
        // the user from candidacy, assignment and new decisions regardless of scope or accumulated
        // business roles and grants. The source of truth is queried at every evaluation.
        var reserved = await ReservedRoleHoldersAsync(
            approvalCase.OrganizationId,
            candidates.Select(candidate => candidate.User.Id).Distinct().ToArray(),
            cancellationToken);
        return reserved.Count == 0
            ? candidates
            : candidates.Where(candidate => !reserved.Contains(candidate.User.Id)).ToArray();
    }

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
    private static EligibleCandidate? Select(
        IReadOnlyList<EligibleCandidate> candidates,
        IReadOnlyDictionary<Guid, int> loads) =>
        // pi-lens-ignore: lsp:CS0103
        ApprovalRoutingPolicy.SelectLowestLoad(candidates, loads);

    private static int LoadOf(IReadOnlyDictionary<Guid, int> loads, Guid userId) =>
        // pi-lens-ignore: lsp:CS0103
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
        EligibleCandidate candidate,
        int load,
        ApprovalAssignmentCause cause,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            CaseId = approvalCase.Id,
            OrganizationId = approvalCase.OrganizationId,
            TaskId = task.Id,
            AssigneeUserId = candidate.User.Id,
            AssignedAt = occurredAt,
            Load = load,
            Cause = cause.ToString(),
            // pi-lens-ignore: lsp:CS0103
            EligibilityEvidenceJson = ApprovalEligibilityEvidence.Canonicalize(candidate.Evidence)
        };
}
