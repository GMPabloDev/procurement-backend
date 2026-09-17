using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Exactly-one fact provider of the sourcing proposal subject (REQ-10). It returns the typed
/// <see cref="SourcingPolicyFactEnvelope"/> and never the Purchase-Request-shaped DTO, so Policy does
/// not reinterpret a PR input as a sourcing one.
/// </summary>
public interface ISourcingPolicyFactProvider
{
    string ProviderId { get; }

    string ContractVersion { get; }

    Task<SourcingPolicyFactEnvelope> GetFactsAsync(
        PolicyFactRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Exactly-one registration per <c>(subject_type, operation)</c>; 0/2 fails closed (REQ-10).</summary>
public interface ISourcingPolicyFactProviderRegistry
{
    ISourcingPolicyFactProvider Resolve(string subjectType, string operation);
}

public sealed class SourcingPolicyFactProviderRegistry(IEnumerable<ISourcingPolicyFactProvider> providers)
    : ISourcingPolicyFactProviderRegistry
{
    private readonly IReadOnlyList<ISourcingPolicyFactProvider> registrations = providers.ToArray();

    public ISourcingPolicyFactProvider Resolve(string subjectType, string operation)
    {
        var matches = registrations.Where(provider =>
            string.Equals(SubjectTypeOf(provider), subjectType, StringComparison.Ordinal) &&
            string.Equals(operation, SourcingCodes.PolicyOperation, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new SourcingDependencyUnavailableException(
                $"Expected exactly one sourcing fact provider for '{subjectType}/{operation}', " +
                $"found {matches.Length}.");
    }

    private static string SubjectTypeOf(ISourcingPolicyFactProvider provider) => SourcingCodes.ApprovalSubjectType;
}

/// <summary>
/// Real in-process fact provider of the sourcing domain (REQ-10). It reads the persisted proposal, its
/// completeness manifest and the attested Purchase Request facts of the covered lines, then recomputes
/// the manifest and facts digests plus the current request evaluation reference before returning the
/// envelope: a stale proposal, a corrupted manifest or a foreign line set fails closed.
/// </summary>
public sealed class SourcingPolicyFactProvider(
    ProcureToPayDbContext dbContext,
    IPolicyFactProviderRegistry factProviders) : ISourcingPolicyFactProvider
{
    public string ProviderId => SourcingCodes.PolicyFactProviderId;

    public string ContractVersion => SourcingCodes.PolicyFactProviderContractVersion;

    public async Task<SourcingPolicyFactEnvelope> GetFactsAsync(
        PolicyFactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SubjectType, SourcingCodes.ApprovalSubjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Operation, SourcingCodes.PolicyOperation, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException(
                "The sourcing provider only serves SOURCING_PO of a SOURCING_PROPOSAL.");
        }

        var proposal = await dbContext.SourcingProposalVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ProposalId == request.SubjectId && record.Version == request.SubjectVersion,
                cancellationToken)
            ?? throw new SourcingDependencyUnavailableException(
                "The sourcing proposal version does not exist.");
        if (request.OrganizationId is Guid organizationId && proposal.OrganizationId != organizationId)
        {
            throw new SourcingDependencyUnavailableException(
                "The sourcing proposal version belongs to another organization.");
        }

        var manifest = ReadManifest(proposal.ManifestJson, proposal.ManifestDigest);
        var root = await dbContext.SourcingProposals
            .AsNoTracking()
            .SingleAsync(record => record.Id == proposal.ProposalId, cancellationToken);
        if (root.CurrentVersion != proposal.Version)
        {
            throw new SourcingDependencyUnavailableException(
                "Only the current proposal version can be evaluated.");
        }

        // The request input, its line facts and the request manifest come from the attested Purchase
        // Request provider; Sourcing never rebuilds them (REQ-10).
        var requestProvider = factProviders.Resolve("PURCHASE_REQUEST", "REQUEST_EVALUATE");
        var requestBundle = await requestProvider.GetFactsAsync(
            new PolicyFactRequest(
                "PURCHASE_REQUEST",
                proposal.RequestId,
                proposal.RequestVersion,
                "REQUEST_EVALUATE",
                request.RequestedAtUtc,
                request.Workload,
                request.CorrelationReference)
            {
                OrganizationId = proposal.OrganizationId
            },
            cancellationToken);
        if (!string.Equals(
                requestBundle.Manifest.Digest, manifest.RequestManifestDigest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException(
                "The request manifest changed after the proposal was built.");
        }

        var coveredLines = SourcingSerialization.ReadContentRefs(proposal.LineIdsJson)
            .Select(reference => new PolicySubjectReference(reference.Id, reference.Version))
            .ToArray();
        var facts = new SortedDictionary<string, PolicyValue>(StringComparer.Ordinal);
        var provenance = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in requestBundle.Request.Lines)
        {
            if (!coveredLines.Any(reference =>
                    reference.Id == line.Subject.Id && reference.Version == line.Subject.Version))
            {
                continue;
            }

            foreach (var fact in line.Facts)
            {
                facts[fact.Key] = fact.Value;
                provenance[fact.Key] = $"PURCHASE_REQUEST_LINE:{line.Subject.Id:D}:{line.Subject.Version}";
            }
        }

        // The candidate supplier is the only fact Sourcing adds: it never publishes a waiver boolean or
        // an evaluation score as a Policy decision (REQ-10).
        var supplierFact = $"SOURCING_CANDIDATE_SUPPLIER:{proposal.SupplierId:D}";
        facts[supplierFact] = PolicyValue.EntityRef(
            "SUPPLIER", proposal.SupplierId, proposal.SupplierVersion);
        provenance[supplierFact] = $"SOURCING_PROPOSAL:{proposal.ProposalId:D}:{proposal.Version}";

        var evaluationRef = ReadEvaluationRef(proposal.RequestBundleRefJson, manifest);
        return new SourcingPolicyFactEnvelope(
            ProviderId,
            ContractVersion,
            new PolicySubjectReference(proposal.ProposalId, proposal.Version),
            requestBundle.Request,
            coveredLines,
            facts,
            provenance,
            manifest,
            evaluationRef);
    }

    private static SourcingCompletenessManifest ReadManifest(string documentJson, string digest)
    {
        // The stored manifest must hash back to the recorded digest before its fields are trusted.
        if (!string.Equals(
                PolicyCanonicalizer.Hash(documentJson), digest, StringComparison.Ordinal))
        {
            throw new SourcingDependencyUnavailableException("The stored completeness manifest is corrupted.");
        }

        return SourcingSerialization.ReadCompletenessManifest(documentJson);
    }

    private static SourcingPolicyEvaluationRef ReadEvaluationRef(
        string json,
        SourcingCompletenessManifest manifest) =>
        SourcingSerialization.ReadPolicyEvaluationRef(json, manifest);
}
