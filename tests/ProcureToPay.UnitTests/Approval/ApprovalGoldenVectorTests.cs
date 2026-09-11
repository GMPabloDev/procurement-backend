using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Golden vectors for the canonical evidence of SPEC 03 (REQ-08, NFR-01, CA-07). The literals
/// fix the bytes of every digest preimage so an accidental change to canonicalization, key
/// order or normalization fails here instead of silently invalidating emitted evidence.
/// </summary>
public sealed class ApprovalGoldenVectorTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CaseId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RequirementId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TaskId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PrerequisiteId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid DecisionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ActorId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OriginatorId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid SubjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TargetOne = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TargetTwo = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Clock = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApprovalWorkloadIdentity Workload = new("internal://procure-to-pay", "adapter");
    private static readonly ApprovalAdapterDescriptor Adapter =
        new("adapter", "PURCHASE_REQUEST", "SUBMIT", "v1", false);

    [Fact]
    public void Canonical_preimages_match_their_golden_bytes_and_digests()
    {
        var submission = Submission();
        var targets = Targets();
        var mismatches = new List<string>();

        var submissionBytes = ApprovalCanonicalJson.Serialize(
            ApprovalSubmissionRules.CommandValue(submission, Adapter, Workload));
        Expect(mismatches, "submission_bytes", submissionBytes, SubmissionBytes());
        Expect(
            mismatches,
            "submission_digest",
            ApprovalSubmissionRules.SubmissionFingerprint(submission, Adapter, Workload),
            SubmissionDigest());

        Expect(
            mismatches,
            "signal_digest",
            ApprovalFingerprints.SignalFingerprint(
                PrerequisiteId, 1, Workload, true, "evidence://budget", new string('e', 64), "signal-1"),
            SignalDigest());

        var eligibility = EvidenceJson();
        Expect(
            mismatches,
            "authority_digest",
            ApprovalFingerprints.AuthorityEvidenceDigest(eligibility),
            AuthorityDigest());

        Expect(
            mismatches,
            "decision_digest",
            ApprovalFingerprints.WorkflowDecisionDigest(
                DecisionId, 1, CaseId, "PURCHASE_REQUEST", SubjectId, 1, new string('a', 64),
                "WR-00000000000000000000000000000000",
                DecisionScopeDescriptor.Create(
                    OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]).Digest(),
                targets, ApprovalDecisionAction.Approve, ApprovalDecisionOrigin.Human, ActorId, Clock,
                "Aprobado por negocio", new string('b', 64), [OriginatorId]),
            DecisionDigest());

        Expect(
            mismatches,
            "decision_fingerprint",
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, targets, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId),
            DecisionFingerprint());

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Permuting_sets_does_not_change_the_digest_and_changing_a_field_does()
    {
        var forward = Targets();
        var reversed = forward.Reverse().ToArray();

        Assert.Equal(
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, forward, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId),
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, reversed, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId));

        Assert.NotEqual(
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, forward, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId),
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, forward, ApprovalDecisionAction.Approve,
                "Aprobado por auditoria", ActorId));

        // Surrounding whitespace and case of the precomposed form are canonicalized away, so
        // they must NOT change the digest (REQ-08).
        Assert.Equal(
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, forward, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId),
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, forward, ApprovalDecisionAction.Approve,
                "  Aprobado por negocio  ", ActorId));
    }

    [Fact]
    public void Excluded_user_sets_are_sorted_and_reject_duplicates()
    {
        var ascending = ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Set(
        [
            ApprovalCanonicalJson.String(OriginatorId),
            ApprovalCanonicalJson.String(ActorId)
        ]));
        var descending = ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Set(
        [
            ApprovalCanonicalJson.String(ActorId),
            ApprovalCanonicalJson.String(OriginatorId)
        ]));
        // Ordinal byte order of the canonical strings is the contract, not insertion order.
        Assert.Equal(ascending, descending);
        Assert.Equal($"[\"{ActorId:D}\",\"{OriginatorId:D}\"]", ascending);

        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainConflictException>(() =>
            ApprovalCanonicalJson.Set(
            [
                ApprovalCanonicalJson.String(OriginatorId),
                ApprovalCanonicalJson.String(OriginatorId)
            ]));
    }

    [Fact]
    public void Strings_are_normalized_to_nfc_timestamps_use_seven_decimals_and_enums_are_uppercase()
    {
        // "cafe" + combining acute accent must normalize to the precomposed form.
        var decomposed = ApprovalCanonicalJson.String("cafe\u0301");
        var composed = ApprovalCanonicalJson.String("caf\u00e9");
        Assert.Equal(ApprovalCanonicalJson.Serialize(composed), ApprovalCanonicalJson.Serialize(decomposed));

        Assert.Equal(
            "\"2026-03-01T12:00:00.0000000Z\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(Clock)));
        Assert.Equal(
            "\"00000000-0000-0000-0000-000000000001\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(Guid.Parse("00000000-0000-0000-0000-000000000001"))));
        Assert.Equal(
            "\"APPROVE\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(ApprovalDecisionAction.Approve)));
    }

    private static void Expect(List<string> mismatches, string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            mismatches.Add($"{name}: expected {expected} but got {actual}");
        }
    }

    private static ApprovalTarget[] Targets() =>
    [
        new("LINE", TargetOne, 1, new string('c', 64)),
        new("LINE", TargetTwo, 1, new string('d', 64))
    ];

    private static ApprovalSubmission Submission() => new(
        "submission-golden",
        OrganizationId,
        "PURCHASE_REQUEST",
        SubjectId,
        1,
        "SUBMIT",
        new string('a', 64),
        null,
        OriginatorId,
        [
            new ApprovalRequirementDefinition(
                "DEPARTMENT_REQ",
                "DEPARTMENT",
                SystemRole.DepartmentApprover,
                AuthorityRequirement.Required(ApprovalAuthorityType.BusinessNeed, 1, null, null),
                DecisionScopeDescriptor.Create(
                    OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]),
                [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject],
                [OriginatorId],
                Targets(),
                [])
        ],
        []);

    private static string EvidenceJson() => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
        ("evaluated_at", ApprovalCanonicalJson.String(Clock)),
        ("user_id", ApprovalCanonicalJson.String(ActorId))));

    private static string SubmissionBytes() => SubmissionGoldenBytes;

    private static string SubmissionDigest() =>
        "de1d2652e92b39cdde248cd94aa35a02df13e680ebdc65ea258094b65186b57d";

    private static string SignalDigest() =>
        "53197969ba7d55777b94fb8350fedff4f909f8dbb796e51970b4aa0550a606ca";

    private static string AuthorityDigest() =>
        "b19e2fcb46b0d8463efe454fcbcacf5564852504782e2b1c47bfeb581241a046";

    private static string DecisionDigest() =>
        "c9f360657831a085d3e941ba4afffda7d1d239782b9f1a5b41344f7cb927363f";

    private static string DecisionFingerprint() =>
        "ee728c31505637e5b9f5912f1635f5a4c7c42c3bdfd9b597c920cfc60e02d98c";

    /// <summary>
    /// Exact canonical bytes of the submission command preimage. Reviewed field by field
    /// against REQ-08 (ordinal key order, lowercase UUIDs, uppercase enums, sorted sets,
    /// nulls present) and its SHA-256 was reproduced with an independent implementation.
    /// </summary>
    private const string SubmissionGoldenBytes =
        """{"adapter_id":"adapter","adapter_version":"v1","canonicalization_version":"approval-canonical-json/v1","operation":"SUBMIT","organization_id":"11111111-1111-1111-1111-111111111111","originator_id":"88888888-8888-8888-8888-888888888888","prerequisites":[],"requester_id":null,"requirements":[{"actions":["APPROVE","REJECT"],"authority":{"amount_base":null,"base_currency":null,"kind":"REQUIRED","minimum_rank":1,"type":"BUSINESSNEED"},"dependencies":[],"exclusions":["88888888-8888-8888-8888-888888888888"],"role":"DEPARTMENTAPPROVER","scope":{"organization_id":"11111111-1111-1111-1111-111111111111","schema_version":"decision-scope/v1","scopes":[{"dimension":"ORGANIZATION","reference_id":null,"reference_version":null}]},"source_requirement_key":"DEPARTMENT_REQ","stage_code":"DEPARTMENT","targets":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"LINE","version":1},{"id":"aaaaaaaa-0000-0000-0000-000000000002","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"LINE","version":1}]}],"source_snapshot_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","subject_id":"99999999-9999-9999-9999-999999999999","subject_type":"PURCHASE_REQUEST","subject_version":1,"submission_key":"submission-golden","workload_client_id":"adapter","workload_issuer":"internal://procure-to-pay"}""";
}
