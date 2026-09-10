using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Policy;

public sealed class PolicyModelTests
{
    [Fact]
    public void Policy_version_requires_one_fallback_per_enabled_scope_before_publish()
    {
        var policy = CreatePolicy(PolicyScope.Line);
        policy.AddRule(new PolicyRule(
            "AMOUNT_RULE",
            PolicyScope.Line,
            [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(500))],
            [new PolicyEffect(PolicyEffectType.RequirePo, "PO_REQUIRED")]));

        Assert.Throws<DomainValidationException>(() => policy.Publish(new string('a', 64)));

        policy.AddRule(new PolicyRule(
            "DEFAULT_LINE",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")],
            isFallback: true));

        policy.Publish(new string('a', 64));
        Assert.Equal(PolicySetStatus.Published, policy.Status);
        Assert.Throws<DomainConflictException>(() => policy.AddRule(
            new PolicyRule("LATE", PolicyScope.Line, [], [new PolicyEffect(PolicyEffectType.Allow, "LATE")])));
    }

    [Fact]
    public void Versioned_fact_references_validate_their_contract_shape()
    {
        var digest = new string('a', 64);
        var code = new VersionedCodeRef("SPEND_CATEGORY", "SOFTWARE", 3, digest);
        var entity = new VersionedEntityRef("SUPPLIER", Guid.NewGuid(), 2);
        var answer = new TypedAnswerRef("DATA_RISK", 1, TypedAnswerValueKind.Boolean, "true");

        Assert.Equal("SPEND_CATEGORY", code.Catalog);
        Assert.Equal(3, code.Version);
        Assert.Equal(2, entity.Version);
        Assert.Equal(TypedAnswerValueKind.Boolean, answer.ValueKind);
        Assert.Throws<DomainValidationException>(() => new VersionedCodeRef("CAT", "CODE", 0, digest));
        // pi-lens-ignore: lsp:CS0246
        Assert.Throws<DomainValidationException>(() => new TypedAnswerRef(
            // pi-lens-ignore: lsp:CS0103
            "DATA_RISK", 1, TypedAnswerValueKind.Boolean, "maybe"));
    }

    [Fact]
    public void Fallback_must_be_unconditional_allow_or_block()
    {
        Assert.Throws<DomainValidationException>(() => new PolicyRule(
            "DEFAULT",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.RequirePo, "DEFAULT")],
            isFallback: true));
    }

    [Fact]
    public void Policy_version_rejects_duplicate_codes_and_disabled_scope_rules()
    {
        var duplicatePolicy = CreatePolicy(PolicyScope.Line);
        duplicatePolicy.AddRule(new PolicyRule("DEFAULT", PolicyScope.Line, [], [
            new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));
        Assert.Throws<DomainConflictException>(() => duplicatePolicy.AddRule(new PolicyRule(
            "DEFAULT", PolicyScope.Line, [], [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT_2")])));

        var disabledScopePolicy = CreatePolicy(PolicyScope.Line);
        disabledScopePolicy.AddRule(new PolicyRule("DEFAULT", PolicyScope.Line, [], [
            new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));
        disabledScopePolicy.AddRule(new PolicyRule("REQUEST_RULE", PolicyScope.Request, [], [
            new PolicyEffect(PolicyEffectType.Allow, "REQUEST")], isFallback: true));

        Assert.Throws<DomainValidationException>(() => disabledScopePolicy.ValidateForPublication());
    }

    [Fact]
    public void Publication_rejects_incompatible_effect_types_for_the_same_requirement_key()
    {
        var policy = CreatePolicy(PolicyScope.Line);
        policy.AddRule(new PolicyRule(
            "BLOCK_RULE", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Block, "SHARED_KEY", "blocked")]));
        policy.AddRule(new PolicyRule(
            "PO_RULE", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.RequirePo, "SHARED_KEY")]));
        policy.AddRule(new PolicyRule(
            "DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")], isFallback: true));

        Assert.Throws<DomainConflictException>(() => policy.ValidateForPublication());
    }

    [Fact]
    public void Approval_descriptor_requires_versioned_authority_except_for_it_and_legal()
    {
        var none = new PolicyApprovalDescriptor(
            SystemRole.ItReviewer,
            null,
            null,
            null,
            null,
            "ORGANIZATION");
        Assert.Null(none.AuthorityLevel);

        Assert.Throws<DomainValidationException>(() => new PolicyApprovalDescriptor(
            SystemRole.FinanceApprover,
            ApprovalAuthorityType.Financial,
            null,
            null,
            null,
            "ORGANIZATION"));
    }

    [Fact]
    public void Policy_predicates_reject_mismatched_operator_values()
    {
        Assert.Throws<DomainValidationException>(() => new PolicyPredicate(
            "CONTRACT_REQUIRED",
            PolicyOperator.IsTrue,
            PolicyValue.Code("ACTIVE")));

        Assert.Throws<DomainValidationException>(() => new PolicyPredicate(
            "PURCHASE_TYPE",
            PolicyOperator.GreaterThan,
            PolicyValue.Code("GOOD")));
    }

    [Fact]
    public void Quotation_effect_requires_a_strictly_lower_exception_floor()
    {
        Assert.Throws<DomainValidationException>(() => new PolicyEffect(
            PolicyEffectType.RequireQuotations,
            "RFQ",
            minimumQuotations: 2,
            minimumExceptionQuotations: 2));

        var effect = new PolicyEffect(
            PolicyEffectType.RequireQuotations,
            "RFQ",
            minimumQuotations: 3,
            minimumExceptionQuotations: 1);
        Assert.Equal(3, effect.MinimumQuotations);
        Assert.Equal(1, effect.MinimumExceptionQuotations);
    }

    private static PolicySetVersion CreatePolicy(PolicyScope scope) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, [scope]);
}
