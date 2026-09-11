using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Policy;

public sealed class PolicyCanonicalizationGoldenTests
{
    [Fact]
    public void Policy_canonicalization_matches_the_contractual_golden_vector()
    {
        var policy = new PolicySetVersion(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            1,
            [PolicyScope.Request, PolicyScope.Line]);
        policy.AddRule(new PolicyRule(
            "Z_RULE",
            PolicyScope.Request,
            [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64)))],
            [new PolicyEffect(PolicyEffectType.Block, "RISK", "Cafe\u0301")]));
        policy.AddRule(new PolicyRule(
            "A_RULE",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")],
            isFallback: true));

        const string expected = "{\"canonicalization_version\":\"policy-canonical-json/v1\",\"organization_id\":\"11111111-1111-1111-1111-111111111111\",\"policy_schema_version\":\"policy-schema/v2\",\"rules\":[{\"code\":\"A_RULE\",\"effects\":[{\"approval\":null,\"documents\":null,\"exception_floor\":null,\"minimum_quotations\":null,\"reason\":null,\"requirement_key\":\"DEFAULT\",\"type\":\"ALLOW\"}],\"fallback\":true,\"predicates\":[],\"revision\":1,\"scope\":\"LINE\"},{\"code\":\"Z_RULE\",\"effects\":[{\"approval\":null,\"documents\":null,\"exception_floor\":null,\"minimum_quotations\":null,\"reason\":\"Café\",\"requirement_key\":\"RISK\",\"type\":\"BLOCK\"}],\"fallback\":false,\"predicates\":[{\"fact_key\":\"DATA_RISK\",\"operator\":\"EQ\",\"value\":{\"catalog\":\"DATA_RISK\",\"currency\":null,\"digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"entity_id\":null,\"kind\":\"VERSIONED_CODE_REF\",\"lower_bound\":null,\"lower_inclusive\":true,\"members\":null,\"question_code\":null,\"reference_type\":null,\"schema_version\":null,\"upper_bound\":null,\"upper_inclusive\":false,\"value\":\"HIGH\",\"value_kind\":null,\"version\":1}}],\"revision\":1,\"scope\":\"REQUEST\"}],\"scopes\":[\"LINE\",\"REQUEST\"]}";

        var actual = PolicyCanonicalizer.CanonicalizePolicy(policy);
        Assert.Equal(expected, actual);
        Assert.Equal("bd090795680014db241d029deec584d9d56c1c0e58797ee5268b4f8ef0fdbaeb", PolicyCanonicalizer.Hash(actual));
    }

    [Fact]
    public void Evaluation_input_canonicalization_matches_the_contractual_golden_vector()
    {
        var actual = PolicyCanonicalizer.CanonicalizeEvaluationInput(
            DateTimeOffset.Parse("2026-09-09T15:00:00.1234567+00:00"),
            "REQUEST",
            new PolicySubjectReference(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2),
            new string('a', 64),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            new string('b', 64),
            null,
            null,
            [new string('d', 64), new string('c', 64)]);

        const string expected = "{\"activation_id\":\"44444444-4444-4444-4444-444444444444\",\"canonicalization_version\":\"policy-canonical-json/v1\",\"evaluated_at_utc\":\"2026-09-09T15:00:00.1234567Z\",\"exception_verification_digests\":[\"" + "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"],\"fact_manifest_digest\":\"" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"operation\":\"REQUEST\",\"policy_content_digest\":\"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"previous_bundle_id\":null,\"previous_result_digest\":null,\"subject_ref\":{\"id\":\"33333333-3333-3333-3333-333333333333\",\"version\":2}}";

        Assert.Equal(expected, actual);
        Assert.Equal("6628cb80c0b4e3d41df24f10c808c5db34a6c30ed2d26d32395c7646e4773660", PolicyCanonicalizer.Hash(actual));
    }

    [Fact]
    public void Exception_verification_canonicalization_matches_the_contractual_golden_vector()
    {
        var input = new PolicyExceptionVerificationInput(
            "workflow", "v1", "decision-1", 2, new string('e', 64),
            Guid.Parse("55555555-5555-5555-5555-555555555555"), new string('c', 64),
            Guid.Parse("66666666-6666-6666-6666-666666666666"), new string('d', 64),
            new PolicySubjectReference(Guid.Parse("77777777-7777-7777-7777-777777777777"), 3),
            new string('b', 64), "QUOTATIONS", 3, 2,
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            DateTimeOffset.Parse("2026-09-09T15:00:00.1234567Z"), "nonce",
            Guid.Parse("99999999-9999-9999-9999-999999999999"), new string('a', 64), true,
            "REQUEST", DateTimeOffset.Parse("2026-09-09T15:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T15:00:00Z"), "binding");

        const string expected = "{\"authority_evidence\":{\"approver_id\":\"99999999-9999-9999-9999-999999999999\",\"evidence_digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"bindings\":{\"base_bundle_id\":\"55555555-5555-5555-5555-555555555555\",\"base_result_digest\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"binding\":\"binding\",\"from\":3,\"manifest_digest\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"policy_content_digest\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"policy_version_id\":\"66666666-6666-6666-6666-666666666666\",\"requester_id\":\"88888888-8888-8888-8888-888888888888\",\"subject_ref\":{\"id\":\"77777777-7777-7777-7777-777777777777\",\"version\":3},\"target_requirement_key\":\"QUOTATIONS\",\"to\":2},\"canonicalization_version\":\"policy-canonical-json/v1\",\"nonce\":\"nonce\",\"scope\":\"REQUEST\",\"segregation_satisfied\":true,\"validity\":{\"valid_from\":\"2026-09-09T15:00:00.0000000Z\",\"valid_to\":\"2026-09-10T15:00:00.0000000Z\"},\"verifier_contract_version\":\"v1\",\"verifier_id\":\"workflow\",\"workflow_decision\":{\"digest\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"id\":\"decision-1\",\"version\":2}}";

        var actual = PolicyCanonicalizer.CanonicalizeExceptionVerification(input);
        Assert.Equal(expected, actual);
        Assert.Equal("cf733438907fb00d266bebf0e153f2e893aa47258238fea288d96093ae5f0a8c", PolicyCanonicalizer.ComputeExceptionVerificationDigest(input));
    }

    [Fact]
    public void Evaluation_result_canonicalization_matches_the_contractual_golden_vector()
    {
        var subject = new PolicySubjectReference(
            Guid.Parse("77777777-7777-7777-7777-777777777777"), 3);
        var control = new PolicyGeneratedControl(
            "QUOTATIONS", PolicyEffectType.RequireQuotations,
            System.Collections.Immutable.ImmutableHashSet.Create(PolicyScope.Line),
            System.Collections.Immutable.ImmutableHashSet.Create(subject.Id),
            "PRE_PROCUREMENT", null, 3,
            System.Collections.Immutable.ImmutableHashSet<string>.Empty,
            System.Collections.Immutable.ImmutableHashSet.Create("RULE_Q"), "Cafe\u0301");
        var scope = new PolicyScopeEvaluation(
            PolicyScope.Line,
            System.Collections.Immutable.ImmutableHashSet.Create(subject.Id),
            ["RULE_Q"], [control], PolicyResult.RequirementsGenerated)
        { SubjectReferences = [subject] };

        const string expected = "{\"canonicalization_version\":\"policy-canonical-json/v1\",\"combined_controls\":[{\"amount_base\":null,\"approval\":null,\"base_currency\":null,\"cost_center_ids\":[],\"fact_provenance\":{},\"minimum_allowed_quotations\":null,\"minimum_quotations\":3,\"origin_facts\":[],\"origin_rules\":[\"RULE_Q\"],\"origin_scopes\":[\"LINE\"],\"phase\":\"PRE_PROCUREMENT\",\"reason\":\"Café\",\"requirement_key\":\"QUOTATIONS\",\"subjects\":[\"77777777-7777-7777-7777-777777777777\"],\"supporting_document_types\":[],\"type\":\"REQUIRE_QUOTATIONS\"}],\"combined_result\":\"REQUIREMENTS_GENERATED\",\"diff\":null,\"evaluation_input_digest\":\"iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii\",\"scope_evaluations\":[{\"controls\":[{\"amount_base\":null,\"approval\":null,\"base_currency\":null,\"cost_center_ids\":[],\"fact_provenance\":{},\"minimum_allowed_quotations\":null,\"minimum_quotations\":3,\"origin_facts\":[],\"origin_rules\":[\"RULE_Q\"],\"origin_scopes\":[\"LINE\"],\"phase\":\"PRE_PROCUREMENT\",\"reason\":\"Café\",\"requirement_key\":\"QUOTATIONS\",\"subjects\":[\"77777777-7777-7777-7777-777777777777\"],\"supporting_document_types\":[],\"type\":\"REQUIRE_QUOTATIONS\"}],\"matched_rules\":[\"RULE_Q\"],\"result\":\"REQUIREMENTS_GENERATED\",\"scope\":\"LINE\",\"subjects\":[\"77777777-7777-7777-7777-777777777777\"]}]}";

        var actual = PolicyCanonicalizer.CanonicalizeEvaluationResult(
            new string('i', 64), [scope], [control], PolicyResult.RequirementsGenerated, null);
        Assert.Equal(expected, actual);
        Assert.Equal("2bdae9241f15c00b6c67f5e724b996a02bb6b65fd3b10d51551a1700c866dc3b", PolicyCanonicalizer.Hash(actual));
    }

    [Fact]
    public void Canonical_set_order_uses_utf8_byte_order()
    {
        // UTF-16 compares the supplementary code point before U+E000; UTF-8 does not.
        Assert.True(PolicyCanonicalizer.CompareCanonical("\uE000", "\U00010000") < 0);
    }

    [Fact]
    public void Policy_value_set_rejects_duplicate_members()
    {
        Assert.Throws<DomainConflictException>(() =>
            PolicyValue.Set(PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64)), PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64))));
    }
}
