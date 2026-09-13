using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Policy;

public enum PolicyExceptionType
{
    ReduceMinValidQuotations = 1
}

/// <summary>
/// Policy-side request that asks the approval workflow to verify a quotation waiver. The
/// binding inputs and the workflow decision identity are the only inputs: approver, evidence,
/// scope, validity and SoD come exclusively from the verified response (SPEC 05 REQ-05/REQ-07).
/// </summary>
public sealed record QuotationWaiverRequest(
    PolicyExceptionType Type,
    PolicyExceptionBindingRequest Binding,
    Guid WorkflowDecisionId,
    int WorkflowDecisionVersion,
    string EvidenceDigest,
    string CorrelationReference)
{
    public int From => Binding.From;
    public int To => Binding.To;
    public int Floor => Binding.Floor;
    public string TargetRequirementKey => Binding.TargetRequirementKey;
    public string Nonce => Binding.Nonce;
    public Guid OriginatorId => Binding.OriginatorId;
    public Guid? RequesterId => Binding.RequesterId;
    public Guid WorkloadSubjectId => Binding.WorkloadSubjectId;
    public string BindingDigest => Binding.ComputeBinding();
}

/// <summary>
/// Authority evidence delivered by the workflow verifier. Every value is server-side: Policy
/// only persists and consumes it.
/// </summary>
public sealed record QuotationWaiverEvidence(
    string EvidenceDigest,
    string Binding,
    string Nonce,
    DateTimeOffset ValidFrom,
    DateTimeOffset ExpiresAt,
    string VerifierReference)
{
    public int WorkflowDecisionVersion { get; init; }
    public string WorkflowDecisionDigest { get; init; } = string.Empty;
    public string AuthorityEvidenceDigest { get; init; } = string.Empty;
    public bool SegregationSatisfied { get; init; }

    /// <summary>Approver identity delivered by the verifier, never trusted from the caller (REQ-14).</summary>
    public Guid ApproverId { get; init; }
    public string ApproverRole { get; init; } = string.Empty;
    public string AuthorityType { get; init; } = string.Empty;

    /// <summary>SHA-256 of the SPEC 01 EligibilityEvidence delivered by the verifier (REQ-14).</summary>
    public string EligibilityEvidenceDigest { get; init; } = string.Empty;

    /// <summary>Decision scope descriptor that must cover the union of the waived scopes (REQ-14).</summary>
    public DecisionScopeDescriptor? DecisionScope { get; init; }

    /// <summary>Versioned lines covered by the verified exception (SPEC 05 REQ-07).</summary>
    public IReadOnlyList<PolicyExceptionTarget> CoveredLines { get; init; } = [];

    public string VerifierId { get; init; } = string.Empty;
    public string VerifierContractVersion { get; init; } = string.Empty;
}

public sealed record PolicyExceptionVerificationInput(
    string VerifierId,
    string VerifierContractVersion,
    string WorkflowDecisionId,
    int WorkflowDecisionVersion,
    string WorkflowDecisionDigest,
    Guid BaseBundleId,
    string BaseResultDigest,
    Guid PolicyVersionId,
    string PolicyContentDigest,
    PolicySubjectReference Subject,
    string ManifestDigest,
    string TargetRequirementKey,
    int From,
    int To,
    Guid RequesterId,
    DateTimeOffset VerifiedAtUtc,
    string Nonce,
    Guid ApproverId,
    string AuthorityEvidenceDigest,
    bool SegregationSatisfied,
    string Scope,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string Binding);

public interface IQuotationWaiverVerifier
{
    string VerifierId { get; }
    string ContractVersion { get; }

    Task<QuotationWaiverEvidence?> VerifyAsync(
        QuotationWaiverRequest request,
        CancellationToken cancellationToken = default);
}

