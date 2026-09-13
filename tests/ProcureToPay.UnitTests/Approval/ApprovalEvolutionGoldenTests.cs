using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Contractual golden vectors of SPEC 04 (Datos y contratos, CA-01, CA-04, CA-05, CA-06): the
/// bytes of every new canonical preimage and of the <c>approval-result/v3</c> derivation, plus the
/// exact property surface of the lifecycle and evidence-revoked events.
/// </summary>
public sealed class ApprovalEvolutionGoldenTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Delegator = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Delegatee = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid EvidenceId = Guid.Parse("99999999-9999-9999-9999-999999999991");
    private static readonly ApprovalTarget Target =
        new("PURCHASE_REQUEST", Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), 1, new string('c', 64));

    [Fact]
    public void Delegation_fingerprint_matches_the_contractual_vector()
    {
        var scope = DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);

        var fingerprint = ApprovalFingerprints.DelegationFingerprint(
            ApprovalFingerprints.DelegationActionCreate,
            ApprovalDelegationActorType.Delegator,
            Delegator,
            Delegator,
            Delegatee,
            SystemRole.DepartmentApprover,
            scope.ToCanonicalValue(),
            "delegation-1",
            delegationId: null,
            expectedVersion: null,
            OrganizationId,
            "Vacation coverage",
            DateTimeOffset.Parse("2026-10-01T00:00:00.0000000Z"),
            DateTimeOffset.Parse("2026-10-08T00:00:00.0000000Z"));

        Assert.Equal("b13f4ba2f009dd886bb3208d58aad5990e7181673acd4fc885d5943e063373e8", fingerprint);
    }

    [Fact]
    public void Supersession_fingerprint_matches_the_contractual_vector()
    {
        var mapping = new[]
        {
            ApprovalSupersessionMapping.Create(Target, Target, "purchase-request-materiality/v1", new string('f', 64))
        };

        var fingerprint = ApprovalFingerprints.SupersessionFingerprint(
            new string('e', 64),
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            3,
            "supersession-1",
            mapping);

        Assert.Equal("ac8007676e8f81ba4fc580b8f5c03e9683b1573f475d6d1204192a792823485c", fingerprint);
    }

    [Fact]
    public void Revocation_fingerprint_matches_the_contractual_vector()
    {
        var fingerprint = ApprovalFingerprints.RevocationFingerprint(
            EvidenceRevocationActorType.Workload,
            actorUserId: null,
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter"),
            EvidenceId,
            1,
            OrganizationId,
            "Subject binding invalidated",
            "revocation-1");

        Assert.Equal("19da7c45c42adb324b54bf7e45a787dcd640054ddc31c42988c31ecf1cd13be9", fingerprint);
    }

    [Fact]
    public void Carry_forward_proof_matches_the_contractual_vector()
    {
        var proof = ApprovalFingerprints.CarryForwardProof(
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            new string('a', 64),
            "WR-00000000000000000000000000000001",
            Target,
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            new string('b', 64),
            Guid.Parse("66666666-6666-6666-6666-666666666667"),
            EvidenceId,
            1,
            new string('a', 64),
            Target);

        Assert.Equal("388ed3c277ece39f56dbf3ae51ae705e4ff257f0e743029a02c3bea76fe0736c", proof);
    }

    [Fact]
    public void Carry_forward_decision_digest_matches_the_contractual_vector()
    {
        var digest = ApprovalFingerprints.CarryForwardDecisionDigest(
            Guid.Parse("66666666-6666-6666-6666-666666666668"),
            1,
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            "PURCHASE_REQUEST",
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            2,
            new string('a', 64),
            "WR-00000000000000000000000000000001",
            new string('d', 64),
            [Target],
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
            "93003e10ecf62a0d795474dc38ea2da5baaf12445c2cedb5ad63741845355850",
            "388ed3c277ece39f56dbf3ae51ae705e4ff257f0e743029a02c3bea76fe0736c",
            EvidenceId,
            1,
            Guid.Parse("66666666-6666-6666-6666-666666666667"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            [Delegatee]);

        Assert.Equal("b0e6b1f008ece288da011247f93d377d3a74ce19bc58d16e9e13db8da18445a4", digest);
    }

    [Fact]
    public void Legacy_authority_evidence_digest_is_unchanged()
    {
        const string evidence =
            "{\"evaluated_at\":\"2026-03-01T12:00:00.0000000Z\",\"user_id\":\"77777777-7777-7777-7777-777777777777\"}";

        Assert.Equal(
            "93003e10ecf62a0d795474dc38ea2da5baaf12445c2cedb5ad63741845355850",
            ApprovalFingerprints.AuthorityEvidenceDigest(evidence));
        Assert.Equal(
            "93003e10ecf62a0d795474dc38ea2da5baaf12445c2cedb5ad63741845355850",
            ApprovalFingerprints.AuthorityEvidenceDigest(evidence, delegationId: null, delegationVersion: null));
    }

    [Fact]
    public void Delegation_evidence_digest_fixes_the_real_delegation_identity()
    {
        const string evidence =
            "{\"evaluated_at\":\"2026-03-01T12:00:00.0000000Z\",\"user_id\":\"77777777-7777-7777-7777-777777777777\"}";
        var delegationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var digest = ApprovalFingerprints.AuthorityEvidenceDigest(evidence, delegationId, 3);

        Assert.NotEqual(ApprovalFingerprints.AuthorityEvidenceDigest(evidence), digest);
        Assert.Equal(
            digest,
            ApprovalFingerprints.AuthorityEvidenceDigest(evidence, delegationId, 3));
    }

    [Fact]
    public void Carry_forward_decision_is_a_system_taskless_derivation()
    {
        var decision = ApprovalDecision.CreateCarryForward(
            Guid.Parse("66666666-6666-6666-6666-666666666668"),
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            OrganizationId,
            Guid.Parse("33333333-3333-3333-3333-333333333334"),
            "CARRY_FORWARD",
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
            new string('b', 64),
            new string('a', 64),
            "{}",
            EvidenceId,
            1,
            Guid.Parse("66666666-6666-6666-6666-666666666667"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            [Target],
            "correlation");

        Assert.Equal(ApprovalDecisionActorType.System, decision.ActorType);
        Assert.Equal(ApprovalDecisionOrigin.CarryForward, decision.Origin);
        Assert.Equal(ApprovalDecisionAction.Approve, decision.Action);
        Assert.Null(decision.ActorUserId);
        Assert.Null(decision.TaskId);
        Assert.Null(decision.DecisionKey);
        Assert.Null(decision.Fingerprint);
        Assert.Equal(decision.SourceDecisionId, Guid.Parse("66666666-6666-6666-6666-666666666667"));
        Assert.Equal(decision.RootHumanDecisionId, Guid.Parse("66666666-6666-6666-6666-666666666666"));
    }

    [Fact]
    public void Approval_result_v3_payload_matches_the_contractual_bytes()
    {
        var approvalCase = RestoredCase();

        var payload = ApprovalOutboxEvent.BuildPayload(
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            ApprovalEvolutionCodes.ResultContractVersionV3,
            approvalCase,
            ApprovalEntitySource.Requirement(
                Guid.Parse("33333333-3333-3333-3333-333333333334"),
                "WR-00000000000000000000000000000001"),
            Target,
            "APPROVED",
            Guid.Parse("66666666-6666-6666-6666-666666666668"),
            "b0e6b1f008ece288da011247f93d377d3a74ce19bc58d16e9e13db8da18445a4",
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"));

        Assert.Equal(
            "{\"case_id\":\"22222222-2222-2222-2222-222222222223\",\"contract_version\":\"approval-result/v3\"," +
            "\"decision_digest\":\"b0e6b1f008ece288da011247f93d377d3a74ce19bc58d16e9e13db8da18445a4\"," +
            "\"decision_id\":\"66666666-6666-6666-6666-666666666668\",\"event_id\":\"12121212-1212-1212-1212-121212121212\"," +
            "\"occurred_at\":\"2026-10-01T12:00:00.0000000Z\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"result\":\"APPROVED\",\"result_source\":{\"id\":\"33333333-3333-3333-3333-333333333334\"," +
            "\"key\":\"WR-00000000000000000000000000000001\",\"type\":\"APPROVAL_REQUIREMENT\"}," +
            "\"subject_id\":\"99999999-9999-9999-9999-999999999999\",\"subject_type\":\"PURCHASE_REQUEST\"," +
            "\"subject_version\":2,\"target\":{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\"," +
            "\"material_snapshot_digest\":\"" + new string('c', 64) + "\"," +
            "\"type\":\"PURCHASE_REQUEST\",\"version\":1}}",
            payload);
    }

    [Fact]
    public void Evidence_revoked_payload_has_its_exact_properties()
    {
        var payload = ApprovalEvolutionEvents.BuildEvidenceRevokedPayload(
            Guid.Parse("13131313-1313-1313-1313-131313131313"),
            ApprovalEvolutionCodes.EvidenceRevokedContractVersion,
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            OrganizationId,
            Guid.Parse("66666666-6666-6666-6666-666666666668"),
            EvidenceId,
            1,
            "WORKLOAD",
            "OWNER_INVALIDATION",
            Guid.Parse("14141414-1414-1414-1414-141414141414"),
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"));

        Assert.Equal(
            "{\"actor_type\":\"WORKLOAD\",\"case_id\":\"22222222-2222-2222-2222-222222222223\"," +
            "\"contract_version\":\"approval-evidence-revoked/v1\",\"decision_id\":\"66666666-6666-6666-6666-666666666668\"," +
            "\"event_id\":\"13131313-1313-1313-1313-131313131313\",\"evidence_id\":\"" + EvidenceId + "\"," +
            "\"evidence_version\":1,\"occurred_at\":\"2026-10-01T12:00:00.0000000Z\"," +
            "\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"reason_code\":\"OWNER_INVALIDATION\"," +
            "\"revocation_id\":\"14141414-1414-1414-1414-141414141414\"}",
            payload);
    }

    [Fact]
    public void Lifecycle_payload_has_its_exact_properties()
    {
        var payload = ApprovalEvolutionEvents.BuildLifecyclePayload(
            Guid.Parse("15151515-1515-1515-1515-151515151515"),
            ApprovalEvolutionCodes.LifecycleContractVersion,
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            OrganizationId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "PURCHASE_REQUEST",
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            2,
            Target,
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"));

        Assert.Equal(
            "{\"case_id\":\"22222222-2222-2222-2222-222222222223\",\"contract_version\":\"approval-case-lifecycle/v1\"," +
            "\"event_id\":\"15151515-1515-1515-1515-151515151515\",\"occurred_at\":\"2026-10-01T12:00:00.0000000Z\"," +
            "\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"previous_case_id\":\"22222222-2222-2222-2222-222222222222\",\"result\":\"SUPERSEDED\"," +
            "\"subject_id\":\"99999999-9999-9999-9999-999999999999\",\"subject_type\":\"PURCHASE_REQUEST\"," +
            "\"subject_version\":2,\"target\":{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\"," +
            "\"material_snapshot_digest\":\"" + new string('c', 64) + "\"," +
            "\"type\":\"PURCHASE_REQUEST\",\"version\":1}}",
            payload);
    }

    internal static ApprovalCase RestoredCase()
    {
        var scope = DecisionScopeDescriptor.Create(
            OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);
        var requirement = ApprovalRequirement.Restore(
            Guid.Parse("33333333-3333-3333-3333-333333333334"),
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            "DEPARTMENT_REQ",
            "WR-00000000000000000000000000000001",
            "DEPARTMENT",
            SystemRole.DepartmentApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
            scope.ToCanonicalJson(),
            [Delegatee],
            [ApprovalDecisionAction.Approve],
            [Target],
            [],
            ApprovalRequirementStatus.Approved,
            3);
        return ApprovalCase.Restore(
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            OrganizationId,
            "PURCHASE_REQUEST",
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            2,
            "SUBMIT",
            new string('a', 64),
            new ApprovalWorkloadIdentity("internal://procure-to-pay", "adapter"),
            "submission-1",
            new string('e', 64),
            Delegatee,
            null,
            DateTimeOffset.Parse("2026-03-01T12:00:00.0000000Z"),
            "correlation",
            ApprovalCaseStatus.Open,
            4,
            null,
            null,
            [requirement],
            [],
            []);
    }
}
