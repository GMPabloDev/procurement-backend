using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Contractual golden vector of <c>approval-supersession-delta/v1</c> and the closed structural
/// rules of the variable-cardinality supersession (SPEC 06 REQ-08, CA-06, CA-07).
/// </summary>
public sealed class ApprovalSupersessionDeltaTests
{
    private static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", "purchase-request-domain");

    private static readonly Guid RetainedLine = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AddedLine = Guid.Parse("33333333-3333-3333-3333-333333333334");
    private static readonly Guid RemovedLine = Guid.Parse("33333333-3333-3333-3333-333333333335");

    [Fact]
    public void Supersession_v3_fingerprint_matches_the_contractual_vector_bytes_and_digest()
    {
        var delta = new[]
        {
            ApprovalSupersessionDeltaEntry.CreateAdded(
                Target(AddedLine, 1, new string('f', 64)),
                "purchase-request-materiality/v1",
                new string('d', 64)),
            ApprovalSupersessionDeltaEntry.CreateRemoved(
                Target(RemovedLine, 1, new string('2', 64)),
                "purchase-request-materiality/v1",
                new string('1', 64)),
            ApprovalSupersessionDeltaEntry.CreateRetained(
                Target(RetainedLine, 1, new string('b', 64)),
                Target(RetainedLine, 2, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('a', 64))
        };

        var preimage = ApprovalFingerprints.SupersessionPreimageV3(
            new string('e', 64),
            Workload,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            2,
            "pr-222-v2",
            delta);
        var bytes = ApprovalCanonicalJson.Serialize(preimage);
        const string expected =
            """{"canonicalization_version":"approval-canonical-json/v3","new_submission_fingerprint":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","owner_workload_client_id":"purchase-request-domain","owner_workload_issuer":"internal://procure-to-pay","previous_case_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","previous_case_version":2,"supersession_key":"pr-222-v2","target_delta":[{"change_kind":"ADDED","materiality_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","materiality_schema_version":"purchase-request-materiality/v1","previous":null,"replacement":{"id":"33333333-3333-3333-3333-333333333334","material_snapshot_digest":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","type":"PURCHASE_REQUEST_LINE","version":1}},{"change_kind":"REMOVED","materiality_digest":"1111111111111111111111111111111111111111111111111111111111111111","materiality_schema_version":"purchase-request-materiality/v1","previous":{"id":"33333333-3333-3333-3333-333333333335","material_snapshot_digest":"2222222222222222222222222222222222222222222222222222222222222222","type":"PURCHASE_REQUEST_LINE","version":1},"replacement":null},{"change_kind":"RETAINED","materiality_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","materiality_schema_version":"purchase-request-materiality/v1","previous":{"id":"33333333-3333-3333-3333-333333333333","material_snapshot_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","type":"PURCHASE_REQUEST_LINE","version":1},"replacement":{"id":"33333333-3333-3333-3333-333333333333","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"PURCHASE_REQUEST_LINE","version":2}}]}""";
        Assert.Equal(expected, bytes);
        Assert.Equal(
            "6016e088b85c6a2ebaf4129461551284133f0872810a8771fdcc79fcaafc4470",
            ApprovalCanonicalJson.Digest(preimage));
    }

    [Fact]
    public void Supersession_v3_digest_is_order_invariant_and_materially_sensitive()
    {
        var retained = ApprovalSupersessionDeltaEntry.CreateRetained(
            Target(RetainedLine, 1, new string('b', 64)),
            Target(RetainedLine, 2, new string('c', 64)),
            "purchase-request-materiality/v1",
            new string('a', 64));
        var added = ApprovalSupersessionDeltaEntry.CreateAdded(
            Target(AddedLine, 1, new string('f', 64)),
            "purchase-request-materiality/v1",
            new string('d', 64));

        var ordered = ApprovalFingerprints.SupersessionFingerprintV3(
            new string('e', 64), Workload, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 2, "pr-222-v2",
            [retained, added]);
        var permuted = ApprovalFingerprints.SupersessionFingerprintV3(
            new string('e', 64), Workload, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 2, "pr-222-v2",
            [added, retained]);
        Assert.Equal(ordered, permuted);

        var changedFact = ApprovalFingerprints.SupersessionFingerprintV3(
            new string('e', 64), Workload, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 2, "pr-222-v2",
            [ApprovalSupersessionDeltaEntry.CreateRetained(
                Target(RetainedLine, 1, new string('b', 64)),
                Target(RetainedLine, 2, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('9', 64)), added]);
        Assert.NotEqual(ordered, changedFact);
    }

    [Fact]
    public void Retained_entries_require_the_stable_identity_and_a_materiality_proof()
    {
        // The same version and digest is a valid retained pair, and so is a changed version.
        var same = ApprovalSupersessionDeltaEntry.CreateRetained(
            Target(RetainedLine, 1, new string('b', 64)),
            Target(RetainedLine, 1, new string('b', 64)),
            "purchase-request-materiality/v1",
            new string('a', 64));
        Assert.Equal(ApprovalSupersessionChangeKind.Retained, same.ChangeKind);

        // A different stable identity is not a retained pair; it is a removal plus an addition.
        Assert.Throws<DomainValidationException>(() => ApprovalSupersessionDeltaEntry.CreateRetained(
            Target(RetainedLine, 1, new string('b', 64)),
            Target(AddedLine, 1, new string('c', 64)),
            "purchase-request-materiality/v1",
            new string('a', 64)));

        // Missing predicates and malformed proofs fail closed.
        Assert.Throws<DomainValidationException>(() => ApprovalSupersessionDeltaEntry.CreateRetained(
            Target(RetainedLine, 1, new string('b', 64)),
            Target(RetainedLine, 2, new string('c', 64)),
            "purchase-request-materiality/v1",
            new string('A', 64)));
        Assert.Throws<DomainValidationException>(() => ApprovalSupersessionDeltaEntry.CreateAdded(
            Target(AddedLine, 1, new string('f', 64)),
            "not a version",
            new string('d', 64)));
    }

    [Fact]
    public void Delta_rules_cover_both_manifests_without_repetition_or_identity_reuse()
    {
        var valid = new[]
        {
            ApprovalSupersessionDeltaEntry.CreateRetained(
                Target(RetainedLine, 1, new string('b', 64)),
                Target(RetainedLine, 2, new string('c', 64)),
                "purchase-request-materiality/v1",
                new string('a', 64)),
            ApprovalSupersessionDeltaEntry.CreateRemoved(
                Target(RemovedLine, 1, new string('2', 64)),
                "purchase-request-materiality/v1",
                new string('1', 64)),
            ApprovalSupersessionDeltaEntry.CreateAdded(
                Target(AddedLine, 1, new string('f', 64)),
                "purchase-request-materiality/v1",
                new string('d', 64))
        };
        ApprovalSupersessionDeltaRules.Validate(valid);

        // Repeating a previous target breaks exact coverage of the old manifest.
        Assert.Throws<DomainConflictException>(() => ApprovalSupersessionDeltaRules.Validate(
            [valid[0], ApprovalSupersessionDeltaEntry.CreateRemoved(
                Target(RetainedLine, 1, new string('b', 64)),
                "purchase-request-materiality/v1",
                new string('a', 64))]));

        // An addition cannot reuse the stable identity of a removed previous target.
        Assert.Throws<DomainConflictException>(() => ApprovalSupersessionDeltaRules.Validate(
            [valid[0], valid[1],
                ApprovalSupersessionDeltaEntry.CreateAdded(
                    Target(RemovedLine, 2, new string('f', 64)),
                    "purchase-request-materiality/v1",
                    new string('d', 64))]));

        // An empty delta covers nothing.
        Assert.Throws<DomainValidationException>(() => ApprovalSupersessionDeltaRules.Validate([]));
    }

    [Fact]
    public void Restore_rehydrates_the_exact_closed_shape()
    {
        var entry = ApprovalSupersessionDeltaEntry.Restore(
            "RETAINED",
            Target(RetainedLine, 1, new string('b', 64)),
            Target(RetainedLine, 2, new string('c', 64)),
            "purchase-request-materiality/v1",
            new string('a', 64));
        Assert.Equal(ApprovalSupersessionChangeKind.Retained, entry.ChangeKind);
        Assert.Throws<DomainValidationException>(() => ApprovalSupersessionDeltaEntry.Restore(
            "MERGED",
            Target(RetainedLine, 1, new string('b', 64)),
            Target(RetainedLine, 2, new string('c', 64)),
            "purchase-request-materiality/v1",
            new string('a', 64)));
    }

    private static ApprovalTarget Target(Guid id, int version, string digest) =>
        new("PURCHASE_REQUEST_LINE", id, version, digest);
}
