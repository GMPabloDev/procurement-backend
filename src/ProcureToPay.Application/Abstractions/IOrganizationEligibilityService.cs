using ProcureToPay.Domain.Modules.Organization;

namespace ProcureToPay.Application.Abstractions;

public interface IOrganizationEligibilityService
{
    Task<IReadOnlyList<EligibleCandidate>> ResolveAsync(
        EligibilityRequest request,
        CancellationToken cancellationToken = default);
}
