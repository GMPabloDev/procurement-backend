using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

/// <summary>
/// Approval case aggregate (REQ-07): owns requirements, tasks and prerequisites and
/// recomputes the case status after every transition.
/// </summary>
public sealed class ApprovalCase
{
    private readonly List<ApprovalRequirement> requirements = [];
    private readonly List<ApprovalTask> tasks = [];
    private readonly List<ExternalPrerequisite> prerequisites = [];
    private readonly List<ApprovalAutomaticEffect> automaticEffects = [];

    private ApprovalCase(
        Guid id,
        Guid organizationId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string operation,
        string sourceSnapshotDigest,
        ApprovalWorkloadIdentity workload,
        string submissionKey,
        string submissionFingerprint,
        Guid originatorId,
        Guid? requesterId,
        DateTimeOffset createdAt,
        string correlationReference)
    {
        Id = id;
        OrganizationId = organizationId;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectVersion = subjectVersion;
        Operation = operation;
        SourceSnapshotDigest = sourceSnapshotDigest;
        WorkloadIssuer = workload.Issuer;
        WorkloadClientId = workload.ClientId;
        SubmissionKey = submissionKey;
        SubmissionFingerprint = submissionFingerprint;
        OriginatorId = originatorId;
        RequesterId = requesterId;
        CreatedAt = createdAt.ToUniversalTime();
        CorrelationReference = correlationReference;
        Status = ApprovalCaseStatus.Open;
    }

    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public string SubjectType { get; }
    public Guid SubjectId { get; }
    public int SubjectVersion { get; }
    public string Operation { get; }
    public string SourceSnapshotDigest { get; }
    public string WorkloadIssuer { get; }
    public string WorkloadClientId { get; }
    public string SubmissionKey { get; }
    public string SubmissionFingerprint { get; }
    public Guid OriginatorId { get; }
    public Guid? RequesterId { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CorrelationReference { get; }
    public ApprovalCaseStatus Status { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public IReadOnlyList<ApprovalRequirement> Requirements => requirements;
    public IReadOnlyList<ApprovalTask> Tasks => tasks;
    public IReadOnlyList<ExternalPrerequisite> Prerequisites => prerequisites;

    /// <summary>
    /// Automatic transitions recorded since the last drain. The orchestrator materializes them as
    /// <c>SYSTEM</c> audit effects with the root audit and its automatic effect key (REQ-08).
    /// </summary>
    public IReadOnlyList<ApprovalAutomaticEffect> DrainAutomaticEffects()
    {
        var drained = automaticEffects.ToArray();
        automaticEffects.Clear();
        return drained;
    }

    /// <summary>Requirement entity source used by events and automatic effects (REQ-08).</summary>
    public ApprovalEntitySource SourceOf(ApprovalRequirement requirement) =>
        ApprovalEntitySource.Requirement(requirement.Id, requirement.WorkflowRequirementKey);

    /// <summary>Prerequisite entity source used by events and automatic effects (REQ-08).</summary>
    public ApprovalEntitySource SourceOf(ExternalPrerequisite prerequisite) =>
        ApprovalEntitySource.Prerequisite(prerequisite.Id, prerequisite.Key);

    public static ApprovalCase Create(
        Guid id,
        ApprovalSubmission submission,
        ApprovalAdapterDescriptor adapter,
        ApprovalWorkloadIdentity workload,
        IReadOnlyDictionary<string, ApprovalWorkloadIdentity> prerequisiteOwners,
        string submissionFingerprint,
        DateTimeOffset createdAt,
        string correlationReference)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(prerequisiteOwners);
        if (id == Guid.Empty)
        {
            throw new DomainValidationException("A case id is required.");
        }

        var approvalCase = new ApprovalCase(
            id,
            submission.OrganizationId,
            submission.SubjectType,
            submission.SubjectId,
            submission.SubjectVersion,
            submission.Operation,
            submission.SourceSnapshotDigest,
            workload,
            submission.SubmissionKey,
            submissionFingerprint,
            submission.OriginatorId,
            submission.RequesterId,
            createdAt,
            correlationReference);

        foreach (var definition in submission.Requirements)
        {
            approvalCase.requirements.Add(ApprovalRequirement.Create(Guid.NewGuid(), id, definition));
        }

        foreach (var definition in submission.Prerequisites)
        {
            if (!prerequisiteOwners.TryGetValue(definition.Key, out var ownerWorkload))
            {
                throw new DomainValidationException(
                    $"The prerequisite '{definition.Key}' has no resolved owner workload.");
            }

            approvalCase.prerequisites.Add(ExternalPrerequisite.Create(Guid.NewGuid(), id, definition, ownerWorkload));
        }

        foreach (var requirement in approvalCase.requirements)
        {
            approvalCase.tasks.Add(ApprovalTask.Create(Guid.NewGuid(), id, requirement.Id));
        }

        // Requirements without dependencies are immediately eligible for routing (REQ-03).
        approvalCase.ActivateEligibleRequirements();
        approvalCase.RecomputeStatus();
        return approvalCase;
    }

    /// <summary>Hydration from persisted state; no validation beyond structural integrity.</summary>
    public static ApprovalCase Restore(
        Guid id,
        Guid organizationId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string operation,
        string sourceSnapshotDigest,
        ApprovalWorkloadIdentity workload,
        string submissionKey,
        string submissionFingerprint,
        Guid originatorId,
        Guid? requesterId,
        DateTimeOffset createdAt,
        string correlationReference,
        ApprovalCaseStatus status,
        int version,
        DateTimeOffset? cancelledAt,
        string? cancellationReason,
        IEnumerable<ApprovalRequirement> requirements,
        IEnumerable<ApprovalTask> tasks,
        IEnumerable<ExternalPrerequisite> prerequisites)
    {
        var approvalCase = new ApprovalCase(
            id, organizationId, subjectType, subjectId, subjectVersion, operation, sourceSnapshotDigest,
            workload, submissionKey, submissionFingerprint, originatorId, requesterId, createdAt,
            correlationReference)
        {
            Status = status,
            Version = version,
            CancelledAt = cancelledAt,
            CancellationReason = cancellationReason
        };
        approvalCase.requirements.AddRange(requirements);
        approvalCase.tasks.AddRange(tasks);
        approvalCase.prerequisites.AddRange(prerequisites);
        return approvalCase;
    }

    public ApprovalRequirement RequireRequirement(Guid requirementId) =>
        requirements.SingleOrDefault(requirement => requirement.Id == requirementId)
        ?? throw new DomainNotFoundException("The approval requirement is not part of this case.");

    public ApprovalTask TaskFor(Guid requirementId) =>
        tasks.SingleOrDefault(task => task.RequirementId == requirementId)
        ?? throw new DomainNotFoundException("The approval task is not part of this case.");

    public ApprovalTask RequireTask(Guid taskId) =>
        tasks.SingleOrDefault(task => task.Id == taskId)
        ?? throw new DomainNotFoundException("The approval task is not visible.");

    public ExternalPrerequisite RequirePrerequisite(Guid prerequisiteId) =>
        prerequisites.SingleOrDefault(prerequisite => prerequisite.Id == prerequisiteId)
        ?? throw new DomainNotFoundException("The external prerequisite is not part of this case.");

    public bool IsDependencySatisfied(ApprovalDependencyRef dependency)
    {
        if (dependency.PredecessorKind == DependencyPredecessorKind.External)
        {
            var prerequisite = prerequisites.SingleOrDefault(item => item.Key == dependency.PredecessorKey);
            return prerequisite is not null &&
                prerequisite.Status == PrerequisiteStatus.Satisfied &&
                prerequisite.CoversTargets(dependency.Targets);
        }

        var predecessor = requirements.SingleOrDefault(item => item.SourceRequirementKey == dependency.PredecessorKey);
        return predecessor is not null &&
            predecessor.Status == ApprovalRequirementStatus.Approved &&
            predecessor.CoversTargets(dependency.Targets);
    }

    /// <summary>WAITING requirements whose whole dependency set is satisfied become UNASSIGNED.</summary>
    public IReadOnlyList<ApprovalRequirement> ActivateEligibleRequirements()
    {
        var activated = new List<ApprovalRequirement>();
        foreach (var requirement in requirements.Where(item => item.Status == ApprovalRequirementStatus.Waiting))
        {
            if (requirement.Dependencies.All(IsDependencySatisfied))
            {
                var before = requirement.Version;
                requirement.Activate();
                activated.Add(requirement);
                automaticEffects.Add(ApprovalAutomaticEffect.Create(
                    "REQUIREMENT_ACTIVATED",
                    SourceOf(requirement),
                    "REQUIREMENT",
                    requirement.Id,
                    before,
                    requirement.Version,
                    StatusJson("WAITING"),
                    StatusJson("UNASSIGNED"),
                    "Dependencies satisfied; the requirement became eligible for routing."));
            }
        }

        if (activated.Count > 0)
        {
            RecomputeStatus();
        }

        return activated;
    }

    /// <summary>Owner workload signals a prerequisite (REQ-03).</summary>
    public void ApplyPrerequisiteSignal(
        ExternalPrerequisite prerequisite,
        bool satisfied,
        string signalKey,
        string signalFingerprint,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        if (prerequisite.CaseId != Id)
        {
            throw new DomainNotFoundException("The external prerequisite is not part of this case.");
        }

        prerequisite.ApplySignal(satisfied, signalKey, signalFingerprint, occurredAt);

        if (satisfied)
        {
            ActivateEligibleRequirements();
        }
        else
        {
            // A failed prerequisite blocks its affected descendants until a new case corrects it.
            RecomputeStatus();
        }
    }

    public void AssignTask(ApprovalTask task, Guid assigneeUserId, DateTimeOffset assignedAt, int load, string evidenceJson, string cause)
    {
        ArgumentNullException.ThrowIfNull(task);
        var requirement = RequireRequirement(task.RequirementId);
        var before = task.Version;
        task.Assign(assignedAt, evidenceJson, cause);
        // The chosen candidate is part of the assignment, not a separate step (REQ-04).
        task.SetAssignee(assigneeUserId);
        requirement.MarkPending();
        automaticEffects.Add(ApprovalAutomaticEffect.Create(
            "TASK_ASSIGNED",
            SourceOf(requirement),
            "TASK",
            task.Id,
            before,
            task.Version,
            StatusJson("UNASSIGNED"),
            AssignmentJson(assigneeUserId, load),
            "Deterministic lowest-load assignment."));
        Version++;
        RecomputeStatus();
    }

    public void UnassignTask(ApprovalTask task, DateTimeOffset occurredAt, string cause)
    {
        ArgumentNullException.ThrowIfNull(task);
        var requirement = RequireRequirement(task.RequirementId);
        var before = task.Version;
        task.Unassign(occurredAt, cause);
        requirement.MarkUnassigned();
        automaticEffects.Add(ApprovalAutomaticEffect.Create(
            "TASK_RELEASED",
            SourceOf(requirement),
            "TASK",
            task.Id,
            before,
            task.Version,
            AssignmentJson(task.CurrentAssigneeUserId, 0),
            StatusJson("UNASSIGNED"),
            "The assignee lost eligibility and no candidate remains."));
        Version++;
        RecomputeStatus();
    }

    /// <summary>Reassignment keeps the task PENDING; the task version invalidates stale decisions.</summary>
    public void ReassignTask(ApprovalTask task, Guid assigneeUserId)
    {
        ArgumentNullException.ThrowIfNull(task);
        var requirement = RequireRequirement(task.RequirementId);
        var before = task.Version;
        task.ReassignTo(assigneeUserId);
        automaticEffects.Add(ApprovalAutomaticEffect.Create(
            "TASK_REASSIGNED",
            SourceOf(requirement),
            "TASK",
            task.Id,
            before,
            task.Version,
            null,
            AssignmentJson(assigneeUserId, 0),
            "Reassignment after authority change."));
        Version++;
    }

    public void ApplyDecision(ApprovalTask task, ApprovalDecisionAction action)
    {
        ArgumentNullException.ThrowIfNull(task);
        var requirement = RequireRequirement(task.RequirementId);
        task.ApplyDecision(action);
        requirement.ApplyDecision(action);

        if (action == ApprovalDecisionAction.Approve)
        {
            // An approval enables its edges: dependents whose whole dependency set is now
            // satisfied become activatable in the same transition (REQ-07).
            ActivateEligibleRequirements();
        }
        else
        {
            CancelDescendants(requirement.SourceRequirementKey);
        }

        Version++;
        RecomputeStatus();
    }

    public void Cancel(string reason, DateTimeOffset occurredAt)
    {
        if (Status == ApprovalCaseStatus.Cancelled)
        {
            throw new DomainConflictException("The approval case is already cancelled.");
        }

        if (Status == ApprovalCaseStatus.Completed)
        {
            throw new DomainConflictException("A completed approval case cannot be cancelled.");
        }

        var normalized = ApprovalLimits.RequireReason(reason);
        foreach (var requirement in requirements.Where(item => !item.IsTerminal))
        {
            var before = requirement.Version;
            var previous = requirement.Status;
            requirement.Cancel(normalized);
            automaticEffects.Add(ApprovalAutomaticEffect.Create(
                "REQUIREMENT_CANCELLED",
                SourceOf(requirement),
                "REQUIREMENT",
                requirement.Id,
                before,
                requirement.Version,
                StatusJson(RequirementStatus(previous)),
                StatusJson("CANCELLED"),
                normalized));
        }

        foreach (var task in tasks.Where(item => !item.IsTerminal))
        {
            var before = task.Version;
            var previous = task.Status;
            task.Cancel();
            var requirement = RequireRequirement(task.RequirementId);
            automaticEffects.Add(ApprovalAutomaticEffect.Create(
                "TASK_CANCELLED",
                SourceOf(requirement),
                "TASK",
                task.Id,
                before,
                task.Version,
                StatusJson(TaskStatus(previous)),
                StatusJson("CANCELLED"),
                normalized));
        }

        foreach (var prerequisite in prerequisites.Where(item => item.Status == PrerequisiteStatus.Waiting))
        {
            var before = prerequisite.Version;
            prerequisite.Cancel();
            automaticEffects.Add(ApprovalAutomaticEffect.Create(
                "PREREQUISITE_CANCELLED",
                SourceOf(prerequisite),
                "PREREQUISITE",
                prerequisite.Id,
                before,
                prerequisite.Version,
                StatusJson("WAITING"),
                StatusJson("CANCELLED"),
                normalized));
        }

        Status = ApprovalCaseStatus.Cancelled;
        CancelledAt = occurredAt.ToUniversalTime();
        CancellationReason = normalized;
        Version++;
    }

    public void RecomputeStatus()
    {
        if (Status == ApprovalCaseStatus.Cancelled)
        {
            return;
        }

        var hasFailedPrerequisite = prerequisites.Any(item => item.Status == PrerequisiteStatus.Failed);
        var pendingPrerequisite = prerequisites.Any(item => item.Status == PrerequisiteStatus.Waiting);
        var hasUnassigned = requirements.Any(item =>
            item.Status is ApprovalRequirementStatus.Unassigned);

        if (hasFailedPrerequisite || hasUnassigned || pendingPrerequisite)
        {
            Status = requirements.All(item => item.IsTerminal) && !hasFailedPrerequisite &&
                     !hasUnassigned && !pendingPrerequisite
                ? ApprovalCaseStatus.Completed
                : ApprovalCaseStatus.Blocked;
            return;
        }

        Status = requirements.All(item => item.IsTerminal)
            ? ApprovalCaseStatus.Completed
            : ApprovalCaseStatus.Open;
    }

    private void CancelDescendants(string sourceKey)
    {
        var direct = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(sourceKey);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var requirement in requirements)
            {
                if (requirement.Dependencies.Any(dependency =>
                        dependency.PredecessorKind == DependencyPredecessorKind.Approval &&
                        string.Equals(dependency.PredecessorKey, current, StringComparison.Ordinal)) &&
                    direct.Add(requirement.SourceRequirementKey))
                {
                    queue.Enqueue(requirement.SourceRequirementKey);
                }
            }
        }

