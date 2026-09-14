using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Real in-process fact provider of the Purchase Requests domain (REQ-05, DEC-03). It only serves
/// a version whose attestation and completeness manifest are persisted and whose digests are still
/// reproducible, and it projects exclusively the closed SPEC 02 fact catalog.
/// </summary>
public sealed class PurchaseRequestPolicyFactProvider(
    ProcureToPayDbContext dbContext,
    PurchaseRequestPersistenceService persistence,
    ILogger<PurchaseRequestPolicyFactProvider> logger) : IPolicyFactProvider
{
    public string SubjectType => PurchaseRequestCodes.SubjectType;

    public string Operation => PurchaseRequestCodes.EvaluationOperation;

    public string ProviderId => PurchaseRequestCodes.ProviderId;

    public string ContractVersion => PurchaseRequestCodes.ProviderContractVersion;

    public async Task<PolicyFactBundle> GetFactsAsync(
        PolicyFactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SubjectType, SubjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Operation, Operation, StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException(
                "The purchase request provider only serves REQUEST_EVALUATE of a PURCHASE_REQUEST.");
        }

        var manifest = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == request.SubjectId && record.RequestVersion == request.SubjectVersion,
                cancellationToken)
            ?? throw new PolicyDependencyUnavailableException(
                "The purchase request version has no completeness manifest and was never attested.");
        if (request.OrganizationId is Guid organizationId && manifest.OrganizationId != organizationId)
        {
            throw new PolicyDependencyUnavailableException(
                "The attested purchase request version belongs to another organization.");
        }

        var snapshot = await persistence.LoadVersionAsync(
            request.SubjectId, request.SubjectVersion, cancellationToken);
        var lines = await persistence.LoadLinesAsync(request.SubjectId, request.SubjectVersion, cancellationToken);
        var attestation = await dbContext.PurchaseRequestReferenceAttestations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == request.SubjectId &&
                          record.RequestVersion == request.SubjectVersion,
                cancellationToken)
            ?? throw new PolicyDependencyUnavailableException(
                "The attested reference snapshot of the purchase request version is missing.");
        if (!string.Equals(attestation.Digest, manifest.ReferenceAttestationDigest, StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException(
                "The reference attestation of the purchase request version is corrupted.");
        }

        var assertions = PurchaseRequestSerialization.ReadAssertions(attestation.AssertionsJson);
        var recomputedAttestation = PurchaseRequestCanonicalizer.ReferenceAttestationDigest(
            snapshot.RequestId,
            snapshot.Version,
            manifest.OrganizationId,
            attestation.AttestedAt,
            assertions);
        if (!string.Equals(recomputedAttestation, manifest.ReferenceAttestationDigest, StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException(
                "The reference attestation digest is not reproducible.");
        }

        var references = snapshot.LineRefs.ToArray();
        var manifestIdentities = references
            .Select(reference => reference.CanonicalIdentity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var storedIdentities = lines.Values
            .Select(line => $"{line.LineId:D}:{line.LineVersion}")
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        if (references.Length != lines.Count ||
            !manifestIdentities.SequenceEqual(storedIdentities, StringComparer.Ordinal))
        {
            throw new PolicyDependencyUnavailableException(
                "The completeness manifest and the stored line set differ.");
        }

        var attestationActive = assertions
            .Where(assertion => assertion.AssertionType == PurchaseRequestAssertionType.ActiveInOrganization)
            .Select(assertion => assertion.SlotIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var ownership = assertions
            .Where(assertion => assertion.AssertionType == PurchaseRequestAssertionType.CostCenterOwnedByDepartment)
            .Select(assertion => assertion.SlotIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var inputLines = new List<PolicyLineInput>(references.Length);
        var provenance = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var line = lines[reference.Id];
            var recomputedLine = PurchaseRequestCanonicalizer.LineContentDigest(
                manifest.OrganizationId,
                snapshot.RequestId,
                reference.Id,
                reference.Version,
                line.Content);
            if (!string.Equals(recomputedLine, reference.ContentDigest, StringComparison.Ordinal))
            {
                throw new PolicyDependencyUnavailableException(
                    "A stored line version content digest is not reproducible.");
            }

            var costCenterSlot = AttestationSlot(
                PurchaseRequestAssertionType.ActiveInOrganization, line.Content.CostCenterRef, null);
            var ownershipSlot = AttestationSlot(
                PurchaseRequestAssertionType.CostCenterOwnedByDepartment,
                line.Content.CostCenterRef,
                line.Content.CostCenterDepartmentRef);
            var facts = PurchaseRequestPolicyProjection.LineFacts(
                line.Content,
                attestationActive.Contains(costCenterSlot),
                ownership.Contains(ownershipSlot));
            var lineProvenance = PurchaseRequestPolicyProjection.LineProvenance(
                snapshot.RequestId,
                snapshot.Version,
                reference.Id,
                reference.Version,
                facts,
                manifest.ReferenceAttestationDigest);
            foreach (var pair in lineProvenance)
            {
                // Line facts repeat their key across lines: the persisted provenance map keeps the
                // line identity in the key so every entry stays unambiguous (REQ-05).
                provenance[$"{reference.Id:D}:{pair.Key}"] = pair.Value;
            }

            inputLines.Add(new PolicyLineInput(
                new PolicySubjectReference(reference.Id, reference.Version),
                facts));
        }

        var input = new PolicyRequestInput(
            new PolicySubjectReference(snapshot.RequestId, snapshot.Version),
            manifest.OrganizationId,
            snapshot.LegalEntityRef.Id,
            inputLines[0].Facts["GROSS_AMOUNT_BASE"].Currency!,
            inputLines,
            PurchaseRequestPolicyProjection.RequestFacts());
        var policyManifest = new PolicyCompletenessManifest(
            snapshot.RequestId,
            snapshot.Version,
            inputLines.Select(line => line.Subject).ToArray(),
            string.Empty)
        {
            Digest = manifest.PolicyManifestDigest
        };
        var expectedPolicyManifest = PolicyEvaluationService.ComputeManifestDigest(
            new PolicyCompletenessManifest(
                snapshot.RequestId,
                snapshot.Version,
                inputLines.Select(line => line.Subject).ToArray(),
                string.Empty));
        if (!string.Equals(expectedPolicyManifest, manifest.PolicyManifestDigest, StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException("The policy completeness manifest is not reproducible.");
        }

        var expectedDomainAttestation = PurchaseRequestCanonicalizer.DomainAttestationDigest(
            snapshot.RequestId,
            snapshot.Version,
            manifest.OrganizationId,
            manifest.RequestContentDigest,
            manifest.ReferenceAttestationDigest,
            manifest.PolicyManifestDigest,
            references);
        if (!string.Equals(expectedDomainAttestation, manifest.DomainAttestationDigest, StringComparison.Ordinal) ||
            !string.Equals(
                PurchaseRequestCanonicalizer.RequestContentDigest(snapshot),
                manifest.RequestContentDigest,
                StringComparison.Ordinal))
        {
            throw new PolicyDependencyUnavailableException(
                "The domain completeness attestation of the purchase request version is corrupted.");
        }

        var bundle = new PolicyFactBundle(input, policyManifest, ProviderId, ContractVersion, string.Empty)
        {
            Provenance = provenance
        };
        logger.LogInformation(
            "Purchase request {RequestId} version {Version} served {LineCount} attested line(s) to Policy.",
            snapshot.RequestId,
            snapshot.Version,
            inputLines.Count);
        return bundle with { FactsDigest = PolicyEvaluationService.ComputeFactsDigest(bundle) };
    }

    private static string AttestationSlot(
        PurchaseRequestAssertionType assertionType,
        VersionedEntityRef source,
        VersionedEntityRef? target)
    {
        var sourceRef = PurchaseRequestAttestedRef.ForEntity(source.EntityType switch
        {
            "COST_CENTER" => PurchaseRequestReferenceType.CostCenter,
            "DEPARTMENT" => PurchaseRequestReferenceType.Department,
            _ => throw new PolicyDependencyUnavailableException("Unsupported attested reference type.")
        }, source.Id, source.Version);
        var targetRef = target is null
            ? null
            : PurchaseRequestAttestedRef.ForEntity(PurchaseRequestReferenceType.Department, target.Id, target.Version);
        return $"{PurchaseRequestCodes.Code(assertionType)}|{PurchaseRequestReferenceAssertion.DescribeReference(sourceRef)}|" +
               (targetRef is null ? string.Empty : PurchaseRequestReferenceAssertion.DescribeReference(targetRef));
    }
}
