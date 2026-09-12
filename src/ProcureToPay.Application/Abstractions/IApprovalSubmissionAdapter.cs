using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// Context handed to a trusted in-process adapter so it can build the full approval
/// submission from its own subject (REQ-01). Requirements, exclusions, scopes and
/// digests are produced by the adapter, never by an HTTP caller.
/// </summary>
public sealed record ApprovalSubmissionRequest(
    Guid OrganizationId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    string ContractVersion,
    string SubmissionKey,
    Guid? RequesterId,
    Guid OriginatorId,
    string CorrelationReference);

public interface IApprovalSubmissionAdapter
{
    ApprovalAdapterDescriptor Descriptor { get; }

    Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Exactly-one adapter per subject type, operation and contract version.</summary>
public interface IApprovalSubmissionAdapterRegistry
{
    IApprovalSubmissionAdapter ResolveExactlyOne(string subjectType, string operation, string? contractVersion);
}

/// <summary>Workloads allowed to submit, signal or cancel; stable across credential rotation.</summary>
public interface IApprovalWorkloadAllowlist
{
    bool IsAllowed(ApprovalWorkloadIdentity workload);
}

/// <summary>
/// Trusted exact-one resolution of a prerequisite owner (adapter id + version) to the workload
/// identity that may signal it (REQ-03, DEC-11). Absence, ambiguity or a non-allowlisted result
/// fails closed before the case exists.
/// </summary>
public interface IApprovalOwnerWorkloadRegistry
{
    ApprovalWorkloadIdentity ResolveExactlyOne(string ownerAdapterId, string ownerAdapterVersion);
}
