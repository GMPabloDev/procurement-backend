using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Trusted in-process adapter that turns the persisted Policy evaluation of a Purchase Request
/// into a single idempotent approval submission (SPEC 05 REQ-01, REQ-03, REQ-04).
///
/// Every target comes from the confirmed completeness manifest and the persisted material
/// projection of the bundle; the caller only supplies the subject reference and the submission
/// key. Each SPEC 02 effect has exactly one projection: approval requirements for
/// <c>REQUIRE_APPROVAL</c>, external prerequisites with a versioned owner for automatic controls
/// and no node for effects the owning domain applies. Unknown controls, missing projections or
/// corrupt digests fail closed before any case exists.
/// </summary>
public sealed class PolicyApprovalAdapter(
    ProcureToPayDbContext dbContext,
    string contractVersion = PolicyApprovalAdapter.ContractVersion,
    IBudgetDemandBuilder? budgetDemandBuilder = null) : IApprovalSubmissionAdapter
{
    public const string AdapterId = "policy-approval-adapter";
    /// <summary>The v2 contract: budget parameters remain the SPEC 05 projection.</summary>
    public const string ContractVersion = "v2";
    /// <summary>
    /// SPEC 08 DEC-08: the v3 contract adds Fiscal Year, Spend Category and one demand per covered
    /// target by delegating to the server-side demand builder. The DAG, actions, actors and snapshot
    /// contract are unchanged, so only the budget projection differs from v2.
    /// </summary>
    public const string ContractVersionV3 = "v3";
    /// <summary>
    /// SPEC 09 REQ-10: the v4 contract keeps the complete budget projection of v3 and partitions
    /// every REQUIRE_ACTIVE_SUPPLIER control by supplier reference, so one combined control over
    /// lines of different suppliers produces one prerequisite per supplier instead of failing closed
    /// or validating a single arbitrary one. The DAG, actions, actors and snapshot are unchanged.
    /// </summary>
    public const string ContractVersionV4 = "v4";
    /// <summary>
    /// SPEC 06 REQ-07: the v2 contract declares its requester, admits requester=originator for the
    /// Purchase Request flow and offers <c>REQUEST_CHANGES</c> beside APPROVE and REJECT.
    /// </summary>
    public const string AdapterIdentity = "policy-approval-adapter/v2";
    public const string AdapterIdentityV3 = "policy-approval-adapter/v3";
    public const string AdapterIdentityV4 = "policy-approval-adapter/v4";
    public const string SubjectType = "PURCHASE_REQUEST";
    public const string Operation = "SUBMIT_PURCHASE_REQUEST";
    public const string SnapshotContractVersion = "policy-evaluation-snapshot/v1";

    private readonly ApprovalAdapterDescriptor descriptorValue = new(
        AdapterId, SubjectType, Operation, contractVersion, RequesterRequired: true,
        AllowsRequesterAsOriginator: true, SupersessionDeltaSupported: true);

    private static readonly IReadOnlyDictionary<PolicyEffectType, string> OwnerAdapters =
        new Dictionary<PolicyEffectType, string>
        {
            [PolicyEffectType.RequireBudgetCheck] = "budget-check-owner",
            [PolicyEffectType.RequireSupportingDocument] = "supporting-document-owner",
            [PolicyEffectType.RequireActiveSupplier] = "active-supplier-owner",
            [PolicyEffectType.RequireQuotations] = "quotation-status-owner",
            [PolicyEffectType.RequireProcurement] = "procurement-stage-owner"
        };

    private static readonly IReadOnlyDictionary<string, int> StageOrder = new Dictionary<string, int>(
        StringComparer.Ordinal)
    {
        ["DEPARTMENT"] = 1,
        ["PRE_PROCUREMENT"] = 2,
        ["PROCUREMENT"] = 3,
        ["PRE_PO"] = 4
    };

    public ApprovalAdapterDescriptor Descriptor => descriptorValue;

    public async Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SubjectType, SubjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Operation, Operation, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The policy approval adapter only serves Purchase Request submissions.");
        }

        var record = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(bundle =>
                bundle.OrganizationId == request.OrganizationId &&
                bundle.SubjectId == request.SubjectId &&
                bundle.SubjectVersion == request.SubjectVersion)
            .OrderByDescending(bundle => bundle.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "No persisted policy evaluation is available for the requested subject version.");

        var bundle = Rehydrate(record);
        await VerifyDigestsAsync(record, bundle, cancellationToken);
        if (bundle.Result == PolicyResult.Blocked)
        {
            throw new DomainConflictException(
                "A blocked policy evaluation cannot be submitted to the approval workflow.");
        }

        var material = MaterialTargets(bundle);
        return await MapAsync(bundle, material, record, request, cancellationToken);
    }

    /// <summary>
    /// Pure mapping of a verified evaluation into the submission graph: one projection per SPEC 02
    /// effect, prerequisites with their fixed versioned owners and explicit dependencies by stage
    /// and target (SPEC 05 REQ-03, REQ-04). Exposed for the mapping matrix tests.
    /// </summary>
    public static ApprovalSubmission Map(
        PolicyEvaluationBundle bundle,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        PolicyEvaluationBundleRecord record,
        ApprovalSubmissionRequest request) =>
        MapCore(
                dbContext: null,
                bundle,
                material,
                record,
                request,
                budgetDemandBuilder: null,
                supplierPartitioning: false,
                SubjectType,
                Operation,
                SnapshotDigest(record, bundle, SubjectType),
                CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Version-aware mapping: every control keeps its projection, and a budget control uses the
    /// server-side demand builder only when the adapter runs the v3 contract (SPEC 08 REQ-05).
    /// </summary>
    public Task<ApprovalSubmission> MapAsync(
        PolicyEvaluationBundle bundle,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        PolicyEvaluationBundleRecord record,
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken) =>
        MapCore(
            dbContext, bundle, material, record, request, budgetDemandBuilder, IsV4, SubjectType, Operation,
            SnapshotDigest(record, bundle, SubjectType), cancellationToken);

    /// <summary>
    /// Mapping under another published identity (SPEC 10 REQ-11): the same control projection, targets
    /// and dependencies, with the subject type, the operation and the source snapshot digest the
    /// owning spec publishes. The mapping never invents a target or a parameter for the new subject.
    /// </summary>
    public Task<ApprovalSubmission> MapAsAsync(
        PolicyEvaluationBundle bundle,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        PolicyEvaluationBundleRecord record,
        ApprovalSubmissionRequest request,
        string subjectType,
        string operation,
        string sourceSnapshotDigest,
        CancellationToken cancellationToken) =>
        MapCore(
            dbContext, bundle, material, record, request, budgetDemandBuilder, IsV4, subjectType, operation,
            sourceSnapshotDigest, cancellationToken);

    private static async Task<ApprovalSubmission> MapCore(
        ProcureToPayDbContext? dbContext,
        PolicyEvaluationBundle bundle,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        PolicyEvaluationBundleRecord record,
        ApprovalSubmissionRequest request,
        IBudgetDemandBuilder? budgetDemandBuilder,
        bool supplierPartitioning,
        string subjectType,
        string operation,
        string sourceSnapshotDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(request);
        var nodes = new List<Node>();
        var requirements = new List<ApprovalRequirementDefinition>();
        var prerequisites = new List<ExternalPrerequisiteDefinition>();
        foreach (var control in bundle.Controls)
        {
            switch (control.Type)
            {
                case PolicyEffectType.Allow:
                case PolicyEffectType.RequirePo:
                case PolicyEffectType.AllowDirectPurchase:
                    break;
                case PolicyEffectType.Block:
                    throw new DomainConflictException(
                        $"The evaluation contains a blocking control '{control.RequirementKey}'.");
                case PolicyEffectType.RequireApproval:
                    nodes.Add(RequirementNode(control, material, request));
                    break;
                case PolicyEffectType.RequireActiveSupplier when supplierPartitioning:
                    // SPEC 09 REQ-10: one prerequisite per supplier reference of the covered targets.
                    nodes.AddRange(SupplierPartitionNodes(control, material, bundle));
                    break;
                default:
                    nodes.Add(await PrerequisiteNodeAsync(
                        dbContext, control, material, request, bundle, budgetDemandBuilder, cancellationToken));
                    break;
            }
        }

        foreach (var node in nodes)
        {
            if (node.IsRequirement)
            {
                var parts = node.Parts!;
                requirements.Add(new ApprovalRequirementDefinition(
                    node.Key,
                    node.Stage,
                    parts.Role,
                    parts.Authority,
                    parts.Scope,
                    // pi-lens-ignore: lsp:CS0117
                    [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
                    parts.Exclusions,
                    node.Targets,
                    DependenciesFor(node, nodes)));
            }
            else
            {
                prerequisites.Add(node.Prerequisite!);
            }
        }

        return new ApprovalSubmission(
            request.SubmissionKey,
            request.OrganizationId,
            subjectType,
            request.SubjectId,
            request.SubjectVersion,
            operation,
            sourceSnapshotDigest,
            request.RequesterId,
            request.OriginatorId,
            requirements,
            prerequisites);
    }
    /// <summary>True when this instance runs the supplier-partitioning contract (SPEC 09 REQ-10).</summary>
    private bool IsV4 => string.Equals(
        descriptorValue.ContractVersion, ContractVersionV4, StringComparison.Ordinal);

    /// <summary>
    /// Deterministic partition of one active-supplier control by the supplier reference attested in
    /// the confirmed snapshot. Every covered target appears exactly once, each partition keeps the
    /// singular <c>active-supplier-owner/v1</c> parameters and a missing or unusable reference fails
    /// closed before any case exists (SPEC 09 REQ-10).
    /// </summary>
    private static IReadOnlyList<Node> SupplierPartitionNodes(
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        PolicyEvaluationBundle bundle)
    {
        var targets = Targets(control, material).ToImmutableArray();
        if (targets.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The control '{control.RequirementKey}' has no confirmed target.");
        }

        var snapshot = bundle.RequestSnapshotJson
            ?? throw new ApprovalDependencyUnavailableException(
                "The evaluation has no persisted snapshot to partition the supplier control.");
        var partitions = new SortedDictionary<string, List<ApprovalTarget>>(StringComparer.Ordinal);
        var references = new Dictionary<string, (Guid Id, int Version)>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var reference = SupplierReferenceOf(snapshot, target)
                ?? throw new ApprovalDependencyUnavailableException(
                    $"The supplier control '{control.RequirementKey}' covers a line without a confirmed supplier.");
            var key = $"{reference.Id:D}:{reference.Version}";
            references[key] = reference;
            if (!partitions.TryGetValue(key, out var bucket))
            {
                bucket = [];
                partitions[key] = bucket;
            }

            bucket.Add(target);
        }

        var nodes = new List<Node>(partitions.Count);
        foreach (var partition in partitions)
        {
            var supplier = references[partition.Key];
            var parameters = SupplierCanonicalizer.SupplierParameters(
                new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef(
                    "SUPPLIER", supplier.Id, supplier.Version));
            var partitionTargets = partition.Value.ToImmutableArray();
            var prerequisite = new ExternalPrerequisiteDefinition(
                SupplierCanonicalizer.PartitionKey(control.RequirementKey, new ProcureToPay.Domain.Modules.Policy.VersionedEntityRef(
                    "SUPPLIER", supplier.Id, supplier.Version)),
                "active-supplier-owner",
                "v1",
                control.Type.ToString().ToUpperInvariant(),
                SourceControlDigest(control, parameters, partitionTargets),
                parameters,
                partitionTargets);
            nodes.Add(Node.ForPrerequisite(prerequisite, control.Phase, partitionTargets));
        }

        return nodes;
    }

    /// <summary>Supplier reference of one covered target in the persisted Policy snapshot.</summary>
    private static (Guid Id, int Version)? SupplierReferenceOf(string requestSnapshotJson, ApprovalTarget target)
    {
        var references = VersionedEntityReferences(
            requestSnapshotJson, [target], "SUPPLIER");
        if (references.Count == 0)
        {
            return null;
        }

        if (references.Count > 1)
        {
            throw new ApprovalDependencyUnavailableException(
                "A covered line cannot declare more than one supplier reference.");
        }

        return (references[0].Id, references[0].Version);
    }

    private static PolicyEvaluationBundle Rehydrate(PolicyEvaluationBundleRecord record)
    {
        try
        {
            return PolicyEvaluationBundleRehydrator.FromJson(record.BundleJson);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new ApprovalDependencyUnavailableException("The persisted policy evaluation is corrupted.");
        }
    }

    private async Task VerifyDigestsAsync(
        PolicyEvaluationBundleRecord record,
        PolicyEvaluationBundle bundle,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(bundle.ResultDigest, record.ResultDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bundle.InputDigest, record.InputDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bundle.PolicyContentDigest, record.PolicyContentDigest, StringComparison.OrdinalIgnoreCase) ||
            bundle.Subject.Id != record.SubjectId ||
            bundle.Subject.Version != record.SubjectVersion)
        {
            throw new ApprovalDependencyUnavailableException(
                "The persisted policy evaluation does not match its row identity.");
        }

        if (string.IsNullOrWhiteSpace(bundle.InputCanonicalJson) ||
            !string.Equals(
                PolicyCanonicalizer.Hash(bundle.InputCanonicalJson), bundle.InputDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApprovalDependencyUnavailableException("The policy input digest is not reproducible.");
        }

        var recomputedResult = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            bundle.InputDigest,
            bundle.ScopeEvaluations,
            bundle.Controls,
            bundle.Result,
            bundle.Diff.Count == 0 ? null : bundle.Diff));
        if (!string.Equals(recomputedResult, bundle.ResultDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApprovalDependencyUnavailableException("The policy result digest is not reproducible.");
        }

        if (string.IsNullOrWhiteSpace(bundle.ManifestDigest) || string.IsNullOrWhiteSpace(bundle.ManifestCanonicalJson) ||
            !string.Equals(
                PolicyCanonicalizer.Hash(bundle.ManifestCanonicalJson), bundle.ManifestDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApprovalDependencyUnavailableException("The confirmed completeness manifest is unavailable.");
        }

        var policy = await dbContext.PolicySetVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == record.PolicySetVersionId, cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException("The evaluated policy version does not exist.");
        if (!string.Equals(policy.ContentDigest, bundle.PolicyContentDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(PolicyCanonicalizer.Hash(policy.ContentJson), policy.ContentDigest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApprovalDependencyUnavailableException("The evaluated policy content digest is not reproducible.");
        }

        if (bundle.MaterialProjection is null ||
            !string.Equals(bundle.MaterialProjection.ContractVersion, PolicyApprovalTargets.ContractVersion,
                StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The evaluation has no material target projection; it must be reevaluated.");
        }

        // Recompute every projected digest from the persisted snapshot so a tampered projection
        // row cannot introduce a target the workflow would otherwise trust.
        foreach (var target in bundle.MaterialProjection.Targets)
        {
            var recomputed = PolicyApprovalTargets.ComputeMaterialDigest(
                bundle.RequestSnapshotJson!,
                bundle.ManifestDigest,
                bundle.MaterialProjection.Provenance,
                new PolicySubjectReference(target.Id, target.Version));
            if (!string.Equals(recomputed, target.MaterialSnapshotDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApprovalDependencyUnavailableException(
                    "The material target projection of the evaluation is not reproducible.");
            }
        }
    }

    private static IReadOnlyDictionary<Guid, ApprovalTarget> MaterialTargets(PolicyEvaluationBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.RequestSnapshotJson) || bundle.MaterialProjection is null)
        {
            throw new ApprovalDependencyUnavailableException(
                "The evaluation has no material target projection; it must be reevaluated.");
        }

        return bundle.MaterialProjection.Targets.ToDictionary(
            target => target.Id,
            target => new ApprovalTarget(
                PolicyApprovalTargets.TargetType, target.Id, target.Version, target.MaterialSnapshotDigest));
    }

    private static Node RequirementNode(
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        ApprovalSubmissionRequest request)
    {
        var approval = control.Approval
            ?? throw new ApprovalDependencyUnavailableException(
                $"The approval control '{control.RequirementKey}' has no descriptor.");
        var targets = Targets(control, material).ToImmutableArray();
        if (targets.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The approval control '{control.RequirementKey}' has no confirmed target.");
        }

        var exclusions = Exclusions(request);
        var scope = ParseScope(approval.DecisionScope, request.OrganizationId);
        var authority = approval.AuthorityType is null || approval.AuthorityLevel is null
            ? AuthorityRequirement.None
            : AuthorityRequirement.Required(
                approval.AuthorityType.Value, approval.AuthorityLevel.Rank, approval.AmountBase,
                approval.BaseCurrency);
        return Node.ForRequirement(
            new ApprovalRequirementParts(control.RequirementKey, approval.Role, authority, scope, exclusions),
            control.Phase,
            targets);
    }

    private static async Task<Node> PrerequisiteNodeAsync(
        ProcureToPayDbContext? dbContext,
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        ApprovalSubmissionRequest request,
        PolicyEvaluationBundle bundle,
        IBudgetDemandBuilder? budgetDemandBuilder,
        CancellationToken cancellationToken)
    {
        if (control.Type == PolicyEffectType.RequireBudgetCheck && budgetDemandBuilder is not null)
        {
            if (dbContext is null)
            {
                throw new ApprovalDependencyUnavailableException(
                    "The v3 budget projection requires the persisted organization identity.");
            }

            return await BudgetPrerequisiteNodeAsync(
                dbContext, control, material, request, bundle, budgetDemandBuilder, cancellationToken);
        }

        return PrerequisiteNode(control, material, request, bundle.RequestSnapshotJson!);
    }

    /// <summary>
    /// Budget projection of the v3 contract (SPEC 08 REQ-05): the parameters are rebuilt from the
    /// confirmed Purchase Request version, so Fiscal Year, Spend Category and the amount of every
    /// covered target survive the control grouping of the policy engine.
    /// </summary>
    private static async Task<Node> BudgetPrerequisiteNodeAsync(
        ProcureToPayDbContext dbContext,
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        ApprovalSubmissionRequest request,
        PolicyEvaluationBundle bundle,
        IBudgetDemandBuilder budgetDemandBuilder,
        CancellationToken cancellationToken)
    {
        var targets = Targets(control, material).ToImmutableArray();
        if (targets.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The budget control '{control.RequirementKey}' has no confirmed target.");
        }

        var manifest = bundle.ManifestDigest
            ?? throw new ApprovalDependencyUnavailableException(
                "The evaluation has no confirmed completeness manifest digest.");
        var (requestId, requestVersion) = PolicyApprovalBudgetProjection.ResolveSubject(
            request.SubjectType, request.SubjectId, request.SubjectVersion);
        var baseCurrency = await BaseCurrencyAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        var parameters = await PolicyApprovalBudgetProjection.BuildParametersAsync(
            budgetDemandBuilder,
            request.OrganizationId,
            requestId,
            requestVersion,
            requestContentDigest: null,
            manifest,
            targets,
            baseCurrency,
            cancellationToken);
        var prerequisite = new ExternalPrerequisiteDefinition(
            control.RequirementKey,
            "budget-check-owner",
            "v1",
            BudgetCodes.RequireBudgetCheckType,
            ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
                ("parameters", ApprovalCanonicalJson.String(parameters)),
                ("phase", ApprovalCanonicalJson.String(control.Phase)),
                ("requirement_key", ApprovalCanonicalJson.String(control.RequirementKey)),
                ("targets", ApprovalRequirementDefinition.TargetsValue(targets)),
                ("type", ApprovalCanonicalJson.String(BudgetCodes.RequireBudgetCheckType)))),
            parameters,
            targets);
        return Node.ForPrerequisite(prerequisite, control.Phase, targets);
    }

    private static async Task<string> BaseCurrencyAsync(
        ProcureToPayDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new ApprovalDependencyUnavailableException("The organization base currency is unavailable.");

    private static Node PrerequisiteNode(
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material,
        ApprovalSubmissionRequest request,
        string requestSnapshotJson)
    {
        if (!OwnerAdapters.TryGetValue(control.Type, out var ownerAdapter))
        {
            throw new ApprovalDependencyUnavailableException(
                $"The policy control '{control.RequirementKey}' of type '{control.Type}' has no defined owner.");
        }

        var targets = Targets(control, material).ToImmutableArray();
        if (targets.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The control '{control.RequirementKey}' has no confirmed target.");
        }

        var parameters = Parameters(control, requestSnapshotJson, targets);
        var prerequisite = new ExternalPrerequisiteDefinition(
            control.RequirementKey,
            ownerAdapter,
            "v1",
            control.Type.ToString().ToUpperInvariant(),
            SourceControlDigest(control, parameters, targets),
            parameters,
            targets);
        return Node.ForPrerequisite(prerequisite, control.Phase, targets);
    }

    private static IEnumerable<ApprovalTarget> Targets(
        PolicyGeneratedControl control,
        IReadOnlyDictionary<Guid, ApprovalTarget> material)
    {
        if (control.SubjectIds.Count == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The control '{control.RequirementKey}' covers no line.");
        }

        foreach (var subjectId in control.SubjectIds.OrderBy(id => id))
        {
            if (!material.TryGetValue(subjectId, out var target))
            {
                throw new ApprovalDependencyUnavailableException(
                    $"The control '{control.RequirementKey}' covers a line without a material projection.");
            }

            yield return target;
        }
    }

    private static ImmutableHashSet<Guid> Exclusions(ApprovalSubmissionRequest request)
    {
        var exclusions = ImmutableHashSet.CreateBuilder<Guid>();
        exclusions.Add(request.OriginatorId);
        if (request.RequesterId is Guid requester && requester != Guid.Empty)
        {
            exclusions.Add(requester);
        }

        return exclusions.ToImmutable();
    }

    private static DecisionScopeDescriptor ParseScope(string decisionScope, Guid organizationId)
    {
        try
        {
            return DecisionScopeDescriptor.Parse(decisionScope, organizationId);
        }
        catch (DomainValidationException exception)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The approval decision scope is not canonical decision-scope/v1: {exception.Message}");
        }
    }

    private static string Stage(PolicyGeneratedControl control) =>
        StageOrder.ContainsKey(control.Phase)
            ? control.Phase
            : throw new ApprovalDependencyUnavailableException(
                $"The control '{control.RequirementKey}' declares an unknown stage '{control.Phase}'.");

    private static string Parameters(
        PolicyGeneratedControl control,
        string requestSnapshotJson,
        ImmutableArray<ApprovalTarget> targets) => control.Type switch
    {
        PolicyEffectType.RequireBudgetCheck => BudgetParameters(control, requestSnapshotJson, targets),
        PolicyEffectType.RequireSupportingDocument => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("document_types", ApprovalCanonicalJson.Set(control.SupportingDocumentTypes)),
            ("minimum_count", ApprovalCanonicalJson.Number(1)))),
        PolicyEffectType.RequireActiveSupplier => SupplierParameters(control, requestSnapshotJson, targets),
        PolicyEffectType.RequireQuotations => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("minimum_allowed_quotations", ApprovalCanonicalJson.NumberOrNull(control.MinimumAllowedQuotations)),
            ("minimum_quotations", ApprovalCanonicalJson.NumberOrNull(control.MinimumQuotations)))),
        PolicyEffectType.RequireProcurement => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("phase", ApprovalCanonicalJson.String(control.Phase)))),
        _ => throw new ApprovalDependencyUnavailableException(
            $"The control '{control.RequirementKey}' of type '{control.Type}' has no defined parameters.")
    };

    /// <summary>
    /// Budget parameters require the frozen Cost Center references and the confirmed amount; a
    /// control without them fails closed instead of persisting an incomplete prerequisite
    /// (SPEC 05 REQ-03, CA-02).
    /// </summary>
    private static string BudgetParameters(
        PolicyGeneratedControl control,
        string requestSnapshotJson,
        ImmutableArray<ApprovalTarget> targets)
    {
        if (control.CostCenterIds.IsEmpty || control.AmountBase is null ||
            string.IsNullOrWhiteSpace(control.BaseCurrency))
        {
            throw new ApprovalDependencyUnavailableException(
                $"The budget control '{control.RequirementKey}' lacks confirmed cost centers, amount or currency.");
        }

        var references = VersionedEntityReferences(requestSnapshotJson, targets, "COST_CENTER")
            .Where(reference => control.CostCenterIds.Contains(reference.Id))
            .OrderBy(reference => reference.Id)
            .ToArray();
        if (references.Length != control.CostCenterIds.Count)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The budget control '{control.RequirementKey}' cannot resolve every cost center version.");
        }

        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("amount_base", ApprovalCanonicalJson.String(
                ApprovalCanonicalJson.FormatDecimal(control.AmountBase.Value))),
            ("base_currency", ApprovalCanonicalJson.String(control.BaseCurrency)),
            ("cost_center_refs", ApprovalCanonicalJson.Set(references.Select(reference =>
                ApprovalCanonicalJson.Object(
                    ("id", ApprovalCanonicalJson.String(reference.Id)),
                    ("version", ApprovalCanonicalJson.Number(reference.Version))))))));
    }

    /// <summary>
    /// The active-supplier prerequisite must carry the versioned supplier reference attested by
    /// the confirmed snapshot; without exactly one it fails closed (SPEC 05 REQ-03, CA-02).
    /// </summary>
    private static string SupplierParameters(
        PolicyGeneratedControl control,
        string requestSnapshotJson,
        ImmutableArray<ApprovalTarget> targets)
    {
        var references = VersionedEntityReferences(requestSnapshotJson, targets, "SUPPLIER");
        if (references.Count != 1)
        {
            throw new ApprovalDependencyUnavailableException(
                $"The supplier control '{control.RequirementKey}' has no unique confirmed supplier reference.");
        }

        var supplier = references[0];
        return ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("supplier_ref", ApprovalCanonicalJson.Object(
                ("id", ApprovalCanonicalJson.String(supplier.Id)),
                ("version", ApprovalCanonicalJson.Number(supplier.Version))))));
    }

    /// <summary>Versioned entity references of one reference type across the confirmed targets.</summary>
    private static IReadOnlyList<(Guid Id, int Version)> VersionedEntityReferences(
        string requestSnapshotJson,
        ImmutableArray<ApprovalTarget> targets,
        string referenceType)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestSnapshotJson);
        var references = new Dictionary<Guid, int>();
        foreach (var target in targets)
        {
            var line = document.RootElement.GetProperty("lines").EnumerateArray()
                .FirstOrDefault(candidate =>
                    candidate.GetProperty("subject").GetProperty("id").GetGuid() == target.Id &&
                    candidate.GetProperty("subject").GetProperty("version").GetInt32() == target.Version);
            if (line.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ApprovalDependencyUnavailableException(
                    "The persisted policy snapshot does not contain a confirmed covered line.");
            }

            CollectReferences(line.GetProperty("facts"), referenceType, references);
        }

        return references.Select(entry => (entry.Key, entry.Value)).ToArray();
    }

    private static void CollectReferences(
        System.Text.Json.JsonElement facts,
        string referenceType,
        Dictionary<Guid, int> references)
    {
        foreach (var fact in facts.EnumerateObject())
        {
            CollectReferenceValue(fact.Value, referenceType, references);
        }
    }

    private static void CollectReferenceValue(
        System.Text.Json.JsonElement value,
        string referenceType,
        Dictionary<Guid, int> references)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var member in value.EnumerateArray())
            {
                CollectReferenceValue(member, referenceType, references);
            }

            return;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !value.TryGetProperty("kind", out var kind) || kind.GetString() != "VERSIONED_ENTITY_REF" ||
            !value.TryGetProperty("reference_type", out var type) || type.GetString() != referenceType ||
            !value.TryGetProperty("entity_id", out var entityId) || entityId.ValueKind != System.Text.Json.JsonValueKind.String ||
            !value.TryGetProperty("version", out var version) || version.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            return;
        }

        references[entityId.GetGuid()] = version.GetInt32();
    }

    private static string SourceControlDigest(
        PolicyGeneratedControl control,
        string parameters,
        ImmutableArray<ApprovalTarget> targets) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("parameters", ApprovalCanonicalJson.String(parameters)),
            ("phase", ApprovalCanonicalJson.String(control.Phase)),
            ("requirement_key", ApprovalCanonicalJson.String(control.RequirementKey)),
            ("targets", ApprovalRequirementDefinition.TargetsValue(targets)),
            ("type", ApprovalCanonicalJson.String(control.Type.ToString().ToUpperInvariant()))));

    private static string SnapshotDigest(
        PolicyEvaluationBundleRecord record,
        PolicyEvaluationBundle bundle,
        string subjectType) =>
        ApprovalCanonicalJson.Digest(ApprovalCanonicalJson.Object(
            ("base_result_digest", ApprovalCanonicalJson.String(bundle.ResultDigest)),
            ("bundle_id", ApprovalCanonicalJson.String(record.Id)),
            ("contract_version", ApprovalCanonicalJson.String(SnapshotContractVersion)),
            ("evaluation_sequence", ApprovalCanonicalJson.Number(record.EvaluationSequence)),
            ("fact_manifest_digest", ApprovalCanonicalJson.String(bundle.ManifestDigest!)),
            ("operation", ApprovalCanonicalJson.String(record.Operation)),
            ("policy_content_digest", ApprovalCanonicalJson.String(bundle.PolicyContentDigest)),
            ("policy_version_id", ApprovalCanonicalJson.String(record.PolicySetVersionId)),
            ("subject_id", ApprovalCanonicalJson.String(record.SubjectId)),
            ("subject_type", ApprovalCanonicalJson.String(subjectType)),
            ("subject_version", ApprovalCanonicalJson.Number(record.SubjectVersion))));

    /// <summary>
    /// Explicit dependencies by confirmed target (SPEC 05 REQ-04, DEC-04): every node depends on
    /// the strictly earlier stage nodes that share at least one target, so Department runs first,
    /// Finance/IT/Legal stay parallel inside PRE_PROCUREMENT and PROCUREMENT/PRE_PO wait for the
    /// controls that precede them.
    /// </summary>
    private static ImmutableArray<ApprovalDependencyRef> DependenciesFor(Node node, IReadOnlyList<Node> nodes)
    {
        var predecessors = new List<ApprovalDependencyRef>();
        foreach (var other in nodes)
        {
            if (ReferenceEquals(other, node) ||
                StageOrder[other.Stage] >= StageOrder[node.Stage])
            {
                continue;
            }

            var shared = other.Targets
                .Where(target => node.Targets.Any(candidate => candidate.SameIdentity(target)))
                .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray();
            if (shared.Length == 0)
            {
                continue;
            }

            predecessors.Add(new ApprovalDependencyRef(
                other.IsRequirement ? DependencyPredecessorKind.Approval : DependencyPredecessorKind.External,
                other.Key,
                shared));
        }

        return predecessors.ToImmutableArray();
    }

    private sealed record Node(
        string Key,
        string Stage,
        ImmutableArray<ApprovalTarget> Targets,
        bool IsRequirement,
        ApprovalRequirementParts? Parts,
        ExternalPrerequisiteDefinition? Prerequisite)
    {
        public static Node ForRequirement(
            ApprovalRequirementParts parts,
            string stage,
            ImmutableArray<ApprovalTarget> targets) =>
            new(parts.Key, RequireStage(stage, parts.Key), targets, true, parts, null);

        public static Node ForPrerequisite(
            ExternalPrerequisiteDefinition prerequisite,
            string stage,
            ImmutableArray<ApprovalTarget> targets) =>
            new(prerequisite.Key, RequireStage(stage, prerequisite.Key), targets, false, null, prerequisite);

        private static string RequireStage(string stage, string key) =>
            StageOrder.ContainsKey(stage)
                ? stage
                : throw new ApprovalDependencyUnavailableException(
                    $"The policy control '{key}' declares an unknown stage '{stage}'.");
    }

    private sealed record ApprovalRequirementParts(
        string Key,
        SystemRole Role,
        AuthorityRequirement Authority,
        DecisionScopeDescriptor Scope,
        ImmutableHashSet<Guid> Exclusions);
}
