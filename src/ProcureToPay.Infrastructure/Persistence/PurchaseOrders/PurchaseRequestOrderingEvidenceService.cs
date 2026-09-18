using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Producer of <c>purchase-request-ordering-evidence/v1</c> (SPEC 11 REQ-03). It rebuilds the proof
/// that the approval case of a Purchase Request version completed with its financial authorities from
/// the persisted case, its requirements, its decisions and its satisfied budget prerequisites, and it
/// refuses to produce evidence for a case that is missing, ambiguous, open or stale.
/// </summary>
public sealed class PurchaseRequestOrderingEvidenceService(ProcureToPayDbContext dbContext)
{
    public async Task<PurchaseRequestOrderingEvidence> ProduceAsync(
        OrderingEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PurchaseOrderCodes.ContractId(request.WorkloadIssuer, "workload_issuer");
        PurchaseOrderCodes.Key(request.WorkloadClientId, "workload_client_id");
        if (request.RequestId == Guid.Empty || request.RequestVersion < 1)
        {
            throw new DomainValidationException("The ordering evidence requires a request version.");
        }

        var version = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == request.RequestId &&
                          record.Version == request.RequestVersion &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The ordered purchase request version is not available.");
        var requestRow = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == request.RequestId && record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException("The ordered request is not available.");
        if (requestRow.CurrentVersion != request.RequestVersion)
        {
            throw new PurchaseOrderUnprocessableException(
                "The ordered purchase request version is no longer current.");
        }

        var cases = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record => record.OrganizationId == request.OrganizationId &&
                             record.SubjectType == "PURCHASE_REQUEST" &&
                             record.SubjectId == request.RequestId &&
                             record.SubjectVersion == request.RequestVersion)
            .OrderByDescending(record => record.Version)
            .ToArrayAsync(cancellationToken);
        if (cases.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The ordered request version has no approval case.");
        }

        var completed = cases
            .Where(record => record.Status == (int)ApprovalCaseStatus.Completed)
            .ToArray();
        if (completed.Length == 0)
        {
            throw new PurchaseOrderUnprocessableException(
                "The approval case of the ordered request version is not completed.");
        }

