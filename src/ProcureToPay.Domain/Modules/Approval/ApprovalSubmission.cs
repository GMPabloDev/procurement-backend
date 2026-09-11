using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Approval;

public sealed record ApprovalWorkloadIdentity(string Issuer, string ClientId);

/// <summary>Versioned adapter contract: mandatory actors declared per operation (REQ-05).</summary>
public sealed record ApprovalAdapterDescriptor(
    string AdapterId,
    string SubjectType,
    string Operation,
    string ContractVersion,
    bool RequesterRequired)
{
    /// <summary>Validates the declared keys and codes of the adapter contract.</summary>
    public void Validate()
    {
        _ = ApprovalLimits.RequireKey(AdapterId, "Adapter id");
        _ = ApprovalLimits.RequireCode(SubjectType, "Subject type");
        _ = ApprovalLimits.RequireCode(Operation, "Operation");
        _ = ApprovalLimits.RequireKey(ContractVersion, "Contract version");
    }
}

public sealed record ApprovalRequirementDefinition
{
    public ApprovalRequirementDefinition(
        string sourceRequirementKey,
        string stageCode,
        SystemRole role,
        AuthorityRequirement authority,
        DecisionScopeDescriptor decisionScope,
        IEnumerable<ApprovalDecisionAction> allowedActions,
        IEnumerable<Guid> excludedUserIds,
        IEnumerable<ApprovalTarget> targets,
        IEnumerable<ApprovalDependencyRef> dependencies)
    {
        SourceRequirementKey = ApprovalLimits.RequireKey(sourceRequirementKey, "source_requirement_key");
        StageCode = ApprovalLimits.RequireCode(stageCode, "stage_code");
        Role = role;
        Authority = authority ?? throw new DomainValidationException("An approval requirement needs an authority.");
        AuthorizationMatrix.Validate(role, Authority);
        DecisionScope = decisionScope ?? throw new DomainValidationException("An approval requirement needs a scope.");
        Actions = (allowedActions ?? []).ToImmutableHashSet();
        if (Actions.Count == 0 || Actions.Any(action => !Enum.IsDefined(action)))
        {
            throw new DomainValidationException("An approval requirement needs at least one allowed action.");
        }

        ExcludedUserIds = (excludedUserIds ?? []).ToImmutableHashSet();
        if (ExcludedUserIds.Any(id => id == Guid.Empty))
        {
            throw new DomainValidationException("Excluded actors cannot be empty identities.");
        }

        Targets = (targets ?? []).ToImmutableHashSet();
        if (Targets.Count == 0)
        {
            throw new DomainValidationException("An approval requirement needs at least one target.");
        }

        if (Targets.Count != Targets.Select(target => target.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count())
        {
            throw new DomainConflictException("An approval requirement cannot repeat targets.");
        }

        Dependencies = (dependencies ?? []).ToImmutableArray();
    }

    public string SourceRequirementKey { get; }
    public string StageCode { get; }
    public SystemRole Role { get; }
    public AuthorityRequirement Authority { get; }
    public DecisionScopeDescriptor DecisionScope { get; }
    public ImmutableHashSet<ApprovalDecisionAction> Actions { get; }
    public ImmutableHashSet<Guid> ExcludedUserIds { get; }
    public ImmutableHashSet<ApprovalTarget> Targets { get; }
    public ImmutableArray<ApprovalDependencyRef> Dependencies { get; }

    public ApprovalTarget DependenciesTarget => Targets.OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal).First();