        foreach (var key in direct)
        {
            var requirement = requirements.Single(item => item.SourceRequirementKey == key);
            if (!requirement.IsTerminal)
            {
                var before = requirement.Version;
                var previous = requirement.Status;
                requirement.Cancel("Cancelled because an upstream approval did not succeed.");
                automaticEffects.Add(ApprovalAutomaticEffect.Create(
                    "REQUIREMENT_CANCELLED",
                    SourceOf(requirement),
                    "REQUIREMENT",
                    requirement.Id,
                    before,
                    requirement.Version,
                    StatusJson(RequirementStatus(previous)),
                    StatusJson("CANCELLED"),
                    "Cancelled because an upstream approval did not succeed."));
                var task = tasks.Single(item => item.RequirementId == requirement.Id);
                var taskBefore = task.Version;
                var taskPrevious = task.Status;
                task.Cancel();
                automaticEffects.Add(ApprovalAutomaticEffect.Create(
                    "TASK_CANCELLED",
                    SourceOf(requirement),
                    "TASK",
                    task.Id,
                    taskBefore,
                    task.Version,
                    StatusJson(TaskStatus(taskPrevious)),
                    StatusJson("CANCELLED"),
                    "Cancelled because an upstream approval did not succeed."));
            }
        }
    }

    private static string StatusJson(string status) =>
        ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("status", ApprovalCanonicalJson.String(status))));

    private static string AssignmentJson(Guid? assigneeUserId, int load) =>
        ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("assignee_user_id", ApprovalCanonicalJson.StringOrNull(assigneeUserId)),
            ("load", ApprovalCanonicalJson.Number(load)),
            ("status", ApprovalCanonicalJson.String("PENDING"))));

    private static string RequirementStatus(ApprovalRequirementStatus status) => status switch
    {
        ApprovalRequirementStatus.Waiting => "WAITING",
        ApprovalRequirementStatus.Unassigned => "UNASSIGNED",
        ApprovalRequirementStatus.Pending => "PENDING",
        ApprovalRequirementStatus.Approved => "APPROVED",
        ApprovalRequirementStatus.Rejected => "REJECTED",
        ApprovalRequirementStatus.ChangesRequested => "CHANGES_REQUESTED",
        ApprovalRequirementStatus.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The requirement status is invalid.")
    };

    private static string TaskStatus(ApprovalTaskStatus status) => status switch
    {
        ApprovalTaskStatus.Unassigned => "UNASSIGNED",
        ApprovalTaskStatus.Pending => "PENDING",
        ApprovalTaskStatus.Approved => "APPROVED",
        ApprovalTaskStatus.Rejected => "REJECTED",
        ApprovalTaskStatus.ChangesRequested => "CHANGES_REQUESTED",
        ApprovalTaskStatus.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The task status is invalid.")
    };
}

