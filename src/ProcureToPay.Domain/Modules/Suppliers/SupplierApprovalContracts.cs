using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>
/// Approval contract of the Supplier governance flows (SPEC 09 REQ-03, Approval y eventos). The
/// material snapshot is derived from the persisted candidate, the base version and the server-side
/// classification; the caller never supplies it.
/// </summary>
public static class SupplierApprovalTargets
{
    public const string ContractVersion = "supplier-approval-target/v1";

    /// <summary>
    /// <c>supplier-approval-target/v1</c>: the exact evidence a decision covers. It carries the
    /// candidate digest, the base version, the classification and the requested status, never the
    /// banking plaintext, contacts or addresses text.
    /// </summary>
    public static string MaterialDigest(
        Guid organizationId,
        Guid supplierId,
        int candidateVersion,
        string candidateContentDigest,
        int? baseVersion,
        string changeKind,
        string requestedStatus,
        IEnumerable<string> sensitiveFields)
    {
        var fields = (sensitiveFields ?? [])
            .Select(field => field.Normalize(System.Text.NormalizationForm.FormC))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToArray();
        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base_version"] = baseVersion,
            ["candidate_content_digest"] = SupplierCodes.Digest(candidateContentDigest, "Candidate content digest"),
            ["candidate_version"] = candidateVersion,
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["change_kind"] = changeKind,
            ["contract_version"] = ContractVersion,
            ["organization_id"] = organizationId.ToString("D"),
            ["requested_status"] = requestedStatus,
            ["sensitive_fields"] = fields,
            ["supplier_id"] = supplierId.ToString("D")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(root));
    }

    /// <summary>Descriptor of the governance adapter (REQ-03).</summary>
    public static ApprovalAdapterDescriptor Descriptor(string subjectType, string operation) =>
        new(
            SupplierCodes.GovernanceAdapterId,
            subjectType,
            operation,
            SupplierCodes.GovernanceAdapterVersion,
            RequesterRequired: true,
            AllowsRequesterAsOriginator: true,
            SupersessionDeltaSupported: false);

    /// <summary>Change kind code of one proposal.</summary>
    public static string ChangeKindCode(SupplierChangeKind kind) => kind switch
    {
        SupplierChangeKind.Create => "CREATE",
        SupplierChangeKind.SensitiveUpdate => "SENSITIVE_UPDATE",
        SupplierChangeKind.NonSensitiveUpdate => "NON_SENSITIVE_UPDATE",
        SupplierChangeKind.StatusChange => "STATUS_CHANGE",
        _ => throw new DomainValidationException("The supplier change kind is invalid.")
    };

    /// <summary>Proposal state code persisted for one lifecycle state.</summary>
    public static string ProposalStateCode(SupplierProposalState state) => state switch
    {
        SupplierProposalState.Draft => "DRAFT",
        SupplierProposalState.Pending => "PENDING",
        SupplierProposalState.Approved => "APPROVED",
        SupplierProposalState.Rejected => "REJECTED",
        SupplierProposalState.ChangesRequested => "CHANGES_REQUESTED",
        SupplierProposalState.Cancelled => "CANCELLED",
        _ => throw new DomainValidationException("The supplier proposal state is invalid.")
    };
}

/// <summary>
/// Persisted proposal of one governed change (REQ-02, REQ-03). The candidate version is immutable;
/// the proposal carries the state, the classification and the approval binding.
/// </summary>
public sealed record SupplierProposalView(
    Guid Id,
    Guid SupplierId,
    int? BaseVersion,
    int? CandidateVersion,
    SupplierChangeKind ChangeKind,
    IReadOnlySet<string> SensitiveFields,
    SupplierOperationalStatus? RequestedStatus,
    SupplierProposalState State,
    Guid EditorUserId,
    Guid? ApprovalCaseId,
    string? RequirementKey,
    string? CaseContractVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
