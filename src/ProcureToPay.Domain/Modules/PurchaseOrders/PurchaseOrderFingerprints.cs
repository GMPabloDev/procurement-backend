using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>
/// Idempotency fingerprints published by SPEC 11 (Canonicalización y digests). Each fingerprint
/// covers exactly the enumerated properties, so a replay of the same preimage is recognized while a
/// different preimage under the same key is a conflict.
/// </summary>
public static class PurchaseOrderFingerprints
{
    /// <summary>One command reference of the budget transition set of an issue (REQ-04).</summary>
    public static SortedDictionary<string, object?> BudgetCommandRef(string operation, string operationKey) =>
        new(StringComparer.Ordinal)
        {
            ["operation"] = operation,
            ["operation_key"] = operationKey
        };

    /// <summary>
    /// <c>award_claim_fingerprint</c>: award reference, canonicalization and contract versions, claim
    /// key, covered lines, organization, PO id and workload identity.
    /// </summary>
    public static string AwardClaimFingerprint(AwardConsumptionClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["award_ref"] = PurchaseOrderCanonicalizer.ContentRef(request.AwardRef),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["claim_key"] = request.ClaimKey,
            ["contract_version"] = PurchaseOrderCodes.AwardConsumptionClaimContract,
            ["covered_lines"] = PurchaseOrderCanonicalizer.Set(
                request.CoveredLines.Select(line => (object?)PurchaseOrderCanonicalizer.ContentRef(line))),
            ["organization_id"] = request.OrganizationId.ToString("D"),
            ["po_id"] = request.PoId.ToString("D"),
            ["workload_client_id"] = request.WorkloadClientId,
            ["workload_issuer"] = request.WorkloadIssuer
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>
    /// <c>po_issue_fingerprint</c>: approval reference, claim reference, the budget transition set,
    /// canonicalization version, expected PO version, issue key, PO content digest and terms digest.
    /// </summary>
    public static string PoIssueFingerprint(
        PurchaseOrderContentRef approvalRef,
        PurchaseOrderContentRef awardClaimRef,
        IEnumerable<(string Operation, string OperationKey)> budgetCommands,
        int expectedPoVersion,
        string issueKey,
        string poContentDigest,
        string termsSnapshotDigest)
    {
        ArgumentNullException.ThrowIfNull(approvalRef);
        ArgumentNullException.ThrowIfNull(awardClaimRef);
        var commands = (budgetCommands ?? [])
            .Select(command => (object?)BudgetCommandRef(command.Operation, command.OperationKey))
            .ToArray();
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["approval_ref"] = PurchaseOrderCanonicalizer.ContentRef(approvalRef),
            ["award_claim_ref"] = PurchaseOrderCanonicalizer.ContentRef(awardClaimRef),
            ["budget_commands"] = PurchaseOrderCanonicalizer.Set(commands),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["expected_po_version"] = expectedPoVersion,
            ["issue_key"] = PurchaseOrderCodes.Key(issueKey, "issue_key"),
            ["po_content_digest"] = PurchaseOrderCodes.Digest(poContentDigest, "PO content digest"),
            ["terms_snapshot_digest"] = PurchaseOrderCodes.Digest(termsSnapshotDigest, "Terms snapshot digest")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}
