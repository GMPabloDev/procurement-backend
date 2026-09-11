using Microsoft.Extensions.Configuration;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Exact-one registry for approval submission adapters (REQ-01). Zero or multiple
/// matches fail closed with an unavailable dependency.
/// </summary>
public sealed class ApprovalSubmissionAdapterRegistry(IEnumerable<IApprovalSubmissionAdapter> adapters)
    : IApprovalSubmissionAdapterRegistry
{
    private readonly IReadOnlyList<IApprovalSubmissionAdapter> registered = adapters.ToArray();

    public IApprovalSubmissionAdapter ResolveExactlyOne(
        string subjectType,
        string operation,
        string? contractVersion)
    {
        var matches = registered
            .Where(adapter =>
                string.Equals(adapter.Descriptor.SubjectType, subjectType, StringComparison.Ordinal) &&
                string.Equals(adapter.Descriptor.Operation, operation, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(contractVersion) ||
                 string.Equals(adapter.Descriptor.ContractVersion, contractVersion, StringComparison.Ordinal)))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                "No approval adapter is registered for the requested subject type and operation.");
        }

        if (matches.Length > 1)
        {
            throw new ApprovalDependencyUnavailableException(
                "More than one approval adapter matches the requested subject type and operation.");
        }

        matches[0].Descriptor.Validate();
        return matches[0];
    }
}

/// <summary>Configuration-backed workload allowlist: <c>Approval:Workloads:0:Issuer|ClientId</c>.</summary>
public sealed class ApprovalWorkloadAllowlist(IConfiguration configuration) : IApprovalWorkloadAllowlist
{
    private readonly IReadOnlySet<string> entries = configuration
        .GetSection("Approval:Workloads")
        .GetChildren()
        .Select(child => $"{child["Issuer"]}|{child["ClientId"]}")
        .Where(entry => !entry.StartsWith("|", StringComparison.Ordinal) &&
                        !entry.EndsWith("|", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    public bool IsAllowed(ApprovalWorkloadIdentity workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        return entries.Contains($"{workload.Issuer}|{workload.ClientId}");
    }

    public void EnsureAllowed(ApprovalWorkloadIdentity workload)
    {
        if (!IsAllowed(workload))
        {
            throw new DomainForbiddenException("The workload identity is not allowed to use the approval workflow.");
        }
    }
}