    /// <summary>Canonical signature of everything that must be identical to group targets (REQ-02).</summary>
    public string GroupingSignature() => ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
        ("actions", ApprovalCanonicalJson.Set(Actions.Select(action => ApprovalCanonicalJson.String(action)))),
        ("authority", AuthorityValue(Authority)),
        ("dependencies", ApprovalCanonicalJson.Set(Dependencies.Select(DependencyValue))),
        ("exclusions", ApprovalCanonicalJson.Set(ExcludedUserIds.Select(ApprovalCanonicalJson.String))),
        ("role", ApprovalCanonicalJson.String(Role)),
        ("scope", DecisionScope.ToCanonicalValue()),
        ("stage_code", ApprovalCanonicalJson.String(StageCode))));

    public string WorkflowRequirementKey()
    {
        var digest = ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("grouping", ApprovalCanonicalJson.String(GroupingSignature())),
            ("targets", TargetsValue(Targets))));
        return $"{ApprovalRequirementKeys.Prefix}{digest[..32].ToUpperInvariant()}";
    }

    public static CanonicalValue TargetsValue(IEnumerable<ApprovalTarget> targets) =>
        ApprovalCanonicalJson.Set(targets
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .Select(TargetValue));

    public static CanonicalValue TargetValue(ApprovalTarget target) => ApprovalCanonicalJson.Object(
        ("id", ApprovalCanonicalJson.String(target.Id)),
        ("material_snapshot_digest", ApprovalCanonicalJson.String(target.MaterialSnapshotDigest)),
        ("type", ApprovalCanonicalJson.String(target.Type)),
        ("version", ApprovalCanonicalJson.Number(target.Version)));

    public static CanonicalValue AuthorityValue(AuthorityRequirement authority) => ApprovalCanonicalJson.Object(
        ("amount_base", ApprovalCanonicalJson.NumberOrNull(authority.AmountBase)),
        ("base_currency", ApprovalCanonicalJson.StringOrNull(authority.BaseCurrency)),
        ("kind", ApprovalCanonicalJson.String(authority.Kind)),
        ("minimum_rank", ApprovalCanonicalJson.NumberOrNull(authority.MinimumRank)),
        ("type", authority.Type is null
            ? ApprovalCanonicalJson.Null()
            : ApprovalCanonicalJson.String(authority.Type.Value)));

    private static CanonicalValue DependencyValue(ApprovalDependencyRef dependency) => ApprovalCanonicalJson.Object(
        ("mode", ApprovalCanonicalJson.String(dependency.Mode)),
        ("predecessor_key", ApprovalCanonicalJson.String(dependency.PredecessorKey)),
        ("predecessor_kind", ApprovalCanonicalJson.String(dependency.PredecessorKind)),
        ("targets", TargetsValue(dependency.Targets)));
}

public sealed record ExternalPrerequisiteDefinition
{
    public ExternalPrerequisiteDefinition(
        string key,
        string ownerAdapterId,
        string ownerAdapterVersion,
        string sourceControlType,
        string sourceControlDigest,
        string parametersJson,
        IEnumerable<ApprovalTarget> targets)
    {
        Key = ApprovalLimits.RequireKey(key, "prerequisite key");
        OwnerAdapterId = ApprovalLimits.RequireKey(ownerAdapterId, "prerequisite owner adapter");
        OwnerAdapterVersion = ApprovalLimits.RequireKey(ownerAdapterVersion, "prerequisite owner version");
        SourceControlType = ApprovalLimits.RequireCode(sourceControlType, "source control type");
        SourceControlDigest = ApprovalLimits.RequireSha256(sourceControlDigest, "source control digest");
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new DomainValidationException("A prerequisite must preserve its typed parameters.");
        }

        ParametersJson = parametersJson;
        Targets = (targets ?? []).ToImmutableHashSet();
        if (Targets.Count == 0)
        {
            throw new DomainValidationException("A prerequisite needs at least one target.");
        }
    }

    public string Key { get; }
    public string OwnerAdapterId { get; }
    public string OwnerAdapterVersion { get; }
    public string SourceControlType { get; }
    public string SourceControlDigest { get; }
    public string ParametersJson { get; }
    public ImmutableHashSet<ApprovalTarget> Targets { get; }
}

public sealed record ApprovalSubmission
{
    public ApprovalSubmission(
        string submissionKey,
        Guid organizationId,
        string subjectType,
        Guid subjectId,
        int subjectVersion,
        string operation,
        string sourceSnapshotDigest,
        Guid? requesterId,
        Guid originatorId,
        IEnumerable<ApprovalRequirementDefinition> requirements,
        IEnumerable<ExternalPrerequisiteDefinition> prerequisites)
    {
        SubmissionKey = ApprovalLimits.RequireKey(submissionKey, "submission_key");
        if (organizationId == Guid.Empty)
        {
            throw new DomainValidationException("A submission needs an organization.");
        }

        OrganizationId = organizationId;
        SubjectType = ApprovalLimits.RequireCode(subjectType, "subject_type");
        if (subjectId == Guid.Empty || subjectVersion < 1)
        {
            throw new DomainValidationException("A submission needs an immutable subject identity and version.");
        }

        SubjectId = subjectId;
        SubjectVersion = subjectVersion;
        Operation = ApprovalLimits.RequireCode(operation, "operation");
        SourceSnapshotDigest = ApprovalLimits.RequireSha256(sourceSnapshotDigest, "source snapshot digest");
        if (requesterId == Guid.Empty)
        {
            throw new DomainValidationException("A declared requester cannot be empty.");
        }

        RequesterId = requesterId;
        if (originatorId == Guid.Empty)
        {
            throw new DomainValidationException("originator_id is required for every business approval.");
        }

        OriginatorId = originatorId;
        Requirements = (requirements ?? []).ToImmutableArray();
        Prerequisites = (prerequisites ?? []).ToImmutableArray();
    }

