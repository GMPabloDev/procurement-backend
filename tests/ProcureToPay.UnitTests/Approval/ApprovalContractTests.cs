using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Canonical evidence, typed decision scope, submission rules and limit enforcement
/// (REQ-01, REQ-02, REQ-05, REQ-08, REQ-09).
/// </summary>
public sealed class ApprovalContractTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Canonical_json_normalizes_strings_ids_timestamps_and_sets()
    {
        var digests = new[]
        {
            ApprovalCanonicalJson.String("cafe\u0301"),
            ApprovalCanonicalJson.String("café")
        };
        Assert.Equal(digests[0], digests[1]);

        Assert.Equal(
            "\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(
                Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))));

        Assert.Equal(
            "\"2026-09-11T12:00:00.0000000Z\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(
                new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero))));

        Assert.Equal(
            "1.25",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Number(1.2500m)));

        var ordered = ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Set(
            [ApprovalCanonicalJson.String("b"), ApprovalCanonicalJson.String("a")]));
        Assert.Equal("[\"a\",\"b\"]", ordered);

        Assert.Throws<DomainConflictException>(() => ApprovalCanonicalJson.Set(
            [ApprovalCanonicalJson.String("a"), ApprovalCanonicalJson.String("a")]));

        var sortedObject = ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("zeta", ApprovalCanonicalJson.Number(1)),
            ("alpha", ApprovalCanonicalJson.Number(2))));
        Assert.Equal("{\"alpha\":2,\"zeta\":1}", sortedObject);

        Assert.Throws<DomainConflictException>(() => ApprovalCanonicalJson.Object(
            ("same", ApprovalCanonicalJson.Null()),
            ("same", ApprovalCanonicalJson.Null())));
    }

    [Fact]
    public void Decision_scope_descriptor_is_an_exact_canonical_contract()
    {
        var departmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var descriptor = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, 3)]);
        var json = descriptor.ToCanonicalJson();

        Assert.Equal(
            "{\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"schema_version\":\"decision-scope/v1\"," +
            "\"scopes\":[{\"dimension\":\"DEPARTMENT\"," +
            "\"reference_id\":\"22222222-2222-2222-2222-222222222222\",\"reference_version\":3}]}",
            json);
        Assert.Equal(json, DecisionScopeDescriptor.Parse(json, OrganizationId).ToCanonicalJson());
        Assert.Equal(64, descriptor.Digest().Length);
    }

    [Theory]
    [InlineData("ORGANIZATION")]
    [InlineData("COST_CENTER")]
    [InlineData("legal_entity")]
    public void Legacy_scope_tokens_are_rejected(string token)
    {
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Parse(token));
    }

    [Fact]
    public void Scope_descriptor_rejects_non_canonical_extra_fields_and_invalid_combinations()
    {
        var departmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var canonical = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, 1)]).ToCanonicalJson();

        // Non-canonical whitespace and unknown fields are both rejected.
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Parse($" {canonical}"));
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Parse(
            canonical[..^1] + ",\"extra\":1}"));
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Parse(
            canonical, Guid.Parse("33333333-3333-3333-3333-333333333333")));

        // SPEC 07 REQ-05: COST_CENTER is now a valid canonical dimension that keeps the frozen
        // UUID/version pair; a missing reference id or version is still invalid.
        var costCenter = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.CostCenter, departmentId, 2)]);
        Assert.Equal(
            costCenter.ToCanonicalJson(),
            DecisionScopeDescriptor.Parse(costCenter.ToCanonicalJson(), OrganizationId).ToCanonicalJson());
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.CostCenter, departmentId, null)]));
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.CostCenter, null, 1)]));

        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Create(
            OrganizationId,
            [
                new DecisionScopeEntry(ScopeDimension.Organization, null, null),
                new DecisionScopeEntry(ScopeDimension.Department, departmentId, 1)
            ]));
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, null)]));
        Assert.Throws<DomainValidationException>(() => DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.LegalEntity, null, 1)]));
    }

    [Fact]
    public void Scope_descriptor_maps_to_authorization_scopes_from_the_catalog()
    {
        var departmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var descriptor = DecisionScopeDescriptor.Create(
            OrganizationId,
            [new DecisionScopeEntry(ScopeDimension.Department, departmentId, 1)]);

        var scopeSet = descriptor.ToAuthorizationScopeSet(_ => "FIN");

        Assert.Equal(ScopeDimension.Department, scopeSet.Scopes.Single().Dimension);
        Assert.Equal("FIN", scopeSet.Scopes.Single().Reference);
    }

    [Fact]
    public void Submission_rules_reject_ambiguous_grouping_and_missing_actor_exclusions()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");

        // Each class of key is unique within the case (Datos y contratos): an adapter cannot
        // split one source requirement into several workflow requirements.
        var partitioned = BuildSubmission(
            originator,
            requirementExclusions: [originator],
            extraRequirement: BuildRequirement("REQ-A", originator, "FINANCE", [originator],
                [Target("LINE", Guid.NewGuid())]));
        Assert.Throws<DomainConflictException>(() =>
            ApprovalSubmissionRules.Validate(partitioned, adapter, workload));

        // SoD is per requirement: excluding the originator in one requirement is not enough.
        var partialExclusion = BuildSubmission(
            originator,
            requirementExclusions: [originator],
            extraRequirement: BuildRequirement("REQ-B", originator, "FINANCE", [],
                [Target("LINE", Guid.NewGuid())]));
        Assert.Throws<DomainValidationException>(() =>
            ApprovalSubmissionRules.Validate(partialExclusion, adapter, workload));

        // originator_id must be excluded everywhere.
        var missingExclusion = BuildSubmission(
            originator,
            requirementExclusions: [],
            extraRequirement: null);
        Assert.Throws<DomainValidationException>(() =>
            ApprovalSubmissionRules.Validate(missingExclusion, adapter, workload));

        // A requester must be excluded as well and cannot equal the originator.
        var requesterNotExcluded = BuildSubmission(
            originator,
            requirementExclusions: [originator],
            extraRequirement: null,
            requesterId: Guid.NewGuid());
        Assert.Throws<DomainValidationException>(() =>
            ApprovalSubmissionRules.Validate(requesterNotExcluded, adapter, workload));
    }

    [Fact]
    public void Submission_rules_reject_duplicated_actions_exclusions_targets_and_prerequisite_keys()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");
        var target = Target("LINE", Guid.NewGuid());

        // Duplicating a target inside one entity is rejected before any collapsing (REQ-09).
        Assert.Throws<DomainConflictException>(() =>
            BuildSubmissionWith(
                originator,
                BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator], [target, target]),
                null));

        // Duplicated actions are rejected on the original sequence.
        Assert.Throws<DomainConflictException>(() => new ApprovalRequirementDefinition(
            "REQ-A", "DEPARTMENT", SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Approve],
            [originator],
            [target],
            []));

        // Duplicated exclusions are rejected on the original sequence.
        Assert.Throws<DomainConflictException>(() => new ApprovalRequirementDefinition(
            "REQ-A", "DEPARTMENT", SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve],
            [originator, originator],
            [target],
            []));

        // A prerequisite cannot repeat targets either.
        Assert.Throws<DomainConflictException>(() => new ExternalPrerequisiteDefinition(
            "PRQ", "adapter", "v1", "BUDGET_CHECK", new string('b', 64), "{}", [target, target]));

        // A prerequisite key cannot repeat within the case.
        var duplicatedPrerequisite = new ApprovalSubmission(
            "key", OrganizationId, "PURCHASE_REQUEST", Guid.NewGuid(), 1, "SUBMIT",
            new string('a', 64), null, originator,
            [BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator], [target])],
            [
                new ExternalPrerequisiteDefinition("PRQ", "adapter", "v1", "BUDGET_CHECK", new string('b', 64), "{}", [target]),
                new ExternalPrerequisiteDefinition("PRQ", "adapter", "v1", "RISK_CHECK", new string('b', 64), "{}", [target])
            ]);
        Assert.Throws<DomainConflictException>(() =>
            ApprovalSubmissionRules.Validate(duplicatedPrerequisite, adapter, workload));

        // A dependency edge cannot repeat targets either: its set is canonical (REQ-08).
        Assert.Throws<DomainConflictException>(() => new ApprovalDependencyRef(
            DependencyPredecessorKind.Approval, "REQ-A", [target, target]));
    }

    [Fact]
    public void Submission_rules_reject_dangling_cycles_and_non_contained_edges()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");
        var sharedTarget = Target("LINE", Guid.NewGuid());

        var danglingFirst = BuildRequirement(
            "REQ-A", originator, "DEPARTMENT", [originator], [sharedTarget],
            [new ApprovalDependencyRef(DependencyPredecessorKind.Approval, "MISSING", [sharedTarget])]);
        var dangling = BuildSubmissionWith(originator, danglingFirst, null);
        Assert.Throws<DomainValidationException>(() =>
            ApprovalSubmissionRules.Validate(dangling, adapter, workload));

        var cycleFirst = BuildRequirement(
            "REQ-A", originator, "DEPARTMENT", [originator], [sharedTarget],
            [new ApprovalDependencyRef(DependencyPredecessorKind.Approval, "REQ-B", [sharedTarget])]);
        var cycleSecond = BuildRequirement(
            "REQ-B", originator, "FINANCE", [originator], [sharedTarget],
            [new ApprovalDependencyRef(DependencyPredecessorKind.Approval, "REQ-A", [sharedTarget])]);
        var cyclic = BuildSubmissionWith(originator, cycleFirst, cycleSecond);
        Assert.Throws<DomainConflictException>(() =>
            ApprovalSubmissionRules.Validate(cyclic, adapter, workload));

        var foreignTarget = Target("LINE", Guid.NewGuid());
        var nonContained = BuildSubmissionWithPrerequisite(
            originator,
            BuildRequirement(
                "REQ-A", originator, "DEPARTMENT", [originator], [sharedTarget],
                [new ApprovalDependencyRef(DependencyPredecessorKind.External, "BUDGET", [foreignTarget])]),
            new[] { ("BUDGET", sharedTarget) });
        Assert.Throws<DomainValidationException>(() =>
            ApprovalSubmissionRules.Validate(nonContained, adapter, workload));
    }

    [Fact]
    public void Submission_rules_enforce_requirement_and_target_limits()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");

        var tooMany = new ApprovalSubmission(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            Enumerable.Range(0, ApprovalLimits.MaxRequirementsPerCase + 1)
                .Select(index => BuildRequirement(
                    $"REQ-{index}", originator, "DEPARTMENT", [originator], [Target("LINE", Guid.NewGuid())]))
                .ToArray(),
            []);
        Assert.Throws<ApprovalPayloadTooLargeException>(() =>
            ApprovalSubmissionRules.Validate(tooMany, adapter, workload));

        var tooManyLinks = new ApprovalSubmission(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            Enumerable.Range(0, 1_001)
                .Select(index => BuildRequirement(
                    $"REQ-{index}", originator, "DEPARTMENT", [originator],
                    Enumerable.Range(0, 10).Select(_ => Target("LINE", Guid.NewGuid())).ToArray()))
                .ToArray(),
            []);
        Assert.Throws<ApprovalPayloadTooLargeException>(() =>
            ApprovalSubmissionRules.Validate(tooManyLinks, adapter, workload));
    }

    [Fact]
    public void Submission_rules_accept_exactly_the_contractual_count_limits()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");

        // Exactly 2000 requirements and exactly 10000 target links: both limits are inclusive.
        var atLimit = new ApprovalSubmission(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            Enumerable.Range(0, ApprovalLimits.MaxRequirementsPerCase)
                .Select(index => BuildRequirement(
                    $"REQ-{index}", originator, "DEPARTMENT", [originator],
                    Enumerable.Range(0, 5).Select(_ => Target("LINE", Guid.NewGuid())).ToArray()))
                .ToArray(),
            []);
        Assert.Equal(
            ApprovalLimits.MaxRequirementsPerCase * 5,
            atLimit.Requirements.Sum(requirement => requirement.Targets.Count));
        ApprovalSubmissionRules.Validate(atLimit, adapter, workload);
    }

    [Fact]
    public void Canonical_size_limit_is_enforced_at_the_exact_byte()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");

        ApprovalSubmission WithPadding(int padding) => new(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            [BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator], [Target("LINE", Guid.NewGuid())])],
            [new ExternalPrerequisiteDefinition(
                "BUDGET_CHECK", "budget-adapter", "v1", "BUDGET_CHECK", new string('d', 64),
                new string('p', padding), [Target("LINE", Guid.NewGuid())])]);

        // The padding is stored verbatim in the canonical preimage, so the fixed overhead is
        // the difference between any measured size and its padding length.
        const int Probe = 1_024;
        var overhead = System.Text.Encoding.UTF8.GetByteCount(ApprovalCanonicalJson.Serialize(
            ApprovalSubmissionRules.CommandValue(WithPadding(Probe), adapter, workload))) - Probe;
        Assert.True(overhead > 0);

        ApprovalSubmissionRules.Validate(
            WithPadding(ApprovalLimits.MaxCanonicalCommandBytes - overhead), adapter, workload);
        Assert.Throws<ApprovalPayloadTooLargeException>(() => ApprovalSubmissionRules.Validate(
            WithPadding(ApprovalLimits.MaxCanonicalCommandBytes - overhead + 1), adapter, workload));
    }

    [Fact]
    public void Submission_fingerprint_is_stable_under_permutation_and_sensitive_to_content()
    {
        var originator = Guid.NewGuid();
        var adapter = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");
        var first = Target("LINE", Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var second = Target("LINE", Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var subjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var left = BuildSubmissionWith(
            originator,
            BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator], [first, second]),
            null,
            subjectId);
        var right = BuildSubmissionWith(
            originator,
            BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator], [second, first]),
            null,
            subjectId);

        var leftFingerprint = ApprovalSubmissionRules.SubmissionFingerprint(left, adapter, workload);
        Assert.Equal(leftFingerprint, ApprovalSubmissionRules.SubmissionFingerprint(right, adapter, workload));

        var changedStage = BuildSubmissionWith(
            originator,
            BuildRequirement("REQ-A", originator, "FINANCE", [originator], [first, second]),
            null,
            subjectId);
        Assert.NotEqual(leftFingerprint, ApprovalSubmissionRules.SubmissionFingerprint(changedStage, adapter, workload));

        // A different target identity changes the fingerprint while the set stays equivalent.
        var differentTarget = BuildSubmissionWith(
            originator,
            BuildRequirement("REQ-A", originator, "DEPARTMENT", [originator],
                [Target("LINE", Guid.Parse("66666666-6666-6666-6666-666666666666"))]),
            null,
            subjectId);
        Assert.NotEqual(leftFingerprint, ApprovalSubmissionRules.SubmissionFingerprint(differentTarget, adapter, workload));
    }

    [Fact]
    public void Decision_and_signal_fingerprints_are_canonical_and_deterministic()
    {
        var target = Target("LINE", Guid.NewGuid());
        var caseId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var decision = ApprovalFingerprints.DecisionFingerprint(
            caseId, requirementId, taskId, 1, 1, [target],
            ApprovalDecisionAction.Approve, "aprobado", actorId);
        Assert.Equal(64, decision.Length);
        Assert.Equal(
            decision,
            ApprovalFingerprints.DecisionFingerprint(
                caseId, requirementId, taskId, 1, 1, [target],
                ApprovalDecisionAction.Approve, "aprobado", actorId));
        Assert.NotEqual(
            decision,
            ApprovalFingerprints.DecisionFingerprint(
                caseId, requirementId, taskId, 1, 1, [target],
                ApprovalDecisionAction.Reject, "aprobado", actorId));

        var prerequisiteId = Guid.NewGuid();
        var workload = new ApprovalWorkloadIdentity("issuer", "adapter");
        var signal = ApprovalFingerprints.SignalFingerprint(
            prerequisiteId, 1, workload, true, "evidence", new string('b', 64), "signal-1");
        Assert.Equal(64, signal.Length);
        Assert.Equal(
            signal,
            ApprovalFingerprints.SignalFingerprint(
                prerequisiteId, 1, workload, true, "evidence", new string('b', 64), "signal-1"));
        Assert.NotEqual(
            signal,
            ApprovalFingerprints.SignalFingerprint(
                prerequisiteId, 1, workload, false, "evidence", new string('b', 64), "signal-1"));

        var authority = ApprovalFingerprints.AuthorityEvidenceDigest("{\"evidence\":true}");
        Assert.Equal(64, authority.Length);
        Assert.Throws<DomainValidationException>(() =>
            ApprovalFingerprints.AuthorityEvidenceDigest(string.Empty));
    }

    [Fact]
    public void Requester_as_originator_is_admitted_only_by_an_adapter_that_declares_it()
    {
        var originator = Guid.NewGuid();
        var workload = new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter");
        var v2 = new ApprovalAdapterDescriptor(
            "policy-approval-adapter", "PURCHASE_REQUEST", "SUBMIT", "v2",
            RequesterRequired: true, AllowsRequesterAsOriginator: true, SupersessionDeltaSupported: true);
        var v1 = new ApprovalAdapterDescriptor("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

        // SPEC 06 REQ-07: the requester creates and presents, so the v2 contract admits the same
        // identity once in the exclusion set and still keeps every decision action available.
        var submission = BuildSubmission(originator, [originator], null, requesterId: originator);
        ApprovalSubmissionRules.Validate(submission, v2, workload);
        var requirement = submission.Requirements.Single();
        Assert.Single(requirement.ExcludedUserIds, id => id == originator);
        Assert.Contains(ApprovalDecisionAction.RequestChanges, requirement.Actions);

        // Every other contract keeps rejecting the same actor, so the relaxation is adapter-scoped.
        Assert.Throws<DomainValidationException>(() => ApprovalSubmissionRules.Validate(submission, v1, workload));
        Assert.True(v2.RequesterRequired);
    }

    [Fact]
    public void Adapter_registry_resolves_the_declared_contract_version_exactly_one()
    {
        var registry = new ApprovalSubmissionAdapterRegistry(
            [new StubAdapter("v1"), new StubAdapter("v2")]);
        Assert.Equal("v1", registry.ResolveExactlyOne("PURCHASE_REQUEST", "SUBMIT", "v1").Descriptor.ContractVersion);
        Assert.Equal("v2", registry.ResolveExactlyOne("PURCHASE_REQUEST", "SUBMIT", "v2").Descriptor.ContractVersion);

        // Without a version the resolution is ambiguous and fails closed (503).
        Assert.Throws<ApprovalDependencyUnavailableException>(() =>
            registry.ResolveExactlyOne("PURCHASE_REQUEST", "SUBMIT", null));
        Assert.Throws<ApprovalDependencyUnavailableException>(() =>
            registry.ResolveExactlyOne("PURCHASE_REQUEST", "SUBMIT", "v9"));
    }

    private sealed class StubAdapter(string contractVersion) : IApprovalSubmissionAdapter
    {
        public ApprovalAdapterDescriptor Descriptor { get; } = new(
            "adapter", "PURCHASE_REQUEST", "SUBMIT", contractVersion, false);

        public Task<ApprovalSubmission> BuildAsync(
            ApprovalSubmissionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ApprovalTarget Target(string type, Guid id) => new(type, id, 1, new string('c', 64));

    private static ApprovalRequirementDefinition BuildRequirement(
        string sourceKey,
        Guid originator,
        string stageCode,
        IEnumerable<Guid> exclusions,
        IEnumerable<ApprovalTarget> targets,
        IEnumerable<ApprovalDependencyRef>? dependencies = null) =>
        new(
            sourceKey,
            stageCode,
            SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            DecisionScopeDescriptor.Create(OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject, ApprovalDecisionAction.RequestChanges],
            exclusions,
            targets,
            dependencies ?? []);

    private static ApprovalSubmission BuildSubmission(
        Guid originator,
        IEnumerable<Guid> requirementExclusions,
        ApprovalRequirementDefinition? extraRequirement,
        Guid? requesterId = null)
    {
        var baseRequirement = BuildRequirement(
            "REQ-A", originator, "DEPARTMENT", requirementExclusions, [Target("LINE", Guid.NewGuid())],
            []);
        var requirements = extraRequirement is null
            ? new[] { baseRequirement }
            : [baseRequirement, extraRequirement];
        return new ApprovalSubmission(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            requesterId,
            originator,
            requirements,
            []);
    }

    private static ApprovalSubmission BuildSubmissionWith(
        Guid originator,
        ApprovalRequirementDefinition first,
        ApprovalRequirementDefinition? second,
        Guid? subjectId = null) =>
        new(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            subjectId ?? Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            second is null ? [first] : [first, second],
            []);

    private static ApprovalSubmission BuildSubmissionWithPrerequisite(
        Guid originator,
        ApprovalRequirementDefinition first,
        (string Key, ApprovalTarget Target)[] prerequisites) =>
        new(
            "key",
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.NewGuid(),
            1,
            "SUBMIT",
            new string('a', 64),
            null,
            originator,
            [first],
            prerequisites.Select(item => new ExternalPrerequisiteDefinition(
                item.Key, "budget-adapter", "v1", "BUDGET_CHECK", new string('d', 64),
                "{\"minimum\":1}", [item.Target])).ToArray());
}
