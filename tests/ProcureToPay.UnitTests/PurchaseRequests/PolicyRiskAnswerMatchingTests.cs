using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseRequests;

/// <summary>
/// SPEC 06 modification of the SPEC 02 matcher (CA-04): <c>RISK_ANSWER</c> is a set of typed
/// answers unique per question and schema, and the predicate identity selects exactly one of them.
/// </summary>
public sealed class PolicyRiskAnswerMatchingTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static TheoryData<PolicyOperator, bool, bool> Matrix => new()
    {
        { PolicyOperator.Equal, true, true },
        { PolicyOperator.Equal, false, false },
        { PolicyOperator.NotEqual, true, false },
        { PolicyOperator.NotEqual, false, true },
        { PolicyOperator.In, true, true },
        { PolicyOperator.In, false, false },
        { PolicyOperator.NotIn, true, false },
        { PolicyOperator.NotIn, false, true }
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Risk_answer_operators_match_by_question_and_schema(
        PolicyOperator @operator,
        bool expectedValue,
        bool matches)
    {
        Assert.Equal(matches, EvaluateSingleLine(
            @operator,
            PredicateAnswer(@operator, expectedValue),
            RiskFact(BooleanAnswer("SECURITY_REVIEW", 1, true))));
    }

    [Fact]
    public void A_missing_answer_never_matches_not_even_a_negative_operator()
    {
        var other = RiskFact(EnumAnswer("DATA_CLASSIFICATION", 1, "INTERNAL"));
        Assert.False(EvaluateSingleLine(PolicyOperator.NotEqual, BooleanAnswer("SECURITY_REVIEW", 1, false), other));
        Assert.False(EvaluateSingleLine(PolicyOperator.NotIn, InAnswer(false), other));
    }

    [Fact]
    public void Several_questions_are_selected_independently()
    {
        var facts = RiskFact(
            BooleanAnswer("SECURITY_REVIEW", 1, true),
            EnumAnswer("DATA_CLASSIFICATION", 1, "HIGH"));
        Assert.True(EvaluateSingleLine(PolicyOperator.Equal, BooleanAnswer("SECURITY_REVIEW", 1, true), facts));
        Assert.True(EvaluateSingleLine(
            PolicyOperator.Equal, EnumAnswer("DATA_CLASSIFICATION", 1, "HIGH"), facts));
        Assert.False(EvaluateSingleLine(PolicyOperator.Equal, BooleanAnswer("SECURITY_REVIEW", 1, false), facts));
    }

    [Fact]
    public void Duplicated_question_and_schema_invalidate_the_bundle()
    {
        Assert.Throws<DomainValidationException>(() => EvaluateSingleLine(
            PolicyOperator.Equal,
            BooleanAnswer("SECURITY_REVIEW", 1, true),
            RiskFact(BooleanAnswer("SECURITY_REVIEW", 1, true), BooleanAnswer("SECURITY_REVIEW", 1, false))));
    }

    [Fact]
    public void An_answer_of_another_schema_version_does_not_match()
    {
        Assert.False(EvaluateSingleLine(
            PolicyOperator.Equal,
            BooleanAnswer("SECURITY_REVIEW", 2, true),
            RiskFact(BooleanAnswer("SECURITY_REVIEW", 1, true))));
    }

    [Fact]
    public void Predicate_validation_requires_homogeneous_sets_for_in_and_not_in()
    {
        _ = new PolicyPredicate("RISK_ANSWER", PolicyOperator.In, PolicyValue.Set(
            BooleanAnswer("SECURITY_REVIEW", 1, true),
            BooleanAnswer("SECURITY_REVIEW", 1, false)));
        Assert.Throws<DomainValidationException>(() => new PolicyPredicate(
            "RISK_ANSWER",
            PolicyOperator.In,
            PolicyValue.Set(
                BooleanAnswer("SECURITY_REVIEW", 1, true),
                BooleanAnswer("DATA_CLASSIFICATION", 1, true))));
        Assert.Throws<DomainValidationException>(() => new PolicyPredicate(
            "RISK_ANSWER",
            PolicyOperator.In,
            BooleanAnswer("SECURITY_REVIEW", 1, true)));
        Assert.Throws<DomainValidationException>(() => new PolicyPredicate(
            "RISK_ANSWER",
            PolicyOperator.GreaterThan,
            BooleanAnswer("SECURITY_REVIEW", 1, true)));
    }

    private static PolicyValue PredicateAnswer(PolicyOperator @operator, bool value) =>
        @operator is PolicyOperator.In or PolicyOperator.NotIn ? InAnswer(value) : BooleanAnswer("SECURITY_REVIEW", 1, value);

    private static PolicyValue InAnswer(bool value) =>
        PolicyValue.Set(BooleanAnswer("SECURITY_REVIEW", 1, value));

    private static PolicyValue BooleanAnswer(string question, int schemaVersion, bool value) =>
        PolicyValue.TypedAnswer(
            question,
            schemaVersion,
            TypedAnswerValueKind.Boolean,
            value ? "true" : "false");

    private static PolicyValue EnumAnswer(string question, int schemaVersion, string value) =>
        PolicyValue.TypedAnswer(question, schemaVersion, TypedAnswerValueKind.EnumCode, value);

    private static IReadOnlyDictionary<string, PolicyValue> RiskFact(params PolicyValue[] answers) =>
        new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
        {
            ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(100, "PEN"),
            ["RISK_ANSWER"] = PolicyValue.Set(answers)
        };

    private static bool EvaluateSingleLine(
        PolicyOperator @operator,
        PolicyValue expected,
        IReadOnlyDictionary<string, PolicyValue> facts)
    {
        var policy = new PolicySetVersion(Guid.NewGuid(), OrganizationId, 1, [PolicyScope.Line]);
        policy.AddRule(new PolicyRule(
            "RISK_RULE",
            PolicyScope.Line,
            [new PolicyPredicate("RISK_ANSWER", @operator, expected)],
            [new PolicyEffect(PolicyEffectType.RequireProcurement, "PROCUREMENT_REVIEW")],
            isFallback: false));
        policy.AddRule(new PolicyRule(
            "LINE_DEFAULT",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.Allow, "LINE_ALLOW")],
            isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            OrganizationId,
            Guid.NewGuid(),
            "PEN",
            [new PolicyLineInput(new PolicySubjectReference(Guid.NewGuid(), 1), facts)]);
        var bundle = PolicyEvaluator.EvaluateRequest(
            policy, request, $"risk-eval-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        return bundle.Controls.Any(control => control.Type == PolicyEffectType.RequireProcurement);
    }
}