public sealed class ApprovalRequirement
{
    private ApprovalRequirement(
        Guid id,
        Guid caseId,
        string sourceRequirementKey,
        string workflowRequirementKey,
        string stageCode,
        SystemRole role,
        AuthorityRequirement authority,
        string decisionScopeJson,
        IEnumerable<Guid> excludedUserIds,
        IEnumerable<ApprovalTarget> targets,
        IEnumerable<ApprovalDependencyRef> dependencies)
    {
        Id = id;
        CaseId = caseId;
        SourceRequirementKey = sourceRequirementKey;
        WorkflowRequirementKey = workflowRequirementKey;
        StageCode = stageCode;
        Role = role;
        Authority = authority;
        DecisionScopeJson = decisionScopeJson;
        ExcludedUserIds = excludedUserIds.ToImmutableArray();
        Targets = targets.ToImmutableArray();
        Dependencies = dependencies.ToImmutableArray();
        Status = ApprovalRequirementStatus.Waiting;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public string SourceRequirementKey { get; }
    public string WorkflowRequirementKey { get; }
    public string StageCode { get; }
    public SystemRole Role { get; }
    public AuthorityRequirement Authority { get; }
    public string DecisionScopeJson { get; }
    public IReadOnlyList<Guid> ExcludedUserIds { get; }
    public IReadOnlyList<ApprovalTarget> Targets { get; }
    public IReadOnlyList<ApprovalDependencyRef> Dependencies { get; }
    public ApprovalRequirementStatus Status { get; private set; }
    public int Version { get; private set; } = 1;

    public bool IsTerminal => Status is ApprovalRequirementStatus.Approved
        or ApprovalRequirementStatus.Rejected
        or ApprovalRequirementStatus.ChangesRequested
        or ApprovalRequirementStatus.Cancelled;

    public static ApprovalRequirement Create(Guid id, Guid caseId, ApprovalRequirementDefinition definition) =>
        new(
            id,
            caseId,
            definition.SourceRequirementKey,
            definition.WorkflowRequirementKey(),
            definition.StageCode,
            definition.Role,
            definition.Authority,
            definition.DecisionScope.ToCanonicalJson(),
            definition.ExcludedUserIds,
            definition.Targets,
            definition.Dependencies);

    public static ApprovalRequirement Restore(
        Guid id,
        Guid caseId,
        string sourceRequirementKey,
        string workflowRequirementKey,
        string stageCode,
        SystemRole role,
        AuthorityRequirement authority,
        string decisionScopeJson,
        IEnumerable<Guid> excludedUserIds,
        IEnumerable<ApprovalTarget> targets,
        IEnumerable<ApprovalDependencyRef> dependencies,
        ApprovalRequirementStatus status,
        int version)
    {
        var requirement = new ApprovalRequirement(
            id, caseId, sourceRequirementKey, workflowRequirementKey, stageCode, role, authority,
            decisionScopeJson, excludedUserIds, targets, dependencies)
        {
            Status = status,
            Version = version
        };
        return requirement;
    }

    public bool CoversTargets(IEnumerable<ApprovalTarget> edgeTargets)
    {
        var identities = Targets.Select(target => target.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);
        return edgeTargets.All(target => identities.Contains(target.CanonicalIdentity));
    }

    public void Activate()
    {
        if (Status != ApprovalRequirementStatus.Waiting)
        {
            throw new DomainConflictException("Only a waiting requirement can become active.");
        }

        Status = ApprovalRequirementStatus.Unassigned;
        Version++;
    }

    public void MarkPending()
    {
        if (Status != ApprovalRequirementStatus.Unassigned)
        {
            throw new DomainConflictException("Only an unassigned requirement can receive an approval task.");
        }

        Status = ApprovalRequirementStatus.Pending;
        Version++;
    }

    public void MarkUnassigned()
    {
        if (Status != ApprovalRequirementStatus.Pending)
        {
            throw new DomainConflictException("Only a pending requirement can lose its assignment.");
        }

        Status = ApprovalRequirementStatus.Unassigned;
        Version++;
    }

    public void ApplyDecision(ApprovalDecisionAction action)
    {
        if (Status != ApprovalRequirementStatus.Pending)
        {
            throw new DomainConflictException("Only a pending requirement can be decided.");
        }

        Status = action switch
        {
            ApprovalDecisionAction.Approve => ApprovalRequirementStatus.Approved,
            ApprovalDecisionAction.Reject => ApprovalRequirementStatus.Rejected,
            ApprovalDecisionAction.RequestChanges => ApprovalRequirementStatus.ChangesRequested,
            _ => throw new DomainValidationException("The approval action is invalid.")
        };
        Version++;
    }

    public void Cancel(string reason)
    {
        if (IsTerminal)
        {
            throw new DomainConflictException("A terminal requirement cannot be cancelled.");
        }

        _ = ApprovalLimits.RequireReason(reason);
        Status = ApprovalRequirementStatus.Cancelled;
        Version++;
    }
}

public sealed class ApprovalTask
{
    private ApprovalTask(Guid id, Guid caseId, Guid requirementId)
    {
        Id = id;
        CaseId = caseId;
        RequirementId = requirementId;
        Status = ApprovalTaskStatus.Unassigned;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public Guid RequirementId { get; }
    public ApprovalTaskStatus Status { get; private set; }
    public Guid? CurrentAssigneeUserId { get; private set; }
    public int Version { get; private set; } = 1;

    public bool IsTerminal => Status is ApprovalTaskStatus.Approved
        or ApprovalTaskStatus.Rejected
        or ApprovalTaskStatus.ChangesRequested
        or ApprovalTaskStatus.Cancelled;

    public static ApprovalTask Create(Guid id, Guid caseId, Guid requirementId)
    {
        if (id == Guid.Empty || requirementId == Guid.Empty)
        {
            throw new DomainValidationException("An approval task requires an identity.");
        }

        return new ApprovalTask(id, caseId, requirementId);
    }

    public static ApprovalTask Restore(
        Guid id,
        Guid caseId,
        Guid requirementId,
        ApprovalTaskStatus status,
        Guid? currentAssigneeUserId,
        int version)
    {
        var task = new ApprovalTask(id, caseId, requirementId)
        {
            Status = status,
            CurrentAssigneeUserId = currentAssigneeUserId,
            Version = version
        };
        return task;
    }

    public void Assign(DateTimeOffset assignedAt, string evidenceJson, string cause)
    {
        if (Status != ApprovalTaskStatus.Unassigned)
        {
            throw new DomainConflictException("Only an unassigned task can receive an assignee.");
        }

        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            throw new DomainValidationException("An assignment requires EligibilityEvidence.");
        }

        _ = assignedAt.ToUniversalTime();
        _ = ApprovalLimits.RequireKey(cause, "Assignment cause");
        Status = ApprovalTaskStatus.Pending;
        Version++;
    }

