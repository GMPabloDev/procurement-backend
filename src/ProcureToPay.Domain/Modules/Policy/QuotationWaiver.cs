using ProcureToPay.Domain.SharedKernel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
}

public sealed record QuotationWaiverEvidence(
    string EvidenceDigest,
    string Binding,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string VerifierReference);

public interface IQuotationWaiverVerifier
{
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
            request.From < 1 || request.To < request.Floor || request.To >= request.From ||
            request.PolicyDigest.Length != 64 || request.EvaluationDigest.Length != 64 ||
            request.EvidenceDigest.Length != 64 || string.IsNullOrWhiteSpace(request.Binding) ||
            string.IsNullOrWhiteSpace(request.Nonce) || request.ApproverId == Guid.Empty ||
            request.WorkloadSubjectId == Guid.Empty || request.OriginatorId == Guid.Empty)
        {
            throw new DomainConflictException("The quotation waiver is invalid or not bound to the evaluation.");
        }
        if (!string.Equals(request.ApproverRole, "PROCUREMENT_APPROVER", StringComparison.Ordinal) ||
            !string.Equals(request.AuthorityType, "PROCUREMENT", StringComparison.Ordinal) ||
            request.ApproverId == request.OriginatorId || request.ApproverId == request.WorkloadSubjectId)
        {
            throw new DomainConflictException("The quotation waiver violates authority or segregation of duties.");
        }

        var evidence = await verifier.VerifyAsync(request, cancellationToken)
            ?? throw new DomainConflictException("Quotation waiver evidence is unavailable.");
        if (evidence.ExpiresAt <= now ||
            !string.Equals(evidence.EvidenceDigest, request.EvidenceDigest, StringComparison.Ordinal) ||
            !string.Equals(evidence.Binding, request.Binding, StringComparison.Ordinal) ||
            !string.Equals(evidence.Nonce, request.Nonce, StringComparison.Ordinal))
        {
            throw new DomainConflictException("Quotation waiver evidence does not match its binding.");
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
            !string.Equals(evidence.Binding, request.Binding, StringComparison.Ordinal) ||
            !string.Equals(evidence.EvidenceDigest, request.EvidenceDigest, StringComparison.Ordinal))
        {
            throw new DomainConflictException("Quotation waiver is not bound to this evaluation.");
        }

        var matched = false;
        var controls = bundle.Controls.Select(control =>
        {
            if (control.Type != PolicyEffectType.RequireQuotations ||
                !string.Equals(control.RequirementKey, target, StringComparison.Ordinal))
            {
                return control;
            }

            matched = true;
            return control with { MinimumQuotations = request.To };
        }).ToArray();
        if (!matched)
        {
            throw new DomainConflictException("Quotation waiver target is not present in the evaluation.");
        }

        var resultDigest = PolicyCanonicalizer.Hash(JsonSerializer.Serialize(controls.Select(control => new
        {
            control.RequirementKey,
            Type = control.Type.ToString(),
            control.MinimumQuotations,
            Subjects = control.SubjectIds.OrderBy(id => id)
        })));
        return bundle with { Controls = controls, ResultDigest = resultDigest };
    }

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