    public string SubmissionKey { get; }
    public Guid OrganizationId { get; }
    public string SubjectType { get; }
    public Guid SubjectId { get; }
    public int SubjectVersion { get; }
    public string Operation { get; }
    public string SourceSnapshotDigest { get; }
    public Guid? RequesterId { get; }
    public Guid OriginatorId { get; }
    public ImmutableArray<ApprovalRequirementDefinition> Requirements { get; }
    public ImmutableArray<ExternalPrerequisiteDefinition> Prerequisites { get; }
}

/// <summary>
/// Structural validation of a submission graph before anything is persisted (REQ-01, REQ-02,
/// REQ-03, REQ-05, REQ-09). Rejects ambiguous grouping, unknown dependency references,
/// non-contained edge targets, cycles and limit violations.
/// </summary>
public static class ApprovalSubmissionRules
{
    public static void Validate(
        ApprovalSubmission submission,
        ApprovalAdapterDescriptor adapter,
        ApprovalWorkloadIdentity workload)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(workload);
        adapter.Validate();

        if (!string.Equals(submission.SubjectType, adapter.SubjectType, StringComparison.Ordinal) ||
            !string.Equals(submission.Operation, adapter.Operation, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The submission does not match the adapter contract.");
        }

        var requirementLinks = submission.Requirements.Sum(requirement => requirement.Targets.Count);
        var prerequisiteLinks = submission.Prerequisites.Sum(prerequisite => prerequisite.Targets.Count);
        if (submission.Requirements.Length > ApprovalLimits.MaxRequirementsPerCase)
        {
            throw new ApprovalPayloadTooLargeException("The case exceeds 2000 approval requirements.");
        }

        if (requirementLinks + prerequisiteLinks > ApprovalLimits.MaxTargetLinksPerCase)
        {
            throw new ApprovalPayloadTooLargeException("The case exceeds 10000 target associations.");
        }

        ValidateActors(submission, adapter);
        ValidateGrouping(submission);
        ValidateReferences(submission);
        ValidateEdges(submission);
        ValidateAcyclic(submission);
        ValidateCanonicalSize(submission, adapter, workload);
    }

    public static string SubmissionFingerprint(
        ApprovalSubmission submission,
        ApprovalAdapterDescriptor adapter,
        ApprovalWorkloadIdentity workload) =>
        ApprovalCanonicalJson.Digest(CommandValue(submission, adapter, workload));

