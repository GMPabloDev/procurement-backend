using System.Collections.Immutable;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Mapping matrix and DAG evidence of SPEC 05 REQ-03/REQ-04 (CA-02, CA-03, CA-04): one
/// projection per SPEC 02 effect, fixed versioned owners, no node for owner-applied effects and
/// dependencies by stage and confirmed target.
/// </summary>
public sealed class PolicyApprovalAdapterTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LineOne = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid LineTwo = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OriginatorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid RequesterId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public void Mapping_table_projects_every_effect_or_fails_closed()
    {
        var submission = Map(
            Control("APPROVE", PolicyEffectType.RequireApproval, "DEPARTMENT", approval: ApprovalDescriptor()),
            Control("BUDGET", PolicyEffectType.RequireBudgetCheck, "PRE_PROCUREMENT"),
            Control("DOCS", PolicyEffectType.RequireSupportingDocument, "PRE_PROCUREMENT"),
            Control("SUPPLIER", PolicyEffectType.RequireActiveSupplier, "PRE_PROCUREMENT"),
            Control("QUOTES", PolicyEffectType.RequireQuotations, "PRE_PROCUREMENT"),
            Control("PROC", PolicyEffectType.RequireProcurement, "PROCUREMENT"),
            Control("ALLOW", PolicyEffectType.Allow, "PRE_PROCUREMENT"),
            Control("PO", PolicyEffectType.RequirePo, "PRE_PO"),
            Control("DIRECT", PolicyEffectType.AllowDirectPurchase, "PRE_PROCUREMENT"));

        Assert.Single(submission.Requirements);
        Assert.Equal("APPROVE", submission.Requirements[0].SourceRequirementKey);
        Assert.Equal("DEPARTMENT", submission.Requirements[0].StageCode);
        Assert.Equal(SystemRole.DepartmentApprover, submission.Requirements[0].Role);
        Assert.Equal(ApprovalAuthorityType.BusinessNeed, submission.Requirements[0].Authority.Type);
        Assert.True(submission.Requirements[0].ExcludedUserIds.SetEquals([OriginatorId, RequesterId]));
        Assert.Equal(5, submission.Prerequisites.Length);
        var owners = submission.Prerequisites.ToDictionary(
            prerequisite => prerequisite.Key, prerequisite => prerequisite.OwnerAdapterId, StringComparer.Ordinal);
        Assert.Equal("budget-check-owner", owners["BUDGET"]);
        Assert.Equal("supporting-document-owner", owners["DOCS"]);
        Assert.Equal("active-supplier-owner", owners["SUPPLIER"]);
        Assert.Equal("quotation-status-owner", owners["QUOTES"]);
        Assert.Equal("procurement-stage-owner", owners["PROC"]);
        Assert.All(submission.Prerequisites, prerequisite => Assert.Equal("v1", prerequisite.OwnerAdapterVersion));
        Assert.All(submission.Prerequisites, prerequisite =>
            Assert.Equal(64, prerequisite.SourceControlDigest.Length));
    }

    [Fact]
    public void Unknown_control_or_phase_fails_closed_and_block_never_submits()
    {
        Assert.Throws<ApprovalDependencyUnavailableException>(() =>
            Map(Control("UNKNOWN", (PolicyEffectType)99, "PRE_PROCUREMENT")));
        Assert.Throws<ApprovalDependencyUnavailableException>(() =>
            Map(Control("APPROVE", PolicyEffectType.RequireApproval, "NONSENSE", approval: ApprovalDescriptor())));

        var blocked = Bundle([Control("BLOCK", PolicyEffectType.Block, "PRE_PROCUREMENT")],
            PolicyResult.Blocked);
        Assert.Throws<DomainConflictException>(() => PolicyApprovalAdapter.Map(
            blocked, Material(LineOne), Record(), Request()));
    }

    [Theory]
    [InlineData(PolicyEffectType.RequireBudgetCheck)]
    [InlineData(PolicyEffectType.RequireSupportingDocument)]
    [InlineData(PolicyEffectType.RequireActiveSupplier)]
    [InlineData(PolicyEffectType.RequireQuotations)]
    [InlineData(PolicyEffectType.RequireProcurement)]
    public void Automatic_controls_never_become_human_tasks(PolicyEffectType type)
    {
        var submission = Map(Control("AUTO", type, "PRE_PROCUREMENT"));

        Assert.Empty(submission.Requirements);
        Assert.Single(submission.Prerequisites);
        Assert.Equal("AUTO", submission.Prerequisites[0].Key);
    }

    [Fact]
    public void Dependencies_follow_stage_and_confirmed_targets()
    {
        var department = Control("DEPT", PolicyEffectType.RequireApproval, "DEPARTMENT",
            approval: ApprovalDescriptor());
        var quotations = Control("QUOTES", PolicyEffectType.RequireQuotations, "PRE_PROCUREMENT");
        var procurement = Control("PROC_APPROVAL", PolicyEffectType.RequireApproval, "PROCUREMENT",
            approval: ApprovalDescriptor(SystemRole.ProcurementApprover, ApprovalAuthorityType.Procurement));
        // Covers a different line, so it stays independent from the others.
        var other = Control("OTHER", PolicyEffectType.RequireApproval, "DEPARTMENT",
            approval: ApprovalDescriptor(), subject: LineTwo);

        var submission = PolicyApprovalAdapter.Map(
            Bundle([department, quotations, procurement, other]),
            new Dictionary<Guid, ApprovalTarget>
            {
                [LineOne] = MaterialTarget(LineOne),
                [LineTwo] = new ApprovalTarget("PURCHASE_REQUEST_LINE", LineTwo, 1, new string('e', 64))
            },
            Record(),
            Request());

        var procurementRequirement = submission.Requirements.Single(
            requirement => requirement.SourceRequirementKey == "PROC_APPROVAL");
        var predecessors = procurementRequirement.Dependencies
            .Select(dependency => (dependency.PredecessorKind, dependency.PredecessorKey))
            .OrderBy(entry => entry.PredecessorKey, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                (DependencyPredecessorKind.Approval, "DEPT"),
                (DependencyPredecessorKind.External, "QUOTES")
            ],
            predecessors);
        Assert.All(procurementRequirement.Dependencies, dependency =>
            Assert.Equal([MaterialTarget(LineOne)], dependency.Targets.ToArray()));
        // Same-stage requirements never wait for each other.
        Assert.Empty(submission.Requirements.Single(
            requirement => requirement.SourceRequirementKey == "DEPT").Dependencies);
        Assert.Empty(submission.Requirements.Single(
            requirement => requirement.SourceRequirementKey == "OTHER").Dependencies);    }

    [Fact]
    public void Targets_are_fully_versioned_and_cover_only_confirmed_lines()
    {
        var submission = Map(Control("QUOTES", PolicyEffectType.RequireQuotations, "PRE_PROCUREMENT"));

        var target = Assert.Single(submission.Prerequisites[0].Targets);
        Assert.Equal("PURCHASE_REQUEST_LINE", target.Type);
        Assert.Equal(LineOne, target.Id);
        Assert.Equal(7, target.Version);
        Assert.Equal(new string('d', 64), target.MaterialSnapshotDigest);
        Assert.Equal(
            new string('d', 64), Material(LineOne, version: 7, digest: new string('d', 64))[LineOne].MaterialSnapshotDigest);
    }

    [Fact]
    public void A_line_without_material_projection_fails_closed()
    {
        var control = Control("QUOTES", PolicyEffectType.RequireQuotations, "PRE_PROCUREMENT",
            extraSubject: LineTwo);
        Assert.Throws<ApprovalDependencyUnavailableException>(() => PolicyApprovalAdapter.Map(
            Bundle([control]), Material(LineOne), Record(), Request()));
    }

    [Fact]
    public void Prerequisite_parameters_preserve_every_policy_parameter()
    {
        var budget = Control("BUDGET", PolicyEffectType.RequireBudgetCheck, "PRE_PROCUREMENT") with
        {
            AmountBase = 500,
            BaseCurrency = "PEN",
            CostCenterIds = ImmutableHashSet.Create(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"))
        };
        var quotations = Control("QUOTES", PolicyEffectType.RequireQuotations, "PRE_PROCUREMENT") with
        {
            MinimumQuotations = 3,
            MinimumAllowedQuotations = 1
        };
        var documents = Control("DOCS", PolicyEffectType.RequireSupportingDocument, "PRE_PROCUREMENT") with
        {
            SupportingDocumentTypes = ImmutableHashSet.Create("QUOTE", "CONTRACT")
        };

        var submission = Map(budget, quotations, documents);
        var parameters = submission.Prerequisites.ToDictionary(
            prerequisite => prerequisite.Key, prerequisite => prerequisite.ParametersJson, StringComparer.Ordinal);

        Assert.Contains("\"amount_base\":\"500\"", parameters["BUDGET"], StringComparison.Ordinal);
        Assert.Contains("\"base_currency\":\"PEN\"", parameters["BUDGET"], StringComparison.Ordinal);
        Assert.Contains("\"minimum_allowed_quotations\":1", parameters["QUOTES"], StringComparison.Ordinal);
        Assert.Contains("\"minimum_quotations\":3", parameters["QUOTES"], StringComparison.Ordinal);
        Assert.Contains("\"minimum_count\":1", parameters["DOCS"], StringComparison.Ordinal);
        Assert.Contains("CONTRACT", parameters["DOCS"], StringComparison.Ordinal);
    }

    private static ApprovalSubmission Map(params PolicyGeneratedControl[] controls) =>
        PolicyApprovalAdapter.Map(Bundle(controls), Material(LineOne), Record(), Request());

    private static IReadOnlyDictionary<Guid, ApprovalTarget> Material(
        Guid line,
        int version = 7,
        string? digest = null) =>
        new Dictionary<Guid, ApprovalTarget>
        {
            [line] = new ApprovalTarget("PURCHASE_REQUEST_LINE", line, version, digest ?? new string('d', 64))
        };

    private static PolicyEvaluationBundle Bundle(
        IReadOnlyList<PolicyGeneratedControl> controls,
        PolicyResult result = PolicyResult.RequirementsGenerated) => new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "adapter-eval",
        new PolicySubjectReference(Guid.Parse("22222222-2222-2222-2222-222222222222"), 3),
        DateTimeOffset.Parse("2026-09-13T12:00:00.0000000Z"),
        new string('b', 64),
        new string('a', 64),
        [],
        controls,
        result,
        new string('c', 64))
    {
        Operation = "PURCHASE_REQUEST",
        ManifestDigest = new string('9', 64),
        MaterialProjection = new PolicyMaterialProjection(
            PolicyApprovalTargets.ContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal),
            [new PolicyMaterialTarget(LineOne, 7, new string('d', 64))])
    };

    private static PolicyEvaluationBundleRecord Record() => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        OrganizationId = OrganizationId,
        EvaluationKey = "adapter-eval",
        Operation = "PURCHASE_REQUEST",
        EvaluationSequence = 1,
        SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        SubjectVersion = 3,
        PolicySetVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        PolicyContentDigest = new string('b', 64),
        InputDigest = new string('a', 64),
        Result = "REQUIREMENTS_GENERATED",
        ResultDigest = new string('c', 64),
        BundleJson = "{}",
        IdempotencyFingerprint = new string('e', 64),
        CorrelationReference = "corr"
    };

    private static ApprovalSubmissionRequest Request() => new(
        OrganizationId,
        PolicyApprovalAdapter.SubjectType,
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        3,
        PolicyApprovalAdapter.Operation,
        PolicyApprovalAdapter.ContractVersion,
        "submission-1",
        RequesterId,
        OriginatorId,
        "corr-1");

    private static PolicyGeneratedControl Control(
        string key,
        PolicyEffectType type,
        string phase,
        PolicyApprovalDescriptor? approval = null,
        Guid? subject = null,
        Guid? extraSubject = null)
    {
        var subjects = ImmutableHashSet.Create(subject ?? LineOne);
        if (extraSubject is Guid second)
        {
            subjects = subjects.Add(second);
        }

        return new PolicyGeneratedControl(
            key,
            type,
            ImmutableHashSet.Create(PolicyScope.Line),
            subjects,
            phase,
            approval,
            null,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("RULE:1"),
            "reason");
    }

    private static PolicyApprovalDescriptor ApprovalDescriptor(
        SystemRole role = SystemRole.DepartmentApprover,
        ApprovalAuthorityType type = ApprovalAuthorityType.BusinessNeed) =>
        new(
            role,
            type,
            new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "LEVEL", 1),
            500,
            "PEN",
            OrganizationScope());

    private static string OrganizationScope() =>
        DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]).ToCanonicalJson();

    private static ApprovalTarget MaterialTarget(Guid line) =>
        new("PURCHASE_REQUEST_LINE", line, 7, new string('d', 64));
}
