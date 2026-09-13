using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Policy;

/// <summary>Cross-scope combination and line/request granularity (CA-04, CA-05, CA-10).</summary>
public sealed class PolicyCombinationTests
{
    [Fact]
    public void Request_controls_expand_over_every_line_declared_by_the_manifest()
    {
        var organizationId = Guid.NewGuid();
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line, PolicyScope.Request],
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true),
            new PolicyRule("THRESHOLD", PolicyScope.Request,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(1000, "PEN"))],
                [new PolicyEffect(PolicyEffectType.RequirePo, "PO_REQUIRED")]),
            new PolicyRule("REQUEST_DEFAULT", PolicyScope.Request, [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_DEFAULT")], isFallback: true));
        var first = Line(300, ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "LOW", 1, new string('a', 64))));
        var second = Line(900, ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "LOW", 1, new string('a', 64))));

        var result = PolicyEvaluator.EvaluateRequest(
            policy, Request(organizationId, first, second), "cross-scope-1", DateTimeOffset.UtcNow);

        var po = Assert.Single(result.Controls, control => control.RequirementKey == "PO_REQUIRED");
        Assert.Contains(PolicyScope.Request, po.OriginScopes);
        Assert.True(po.SubjectIds.SetEquals([first.Subject.Id, second.Subject.Id]));
        Assert.Equal(PolicyResult.RequirementsGenerated, result.Result);
        Assert.Single(result.ScopeEvaluations, scope => scope.Scope == PolicyScope.Request);
        Assert.Equal(2, result.ScopeEvaluations.Count(scope => scope.Scope == PolicyScope.Line));
    }

    [Fact]
    public void Block_in_any_line_blocks_the_combined_bundle_and_beats_allow()
    {
        var organizationId = Guid.NewGuid();
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line],
            new PolicyRule("BLOCK_HIGH", PolicyScope.Line,
                [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64)))],
                [new PolicyEffect(PolicyEffectType.Block, "RISK_BLOCKED", "high risk")]),
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
        var risky = Line(10, ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64))));
        var safe = Line(10, ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "LOW", 1, new string('a', 64))));

        var result = PolicyEvaluator.EvaluateRequest(
            policy, Request(organizationId, risky, safe), "cross-scope-block", DateTimeOffset.UtcNow);

        Assert.Equal(PolicyResult.Blocked, result.Result);
        var block = Assert.Single(result.Controls, control => control.Type == PolicyEffectType.Block);
        Assert.True(block.SubjectIds.SetEquals([risky.Subject.Id]));
    }

    [Fact]
    public void Same_key_keeps_the_strictest_variant_and_distinct_keys_survive()
    {
        var organizationId = Guid.NewGuid();
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line],
            new PolicyRule("PO_AND_DIRECT", PolicyScope.Line, [],
                [
                    new PolicyEffect(PolicyEffectType.RequirePo, "FULFILLMENT"),
                    new PolicyEffect(PolicyEffectType.AllowDirectPurchase, "FULFILLMENT")
                ]),
            new PolicyRule("QUOTATIONS_LOW", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireQuotations, "RFQ", minimumQuotations: 2)]),
            new PolicyRule("QUOTATIONS_HIGH", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireQuotations, "RFQ", minimumQuotations: 5)]),
            new PolicyRule("DOC_QUOTE", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireSupportingDocument, "DOCUMENTS",
                    supportingDocumentTypes: new HashSet<string> { "QUOTE" })]),
            new PolicyRule("DOC_CONTRACT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireSupportingDocument, "DOCUMENTS",
                    supportingDocumentTypes: new HashSet<string> { "CONTRACT" })]),
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));

        var result = PolicyEvaluator.EvaluateRequest(
            policy, Request(organizationId, Line(10)), "cross-scope-strict", DateTimeOffset.UtcNow);

        Assert.Contains(result.Controls, control => control.RequirementKey == "FULFILLMENT" &&
            control.Type == PolicyEffectType.RequirePo);
        Assert.DoesNotContain(result.Controls, control =>
            control.RequirementKey == "FULFILLMENT" && control.Type == PolicyEffectType.AllowDirectPurchase);
        Assert.Equal(5, Assert.Single(result.Controls, control => control.RequirementKey == "RFQ").MinimumQuotations);
        var documents = Assert.Single(result.Controls, control => control.RequirementKey == "DOCUMENTS");
        Assert.True(documents.SupportingDocumentTypes.SetEquals(["QUOTE", "CONTRACT"]));
    }

    [Fact]
    public void Identical_line_descriptors_regroup_but_incompatible_authority_fails_publication()
    {
        var organizationId = Guid.NewGuid();
        var levelTwo = new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "FIN_L2", 2);
        var levelFour = new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "FIN_L4", 4);
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line],
            new PolicyRule("FINANCE_LOW", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireApproval, "FINANCE",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, levelTwo, 500, "PEN", PolicyScopeFixtures.Organization()))]),
            new PolicyRule("FINANCE_HIGH", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireApproval, "FINANCE",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, levelFour, 900, "PEN", PolicyScopeFixtures.Organization()))]),
            new PolicyRule("PROCUREMENT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.RequireProcurement, "PROCUREMENT")]),
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
        var first = Line(10);
        var second = Line(10);

        var result = PolicyEvaluator.EvaluateRequest(
            policy, Request(organizationId, first, second), "cross-scope-authority", DateTimeOffset.UtcNow);

        var finance = Assert.Single(result.Controls, control => control.RequirementKey == "FINANCE");
        Assert.Equal(4, finance.Approval!.AuthorityLevel!.Rank);
        Assert.Equal(900m, finance.Approval.AmountBase);
        Assert.True(finance.SubjectIds.SetEquals([first.Subject.Id, second.Subject.Id]));
        Assert.Contains(result.Controls, control => control.RequirementKey == "PROCUREMENT");

        var incompatible = new PolicySetVersion(Guid.NewGuid(), organizationId, 2, [PolicyScope.Line]);
        incompatible.AddRule(new PolicyRule("IT_REVIEW", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.RequireApproval, "REVIEW",
                approval: new PolicyApprovalDescriptor(SystemRole.ItReviewer, null, null, null, null, PolicyScopeFixtures.Organization()))]));
        incompatible.AddRule(new PolicyRule("LEGAL_REVIEW", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.RequireApproval, "REVIEW",
                approval: new PolicyApprovalDescriptor(SystemRole.LegalReviewer, null, null, null, null, PolicyScopeFixtures.Organization()))]));
        incompatible.AddRule(new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
            [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));

        Assert.Throws<DomainConflictException>(() =>
            incompatible.Publish(PolicyCanonicalizer.ComputePolicyDigest(incompatible)));
    }

    [Fact]
    public void Department_approval_targets_the_cost_center_owner_not_the_beneficiary_department()
    {
        var organizationId = Guid.NewGuid();
        var costCenterDepartment = new PolicyValueReference(Guid.NewGuid(), 1);
        var beneficiaryDepartment = new PolicyValueReference(Guid.NewGuid(), 1);
        var authority = new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "DEPT_L1", 1);
        var departmentScope = PolicyScopeFixtures.Department(Guid.NewGuid(), 1);
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line],
            new PolicyRule("DEPARTMENT_APPROVAL", PolicyScope.Line,
                [new PolicyPredicate("COST_CENTER_DEPARTMENT", PolicyOperator.Equal,
                    PolicyValue.Reference("DEPARTMENT", costCenterDepartment.Id, costCenterDepartment.Version))],
                [new PolicyEffect(PolicyEffectType.RequireApproval, "DEPT_APPROVAL",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.DepartmentApprover, ApprovalAuthorityType.BusinessNeed,
                        authority, 500, "PEN", departmentScope))]),
            new PolicyRule("RISK_IT_REVIEW", PolicyScope.Line,
                [new PolicyPredicate("DATA_RISK", PolicyOperator.Equal, PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64)))],
                [new PolicyEffect(PolicyEffectType.RequireApproval, "IT_REVIEW",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.ItReviewer, null, null, null, null, PolicyScopeFixtures.Organization()))]),
            new PolicyRule("CONTRACT_LEGAL_REVIEW", PolicyScope.Line,
                [new PolicyPredicate("CONTRACT_REQUIRED", PolicyOperator.IsTrue, PolicyValue.Boolean(true))],
                [new PolicyEffect(PolicyEffectType.RequireApproval, "LEGAL_REVIEW",
                    approval: new PolicyApprovalDescriptor(
                        SystemRole.LegalReviewer, null, null, null, null, PolicyScopeFixtures.Organization()))]),
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true));
        // Cheap line: risk and contract demand reviews even though the amount is low.
        var covered = Line(10,
            ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "HIGH", 1, new string('a', 64))),
            ("CONTRACT_REQUIRED", PolicyValue.Boolean(true)),
            ("COST_CENTER_DEPARTMENT", PolicyValue.Reference("DEPARTMENT", costCenterDepartment.Id, 1)),
            ("BENEFICIARY_DEPARTMENT", PolicyValue.Reference("DEPARTMENT", beneficiaryDepartment.Id, 1)));
        // Same amount and risk profile, but the Cost Center belongs to another Department.
        var uncovered = Line(10,
            ("DATA_RISK", PolicyValue.VersionedCode("DATA_RISK", "LOW", 1, new string('a', 64))),
            ("CONTRACT_REQUIRED", PolicyValue.Boolean(false)),
            ("COST_CENTER_DEPARTMENT", PolicyValue.Reference("DEPARTMENT", beneficiaryDepartment.Id, 1)),
            ("BENEFICIARY_DEPARTMENT", PolicyValue.Reference("DEPARTMENT", costCenterDepartment.Id, 1)));

        var result = PolicyEvaluator.EvaluateRequest(
            policy, Request(organizationId, covered, uncovered), "cost-center-owner", DateTimeOffset.UtcNow);

        var department = Assert.Single(result.Controls, control => control.RequirementKey == "DEPT_APPROVAL");
        Assert.Equal(SystemRole.DepartmentApprover, department.Approval!.Role);
        Assert.Equal(ApprovalAuthorityType.BusinessNeed, department.Approval.AuthorityType);
        Assert.Equal(departmentScope, department.Approval.DecisionScope);
        Assert.True(department.SubjectIds.SetEquals([covered.Subject.Id]));

        var itReview = Assert.Single(result.Controls, control => control.RequirementKey == "IT_REVIEW");
        Assert.Equal(SystemRole.ItReviewer, itReview.Approval!.Role);
        Assert.Null(itReview.Approval.AuthorityType);
        Assert.Null(itReview.Approval.AuthorityLevel);
        Assert.Contains("RISK_IT_REVIEW:1", itReview.OriginRuleCodes);

        var legalReview = Assert.Single(result.Controls, control => control.RequirementKey == "LEGAL_REVIEW");
        Assert.Equal(SystemRole.LegalReviewer, legalReview.Approval!.Role);
        Assert.Contains("CONTRACT_LEGAL_REVIEW:1", legalReview.OriginRuleCodes);

        Assert.DoesNotContain(result.Controls, control =>
            control.SubjectIds.Contains(uncovered.Subject.Id) &&
            control.OriginRuleCodes.Any(code => code.StartsWith("DEPARTMENT_APPROVAL", StringComparison.Ordinal)));
    }

    [Fact]
    public void Request_total_is_computed_by_the_engine_and_ignores_a_caller_supplied_aggregate()
    {
        var organizationId = Guid.NewGuid();
        var policy = PublishedPolicy(organizationId, [PolicyScope.Line, PolicyScope.Request],
            new PolicyRule("LINE_DEFAULT", PolicyScope.Line, [],
                [new PolicyEffect(PolicyEffectType.Allow, "LINE_DEFAULT")], isFallback: true),
            new PolicyRule("THRESHOLD", PolicyScope.Request,
                [new PolicyPredicate("GROSS_AMOUNT_BASE", PolicyOperator.GreaterThan, PolicyValue.Money(1000, "PEN"))],
                [new PolicyEffect(PolicyEffectType.RequirePo, "PO_REQUIRED")]),
            new PolicyRule("REQUEST_DEFAULT", PolicyScope.Request, [],
                [new PolicyEffect(PolicyEffectType.Allow, "REQUEST_DEFAULT")], isFallback: true));
        // The caller claims a trivial total; the engine must aggregate the lines instead.
        var request = new PolicyRequestInput(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            organizationId,
            Guid.NewGuid(),
            "PEN",
            [Line(300), Line(900)],
            new Dictionary<string, PolicyValue>(StringComparer.Ordinal)
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(1, "PEN")
            });

        var result = PolicyEvaluator.EvaluateRequest(
            policy, request, "request-total", DateTimeOffset.UtcNow);

        Assert.Contains(result.Controls, control => control.RequirementKey == "PO_REQUIRED");
    }

    private sealed record PolicyValueReference(Guid Id, int Version);

    private static PolicySetVersion PublishedPolicy(
        Guid organizationId,
        IReadOnlyCollection<PolicyScope> scopes,
        params PolicyRule[] rules)
    {
        var policy = new PolicySetVersion(Guid.NewGuid(), organizationId, 1, scopes);
        foreach (var rule in rules)
        {
            policy.AddRule(rule);
        }

        policy.Publish(PolicyCanonicalizer.ComputePolicyDigest(policy));
        return policy;
    }

    private static PolicyLineInput Line(decimal amount, params (string Key, PolicyValue Value)[] facts) =>
        new(
            new PolicySubjectReference(Guid.NewGuid(), 1),
            new Dictionary<string, PolicyValue>(facts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal))
            {
                ["GROSS_AMOUNT_BASE"] = PolicyValue.Money(amount, "PEN")
            });

    private static PolicyRequestInput Request(
        Guid organizationId,
        params PolicyLineInput[] lines) =>
        new(new PolicySubjectReference(Guid.NewGuid(), 1), organizationId, Guid.NewGuid(), "PEN", lines);
}