    /// <summary>
    /// Canonical command preimage: every submitted field except correlation and server clock.
    /// Used both for the 5 MiB limit and for the idempotency fingerprint (REQ-01, REQ-08).
    /// </summary>
    public static CanonicalValue CommandValue(
        ApprovalSubmission submission,
        ApprovalAdapterDescriptor adapter,
        ApprovalWorkloadIdentity workload)
    {
        var requirementValues = submission.Requirements
            .Select(requirement => ApprovalCanonicalJson.Object(
                ("actions", ApprovalCanonicalJson.Set(requirement.Actions.Select(action => ApprovalCanonicalJson.String(action)))),
                ("authority", ApprovalRequirementDefinition.AuthorityValue(requirement.Authority)),
                ("dependencies", ApprovalCanonicalJson.Set(requirement.Dependencies.Select(DependencyValue))),
                ("exclusions", ApprovalCanonicalJson.Set(requirement.ExcludedUserIds.Select(ApprovalCanonicalJson.String))),
                ("role", ApprovalCanonicalJson.String(requirement.Role)),
                ("scope", requirement.DecisionScope.ToCanonicalValue()),
                ("source_requirement_key", ApprovalCanonicalJson.String(requirement.SourceRequirementKey)),
                ("stage_code", ApprovalCanonicalJson.String(requirement.StageCode)),
                ("targets", ApprovalRequirementDefinition.TargetsValue(requirement.Targets))))
            .ToArray();
        var prerequisiteValues = submission.Prerequisites
            .Select(prerequisite => ApprovalCanonicalJson.Object(
                ("key", ApprovalCanonicalJson.String(prerequisite.Key)),
                ("owner_adapter_id", ApprovalCanonicalJson.String(prerequisite.OwnerAdapterId)),
                ("owner_adapter_version", ApprovalCanonicalJson.String(prerequisite.OwnerAdapterVersion)),
                ("parameters", ApprovalCanonicalJson.String(prerequisite.ParametersJson)),
                ("source_control_digest", ApprovalCanonicalJson.String(prerequisite.SourceControlDigest)),
                ("source_control_type", ApprovalCanonicalJson.String(prerequisite.SourceControlType)),
                ("targets", ApprovalRequirementDefinition.TargetsValue(prerequisite.Targets))))
            .ToArray();

        return ApprovalCanonicalJson.Object(
            ("adapter_id", ApprovalCanonicalJson.String(adapter.AdapterId)),
            ("adapter_version", ApprovalCanonicalJson.String(adapter.ContractVersion)),
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("operation", ApprovalCanonicalJson.String(submission.Operation)),
            ("organization_id", ApprovalCanonicalJson.String(submission.OrganizationId)),
            ("originator_id", ApprovalCanonicalJson.String(submission.OriginatorId)),
            ("prerequisites", ApprovalCanonicalJson.Array(prerequisiteValues)),
            ("requester_id", ApprovalCanonicalJson.StringOrNull(submission.RequesterId)),
            ("requirements", ApprovalCanonicalJson.Array(requirementValues)),
            ("source_snapshot_digest", ApprovalCanonicalJson.String(submission.SourceSnapshotDigest)),
            ("subject_id", ApprovalCanonicalJson.String(submission.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(submission.SubjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(submission.SubjectVersion)),
            ("submission_key", ApprovalCanonicalJson.String(submission.SubmissionKey)),
            ("workload_client_id", ApprovalCanonicalJson.String(workload.ClientId)),
            ("workload_issuer", ApprovalCanonicalJson.String(workload.Issuer)));
    }

    private static CanonicalValue DependencyValue(ApprovalDependencyRef dependency) => ApprovalCanonicalJson.Object(
        ("mode", ApprovalCanonicalJson.String(dependency.Mode)),
        ("predecessor_key", ApprovalCanonicalJson.String(dependency.PredecessorKey)),
        ("predecessor_kind", ApprovalCanonicalJson.String(dependency.PredecessorKind)),
        ("targets", ApprovalRequirementDefinition.TargetsValue(dependency.Targets)));

    private static void ValidateActors(ApprovalSubmission submission, ApprovalAdapterDescriptor adapter)
    {
        if (submission.RequesterId is null && adapter.RequesterRequired)
        {
            throw new DomainValidationException(
                "The adapter contract requires requester_id for this operation.");
        }

        var excluded = submission.Requirements.SelectMany(requirement => requirement.ExcludedUserIds).ToHashSet();
        if (!excluded.Contains(submission.OriginatorId))
        {
            throw new DomainValidationException("originator_id must be excluded from every approval requirement.");
        }

        if (submission.RequesterId is not null && !excluded.Contains(submission.RequesterId.Value))
        {
            throw new DomainValidationException("requester_id must be excluded from every approval requirement.");
        }

        if (submission.RequesterId is not null && submission.RequesterId == submission.OriginatorId)
        {
            throw new DomainValidationException("Requester and originator cannot be the same actor.");
        }
    }

    private static void ValidateGrouping(ApprovalSubmission submission)
    {
        var duplicatedWorkflowKey = submission.Requirements
            .GroupBy(requirement => requirement.WorkflowRequirementKey(), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatedWorkflowKey is not null)
        {
            throw new DomainConflictException("Two approval requirements resolve to the same workflow key.");
        }

        // Partitioning by the adapter is legitimate (REQ-02), but one source requirement
        // cannot cover the same target twice through different workflow requirements.
        foreach (var group in submission.Requirements.GroupBy(
                     requirement => requirement.SourceRequirementKey, StringComparer.Ordinal))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in group.SelectMany(requirement => requirement.Targets))
            {
                if (!seen.Add(target.CanonicalIdentity))
                {
                    throw new DomainConflictException(
                        $"Source requirement '{group.Key}' repeats the same target across partitions.");
                }
            }
        }
    }

