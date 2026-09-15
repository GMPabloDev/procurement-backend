using ProcureToPay.Domain.Modules.Budget;

namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// In-process request of the budget demand builder (SPEC 08 Builder y respuesta de precheck). The
/// caller only identifies the immutable Purchase Request version it already confirmed; every amount,
/// position and reference is derived server-side.
/// </summary>
public sealed record BudgetDemandBuildRequest(
    string ContractVersion,
    Guid OrganizationId,
    Guid RequestId,
    int RequestVersion,
    string? RequestContentDigest,
    string ManifestDigest,
    IReadOnlyList<BudgetTarget>? Targets);

/// <summary>Server-built demands of one Purchase Request version (REQ-04, REQ-05).</summary>
public sealed record BudgetDemandBuildResult(
    string ContractVersion,
    Guid OrganizationId,
    Guid RequestId,
    int RequestVersion,
    string RequestContentDigest,
    string ManifestDigest,
    string BaseCurrency,
    IReadOnlyList<BudgetDemand> Demands);

/// <summary>
/// Exactly-one in-process builder that turns a confirmed Purchase Request version into the demands a
/// budget check covers (REQ-04, REQ-05). It never accepts amounts, positions or references from the
/// caller, so the same demands feed both the precheck and the Approval projection.
/// </summary>
public interface IBudgetDemandBuilder
{
    string SourceType { get; }

    string ContractVersion { get; }

    Task<BudgetDemandBuildResult> BuildAsync(
        BudgetDemandBuildRequest request,
        CancellationToken cancellationToken = default);
}
