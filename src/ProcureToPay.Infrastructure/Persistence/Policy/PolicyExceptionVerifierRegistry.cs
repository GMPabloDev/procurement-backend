using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed class PolicyExceptionVerifierRegistry(
    IEnumerable<IQuotationWaiverVerifier> verifiers)
{
    private readonly IReadOnlyList<IQuotationWaiverVerifier> verifiers = verifiers.ToArray();

    public IQuotationWaiverVerifier Resolve()
    {
        return verifiers.Count == 1
            ? verifiers[0]
            : throw new PolicyDependencyUnavailableException(
                $"Expected exactly one quotation waiver verifier, found {verifiers.Count}.");
    }
}

public sealed class DefaultDenyQuotationWaiverVerifier : IQuotationWaiverVerifier
{
    public Task<QuotationWaiverEvidence?> VerifyAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<QuotationWaiverEvidence?>(null);
}
