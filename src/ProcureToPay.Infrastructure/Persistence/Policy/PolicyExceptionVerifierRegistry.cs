using ProcureToPay.Domain.Modules.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Policy;

public sealed class PolicyExceptionVerifierRegistry(
    IEnumerable<IQuotationWaiverVerifier> verifiers)
{
    private readonly IReadOnlyList<IQuotationWaiverVerifier> verifiers = verifiers.ToArray();

    public IQuotationWaiverVerifier Resolve()
    {
        return verifiers.Count switch
        {
            0 => DefaultDenyVerifier.Instance,
            1 => verifiers[0],
            _ => throw new PolicyDependencyUnavailableException(
                $"Expected exactly one quotation waiver verifier, found {verifiers.Count}.")
        };
    }

    private sealed class DefaultDenyVerifier : IQuotationWaiverVerifier
    {
        public static readonly DefaultDenyVerifier Instance = new();

        public string VerifierId => "APPROVAL_WORKFLOW";
        public string ContractVersion => "policy-exception-verifier/v1";

        public Task<QuotationWaiverEvidence?> VerifyAsync(
            QuotationWaiverRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotationWaiverEvidence?>(null);
    }
}

public sealed class DefaultDenyQuotationWaiverVerifier : IQuotationWaiverVerifier
{
    public string VerifierId => "APPROVAL_WORKFLOW";
    public string ContractVersion => "policy-exception-verifier/v1";

    public Task<QuotationWaiverEvidence?> VerifyAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<QuotationWaiverEvidence?>(null);
}
