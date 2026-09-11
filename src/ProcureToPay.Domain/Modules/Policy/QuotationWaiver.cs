using ProcureToPay.Domain.SharedKernel;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ProcureToPay.Domain.Modules.Policy;

public enum PolicyExceptionType
{
    ReduceMinValidQuotations = 1
}

public sealed record QuotationWaiverRequest(
    PolicyExceptionType Type,
    int From,
    int To,
    int Floor,
    string PolicyDigest,
    string EvaluationDigest,
    string Binding,
    string Nonce,
    string EvidenceDigest,
    string ApproverRole,
    string AuthorityType,
    Guid ApproverId,
    Guid WorkloadSubjectId,
    Guid OriginatorId)
{
    public string? TargetRequirementKey { get; init; }

    /// <summary>Lines the engine is asking the workflow to waive, echoed into the verified coverage (REQ-14).</summary>
    public ImmutableHashSet<Guid> TargetLineIds { get; init; } = ImmutableHashSet<Guid>.Empty;
}

public sealed record QuotationWaiverEvidence(
    string EvidenceDigest,
    string Binding,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string VerifierReference)
{
    public int WorkflowDecisionVersion { get; init; }
    public string WorkflowDecisionDigest { get; init; } = string.Empty;
    public string AuthorityEvidenceDigest { get; init; } = string.Empty;
    public bool SegregationSatisfied { get; init; }

    /// <summary>Approver identity delivered by the verifier, never trusted from the HTTP request (REQ-14).</summary>
    public Guid ApproverId { get; init; }
    public string ApproverRole { get; init; } = string.Empty;
    public string AuthorityType { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;

    /// <summary>SHA-256 of the SPEC 01 EligibilityEvidence delivered by the verifier (REQ-14).</summary>
    public string EligibilityEvidenceDigest { get; init; } = string.Empty;

    /// <summary>Lines covered by the verified exception; must cover the target lines (REQ-14).</summary>
    public ImmutableHashSet<Guid> CoveredLineIds { get; init; } = ImmutableHashSet<Guid>.Empty;
    public DateTimeOffset ValidFrom { get; init; }
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
        CancellationToken cancellationToken = default,
        IReadOnlySet<PolicyScope>? targetScopes = null,
        IReadOnlySet<Guid>? targetLines = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (request.Type != PolicyExceptionType.ReduceMinValidQuotations ||
            request.From < 1 || request.To < request.Floor || request.To >= request.From ||
            request.PolicyDigest.Length != 64 || request.EvaluationDigest.Length != 64 ||
            request.EvidenceDigest.Length != 64 || string.IsNullOrWhiteSpace(request.Binding) ||
            string.IsNullOrWhiteSpace(request.Nonce) || request.ApproverId == Guid.Empty ||
            request.WorkloadSubjectId == Guid.Empty || request.OriginatorId == Guid.Empty)
        {
            throw new DomainConflictException("The quotation waiver is invalid or not bound to the evaluation.");
        }

        var evidence = await verifier.VerifyAsync(request, cancellationToken)
            ?? throw new DomainConflictException("Quotation waiver evidence is unavailable.");
        if (evidence.ExpiresAt <= now ||
            !string.Equals(evidence.EvidenceDigest, request.EvidenceDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Binding, request.Binding, StringComparison.Ordinal) ||
            !string.Equals(evidence.Nonce, request.Nonce, StringComparison.Ordinal) ||
            evidence.WorkflowDecisionVersion < 1 ||
            !IsSha256(evidence.WorkflowDecisionDigest) ||
            !IsSha256(evidence.AuthorityEvidenceDigest) ||
            !IsSha256(evidence.EligibilityEvidenceDigest) ||
            !evidence.SegregationSatisfied)
        {
            throw new DomainConflictException("Quotation waiver evidence does not match its binding.");
        }

        // Authority, approver, scope, validity and verifier identity come from the registered verifier (REQ-14).
        if (!string.Equals(evidence.ApproverRole, "PROCUREMENT_APPROVER", StringComparison.Ordinal) ||
            !string.Equals(evidence.AuthorityType, "PROCUREMENT", StringComparison.Ordinal) ||
            evidence.ApproverId == Guid.Empty ||
            evidence.ApproverId == request.OriginatorId ||
            evidence.ApproverId == request.WorkloadSubjectId ||
            string.IsNullOrWhiteSpace(evidence.Scope) ||
            !string.Equals(evidence.VerifierId, verifier.VerifierId, StringComparison.Ordinal) ||
            !string.Equals(evidence.VerifierContractVersion, verifier.ContractVersion, StringComparison.Ordinal) ||
            evidence.ValidFrom > now ||
            evidence.ExpiresAt <= now ||
            evidence.ExpiresAt <= evidence.ValidFrom)
        {
            throw new DomainConflictException("Quotation waiver evidence does not carry verifiable authority.");
        }

        // One verified evidence must cover the union of scopes and lines it reduces (REQ-14).
        if (targetScopes is not null && targetScopes.Count > 0)
        {
            var covered = evidence.Scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (targetScopes.Any(scope => !covered.Contains(scope.ToString())))
            {
                throw new DomainConflictException("Quotation waiver evidence does not cover the target scopes.");
            }
        }
        if (targetLines is not null && targetLines.Count > 0 &&
            !evidence.CoveredLineIds.IsSupersetOf(targetLines))
        {
            throw new DomainConflictException("Quotation waiver evidence does not cover the target lines.");
        }

        // Caller-provided hints, when present, must match the verified evidence.
        if ((!string.IsNullOrWhiteSpace(request.ApproverRole) &&
             !string.Equals(request.ApproverRole, evidence.ApproverRole, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(request.AuthorityType) &&
             !string.Equals(request.AuthorityType, evidence.AuthorityType, StringComparison.Ordinal)) ||
            request.ApproverId != evidence.ApproverId)
        {
            throw new DomainConflictException("Quotation waiver evidence contradicts the requested authority.");
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
            !string.Equals(request.PolicyDigest, bundle.PolicyContentDigest, StringComparison.Ordinal) ||
            !string.Equals(request.EvaluationDigest, bundle.ResultDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Binding, request.Binding, StringComparison.Ordinal) ||
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

    public static string ComputeBinding(
        Guid organizationId,
        Guid policyId,
        Guid subjectId,
        string policyDigest,
        string evaluationDigest,
        string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", organizationId,
            policyId, subjectId, policyDigest, evaluationDigest, nonce)))).ToLowerInvariant();
}
