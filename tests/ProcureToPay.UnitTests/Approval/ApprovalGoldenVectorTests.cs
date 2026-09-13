using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.UnitTests.Approval;

/// <summary>
/// Golden vectors for the canonical evidence of SPEC 03 (REQ-08, NFR-01, CA-07). The literals
/// fix the bytes of every v2 digest preimage so an accidental change to canonicalization, key
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
    private static readonly Guid EventId = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid RootAuditId = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
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
            "signal_bytes",
            SignalPreimage(),
            SignalBytes());
        Expect(
            mismatches,
            "signal_digest",
            ApprovalFingerprints.SignalFingerprint(
                PrerequisiteId, 1, Workload, true, "evidence://budget", new string('e', 64), "signal-1"),
            SignalDigest());

        var eligibility = EvidenceJson();
        Expect(
            mismatches,
            "authority_bytes",
            AuthorityPreimage(eligibility),
            AuthorityBytes());
        Expect(
            mismatches,
            "authority_digest",
            ApprovalFingerprints.AuthorityEvidenceDigest(eligibility),
            AuthorityDigest());

        Expect(
            mismatches,
            "decision_bytes",
            WorkflowDecisionPreimage(),
            DecisionBytes());
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
            "decision_fingerprint_bytes",
            DecisionFingerprintPreimage(),
            DecisionFingerprintBytes());
        Expect(
            mismatches,
            "decision_fingerprint",
            ApprovalFingerprints.DecisionFingerprint(
                CaseId, RequirementId, TaskId, 3, 2, targets, ApprovalDecisionAction.Approve,
                "Aprobado por negocio", ActorId),
            DecisionFingerprint());

        // The literals below were reviewed field by field against the REQ-08 table; their
        // SHA-256 is pinned here with an independent implementation, not the domain digest.
        Assert.Equal(SignalDigest(), Sha256Of(SignalBytes()));
        Assert.Equal(AuthorityDigest(), Sha256Of(AuthorityBytes()));
        Assert.Equal(DecisionDigest(), Sha256Of(DecisionBytes()));
        Assert.Equal(DecisionFingerprint(), Sha256Of(DecisionFingerprintBytes()));
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Approval_result_v2_publishes_exactly_its_declared_properties_and_sources()
    {
        var approvalCase = Case();
        var resultSource = ApprovalEntitySource.Requirement(RequirementId, "WR-00000000000000000000000000000000");
        var payload = ApprovalOutboxEvent.BuildPayload(
            EventId,
            ApprovalOutboxPolicy.ContractVersion,
            approvalCase,
            resultSource,
            Targets()[0],
            "APPROVED",
            DecisionId,
            D('d'),
            Clock);
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal(
            [
                "case_id", "contract_version", "decision_digest", "decision_id", "event_id", "occurred_at",
                "organization_id", "result", "result_source", "subject_id", "subject_type", "subject_version", "target"
            ],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("approval-result/v2", root.GetProperty("contract_version").GetString());
        Assert.Equal(D('d'), root.GetProperty("decision_digest").GetString());
        Assert.Equal(DecisionId.ToString("D"), root.GetProperty("decision_id").GetString());
        var source = root.GetProperty("result_source");
        Assert.Equal(
            ["id", "key", "type"],
            source.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(RequirementId.ToString("D"), source.GetProperty("id").GetString());
        Assert.Equal("WR-00000000000000000000000000000000", source.GetProperty("key").GetString());
        Assert.Equal("APPROVAL_REQUIREMENT", source.GetProperty("type").GetString());

        // A non-decision result carries a null decision id and digest, whatever the source type is.
        var cancelled = ApprovalOutboxEvent.BuildPayload(
            EventId,
            ApprovalOutboxPolicy.ContractVersion,
            approvalCase,
            ApprovalEntitySource.Prerequisite(PrerequisiteId, "BUDGET_CHECK"),
            Targets()[0],
            "CANCELLED",
            null,
            null,
            Clock);
        using var cancelledDocument = System.Text.Json.JsonDocument.Parse(cancelled);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            cancelledDocument.RootElement.GetProperty("decision_digest").ValueKind);
        Assert.Equal(
            "EXTERNAL_PREREQUISITE",
            cancelledDocument.RootElement.GetProperty("result_source").GetProperty("type").GetString());
    }

    [Fact]
    public void Approval_result_v2_rejects_invalid_source_and_result_combinations()
    {
        var approvalCase = Case();
        var requirementSource = ApprovalEntitySource.Requirement(
            RequirementId, "WR-00000000000000000000000000000000");
        var prerequisiteSource = ApprovalEntitySource.Prerequisite(PrerequisiteId, "BUDGET_CHECK");

        // A requirement can never publish SATISFIED and a prerequisite can never publish APPROVED.
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            ApprovalOutboxEvent.BuildPayload(
                EventId, ApprovalOutboxPolicy.ContractVersion, approvalCase, requirementSource,
                Targets()[0], "SATISFIED", DecisionId, D('d'), Clock));
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            ApprovalOutboxEvent.BuildPayload(
                EventId, ApprovalOutboxPolicy.ContractVersion, approvalCase, prerequisiteSource,
                Targets()[0], "APPROVED", DecisionId, D('d'), Clock));
        // A decision result always needs the workflow decision digest.
        Assert.Throws<ProcureToPay.Domain.SharedKernel.DomainValidationException>(() =>
            ApprovalOutboxEvent.BuildPayload(
                EventId, ApprovalOutboxPolicy.ContractVersion, approvalCase, requirementSource,
                Targets()[0], "APPROVED", null, null, Clock));
    }

    [Fact]
    public void Automatic_effect_keys_distinguish_source_entities_on_the_same_target()
    {
        var causedBy = ApprovalCausalLink.Approval(RootAuditId);
        var firstPrerequisite = ApprovalEntitySource.Prerequisite(PrerequisiteId, "BUDGET_CHECK");
        var secondPrerequisite = ApprovalEntitySource.Prerequisite(Guid.Parse("dddddddd-0000-0000-0000-00000000000d"), "RISK_CHECK");
        var requirement = ApprovalEntitySource.Requirement(RequirementId, "WR-00000000000000000000000000000000");

        string Key(ApprovalEntitySource source) => ApprovalFingerprints.AutomaticEffectKey(
            "PREREQUISITE_CANCELLED",
            beforeVersion: 1,
            afterVersion: 2,
            CaseId,
            causedBy,
            source,
            OrganizationId,
            targetType: "PREREQUISITE",
            targetId: PrerequisiteId);

        // Two different prerequisites on the same case, target and root never collide (PEND-04).
        Assert.NotEqual(Key(firstPrerequisite), Key(secondPrerequisite));
        Assert.NotEqual(Key(firstPrerequisite), Key(requirement));
        // Determinism: the same transition and cause always produce the same key (NFR-01).
        Assert.Equal(Key(firstPrerequisite), Key(firstPrerequisite));
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
        // The v2 code exposes the contract codes, not the enum names (APPROVAL_REQUIREMENT, not APPROVALREQUIREMENT).
        Assert.Equal(
            "\"APPROVAL_REQUIREMENT\"",
            ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.String(
                ApprovalEntitySource.Requirement(RequirementId, "WR-00000000000000000000000000000000").Code)));
    }

    private static void Expect(List<string> mismatches, string name, string actual, string expected)    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            mismatches.Add($"{name}: expected {expected} but got {actual}");
        }
    }

    private static string D(char value) => new(value, 64);

    private static ApprovalTarget[] Targets() =>
    [
        new("LINE", TargetOne, 1, new string('c', 64)),
        new("LINE", TargetTwo, 1, new string('d', 64))
    ];

    private static ApprovalCase Case() => ApprovalCase.Create(
        CaseId,
        Submission(),
        Adapter,
        Workload,
        new Dictionary<string, ApprovalWorkloadIdentity>(StringComparer.Ordinal),
        new string('f', 64),
        Clock,
        "correlation");

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

    /// <summary>Canonical v2 preimage of the signal fingerprint (REQ-08 table).</summary>
    private static string SignalPreimage() => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
        ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
        ("evidence_digest", ApprovalCanonicalJson.String(new string('e', 64))),
        ("evidence_reference", ApprovalCanonicalJson.String("evidence://budget")),
        ("owner_client_id", ApprovalCanonicalJson.String(Workload.ClientId)),
        ("owner_issuer", ApprovalCanonicalJson.String(Workload.Issuer)),
        ("prerequisite_id", ApprovalCanonicalJson.String(PrerequisiteId)),
        ("prerequisite_version", ApprovalCanonicalJson.Number(1)),
        ("result", ApprovalCanonicalJson.String("SATISFIED")),
        ("signal_key", ApprovalCanonicalJson.String("signal-1"))));

    /// <summary>Canonical v2 preimage of the authority evidence digest (delegations are null in this spec).</summary>
    private static string AuthorityPreimage(string eligibilityEvidenceJson) =>
        ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
            ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
            ("delegation_id", ApprovalCanonicalJson.Null()),
            ("delegation_version", ApprovalCanonicalJson.Null()),
            ("eligibility_evidence", ApprovalCanonicalJson.String(eligibilityEvidenceJson))));

    /// <summary>Canonical v2 preimage of the workflow decision digest (REQ-08 table).</summary>
    private static string WorkflowDecisionPreimage() => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
        ("action", ApprovalCanonicalJson.String(ApprovalDecisionAction.Approve)),
        ("actor_user_id", ApprovalCanonicalJson.String(ActorId)),
        ("authority_evidence_digest", ApprovalCanonicalJson.String(new string('b', 64))),
        ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
        ("case_id", ApprovalCanonicalJson.String(CaseId)),
        ("decided_at", ApprovalCanonicalJson.String(Clock)),
        ("decision_id", ApprovalCanonicalJson.String(DecisionId)),
        ("decision_scope_digest", ApprovalCanonicalJson.String(
            DecisionScopeDescriptor.Create(
                OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]).Digest())),
        ("decision_version", ApprovalCanonicalJson.Number(1)),
        ("exclusions", ApprovalCanonicalJson.Set([ApprovalCanonicalJson.String(OriginatorId)])),
        ("origin", ApprovalCanonicalJson.String(ApprovalDecisionOrigin.Human)),
        ("reason", ApprovalCanonicalJson.String("Aprobado por negocio")),
        ("requirement_key", ApprovalCanonicalJson.String("WR-00000000000000000000000000000000")),
        ("segregation_satisfied", ApprovalCanonicalJson.Bool(true)),
        ("snapshot_digest", ApprovalCanonicalJson.String(new string('a', 64))),
        ("subject_id", ApprovalCanonicalJson.String(SubjectId)),
        ("subject_type", ApprovalCanonicalJson.String("PURCHASE_REQUEST")),
        ("subject_version", ApprovalCanonicalJson.Number(1)),
        ("targets", ApprovalRequirementDefinition.TargetsValue(Targets()))));

    /// <summary>Canonical v2 preimage of the decision fingerprint (REQ-08 table).</summary>
    private static string DecisionFingerprintPreimage() => ApprovalCanonicalJson.Serialize(ApprovalCanonicalJson.Object(
        ("action", ApprovalCanonicalJson.String(ApprovalDecisionAction.Approve)),
        ("actor_user_id", ApprovalCanonicalJson.String(ActorId)),
        ("canonicalization_version", ApprovalCanonicalJson.String(ApprovalCanonicalJson.CanonicalizationVersion)),
        ("case_id", ApprovalCanonicalJson.String(CaseId)),
        ("reason", ApprovalCanonicalJson.String("Aprobado por negocio")),
        ("requirement_id", ApprovalCanonicalJson.String(RequirementId)),
        ("requirement_version", ApprovalCanonicalJson.Number(3)),
        ("targets", ApprovalRequirementDefinition.TargetsValue(Targets())),
        ("task_id", ApprovalCanonicalJson.String(TaskId)),
        ("task_version", ApprovalCanonicalJson.Number(2))));

    private static string SignalBytes() => SignalGoldenBytes;

    private static string AuthorityBytes() => AuthorityGoldenBytes;

    private static string DecisionBytes() => DecisionGoldenBytes;

    private static string DecisionFingerprintBytes() => DecisionFingerprintGoldenBytes;

    private static string SubmissionBytes() => SubmissionGoldenBytes;

    private static string SubmissionDigest() =>
        "c4f08ea7aa40ff7e39ca20fd3bf4a3be1feff03052e02c214b5d9ec472844971";

    private static string SignalDigest() =>
        "674a49d606cccbb919fb81cce45d6c271b7dbda6ca78da95f81db109cae830c8";

    private static string AuthorityDigest() =>
        "93003e10ecf62a0d795474dc38ea2da5baaf12445c2cedb5ad63741845355850";

    private static string DecisionDigest() =>
        "04eddbded05001f26209e0946d67051e9b93020cfde1f0e7d10302d14ee20386";

    private static string DecisionFingerprint() =>
        "0ffd7bc164c2daccc5930b473a5a523b5337f1f08ceff6ffbe73c1584e972f36";

    /// <summary>Independent SHA-256 over the UTF-8 bytes of a golden preimage (no domain code).</summary>
    private static string Sha256Of(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Exact canonical bytes of the v2 submission command preimage. Reviewed field by field
    /// against REQ-08 (ordinal key order, lowercase UUIDs, uppercase enums, sorted sets,
    /// nulls present) and its SHA-256 was reproduced with an independent implementation.
    /// </summary>
    private const string SubmissionGoldenBytes =
        """{"adapter_id":"adapter","adapter_version":"v1","canonicalization_version":"approval-canonical-json/v2","operation":"SUBMIT","organization_id":"11111111-1111-1111-1111-111111111111","originator_id":"88888888-8888-8888-8888-888888888888","prerequisites":[],"requester_id":null,"requirements":[{"actions":["APPROVE","REJECT"],"authority":{"amount_base":null,"base_currency":null,"kind":"REQUIRED","minimum_rank":1,"type":"BUSINESSNEED"},"dependencies":[],"exclusions":["88888888-8888-8888-8888-888888888888"],"role":"DEPARTMENTAPPROVER","scope":{"organization_id":"11111111-1111-1111-1111-111111111111","schema_version":"decision-scope/v1","scopes":[{"dimension":"ORGANIZATION","reference_id":null,"reference_version":null}]},"source_requirement_key":"DEPARTMENT_REQ","stage_code":"DEPARTMENT","targets":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"LINE","version":1},{"id":"aaaaaaaa-0000-0000-0000-000000000002","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"LINE","version":1}]}],"source_snapshot_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","subject_id":"99999999-9999-9999-9999-999999999999","subject_type":"PURCHASE_REQUEST","subject_version":1,"submission_key":"submission-golden","workload_client_id":"adapter","workload_issuer":"internal://procure-to-pay"}""";

    // Exact canonical bytes of the remaining four v2 preimages of the CA-07 fixture, reviewed
    // field by field against the REQ-08 table (ordinal key order, lowercase UUIDs, uppercase
    // enums, sorted sets, nulls present); their SHA-256 matches the five contractual digests.
    private const string SignalGoldenBytes =
        """{"canonicalization_version":"approval-canonical-json/v2","evidence_digest":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","evidence_reference":"evidence://budget","owner_client_id":"adapter","owner_issuer":"internal://procure-to-pay","prerequisite_id":"55555555-5555-5555-5555-555555555555","prerequisite_version":1,"result":"SATISFIED","signal_key":"signal-1"}""";

    private const string AuthorityGoldenBytes =
        """{"canonicalization_version":"approval-canonical-json/v2","delegation_id":null,"delegation_version":null,"eligibility_evidence":"{\"evaluated_at\":\"2026-03-01T12:00:00.0000000Z\",\"user_id\":\"77777777-7777-7777-7777-777777777777\"}"}""";

    private const string DecisionGoldenBytes =
        """{"action":"APPROVE","actor_user_id":"77777777-7777-7777-7777-777777777777","authority_evidence_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","canonicalization_version":"approval-canonical-json/v2","case_id":"22222222-2222-2222-2222-222222222222","decided_at":"2026-03-01T12:00:00.0000000Z","decision_id":"66666666-6666-6666-6666-666666666666","decision_scope_digest":"b54c349040ca2907d59e1384a5c6dae32e2d8fed4c9d440e6b57946289749596","decision_version":1,"exclusions":["88888888-8888-8888-8888-888888888888"],"origin":"HUMAN","reason":"Aprobado por negocio","requirement_key":"WR-00000000000000000000000000000000","segregation_satisfied":true,"snapshot_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","subject_id":"99999999-9999-9999-9999-999999999999","subject_type":"PURCHASE_REQUEST","subject_version":1,"targets":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"LINE","version":1},{"id":"aaaaaaaa-0000-0000-0000-000000000002","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"LINE","version":1}]}""";

    private const string DecisionFingerprintGoldenBytes =
        """{"action":"APPROVE","actor_user_id":"77777777-7777-7777-7777-777777777777","canonicalization_version":"approval-canonical-json/v2","case_id":"22222222-2222-2222-2222-222222222222","reason":"Aprobado por negocio","requirement_id":"33333333-3333-3333-3333-333333333333","requirement_version":3,"targets":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","material_snapshot_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","type":"LINE","version":1},{"id":"aaaaaaaa-0000-0000-0000-000000000002","material_snapshot_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","type":"LINE","version":1}],"task_id":"44444444-4444-4444-4444-444444444444","task_version":2}""";
}