    public void SetAssignee(Guid assigneeUserId)
    {
        if (assigneeUserId == Guid.Empty)
        {
            throw new DomainValidationException("An assignee identity is required.");
        }

        CurrentAssigneeUserId = assigneeUserId;
    }

    /// <summary>
    /// Reassignment keeps the task PENDING but invalidates stale decision attempts, because the
    /// task version is part of the decision fingerprint (REQ-04, REQ-06).
    /// </summary>
    public void ReassignTo(Guid assigneeUserId)
    {
        if (Status != ApprovalTaskStatus.Pending)
        {
            throw new DomainConflictException("Only a pending task can be reassigned.");
        }

        SetAssignee(assigneeUserId);
        Version++;
    }

    public void Unassign(DateTimeOffset occurredAt, string cause)
    {
        if (Status != ApprovalTaskStatus.Pending)
        {
            throw new DomainConflictException("Only a pending task can lose its assignee.");
        }

        _ = occurredAt.ToUniversalTime();
        _ = ApprovalLimits.RequireKey(cause, "Unassignment cause");
        Status = ApprovalTaskStatus.Unassigned;
        CurrentAssigneeUserId = null;
        Version++;
    }

    public void ApplyDecision(ApprovalDecisionAction action)
    {
        if (Status != ApprovalTaskStatus.Pending)
        {
            throw new DomainConflictException("Only a pending task can be decided.");
        }

        Status = action switch
        {
            ApprovalDecisionAction.Approve => ApprovalTaskStatus.Approved,
            ApprovalDecisionAction.Reject => ApprovalTaskStatus.Rejected,
            ApprovalDecisionAction.RequestChanges => ApprovalTaskStatus.ChangesRequested,
            _ => throw new DomainValidationException("The approval action is invalid.")
        };
        Version++;
    }

