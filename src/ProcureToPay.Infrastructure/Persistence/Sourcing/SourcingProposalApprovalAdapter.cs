using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>
/// Trusted in-process adapter that turns the approved sourcing proposal into one idempotent approval
/// submission (SPEC 10 REQ-11). It receives only the proposal reference, resolves the Policy bundle,
/// the manifest and the attested request facts server-side, projects every control through the SPEC 05
/// mapping with the exact Purchase Request lines as targets, and re-checks the supplier and catalogue
/// eligibility before any case exists.
/// </summary>
public sealed class SourcingProposalApprovalAdapter(
    ProcureToPayDbContext dbContext,
    PolicyApprovalAdapter policyAdapter) : IApprovalSubmissionAdapter
{
    public const string AdapterId = SourcingCodes.ApprovalAdapterId;
    public const string ContractVersion = SourcingCodes.ApprovalAdapterVersion;
    public const string AdapterIdentity = "sourcing-policy-approval-adapter/v1";
    public const string SnapshotContractVersion = "sourcing-approval-target/v1";

    private readonly ApprovalAdapterDescriptor descriptorValue = new(
        AdapterId,
        SourcingCodes.ApprovalSubjectType,
        SourcingCodes.ApprovalOperation,
        ContractVersion,
        RequesterRequired: true,
        AllowsRequesterAsOriginator: true,
        SupersessionDeltaSupported: false);

    public ApprovalAdapterDescriptor Descriptor => descriptorValue;

    public async Task<ApprovalSubmission> BuildAsync(
        ApprovalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SubjectType, SourcingCodes.ApprovalSubjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Operation, SourcingCodes.ApprovalOperation, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The sourcing approval adapter only serves SOURCING_PROPOSAL submissions.");
        }

        var proposal = await dbContext.SourcingProposalVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ProposalId == request.SubjectId &&
                          record.Version == request.SubjectVersion &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "The sourcing proposal version does not exist.");
        var content = SourcingProposalDocument.Read(proposal.DocumentJson, proposal.ContentDigest);

        // REQ-11: the supplier is rechecked server-side before the case exists; a stale or blocked
        // supplier never becomes an approval subject.
        var status = await dbContext.SupplierVersions
            .AsNoTracking()
            .Where(record => record.SupplierId == proposal.SupplierId &&
                             record.Version == proposal.SupplierVersion)
            .Select(record => (int?)record.Status)
            .SingleOrDefaultAsync(cancellationToken);
        var currentVersion = await dbContext.Suppliers
            .AsNoTracking()
            .Where(record => record.Id == proposal.SupplierId)
            .Select(record => record.OperationalVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (status != (int)SupplierOperationalStatus.Active || currentVersion != proposal.SupplierVersion)
        {
            throw new AwardNotEligibleException("The proposal supplier is not eligible for approval.");
        }

        // The sourcing evaluation of this proposal: the manifest digest it published is the binding
        // between the persisted proposal and the Policy bundle that approved its facts (REQ-10).
        var record = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(bundle =>
                bundle.OrganizationId == request.OrganizationId &&
                bundle.SubjectId == proposal.ProposalId &&
                bundle.SubjectVersion == proposal.Version &&
                bundle.Operation == "SOURCING_PO")
            .OrderByDescending(bundle => bundle.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "The proposal has no persisted sourcing policy evaluation.");
        var bundle = Rehydrate(record);
        if (!string.Equals(bundle.PolicyContentDigest, record.PolicyContentDigest, StringComparison.Ordinal) ||
            !string.Equals(bundle.ResultDigest, record.ResultDigest, StringComparison.Ordinal))
        {
            // The stored bytes must reproduce the recorded digests: a tampered row never approves.
            throw new ApprovalDependencyUnavailableException("The persisted policy evaluation is corrupted.");
        }

        if (!ProposalManifestBound(bundle, proposal))
        {
            // The manifest digest is the published binding between the proposal and the Policy bundle
            // that evaluated its facts (REQ-10); anything else is diverging evidence.
            throw new ApprovalDependencyUnavailableException(
                "The persisted sourcing evaluation does not match the proposal manifest.");
        }

        if (bundle.Result == PolicyResult.Blocked)
        {
            throw new DomainConflictException(
                "A blocked sourcing evaluation cannot be submitted to the approval workflow.");
        }

        // The request bundle contributes the attested snapshot the budget, document and supplier
        // projections read; the request evaluation is the one the proposal published as its bundle.
        var requestBundleRef = SourcingSerialization.ReadPolicyEvaluationRef(
            proposal.RequestBundleRefJson, ReadManifest(proposal));
        var requestRecord = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                bundle => bundle.Id == requestBundleRef.Id &&
                          bundle.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new ApprovalDependencyUnavailableException(
                "The request policy evaluation of the proposal is unavailable.");
        var requestBundle = Rehydrate(requestRecord);
        if (!string.Equals(requestBundle.ResultDigest, requestBundleRef.ResultDigest, StringComparison.Ordinal))
        {
            throw new ApprovalDependencyUnavailableException(
                "The request evaluation of the proposal is not the published one.");
        }

        var material = content.AwardCandidate.Lines.ToDictionary(
            line => line.LineRef.Id,
            line => new ApprovalTarget(
                PolicyApprovalTargets.TargetType, line.LineRef.Id, line.LineRef.Version, line.Digest));
        var projection = new PolicyMaterialProjection(
            PolicyApprovalTargets.ContractVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "SOURCING_PROPOSAL"
            },
            content.AwardCandidate.Lines
                .Select(line => new PolicyMaterialTarget(
                    line.LineRef.Id, line.LineRef.Version, line.Digest))
                .ToImmutableArray());
        var mapped = bundle with
        {
            Operation = "SOURCING_PO",
            MaterialProjection = projection,
            RequestSnapshotJson = requestBundle.RequestSnapshotJson
        };

        return await policyAdapter.MapAsAsync(
            mapped,
            material,
            record,
            request,
            SourcingCodes.ApprovalSubjectType,
            SourcingCodes.ApprovalOperation,
            SnapshotDigest(mapped, proposal, requestBundleRef),
            cancellationToken);
    }

    /// <summary>
    /// <c>source snapshot digest</c> of REQ-11: the SHA-256 of exactly
    /// <c>{canonicalization_version, manifest_digest, policy_result_digest, proposal_content_digest,
    /// request_result_digest}</c> under <c>policy-canonical-json/v1</c>.
    /// </summary>
    private static string SnapshotDigest(
        PolicyEvaluationBundle bundle,
        SourcingProposalVersionRecord proposal,
        SourcingPolicyEvaluationRef requestBundleRef) =>
        PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["manifest_digest"] = bundle.ManifestDigest ?? proposal.ManifestDigest,
                ["policy_result_digest"] = bundle.ResultDigest,
                ["proposal_content_digest"] = proposal.ContentDigest,
                ["request_result_digest"] = requestBundleRef.ResultDigest
            }));

    /// <summary>
    /// True when the persisted Policy bundle attests the completeness manifest of the proposal: either
    /// its own manifest digest is that document (historical shape) or the sourcing evaluation carries
    /// the digest in <c>provider_attestation.attestation_digest</c> (published REQ-10 shape).
    /// </summary>
    private static bool ProposalManifestBound(
        PolicyEvaluationBundle bundle,
        SourcingProposalVersionRecord proposal) =>
        string.Equals(bundle.ManifestDigest, proposal.ManifestDigest, StringComparison.Ordinal) ||
        string.Equals(
            AttestationDigest(bundle.ManifestCanonicalJson), proposal.ManifestDigest, StringComparison.Ordinal);

    private static string? AttestationDigest(string? manifestCanonicalJson)
    {
        if (string.IsNullOrWhiteSpace(manifestCanonicalJson))
        {
            return null;
        }

        try
        {
            var root = JsonDocument.Parse(manifestCanonicalJson).RootElement;
            return root.TryGetProperty("provider_attestation", out var attestation) &&
                   attestation.TryGetProperty("attestation_digest", out var digest) &&
                   digest.ValueKind == JsonValueKind.String
                ? digest.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PolicyEvaluationBundle Rehydrate(PolicyEvaluationBundleRecord record)
    {
        try
        {
            return PolicyEvaluationBundleRehydrator.FromJson(record.BundleJson);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new ApprovalDependencyUnavailableException("The persisted policy evaluation is corrupted.");
        }
    }

    private static SourcingCompletenessManifest ReadManifest(SourcingProposalVersionRecord proposal) =>
        SourcingSerialization.ReadCompletenessManifest(proposal.ManifestJson);
}