        if (completed.Length > 1 && cases[0].Status != (int)ApprovalCaseStatus.Completed)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The approval case of the ordered request version is ambiguous.");
        }

        var approved = completed[0];
        var requirements = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .Where(record => record.CaseId == approved.Id)
            .OrderBy(record => record.WorkflowRequirementKey)
            .ToArrayAsync(cancellationToken);
        if (requirements.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The completed approval case carries no requirement.");
        }

        var decisions = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .Where(record => record.CaseId == approved.Id)
            .OrderBy(record => record.RequirementId)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var decisionTargets = await dbContext.ApprovalDecisionTargets
            .AsNoTracking()
            .Where(record => record.CaseId == approved.Id)
            .ToArrayAsync(cancellationToken);
        var evidence = new List<OrderingEvidenceRequirement>(requirements.Length);
        var results = new List<OrderingEvidenceResultRef>(requirements.Length);
        var covered = new Dictionary<string, OrderingEvidenceTarget>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            var targets = Targets(requirement.TargetsJson);
            var authority = Authority(requirement.AuthorityJson);
            evidence.Add(new OrderingEvidenceRequirement(
                requirement.WorkflowRequirementKey,
                RoleCode(requirement.Role),
                ScopeOf(requirement.DecisionScopeJson),
                targets,
                authority.Type,
                authority.Level,
                authority.AmountBase,
                authority.Currency));
            var decision = decisions.LastOrDefault(record => record.RequirementId == requirement.Id)
                ?? throw new PurchaseOrderUnprocessableException(
                    "A completed approval case has a requirement without a decision.");
            var decided = decisionTargets
                .Where(record => record.DecisionId == decision.Id)
                .Select(record => new OrderingEvidenceTarget(
                    record.TargetId, record.TargetVersion, record.MaterialSnapshotDigest))
                .OrderBy(record => record.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray();
            foreach (var target in decided.Length == 0 ? targets : decided)
            {
                if (!covered.TryAdd(target.CanonicalIdentity, target))
                {
                    throw new PurchaseOrderDependencyUnavailableException(
                        "The completed approval case covers a target twice.");
                }
            }

            results.Add(new OrderingEvidenceResultRef(
                requirement.Id,
                ActionCode(decision.Action),
                decision.Id,
                decision.DecisionDigest,
                decision.DecisionKey,
                decided.Length == 0 ? targets : decided));
        }

        // The ordered targets must be exactly the targets the completed case decided; anything else
        // would let a Purchase Order borrow the authority of another line.
        var ordered = request.CoveredTargets.Select(target => target.CanonicalIdentity).ToArray();
        var decidedIdentities = covered.Keys.OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
        if (!ordered.SequenceEqual(decidedIdentities, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The ordered targets are not exactly the targets of the completed approval case.");
        }

        var coveredTargets = ordered.Select(identity => covered[identity]).ToArray();

        var prerequisites = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == approved.Id)
            .OrderBy(record => record.Key)
            .ToArrayAsync(cancellationToken);
        var budget = new List<OrderingEvidenceBudgetRef>();
        foreach (var prerequisite in prerequisites.Where(record =>
                     record.Status == (int)PrerequisiteStatus.Satisfied))
        {
            var signal = await dbContext.ApprovalPrerequisiteSignals
                .AsNoTracking()
                .Where(record => record.PrerequisiteId == prerequisite.Id && record.Satisfied)
                .OrderByDescending(record => record.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
            budget.Add(new OrderingEvidenceBudgetRef(
                prerequisite.Id,
                prerequisite.Key,
                signal?.EvidenceReference ?? prerequisite.SignalKey,
                signal?.EvidenceDigest ?? prerequisite.SourceControlDigest));
        }

        var bundleRef = await PolicyBundleRefAsync(approved, request, cancellationToken);
        var caseRef = new OrderingEvidenceCaseRef(
            approved.Id,
            approved.Version,
            OrderingEvidenceCaseRef.ComputeDigest(
                approved.Id,
                approved.Version,
                approved.Operation,
                approved.SubjectType,
                approved.SubjectId,
                approved.SubjectVersion,
                approved.SourceSnapshotDigest,
                approved.SubmissionFingerprint),
            approved.Operation,
            approved.SubjectId,
            approved.SubjectType,
            approved.SubjectVersion);
        var document = new PurchaseRequestOrderingEvidence(
            request.OrganizationId,
            new PurchaseOrderContentRef(request.RequestId, request.RequestVersion, version.ContentDigest),
            bundleRef,
            caseRef,
            coveredTargets,
            evidence,
            results,
            budget,
            // The evidence is reproducible from its sources: the checked instant is the persisted
            // instant of the completed case, never the caller clock.
            approved.CreatedAt);
        await PersistAsync(approved, document, cancellationToken);
        return document;
    }

    /// <summary>Reads one persisted evidence document by its digest, refusing a corrupted row.</summary>
    public async Task<PurchaseRequestOrderingEvidenceRecord> ReadAsync(
        Guid organizationId,
        string digest,
        CancellationToken cancellationToken = default) =>
        await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == organizationId &&
                          record.ContentDigest == digest,
                cancellationToken)
        ?? throw new PurchaseOrderDependencyUnavailableException(
            "The ordering evidence of the order is not available.");

    private async Task PersistAsync(
        ApprovalCaseRecord approved,
        PurchaseRequestOrderingEvidence document,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == document.OrganizationId &&
                          record.RequestId == document.RequestRef.Id &&
                          record.RequestVersion == document.RequestRef.Version &&
                          record.CaseVersion == approved.Version,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.ContentDigest, document.Digest, StringComparison.Ordinal))
            {
                throw new PurchaseOrderDependencyUnavailableException(
                    "The recorded ordering evidence of this request version is not reproducible.");
            }

            return;
        }

        dbContext.PurchaseRequestOrderingEvidence.Add(new PurchaseRequestOrderingEvidenceRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = document.OrganizationId,
            RequestId = document.RequestRef.Id,
            RequestVersion = document.RequestRef.Version,
            PolicyBundleId = document.PolicyBundleRef.Id,
            PolicyBundleVersion = (int)document.PolicyBundleRef.EvaluationSequence,
            PolicyBundleDigest = document.PolicyBundleRef.ResultDigest,
            CaseId = document.CaseRef.CaseId,
            CaseVersion = document.CaseRef.CaseVersion,
            CaseDigest = document.CaseRef.ContentDigest,
            CoveredTargetsJson = PurchaseOrderSerialization.Targets(document.CoveredTargets),
            RequirementsJson = PurchaseOrderSerialization.Requirements(document.Requirements),
            BudgetEvidenceRefsJson = PurchaseOrderSerialization.BudgetEvidenceRefs(document.BudgetEvidenceRefs),
            ResultRefsJson = PurchaseOrderSerialization.ResultRefs(document.ResultRefs),
            DocumentJson = document.CanonicalDocument(),
            ContentDigest = document.Digest,
            CheckedAt = document.CheckedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<SourcingPolicyEvaluationRef> PolicyBundleRefAsync(
        ApprovalCaseRecord approved,
        OrderingEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var bundleIds = await dbContext.PurchaseRequestSubmissionAttempts
            .AsNoTracking()
            .Where(attempt => attempt.OrganizationId == request.OrganizationId &&
                              attempt.RequestId == request.RequestId &&
                              attempt.RequestVersion == request.RequestVersion &&
                              attempt.PolicyEvaluationBundleId != null)
            .OrderByDescending(attempt => attempt.UpdatedAt)
            .Select(attempt => attempt.PolicyEvaluationBundleId!.Value)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (bundleIds.Length == 0)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The ordered request version has no persisted policy evaluation.");
        }

        if (bundleIds.Length > 1)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The ordered request version has more than one persisted policy evaluation.");
        }

        var bundleId = bundleIds[0];
        var bundle = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == bundleId && record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the ordered request version is unavailable.");
        _ = approved;
        PolicyEvaluationBundle rehydrated;
        try
        {
            rehydrated = PolicyEvaluationBundleRehydrator.FromJson(bundle.BundleJson);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the ordered request version is corrupted.");
        }

        if (!string.Equals(rehydrated.ResultDigest, bundle.ResultDigest, StringComparison.Ordinal) ||
            !string.Equals(rehydrated.InputDigest, bundle.InputDigest, StringComparison.Ordinal) ||
            !string.Equals(rehydrated.PolicyContentDigest, bundle.PolicyContentDigest, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(rehydrated.FactsDigest) ||
            string.IsNullOrWhiteSpace(rehydrated.ManifestDigest))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the ordered request version is not reproducible.");
        }

        return new SourcingPolicyEvaluationRef(
            bundle.EvaluationSequence,
            bundle.Id,
            rehydrated.FactsDigest!,
            bundle.InputDigest,
            rehydrated.ManifestDigest!,
            bundle.PolicyContentDigest,
            bundle.ResultDigest);
    }

    private static IReadOnlyList<OrderingEvidenceTarget> Targets(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var targets = new List<OrderingEvidenceTarget>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            targets.Add(new OrderingEvidenceTarget(
                element.GetProperty("id").GetGuid(),
                element.GetProperty("version").GetInt32(),
                element.GetProperty("materialSnapshotDigest").GetString()!));
        }

        return targets
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static (string? Type, int? Level, decimal? AmountBase, string? Currency) Authority(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal))
        {
            return (null, null, null, null);
        }

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return (null, null, null, null);
        }

        return (
            type.GetString(),
            root.TryGetProperty("level", out var level) &&
            level.ValueKind == System.Text.Json.JsonValueKind.Number
                ? level.GetInt32()
                : null,
            root.TryGetProperty("amount_base", out var amount) && amount.ValueKind == System.Text.Json.JsonValueKind.String
                ? decimal.Parse(amount.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : null,
            root.TryGetProperty("currency", out var currency) &&
            currency.ValueKind == System.Text.Json.JsonValueKind.String
                ? currency.GetString()
                : null);
    }

    private static string ScopeOf(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.TryGetProperty("scope", out var scope) && scope.ValueKind == System.Text.Json.JsonValueKind.String
            ? scope.GetString()!
            : "ORGANIZATION";
    }

    /// <summary>Contract code of one system role; the CLR enum name never leaves the domain.</summary>
    private static string RoleCode(int role) => role switch
    {
        (int)SystemRole.Requester => "REQUESTER",
        (int)SystemRole.DepartmentApprover => "DEPARTMENT_APPROVER",
        (int)SystemRole.FinanceApprover => "FINANCE_APPROVER",
        (int)SystemRole.ProcurementApprover => "PROCUREMENT_APPROVER",
        (int)SystemRole.ProcurementBuyer => "PROCUREMENT_BUYER",
        (int)SystemRole.Auditor => "AUDITOR",
        (int)SystemRole.Admin => "ADMIN",
        _ => "UNKNOWN"
    };

    /// <summary>Result code of one decided action.</summary>
    private static string ActionCode(int action) => action switch
    {
        (int)ApprovalDecisionAction.Approve => "APPROVED",
        (int)ApprovalDecisionAction.Reject => "REJECTED",
        (int)ApprovalDecisionAction.RequestChanges => "CHANGES_REQUESTED",
        _ => "UNKNOWN"
    };
}