    public void Cancel()
    {
        if (IsTerminal)
        {
            throw new DomainConflictException("A terminal task cannot be cancelled.");
        }

        Status = ApprovalTaskStatus.Cancelled;
        CurrentAssigneeUserId = null;
        Version++;
    }
}

public sealed class ExternalPrerequisite
{
    private ExternalPrerequisite(
        Guid id,
        Guid caseId,
        string key,
        string ownerAdapterId,
        string ownerAdapterVersion,
        ApprovalWorkloadIdentity ownerWorkload,
        string sourceControlType,
        string sourceControlDigest,
        string parametersJson,
        IEnumerable<ApprovalTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(ownerWorkload);
        Id = id;
        CaseId = caseId;
        Key = key;
        OwnerAdapterId = ownerAdapterId;
        OwnerAdapterVersion = ownerAdapterVersion;
        OwnerWorkloadIssuer = ownerWorkload.Issuer;
        OwnerWorkloadClientId = ownerWorkload.ClientId;
        SourceControlType = sourceControlType;
        SourceControlDigest = sourceControlDigest;
        ParametersJson = parametersJson;
        Targets = targets.ToImmutableArray();
        Status = PrerequisiteStatus.Waiting;
    }

    public Guid Id { get; }
    public Guid CaseId { get; }
    public string Key { get; }
    public string OwnerAdapterId { get; }
    public string OwnerAdapterVersion { get; }
    /// <summary>Workload identity resolved exact-one at ingestion and persisted (REQ-03, DEC-11).</summary>
    public string OwnerWorkloadIssuer { get; }
    public string OwnerWorkloadClientId { get; }
    public ApprovalWorkloadIdentity OwnerWorkload => new(OwnerWorkloadIssuer, OwnerWorkloadClientId);
    public string SourceControlType { get; }
    public string SourceControlDigest { get; }
    public string ParametersJson { get; }
    public IReadOnlyList<ApprovalTarget> Targets { get; }
    public PrerequisiteStatus Status { get; private set; }
    public int Version { get; private set; } = 1;
    public string? SignalKey { get; private set; }
    public string? SignalFingerprint { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static ExternalPrerequisite Create(
        Guid id,
        Guid caseId,
        ExternalPrerequisiteDefinition definition,
        ApprovalWorkloadIdentity ownerWorkload) =>
        new(
            id, caseId, definition.Key, definition.OwnerAdapterId, definition.OwnerAdapterVersion, ownerWorkload,
            definition.SourceControlType, definition.SourceControlDigest, definition.ParametersJson,
            definition.Targets);

    public static ExternalPrerequisite Restore(
        Guid id,
        Guid caseId,
        string key,
        string ownerAdapterId,
        string ownerAdapterVersion,
        ApprovalWorkloadIdentity ownerWorkload,
        string sourceControlType,
        string sourceControlDigest,
        string parametersJson,
        IEnumerable<ApprovalTarget> targets,
        PrerequisiteStatus status,
        int version,
        string? signalKey,
        string? signalFingerprint,
        DateTimeOffset? resolvedAt)
    {
        var prerequisite = new ExternalPrerequisite(
            id, caseId, key, ownerAdapterId, ownerAdapterVersion, ownerWorkload, sourceControlType,
            sourceControlDigest, parametersJson, targets)
        {
            Status = status,
            Version = version,
            SignalKey = signalKey,
            SignalFingerprint = signalFingerprint,
            ResolvedAt = resolvedAt
        };
        return prerequisite;
    }

    public bool CoversTargets(IEnumerable<ApprovalTarget> edgeTargets)
    {
        var identities = Targets.Select(target => target.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);
        return edgeTargets.All(target => identities.Contains(target.CanonicalIdentity));
    }

    public void ApplySignal(bool satisfied, string signalKey, string signalFingerprint, DateTimeOffset occurredAt)
    {
        if (Status != PrerequisiteStatus.Waiting)
        {
            throw new DomainConflictException("Only a waiting prerequisite can be signalled.");
        }

        SignalKey = ApprovalLimits.RequireKey(signalKey, "signal_key");
        SignalFingerprint = ApprovalLimits.RequireSha256(signalFingerprint, "signal fingerprint");
        Status = satisfied ? PrerequisiteStatus.Satisfied : PrerequisiteStatus.Failed;
        ResolvedAt = occurredAt.ToUniversalTime();
        Version++;
    }

    public void Cancel()
    {
        if (Status != PrerequisiteStatus.Waiting)
        {
            throw new DomainConflictException("Only a waiting prerequisite can be cancelled.");
        }

        Status = PrerequisiteStatus.Cancelled;
        Version++;
    }
}
