#pragma warning disable CS0246, CS0103

using System.Collections.Immutable;
using System.Text.Json;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Policy;

public sealed class PolicyEvaluatorTests
{
    [Fact]
    public void Canonical_policy_digest_is_independent_of_rule_insertion_order()
    {
        var organizationId = Guid.NewGuid();
        var first = CreatePublishedPolicy(organizationId, reverse: false);
        var second = CreatePublishedPolicy(organizationId, reverse: true);

        Assert.Equal(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void Request_evaluation_aggregates_lines_and_combines_line_and_request_controls()
    {
        var organizationId = Guid.NewGuid();
        var policy = CreatePublishedPolicy(organizationId, reverse: false);
        // pi-lens-ignore: CS0246
        var lineOne = new PolicyLineInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(400),
                ["DATA_RISK"] = PolicyValue.Code("HIGH")
            });
        // pi-lens-ignore: CS0246
        var lineTwo = new PolicyLineInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(700),
                ["DATA_RISK"] = PolicyValue.Code("LOW")
            });
        // pi-lens-ignore: CS0246
        var request = new PolicyRequestInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            organizationId,
            Guid.NewGuid(),
            "PEN",
            [lineOne, lineTwo]);

        // pi-lens-ignore: CS0103
        var result = PolicyEvaluator.EvaluateRequest(
            policy,
            request,
            "eval-001",
            DateTimeOffset.Parse("2026-09-09T15:00:00Z"));

        Assert.Equal(PolicyResult.RequirementsGenerated, result.Result);
        Assert.Contains(result.Controls, control => control.Type == PolicyEffectType.RequirePo);
        Assert.Contains(result.Controls, control =>
            control.Type == PolicyEffectType.RequireApproval &&
            control.Approval?.Role == SystemRole.ItReviewer &&
            control.SubjectIds.Contains(lineOne.Subject.Id));
        Assert.DoesNotContain(result.Controls, control => control.Type == PolicyEffectType.AllowDirectPurchase);
    }

    [Fact]
    public void Sourcing_evaluation_is_a_bundle_linked_to_the_current_request_bundle()
    {
        var organizationId = Guid.NewGuid();
        var policy = CreatePublishedPolicy(organizationId, reverse: false, includeSourcing: true);
        // pi-lens-ignore: CS0246
        var line = new PolicyLineInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(100),
                ["DATA_RISK"] = PolicyValue.Code("LOW")
            });
        // pi-lens-ignore: CS0246
        var request = new PolicyRequestInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            organizationId,
            Guid.NewGuid(),
            "PEN",
            [line]);
        // pi-lens-ignore: CS0103
        var requestEvaluation = PolicyEvaluator.EvaluateRequest(
            policy, request, "request-001", DateTimeOffset.Parse("2026-09-09T15:00:00Z"));
        // pi-lens-ignore: CS0246
        var sourcing = new PolicySourcingInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            request,
            [line.Subject],
            new Dictionary<string, PolicyValue>(),
            requestEvaluation);

        // pi-lens-ignore: CS0103
        var result = PolicyEvaluator.EvaluateSourcing(
            policy, sourcing, "sourcing-001", DateTimeOffset.Parse("2026-09-09T15:00:00Z"));

        Assert.Single(result.ScopeEvaluations);
        Assert.Equal(PolicyScope.SourcingPo, result.ScopeEvaluations[0].Scope);
        Assert.Equal(requestEvaluation.Id, result.PreviousBundleId);
    }

    [Fact]
    public void Persisted_bundle_json_rehydrates_without_changing_contract_fields()
    {
        var organizationId = Guid.NewGuid();
        var policy = CreatePublishedPolicy(organizationId, reverse: false);
        var line = new PolicyLineInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(100),
                ["DATA_RISK"] = PolicyValue.Code("LOW")
            });
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1), organizationId, Guid.NewGuid(), "PEN", [line]);
        var original = PolicyEvaluator.EvaluateRequest(policy, request, "rehydrate-001", DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var replay = PolicyEvaluationBundleRehydrator.FromJson(json);

        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(original.InputDigest, replay.InputDigest);
        Assert.Equal(original.ResultDigest, replay.ResultDigest);
        Assert.Equal(original.Controls.Count, replay.Controls.Count);
        Assert.Equal(original.ScopeEvaluations.Count, replay.ScopeEvaluations.Count);
    }

    [Fact]
    public void Quotation_waiver_uses_published_from_and_floor_and_recomputes_result_digest()
    {
        var subject = new PolicySubjectReference(Guid.NewGuid(), 1);
        var control = new PolicyGeneratedControl(
            "QUOTATIONS",
            PolicyEffectType.RequireQuotations,
            ImmutableHashSet.Create(PolicyScope.Line),
            ImmutableHashSet.Create(subject.Id),
            "PRE_PROCUREMENT",
            null,
            3,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("RULE:1"),
            "RULE") { MinimumAllowedQuotations = 1 };
        var scope = new PolicyScopeEvaluation(
            PolicyScope.Line,
            ImmutableHashSet.Create(subject.Id),
            ["RULE:1"],
            [control],
            PolicyResult.RequirementsGenerated) { SubjectReferences = [subject] };
        var bundle = new PolicyEvaluationBundle(
            Guid.NewGuid(), "waiver-001", subject, DateTimeOffset.UtcNow, "a".PadLeft(64, 'a'),
            "b".PadLeft(64, 'b'), [scope], [control], PolicyResult.RequirementsGenerated,
            "c".PadLeft(64, 'c'));
        var request = new QuotationWaiverRequest(
            PolicyExceptionType.ReduceMinValidQuotations, 3, 2, 1,
            bundle.PolicyContentDigest, bundle.ResultDigest, "binding", "nonce", "evidence",
            "PROCUREMENT_APPROVER", "PROCUREMENT", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        { TargetRequirementKey = "QUOTATIONS" };
        var evidence = new QuotationWaiverEvidence(
            "evidence", "binding", "nonce", DateTimeOffset.UtcNow.AddMinutes(5), "decision-1");

        var reduced = QuotationWaiverEvaluator.ApplyVerifiedQuotationWaiver(bundle, request, evidence);

        Assert.Equal(2, reduced.Controls.Single().MinimumQuotations);
        Assert.Equal(2, reduced.ScopeEvaluations.Single().Controls.Single().MinimumQuotations);
        Assert.NotEqual(bundle.ResultDigest, reduced.ResultDigest);
        Assert.Throws<DomainConflictException>(() =>
            QuotationWaiverEvaluator.ApplyVerifiedQuotationWaiver(
                bundle, request with { From = 4 }, evidence));
    }

    [Fact]
    public void Block_control_wins_over_allow_and_policy_must_be_published()
    {
        var organizationId = Guid.NewGuid();
        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, [PolicyScope.Line]);
        policy.AddRule(new PolicyRule(
            "BLOCK_HIGH_RISK",
            PolicyScope.Line,
            [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.Code("HIGH"))],
            [new PolicyEffect(PolicyEffectType.Block, "RISK_BLOCKED", "High risk") ]));
        policy.AddRule(new PolicyRule(
            "DEFAULT",
            PolicyScope.Line,
            [],
            [new PolicyEffect(PolicyEffectType.Allow, "DEFAULT")],
            isFallback: true));
        // pi-lens-ignore: CS0103
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));

        // pi-lens-ignore: CS0246
        var request = new PolicyRequestInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            organizationId,
            Guid.NewGuid(),
            "PEN",
            // pi-lens-ignore: CS0246
            [new PolicyLineInput(
                // pi-lens-ignore: CS0246
                new PolicySubjectReference(Guid.NewGuid(), 1),
                new Dictionary<string, PolicyValue>
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10),
                    ["DATA_RISK"] = PolicyValue.Code("HIGH")
                })]);

        // pi-lens-ignore: CS0103
        var result = PolicyEvaluator.EvaluateRequest(policy, request, "block-001", DateTimeOffset.UtcNow);

        Assert.Equal(PolicyResult.Blocked, result.Result);
        Assert.Contains(result.Controls, control => control.Type == PolicyEffectType.Block);
    }

    [Fact]
    public void Evaluation_rejects_unpublished_policy()
    {
        var policy = new PolicySetVersion(Guid.NewGuid(), Guid.NewGuid(), 1, [PolicyScope.Line]);
        // pi-lens-ignore: CS0246
        var request = new PolicyRequestInput(
            // pi-lens-ignore: CS0246
            new PolicySubjectReference(Guid.NewGuid(), 1),
            policy.OrganizationId,
            Guid.NewGuid(),
            "PEN",
            // pi-lens-ignore: CS0246
            [new PolicyLineInput(
                // pi-lens-ignore: CS0246
                new PolicySubjectReference(Guid.NewGuid(), 1),
                new Dictionary<string, PolicyValue>
                {
                    ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(10)
                })]);

        Assert.Throws<DomainConflictException>(() =>
            // pi-lens-ignore: CS0103
            PolicyEvaluator.EvaluateRequest(policy, request, "unpublished", DateTimeOffset.UtcNow));
    }

    private static PolicySetVersion CreatePublishedPolicy(Guid organizationId, bool reverse, bool includeSourcing = false)
    {
        var scopes = includeSourcing
            ? new[] { PolicyScope.Line, PolicyScope.Request, PolicyScope.SourcingPo }
            : new[] { PolicyScope.Line, PolicyScope.Request };
        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, scopes);
        var rules = new PolicyRule[]
        {
            new(
                "LINE_HIGH_RISK",
                PolicyScope.Line,
                [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.Code("HIGH"))],
                [new PolicyEffect(
                    PolicyEffectType.RequireApproval,
                    "IT_REVIEW",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.ItReviewer,
                        null,
                        null,
                        null,
                        null,
                        "ORGANIZATION") )]),
            new(
                "REQUEST_THRESHOLD",
                PolicyScope.Request,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(1000))],
                [new PolicyEffect(PolicyEffectType.RequirePo, "PO_REQUIRED")]),
            new(
                "LINE_LOW_VALUE",
                PolicyScope.Line,
                [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.Code("LOW"))],
                [new PolicyEffect(PolicyEffectType.AllowDirectPurchase, "DIRECT")]),
            new(
                "DEFAULT_LINE",
                PolicyScope.Line,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")],
                isFallback: true),
            new(
                "DEFAULT_REQUEST",
                PolicyScope.Request,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_DEFAULT")],
                isFallback: true),
            new(
                "DEFAULT_SOURCING",
                PolicyScope.SourcingPo,
                [],
                [new PolicyEffect(PolicyEffectType.Allow, "SOURCING_DEFAULT")],
                isFallback: true)
        };

        foreach (var rule in (reverse ? rules.Reverse() : rules).Where(rule => includeSourcing || rule.Scope != PolicyScope.SourcingPo))
        {
            policy.AddRule(rule);
        }

        // pi-lens-ignore: CS0103
        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        return policy;
    }
}