    private static void ValidateReferences(ApprovalSubmission submission)
    {
        var requirementKeys = submission.Requirements
            .Select(requirement => requirement.SourceRequirementKey)
            .ToHashSet(StringComparer.Ordinal);
        var prerequisiteKeys = submission.Prerequisites
            .Select(prerequisite => prerequisite.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (requirementKeys.Overlaps(prerequisiteKeys))
        {
            throw new DomainConflictException("Requirement and prerequisite keys cannot collide.");
        }

        foreach (var requirement in submission.Requirements)
        {
            foreach (var dependency in requirement.Dependencies)
            {
                if (dependency.PredecessorKind == DependencyPredecessorKind.Approval)
                {
                    if (!requirementKeys.Contains(dependency.PredecessorKey))
                    {
                        throw new DomainValidationException(
                            $"Dependency '{dependency.PredecessorKey}' does not reference a submitted requirement.");
                    }

                    if (string.Equals(dependency.PredecessorKey, requirement.SourceRequirementKey, StringComparison.Ordinal))
                    {
                        throw new DomainValidationException("A requirement cannot depend on itself.");
                    }
                }
                else if (!prerequisiteKeys.Contains(dependency.PredecessorKey))
                {
                    throw new DomainValidationException(
                        $"Dependency '{dependency.PredecessorKey}' does not reference a submitted prerequisite.");
                }
            }
        }
    }

    private static void ValidateEdges(ApprovalSubmission submission)
    {
        var nodes = submission.Requirements
            .GroupBy(requirement => requirement.SourceRequirementKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<ApprovalTarget>)group.SelectMany(requirement => requirement.Targets).ToArray(),
                StringComparer.Ordinal);
        foreach (var prerequisite in submission.Prerequisites)
        {
            nodes[prerequisite.Key] = prerequisite.Targets.ToArray();
        }

        foreach (var requirement in submission.Requirements)
        {
            foreach (var dependency in requirement.Dependencies)
            {
                ApprovalTargetRules.EnsureSubset(
                    dependency.Targets, nodes[dependency.PredecessorKey], $"predecessor '{dependency.PredecessorKey}'");
                ApprovalTargetRules.EnsureSubset(
                    dependency.Targets, requirement.Targets.ToArray(), $"requirement '{requirement.SourceRequirementKey}'");
            }
        }
    }

    private static void ValidateAcyclic(ApprovalSubmission submission)
    {
        var adjacency = submission.Requirements
            .GroupBy(requirement => requirement.SourceRequirementKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(requirement => requirement.Dependencies)
                    .Where(dependency => dependency.PredecessorKind == DependencyPredecessorKind.Approval)
                    .Select(dependency => dependency.PredecessorKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in adjacency.Keys)
        {
            Visit(key, adjacency, visiting, visited);
        }
    }

    private static void Visit(
        string key,
        IReadOnlyDictionary<string, string[]> adjacency,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        if (visited.Contains(key))
        {
            return;
        }

        if (!visiting.Add(key))
        {
            throw new DomainConflictException("Approval dependencies must form an acyclic graph.");
        }

        foreach (var predecessor in adjacency[key])
        {
            Visit(predecessor, adjacency, visiting, visited);
        }

        visiting.Remove(key);
        visited.Add(key);
    }

    private static void ValidateCanonicalSize(
        ApprovalSubmission submission,
        ApprovalAdapterDescriptor adapter,
        ApprovalWorkloadIdentity workload)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(
            ApprovalCanonicalJson.Serialize(CommandValue(submission, adapter, workload)));
        if (bytes > ApprovalLimits.MaxCanonicalCommandBytes)
        {
            throw new ApprovalPayloadTooLargeException("The canonical command exceeds 5 MiB.");
        }
    }
}

public sealed class ApprovalPayloadTooLargeException(string message) : DomainException(message);

public sealed class ApprovalDependencyUnavailableException(string message) : DomainException(message);
