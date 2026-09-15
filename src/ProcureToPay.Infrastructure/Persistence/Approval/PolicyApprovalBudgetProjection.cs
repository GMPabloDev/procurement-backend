using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Budget projection of <c>policy-approval-adapter/v3</c> (SPEC 08 REQ-05, DEC-08). The adapter
/// itself keeps the DAG, actions and snapshot of SPEC 05; only the budget parameters change, so the
/// projection lives here and delegates every amount, position and reference to the server-side
/// demand builder instead of the SPEC 02 control facts.
/// </summary>
public static class PolicyApprovalBudgetProjection
{
    /// <summary>
    /// Builds the exact <c>budget-check-owner/v1</c> parameters for one budget control, or fails
    /// closed when the confirmed request version cannot reproduce every covered target.
    /// </summary>
    public static async Task<string> BuildParametersAsync(
        IBudgetDemandBuilder builder,
        Guid organizationId,
        Guid requestId,
        int requestVersion,
        string? requestContentDigest,
        string manifestDigest,
        IReadOnlyList<ApprovalTarget> controlTargets,
        string baseCurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(controlTargets);
        if (controlTargets.Count == 0)
        {
            throw new ApprovalDependencyUnavailableException(
                "The budget control covers no confirmed target.");
        }

        var targets = controlTargets
            .Select(target => new BudgetTarget(
                target.Id,
                target.Version,
                target.MaterialSnapshotDigest,
                BudgetCodes.PurchaseRequestLineType))
            .ToArray();
        if (targets.Select(target => target.Id).Distinct().Count() != targets.Length)
        {
            throw new ApprovalDependencyUnavailableException("The budget control repeats a confirmed target.");
        }

        // When the caller does not carry the request content digest, the builder verifies it from
        // the confirmed manifest, which is the authoritative source of the same identity (REQ-04).
        var build = await builder.BuildAsync(
            new BudgetDemandBuildRequest(
                BudgetCodes.DemandBuildVersion,
                organizationId,
                requestId,
                requestVersion,
                requestContentDigest,
                manifestDigest,
                targets) { RequestContentDigest = requestContentDigest },
            cancellationToken);
        if (build.Demands.Count != targets.Length)
        {
            throw new ApprovalDependencyUnavailableException(
                "The budget control could not reproduce exactly one demand per covered target.");
        }

        foreach (var demand in build.Demands)
        {
            if (demand.Target is null ||
                !targets.Any(target =>
                    target.Id == demand.Target.Id &&
                    target.Version == demand.Target.Version &&
                    string.Equals(
                        target.MaterialSnapshotDigest,
                        demand.Target.MaterialSnapshotDigest,
                        StringComparison.Ordinal)))
            {
                throw new ApprovalDependencyUnavailableException(
                    "A budget demand does not match the confirmed material target of its control.");
            }
        }

        return new BudgetPrerequisiteParameters(
                baseCurrency,
                BudgetCodes.PrerequisiteContractVersion,
                build.Demands)
            .ToCanonicalJson();
    }

    /// <summary>
    /// Loads the confirmed Purchase Request version referenced by the evaluation snapshot. The
    /// adapter only accepts the subject of its own operation, so the identity is never inferred.
    /// </summary>
    public static (Guid RequestId, int RequestVersion) ResolveSubject(string subjectType, Guid subjectId, int subjectVersion)
    {
        if (!string.Equals(subjectType, PurchaseRequestCodes.SubjectType, StringComparison.Ordinal) ||
            subjectId == Guid.Empty ||
            subjectVersion < 1)
        {
            throw new ApprovalDependencyUnavailableException(
                "The budget projection requires the confirmed Purchase Request subject.");
        }

        return (subjectId, subjectVersion);
    }
}
