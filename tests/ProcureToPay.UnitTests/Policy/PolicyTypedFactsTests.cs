using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>Typed facts, type-per-fact validation, MoneyBase and budget control parameters (REQ-04, REQ-07, CA-02, CA-05).</summary>
public sealed class PolicyTypedFactsTests
{
    [Fact]
    public void Fact_catalog_rejects_values_or_operators_outside_the_declared_type()
    {
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("PURCHASE_TYPE", PolicyOperator.GreaterThan, PolicyValue.Money(10, "PEN")));
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.Equal, PolicyValue.Money(10, "PEN")));
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.Code("HIGH")));
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("COST_CENTER_ACTIVE", PolicyOperator.IsTrue, PolicyValue.Code("YES")));
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("PURCHASE_TYPE", PolicyOperator.Equal, PolicyValue.Code("WEIRD")));
        Assert.Throws<DomainValidationException>(() =>
            new PolicyPredicate("LEGAL_ENTITY_ID", PolicyOperator.Equal,
                PolicyValue.EntityRef("LEGAL_ENTITY", Guid.NewGuid(), 1)));

        _ = new PolicyPredicate("PURCHASE_TYPE", PolicyOperator.Equal, PolicyValue.Code("GOOD"));
        _ = new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(1000, "PEN"));
        _ = new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, Risk("HIGH"));
        _ = new PolicyPredicate("COST_CENTER_ACTIVE", PolicyOperator.IsTrue, PolicyValue.Boolean(true));
        _ = new PolicyPredicate("RISK_ANSWER", PolicyOperator.Equal,
            PolicyValue.TypedAnswer("SECURITY_REVIEW", 1, TypedAnswerValueKind.Boolean, "true"));
    }

    [Fact]
    public void Money_facts_must_use_the_bundle_base_currency()
    {
        var policy = PublishedPolicy();
        var foreignLine = new PolicyLineInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10, "USD")
            });
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            policy.OrganizationId,
            Guid.NewGuid(),
            "PEN",
            [foreignLine]);

        Assert.Throws<DomainValidationException>(() =>
            PolicyEvaluator.EvaluateRequest(policy, request, "currency-mismatch", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Budget_control_carries_cost_centers_amount_currency_and_origin_facts()
    {
        var organizationId = Guid.NewGuid();
        var costCenterId = Guid.NewGuid();
        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, [PolicyScope.Line]);
        policy.AddRule(new PolicyRule("BUDGET", PolicyScope.Line,
            [new PolicyPredicate("COST_CENTER_ACTIVE", PolicyOperator.IsTrue, PolicyValue.Boolean(true))],
            [new PolicyEffect(PolicyEffectType.RequireBudgetCheck, "BUDGET_CHECK")]));
        policy.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        var line = new PolicyLineInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(500, "PEN"),
                ["COST_CENTER"] = PolicyValue.EntityRef("COST_CENTER", costCenterId, 2),
                ["COST_CENTER_ACTIVE"] = PolicyValue.Boolean(true)
            });
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1), organizationId, Guid.NewGuid(), "PEN", [line]);

        var result = PolicyEvaluator.EvaluateRequest(
            policy, request, "budget-params", DateTimeOffset.UtcNow);

        var budget = Assert.Single(result.Controls, control => control.RequirementKey == "BUDGET_CHECK");
        Assert.Equal(costCenterId, Assert.Single(budget.CostCenterIds));
        Assert.Equal(500m, budget.AmountBase);
        Assert.Equal("PEN", budget.BaseCurrency);
        Assert.Contains("COST_CENTER_ACTIVE", budget.OriginFacts);
    }

    private static PolicyValue Risk(string code) =>
        PolicyValue.VersionedCode("DATA_RISK", code, 1, new string('a', 64));

    private static PolicySetVersion PublishedPolicy()
    {
        var policy = new PolicySetVersion(Guid.NewGuid(), Guid.NewGuid(), 1, [PolicyScope.Line]);
        policy.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        return policy;
    }
}
