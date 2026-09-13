using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Contractual golden vectors of SPEC 05 (CA-07): the three own vectors published in
/// <c>specs/05-integracion-policy-approval-workflow.md</c> must be reproduced byte for byte.
/// </summary>
public sealed class PolicyExceptionGoldenTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BaseBundleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PolicyVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid FirstLineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SecondLineId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid RequesterId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OriginatorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid WorkloadSubjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ReferenceId = Guid.Parse("eeeeeeee-1111-1111-1111-111111111111");
    private static readonly Guid CaseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RequirementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid DecisionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ApproverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private const string MaterialDigest = "7ff665cae24dfb390c58c1e784c83ef9f5781f8966f387beb9e691b2734477de";
    private const string BindingDigest = "4e03bdf6961a2576a4027d303b78931748883508e253dc6badb944a065fd22b5";
    private const string EvidenceDigest = "43eba6c5443c410c6d12a5afc589734466e1f8c621c74561ab9ec64b1b3b3ce1";
    private const string AuthorityEvidenceDigest = "1aec4f5a01d0d2aa1bbd6e5e9a4e804fc2fb105e4581a26d99dc8cb42a78f1a0";
    private const string WorkflowDecisionDigest = "11f973d785062fabb12e1305e7d6b9374386fd02215e83a5e0885ac424bc977a";
    private const string EligibilityEvidenceDigest = "c6d702ed546d874fbd3a364b09865c85d9530119b3402f082603358397853b45";

    private static readonly string SnapshotJson = string.Join(string.Empty,
    [
        "{\"base_currency\":\"EUR\",\"canonicalization_version\":\"policy-canonical-json/v1\",",
        "\"evaluated_at_utc\":\"2026-09-13T12:00:00.0000000Z\",\"facts\":{\"purchase_type\":\"GOOD\"},",
        "\"legal_entity_id\":\"aaaaaaaa-0000-0000-0000-000000000000\",",
        "\"lines\":[{\"facts\":{\"amount_base\":\"100\"},\"subject\":{\"id\":\"55555555-5555-5555-5555-555555555555\",\"version\":7}}],",
        "\"organization_id\":\"11111111-1111-1111-1111-111111111111\",",
        "\"subject\":{\"id\":\"22222222-2222-2222-2222-222222222222\",\"version\":3}}"
    ]);

    private static PolicyExceptionBindingRequest BindingRequest() => new(
        OrganizationId,
        "PURCHASE_REQUEST",
        SubjectId,
        3,
        BaseBundleId,
        new string('a', 64),
        PolicyVersionId,
        new string('b', 64),
        new string('c', 64),
        "QUOTES",
        [
            new PolicyExceptionTarget("PURCHASE_REQUEST_LINE", FirstLineId, 7, new string('d', 64)),
            new PolicyExceptionTarget("PURCHASE_REQUEST_LINE", SecondLineId, 8, new string('e', 64))
        ],
        3,
        1,
        1,
        ReferenceId,
        RequesterId,
        OriginatorId,
        WorkloadSubjectId,
        DateTimeOffset.Parse("2026-09-13T11:59:00.0000000Z"),
        DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
        "waiver-1");

    [Fact]
    public void Target_material_golden_matches_the_published_bytes()
    {
        var digest = PolicyApprovalTargets.ComputeMaterialDigest(
            SnapshotJson,
            new string('c', 64),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amount_base"] = "provider://amount",
                ["purchase_type"] = "provider://request"
            },
            new PolicySubjectReference(FirstLineId, 7));

        Assert.Equal(MaterialDigest, digest);
    }

    [Fact]
    public void Binding_golden_matches_the_published_bytes()
    {
        var request = BindingRequest();
        var serialized = ApprovalCanonicalJson.Serialize(PolicyExceptionDigests.BindingValue(request));

        Assert.Equal(
            "{\"base_bundle_id\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"base_result_digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
            "\"canonicalization_version\":\"approval-canonical-json/v2\"," +
            "\"contract_version\":\"workflow-verification-request/v1\"," +
            "\"covered_lines\":[{\"id\":\"55555555-5555-5555-5555-555555555555\"," +
            "\"material_snapshot_digest\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"," +
            "\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":7},{\"id\":\"66666666-6666-6666-6666-666666666666\"," +
            "\"material_snapshot_digest\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\"," +
            "\"type\":\"PURCHASE_REQUEST_LINE\",\"version\":8}],\"floor\":1,\"from\":3," +
            "\"manifest_digest\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"," +
            "\"nonce\":\"waiver-1\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"originator_id\":\"88888888-8888-8888-8888-888888888888\"," +
            "\"policy_content_digest\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"," +
            "\"policy_version_id\":\"44444444-4444-4444-4444-444444444444\"," +
            "\"reference_id\":\"eeeeeeee-1111-1111-1111-111111111111\"," +
            "\"requested_at\":\"2026-09-13T11:59:00.0000000Z\"," +
            "\"requested_valid_to\":\"2026-10-01T12:00:00.0000000Z\"," +
            "\"requester_id\":\"77777777-7777-7777-7777-777777777777\"," +
            "\"subject_id\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"subject_type\":\"PURCHASE_REQUEST\",\"subject_version\":3," +
            "\"target_requirement_key\":\"QUOTES\",\"to\":1," +
            "\"workload_subject_id\":\"99999999-9999-9999-9999-999999999999\"}",
            serialized);
        Assert.Equal(BindingDigest, request.ComputeBinding());
    }

    [Fact]
    public void Evidence_golden_matches_the_published_bytes()
    {
        const string eligibilityEvidence =
            "{\"evaluated_at\":\"2026-09-13T12:00:00.0000000Z\",\"user_id\":\"dddddddd-dddd-dddd-dddd-dddddddddddd\"}";
        Assert.Equal(EligibilityEvidenceDigest,
            PolicyExceptionDigests.ComputeEligibilityEvidenceDigest(eligibilityEvidence));

        var digest = PolicyExceptionDigests.ComputeEvidence(
            BindingRequest(),
            CaseId,
            RequirementId,
            "WR-QUOTES",
            DecisionId,
            1,
            WorkflowDecisionDigest,
            ApproverId,
            "PROCUREMENT_APPROVER",
            "PROCUREMENT",
            eligibilityEvidence,
            EligibilityEvidenceDigest,
            AuthorityEvidenceDigest,
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
            true,
            DateTimeOffset.Parse("2026-09-13T12:00:00.0000000Z"),
            DateTimeOffset.Parse("2026-10-01T12:00:00.0000000Z"),
            null,
            "approval-exception://aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Assert.Equal(EvidenceDigest, digest);
    }

    [Fact]
    public void Binding_rejects_repeated_lines_and_invalid_reduction()
    {
        var request = BindingRequest();
        Assert.Throws<DomainConflictException>(() => new PolicyExceptionBindingRequest(
            OrganizationId, "PURCHASE_REQUEST", SubjectId, 3, BaseBundleId, new string('a', 64),
            PolicyVersionId, new string('b', 64), new string('c', 64), "QUOTES",
            [request.CoveredLines[0], request.CoveredLines[0]],
            3, 1, 1, ReferenceId, RequesterId, OriginatorId, WorkloadSubjectId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "waiver-1"));
        Assert.Throws<DomainValidationException>(() => new PolicyExceptionBindingRequest(
            OrganizationId, "PURCHASE_REQUEST", SubjectId, 3, BaseBundleId, new string('a', 64),
            PolicyVersionId, new string('b', 64), new string('c', 64), "QUOTES",
            [request.CoveredLines[0]], 3, 3, 1, ReferenceId, RequesterId, OriginatorId, WorkloadSubjectId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "waiver-1"));
    }
}
