using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.UnitTests.Policy;

public sealed class PolicyEvaluationDiffTests
{
    private static readonly Guid Subject = Guid.NewGuid();

    [Fact]
    public void Added_when_the_reevaluation_generates_a_control_that_did_not_exist()
    {
        var previous = Bundle([]);
        var current = Bundle([Control("PO_REQUIRED", PolicyEffectType.RequirePo)]);

        var diff = PolicyEvaluationDiff.Compute(previous, current);

        var entry = Assert.Single(diff);
        Assert.Equal(PolicyEvaluationDiff.Added, entry.Change);
        Assert.Equal("PO_REQUIRED", entry.RequirementKey);
        Assert.Equal(PolicyEffectType.RequirePo, entry.Type);
        Assert.Null(entry.PreviousMinimumQuotations);
    }

    [Fact]
    public void Removed_when_the_previous_control_is_absent_or_relaxed()
    {
        var absent = PolicyEvaluationDiff.Compute(
            Bundle([Control("PROCUREMENT", PolicyEffectType.RequireProcurement)]),
            Bundle([]));
        Assert.Equal(PolicyEvaluationDiff.Removed, Assert.Single(absent).Change);

        var relaxed = PolicyEvaluationDiff.Compute(
            Bundle([Control("RFQ", PolicyEffectType.RequireQuotations, minimumQuotations: 3, minimumAllowed: 1)]),
            Bundle([Control("RFQ", PolicyEffectType.RequireQuotations, minimumQuotations: 2, minimumAllowed: 1)]));
        var waiver = Assert.Single(relaxed);
        Assert.Equal(PolicyEvaluationDiff.Removed, waiver.Change);
        Assert.Equal(3, waiver.PreviousMinimumQuotations);
        Assert.Equal(2, waiver.CurrentMinimumQuotations);
    }

    [Fact]
    public void Unchanged_when_key_type_subjects_phase_and_parameters_match()
    {
        var approval = Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 2, amount: 500);
        var previous = Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, approval)]);
        var current = Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, approval)]);

        var entry = Assert.Single(PolicyEvaluationDiff.Compute(previous, current));

        Assert.Equal(PolicyEvaluationDiff.Unchanged, entry.Change);
    }

    [Fact]
    public void Hardened_when_authority_amount_or_documents_increase()
    {
        var authority = Assert.Single(PolicyEvaluationDiff.Compute(
            Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 2, amount: 500))]),
            Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 3, amount: 500))])));
        Assert.Equal(PolicyEvaluationDiff.Hardened, authority.Change);

        var amount = Assert.Single(PolicyEvaluationDiff.Compute(
            Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 2, amount: 500))]),
            Bundle([Control("FINANCE", PolicyEffectType.RequireApproval, Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 2, amount: 900))])));
        Assert.Equal(PolicyEvaluationDiff.Hardened, amount.Change);

        var documents = Assert.Single(PolicyEvaluationDiff.Compute(
            Bundle([Control("DOCS", PolicyEffectType.RequireSupportingDocument, documents: ["QUOTE"])]),
            Bundle([Control("DOCS", PolicyEffectType.RequireSupportingDocument, documents: ["QUOTE", "CONTRACT"])])));
        Assert.Equal(PolicyEvaluationDiff.Hardened, documents.Change);
    }

    [Fact]
    public void Distinct_keys_and_subjects_are_reported_separately()
    {
        var otherSubject = Guid.NewGuid();
        var levelTwo = Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 2, amount: 500);
        var levelFour = Approval(SystemRole.FinanceApprover, ApprovalAuthorityType.Financial, rank: 4, amount: 500);
        var previous = Bundle([
            Control("FINANCE", PolicyEffectType.RequireApproval, levelTwo),
            Control("FINANCE", PolicyEffectType.RequireApproval, levelTwo, subject: otherSubject),
            Control("RFQ", PolicyEffectType.RequireQuotations, minimumQuotations: 3)
        ]);
        var current = Bundle([
            Control("FINANCE", PolicyEffectType.RequireApproval, levelTwo),
            Control("FINANCE", PolicyEffectType.RequireApproval, levelFour, subject: otherSubject)
        ]);

        var diff = PolicyEvaluationDiff.Compute(previous, current);

        Assert.Equal(3, diff.Count);
        Assert.Contains(diff, entry => entry.Change == PolicyEvaluationDiff.Unchanged && entry.SubjectIds.Contains(Subject));
        Assert.Contains(diff, entry => entry.Change == PolicyEvaluationDiff.Hardened && entry.SubjectIds.Contains(otherSubject));
        Assert.Contains(diff, entry => entry.Change == PolicyEvaluationDiff.Removed && entry.RequirementKey == "RFQ");
    }

    [Fact]
    public void Coverage_growth_is_hardened_and_coverage_shrinkage_is_removed()
    {
        var secondSubject = Guid.NewGuid();
        var previous = Bundle([Control("DOCS", PolicyEffectType.RequireSupportingDocument, documents: ["QUOTE"])]);
        var grown = Bundle([Control("DOCS", PolicyEffectType.RequireSupportingDocument, documents: ["QUOTE"], extraSubjects: [secondSubject])]);
        var shrunk = Bundle([Control("DOCS", PolicyEffectType.RequireSupportingDocument, documents: ["QUOTE", "CONTRACT"])]);

        Assert.Equal(PolicyEvaluationDiff.Hardened, Assert.Single(PolicyEvaluationDiff.Compute(previous, grown)).Change);
        Assert.Equal(PolicyEvaluationDiff.Removed, Assert.Single(PolicyEvaluationDiff.Compute(shrunk, previous)).Change);
    }

    private static PolicyEvaluationBundle Bundle(IReadOnlyList<PolicyGeneratedControl> controls) =>
        new(
            Guid.NewGuid(),
            "diff-eval",
            new PolicySubjectReference(Subject, 1),
            DateTimeOffset.UnixEpoch,
            new string('a', 64),
            new string('b', 64),
            [],
            controls,
            controls.Count == 0 ? PolicyResult.Passed : PolicyResult.RequirementsGenerated,
            new string('c', 64));

    private static PolicyApprovalDescriptor Approval(
        SystemRole role,
        ApprovalAuthorityType authorityType,
        int rank,
        decimal amount) =>
        new(
            role,
            authorityType,
            new PolicyAuthorityLevelSnapshot(Guid.NewGuid(), 1, "LEVEL", rank),
            amount,
            "PEN",
            "ORGANIZATION");

    private static PolicyGeneratedControl Control(
        string key,
        PolicyEffectType type,
        PolicyApprovalDescriptor? approval = null,
        int? minimumQuotations = null,
        int? minimumAllowed = null,
        string phase = "PRE_PROCUREMENT",
        Guid? subject = null,
        IReadOnlyList<string>? documents = null,
        IReadOnlyList<Guid>? extraSubjects = null) =>
        new(
            key,
            type,
            ImmutableHashSet.Create(PolicyScope.Line),
            (extraSubjects is null ? ImmutableHashSet.Create(subject ?? Subject) : ImmutableHashSet.Create(subject ?? Subject).Union(extraSubjects)).ToImmutableHashSet(),
            phase,
            approval,
            minimumQuotations,
            documents is null
                ? ImmutableHashSet<string>.Empty
                : ImmutableHashSet.CreateRange(StringComparer.Ordinal, documents),
            ImmutableHashSet.Create("RULE:1"),
            "RULE")
        {
            MinimumAllowedQuotations = minimumAllowed
        };
}