public static class QuotationWaiverEvaluator
{
    public static async Task<QuotationWaiverEvidence> VerifyAsync(
        QuotationWaiverRequest request,
        IQuotationWaiverVerifier verifier,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (request.Type != PolicyExceptionType.ReduceMinValidQuotations ||
            request.WorkflowDecisionId == Guid.Empty ||
            request.WorkflowDecisionVersion < 1 ||
            !IsSha256(request.EvidenceDigest) ||
            string.IsNullOrWhiteSpace(request.Binding.Nonce))
        {
            throw new DomainConflictException("The quotation waiver is invalid or not bound to the evaluation.");
        }

        var evidence = await verifier.VerifyAsync(request, cancellationToken)
            ?? throw new DomainConflictException("Quotation waiver evidence is unavailable.");
        if (evidence.ValidFrom > now ||
            evidence.ExpiresAt <= now ||
            evidence.ExpiresAt <= evidence.ValidFrom ||
            !string.Equals(evidence.EvidenceDigest, request.EvidenceDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Binding, request.BindingDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Nonce, request.Nonce, StringComparison.Ordinal) ||
            evidence.WorkflowDecisionVersion < 1 ||
            !IsSha256(evidence.WorkflowDecisionDigest) ||
            !IsSha256(evidence.AuthorityEvidenceDigest) ||
            !IsSha256(evidence.EligibilityEvidenceDigest) ||
            !evidence.SegregationSatisfied ||
            evidence.DecisionScope is null)
        {
            throw new DomainConflictException("Quotation waiver evidence does not match its binding.");
        }

        // Authority, approver, scope and validity come from the registered verifier (REQ-14).
        if (!string.Equals(evidence.ApproverRole, "PROCUREMENT_APPROVER", StringComparison.Ordinal) ||
            !string.Equals(evidence.AuthorityType, "PROCUREMENT", StringComparison.Ordinal) ||
            evidence.ApproverId == Guid.Empty ||
            evidence.ApproverId == request.OriginatorId ||
            evidence.ApproverId == request.WorkloadSubjectId ||
            evidence.ApproverId == request.RequesterId ||
            !string.Equals(evidence.VerifierId, verifier.VerifierId, StringComparison.Ordinal) ||
            !string.Equals(evidence.VerifierContractVersion, verifier.ContractVersion, StringComparison.Ordinal))
        {
            throw new DomainConflictException("Quotation waiver evidence does not carry verifiable authority.");
        }

        // The verified scope must cover the whole organization of the evaluation: it is the only
        // scope dimension that covers the union of the waived lines in this release (REQ-14).
        var scope = evidence.DecisionScope;
        if (scope.OrganizationId != request.Binding.OrganizationId ||
            scope.Scopes.Count != 1 ||
            scope.Scopes[0].Dimension != Modules.Organization.ScopeDimension.Organization)
        {
            throw new DomainConflictException("Quotation waiver evidence does not cover the evaluated scopes.");
        }

        // One verified evidence must cover every requested line by full identity (REQ-14).
        var covered = evidence.CoveredLines
            .Select(target => target.CanonicalIdentity)
            .ToHashSet(StringComparer.Ordinal);
        if (request.Binding.CoveredLines.Any(target => !covered.Contains(target.CanonicalIdentity)))
        {
            throw new DomainConflictException("Quotation waiver evidence does not cover the target lines.");
        }

        return evidence;
    }

    public static PolicyEvaluationBundle ApplyVerifiedQuotationWaiver(
        PolicyEvaluationBundle bundle,
        QuotationWaiverRequest request,
        QuotationWaiverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(evidence);
        var target = request.TargetRequirementKey;
        if (string.IsNullOrWhiteSpace(target) ||
            !string.Equals(request.Binding.PolicyContentDigest, bundle.PolicyContentDigest, StringComparison.Ordinal) ||
            !string.Equals(request.Binding.BaseResultDigest, bundle.ResultDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Binding, request.BindingDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.EvidenceDigest, request.EvidenceDigest, StringComparison.Ordinal))
        {
            throw new DomainConflictException("Quotation waiver is not bound to this evaluation.");
        }

        var targets = bundle.Controls.Where(control =>
            control.Type == PolicyEffectType.RequireQuotations &&
            string.Equals(control.RequirementKey, target, StringComparison.Ordinal)).ToArray();
        if (targets.Length == 0)
        {
            throw new DomainConflictException("Quotation waiver target is not present in the evaluation.");
        }
        if (targets.Any(control => control.MinimumAllowedQuotations is null))
        {
            throw new DomainConflictException("The quotation requirement is NOT_EXCEPTIONABLE.");
        }
        if (request.To < request.Floor || request.To >= request.From ||
            targets.Any(control => control.MinimumQuotations != request.From ||
                control.MinimumAllowedQuotations is null ||
                request.To < control.MinimumAllowedQuotations.Value))
        {
            throw new DomainConflictException("Quotation waiver bounds do not match the published control.");
        }

        static bool IsTarget(PolicyGeneratedControl control, PolicyGeneratedControl targetControl) =>
            control.Type == targetControl.Type &&
            string.Equals(control.RequirementKey, targetControl.RequirementKey, StringComparison.Ordinal) &&
            control.SubjectIds.IsSubsetOf(targetControl.SubjectIds);
        var controls = bundle.Controls.Select(control =>
            targets.Any(targetControl => IsTarget(control, targetControl))
                ? control with { MinimumQuotations = request.To }
                : control).ToArray();
        var scopes = bundle.ScopeEvaluations.Select(scope => scope with
        {
            Controls = scope.Controls.Select(control =>
                targets.Any(targetControl => IsTarget(control, targetControl))
                    ? control with { MinimumQuotations = request.To } : control).ToArray()
        }).ToArray();
        var reduced = bundle with
        {
            ScopeEvaluations = scopes,
            Controls = controls
        };
        var diff = PolicyEvaluationDiff.Compute(bundle, reduced);
        var resultDigest = PolicyCanonicalizer.Hash(PolicyCanonicalizer.CanonicalizeEvaluationResult(
            bundle.InputDigest, scopes, controls, bundle.Result, diff));
        return reduced with
        {
            Diff = diff,
            ResultDigest = resultDigest
        };
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));
}
