using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Server-side verification of a quotation waiver (SPEC 05 REQ-06, REQ-08). Every authority,
/// scope, validity and SoD value comes from the persisted decision, never from the caller; the
/// <c>binding</c> and <c>evidence_digest</c> are recomputed and compared, and an immutable
/// verification row is created or replayed under the two contractual unique keys.
/// </summary>
public sealed class PolicyExceptionVerificationService(
    ProcureToPayDbContext dbContext,
    ILogger<PolicyExceptionVerificationService> logger)
{
    public async Task<PolicyExceptionVerificationOutcome> VerifyAsync(
        WorkflowVerificationRequest request,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                request.ContractVersion,
                PolicyExceptionContract.VerificationRequestVersion,
                StringComparison.Ordinal))
        {
            throw new DomainValidationException("The policy exception verification contract version is not supported.");
        }

        var binding = Request(request);
        // The correlation reference is a required opaque server-visible value (SPEC 03 REQ-09);
        // a missing or malformed one is a malformed verification request (SPEC 05 REQ-06).
        _ = ApprovalLimits.RequireCorrelation(request.CorrelationReference);
        var bindingDigest = binding.ComputeBinding();
        if (string.IsNullOrWhiteSpace(request.Binding) ||
            !string.Equals(bindingDigest, request.Binding, StringComparison.OrdinalIgnoreCase))
        {
            throw new PolicyExceptionBindingMismatchException(
                "The submitted binding is not reproducible from the request fields.");
        }

        var utcNow = occurredAt.ToUniversalTime();
        var persisted = await dbContext.ApprovalPolicyExceptionRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.OrganizationId == binding.OrganizationId &&
                record.BaseBundleId == binding.BaseBundleId &&
                record.TargetRequirementKey == binding.TargetRequirementKey &&
                record.Binding == bindingDigest,
                cancellationToken)
            ?? throw new DomainNotFoundException("The policy exception evidence is not visible.");
        EnsureSameRequest(persisted, binding);

        var requirement = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == persisted.RequirementId, cancellationToken)
            ?? throw new DomainNotFoundException("The policy exception requirement is not visible.");
        if (requirement.Status != (int)ApprovalRequirementStatus.Approved)
        {
            throw new DomainException("The policy exception requirement is not approved.");
        }

        var decision = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.OrganizationId == binding.OrganizationId &&
                record.Id == request.WorkflowDecisionId &&
                record.RequirementId == requirement.Id,
                cancellationToken)
            ?? throw new DomainNotFoundException("The workflow decision is not visible.");
        var evidence = await dbContext.DecisionAuthorityEvidences
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == decision.EvidenceId, cancellationToken);
        var authority = ApprovalJsonPersistence.DeserializeAuthority(requirement.AuthorityJson);
        var scope = DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson, binding.OrganizationId);
        var coveredLines = PolicyExceptionTarget.ParseCanonicalSet(persisted.CoveredLinesJson);
        var validFrom = decision.DecidedAt;
        var validTo = MinValidity(AuthorityValidTo(evidence?.EvidenceJson), binding.RequestedValidTo);
        var revokedAt = evidence?.RevokedAt;
        var verifierReference = PolicyExceptionDigests.VerifierReference(decision.CaseId, requirement.Id);
        var eligibilityEvidenceDigest =
            PolicyExceptionDigests.ComputeEligibilityEvidenceDigest(decision.EligibilityEvidenceJson);
        var approverId = decision.ActorUserId;
        var evidenceDigest = approverId is null
            ? string.Empty
            : PolicyExceptionDigests.ComputeEvidence(
                binding,
                decision.CaseId,
                requirement.Id,
                requirement.WorkflowRequirementKey,
                decision.Id,
                decision.Version,
                decision.DecisionDigest,
                approverId.Value,
                Role(requirement.Role),
                Authority(authority),
                decision.EligibilityEvidenceJson,
                eligibilityEvidenceDigest,
                decision.AuthorityEvidenceDigest,
                scope,
                segregationSatisfied: true,
                validFrom,
                validTo,
                revokedAt,
                verifierReference);

        var replay = await FindReplayAsync(binding, request, evidenceDigest, cancellationToken);
        // Validity and revocation are re-checked even for a replay: a verified response can never
        // claim authority that lapsed or was revoked (SPEC 05 REQ-06, REQ-08, CA-08).
        EnsureDecisionValid(request, decision, authority, requirement, binding, evidenceDigest, validFrom, validTo,
            revokedAt, utcNow);
        if (replay is not null)
        {
            ApprovalTelemetry.RecordPolicyException("REPLAYED");
            return new PolicyExceptionVerificationOutcome(
                replay.Id,
                persisted.Id,
                Replayed: true,
                BuildResponse(
                    request, binding, requirement, decision, evidence, scope, coveredLines,
                    eligibilityEvidenceDigest, evidenceDigest, validFrom, validTo, revokedAt,
                    verifierReference));
        }

        var record = await CreateAsync(
            binding, request, persisted, decision, requirement, evidenceDigest, revokedAt,
            verifierReference, utcNow, cancellationToken);
        logger.LogInformation(
            "Policy exception {RequestId} verified against decision {DecisionId}.",
            persisted.Id,
            decision.Id);
        ApprovalTelemetry.RecordPolicyException("VERIFIED");
        return new PolicyExceptionVerificationOutcome(
            record.Id,
            persisted.Id,
            Replayed: false,
            BuildResponse(
                request, binding, requirement, decision, evidence, scope, coveredLines,
                eligibilityEvidenceDigest, evidenceDigest, validFrom, validTo, revokedAt,
                verifierReference));
    }

    private static void EnsureDecisionValid(
        WorkflowVerificationRequest request,
        ApprovalDecisionRecord decision,
        AuthorityRequirement authority,
        ApprovalRequirementRecord requirement,
        PolicyExceptionBindingRequest binding,
        string evidenceDigest,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset? revokedAt,
        DateTimeOffset utcNow)
    {
        if (decision.Action != (int)ApprovalDecisionAction.Approve ||
            decision.Origin != (int)ApprovalDecisionOrigin.Human)
        {
            throw new DomainException("The policy exception requires a human APPROVE decision.");
        }

        if (decision.Version != request.WorkflowDecisionVersion)
        {
            throw new DomainException("The workflow decision version does not match the persisted decision.");
        }

        var approverId = decision.ActorUserId
            ?? throw new DomainException("The policy exception decision has no human approver.");
        var excluded = ApprovalJsonPersistence.DeserializeGuids(requirement.ExcludedUserIdsJson);
        if (excluded.Contains(approverId) ||
            approverId == binding.RequesterId ||
            approverId == binding.OriginatorId ||
            approverId == binding.WorkloadSubjectId)
        {
            throw new DomainException("The approver is excluded from the policy exception (segregation of duties).");
        }

        if (requirement.Role != (int)SystemRole.ProcurementApprover ||
            authority.Type != ApprovalAuthorityType.Procurement)
        {
            throw new DomainException("The policy exception requirement does not carry PROCUREMENT authority.");
        }

        if (revokedAt is not null || utcNow < validFrom || utcNow >= validTo)
        {
            throw new DomainException("The policy exception evidence is expired or revoked.");
        }

        if (string.IsNullOrWhiteSpace(evidenceDigest) ||
            string.IsNullOrWhiteSpace(request.EvidenceDigest) ||
            !string.Equals(evidenceDigest, request.EvidenceDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new PolicyExceptionBindingMismatchException(
                "The submitted evidence digest does not match the persisted decision evidence.");
        }
    }

    private static WorkflowVerificationResponse BuildResponse(
        WorkflowVerificationRequest request,
        PolicyExceptionBindingRequest binding,
        ApprovalRequirementRecord requirement,
        ApprovalDecisionRecord decision,
        DecisionAuthorityEvidenceRecord? evidence,
        DecisionScopeDescriptor scope,
        IReadOnlyList<PolicyExceptionTarget> coveredLines,
        string eligibilityEvidenceDigest,
        string evidenceDigest,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset? revokedAt,
        string verifierReference) =>
        new()
        {
            ContractVersion = PolicyExceptionContract.VerificationResponseVersion,
            Verified = true,
            VerifierId = PolicyExceptionContract.VerifierId,
            VerifierContractVersion = PolicyExceptionContract.VerificationResponseVersion,
            VerifierReference = verifierReference,
            CaseId = decision.CaseId,
            RequirementId = requirement.Id,
            RequirementKey = requirement.WorkflowRequirementKey,
            EvidenceDigest = evidenceDigest,
            Binding = binding.ComputeBinding(),
            Nonce = binding.Nonce,
            WorkflowDecisionId = decision.Id,
            WorkflowDecisionVersion = decision.Version,
            WorkflowDecisionDigest = decision.DecisionDigest,
            ApproverId = decision.ActorUserId,
            ApproverRole = Role(requirement.Role),
            AuthorityType = Authority(ApprovalJsonPersistence.DeserializeAuthority(requirement.AuthorityJson)),
            EligibilityEvidence = decision.EligibilityEvidenceJson,
            EligibilityEvidenceDigest = eligibilityEvidenceDigest,
            AuthorityEvidenceDigest = decision.AuthorityEvidenceDigest,
            DecisionScope = scope,
            CoveredLines = coveredLines,
            SegregationSatisfied = true,
            ValidFrom = validFrom,
            ValidTo = validTo,
            RevokedAt = revokedAt
        };

    /// <summary>
    /// Evidence digest of the policy exception linked to a decided requirement, or <c>null</c>
    /// when the requirement has no extension (SPEC 05 REQ-05). It lets the approval response hand
    /// Policy the exact value the verifier will compare.
    /// </summary>
    public async Task<string?> EvidenceDigestForRequirementAsync(
        Guid organizationId,
        Guid caseId,
        Guid requirementId,
        CancellationToken cancellationToken = default)
    {
        var persisted = await dbContext.ApprovalPolicyExceptionRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.OrganizationId == organizationId &&
                record.CaseId == caseId &&
                record.RequirementId == requirementId,
                cancellationToken);
        if (persisted is null)
        {
            return null;
        }

        var requirement = await dbContext.ApprovalRequirements
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == requirementId, cancellationToken);
        var decision = await dbContext.ApprovalDecisions
            .AsNoTracking()
            .Where(record =>
                record.RequirementId == requirementId &&
                record.Action == (int)ApprovalDecisionAction.Approve &&
                record.Origin == (int)ApprovalDecisionOrigin.Human)
            .OrderByDescending(record => record.DecidedAt)
            .ThenBy(record => record.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (requirement is null || decision?.ActorUserId is null)
        {
            return null;
        }

        var evidence = await dbContext.DecisionAuthorityEvidences
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == decision.EvidenceId, cancellationToken);
        var binding = RebuildBinding(persisted);
        var authority = ApprovalJsonPersistence.DeserializeAuthority(requirement.AuthorityJson);
        var scope = DecisionScopeDescriptor.Parse(requirement.DecisionScopeJson, organizationId);
        var validTo = MinValidity(AuthorityValidTo(evidence?.EvidenceJson), binding.RequestedValidTo);
        var eligibilityEvidenceDigest =
            PolicyExceptionDigests.ComputeEligibilityEvidenceDigest(decision.EligibilityEvidenceJson);
        return PolicyExceptionDigests.ComputeEvidence(
            binding,
            decision.CaseId,
            requirement.Id,
            requirement.WorkflowRequirementKey,
            decision.Id,
            decision.Version,
            decision.DecisionDigest,
            decision.ActorUserId.Value,
            Role(requirement.Role),
            Authority(authority),
            decision.EligibilityEvidenceJson,
            eligibilityEvidenceDigest,
            decision.AuthorityEvidenceDigest,
            scope,
            segregationSatisfied: true,
            decision.DecidedAt,
            validTo,
            evidence?.RevokedAt,
            PolicyExceptionDigests.VerifierReference(decision.CaseId, requirement.Id));
    }

    /// <summary>Rebuilds the binding inputs from the immutable persisted extension.</summary>
    public static PolicyExceptionBindingRequest RebuildBinding(ApprovalPolicyExceptionRequestRecord record) => new(
        record.OrganizationId,
        record.SubjectType,
        record.SubjectId,
        record.SubjectVersion,
        record.BaseBundleId,
        record.BaseResultDigest,
        record.PolicyVersionId,
        record.PolicyContentDigest,
        record.ManifestDigest,
        record.TargetRequirementKey,
        PolicyExceptionTarget.ParseCanonicalSet(record.CoveredLinesJson),
        record.From,
        record.To,
        record.Floor,
        record.ReferenceId,
        record.RequesterId,
        record.OriginatorId,
        record.WorkloadSubjectId,
        record.RequestedAt,
        record.RequestedValidTo,
        record.Nonce);

    private static PolicyExceptionBindingRequest Request(WorkflowVerificationRequest request) => new(
        request.OrganizationId,
        request.SubjectType ?? string.Empty,
        request.SubjectId,
        request.SubjectVersion,
        request.BaseBundleId,
        request.BaseResultDigest ?? string.Empty,
        request.PolicyVersionId,
        request.PolicyContentDigest ?? string.Empty,
        request.ManifestDigest ?? string.Empty,
        request.TargetRequirementKey ?? string.Empty,
        request.CoveredLines ?? [],
        request.From,
        request.To,
        request.Floor,
        request.ReferenceId,
        request.RequesterId,
        request.OriginatorId,
        request.WorkloadSubjectId,
        request.RequestedAt,
        request.RequestedValidTo,
        request.Nonce ?? string.Empty);

    private static void EnsureSameRequest(
        ApprovalPolicyExceptionRequestRecord persisted,
        PolicyExceptionBindingRequest binding)
    {
        var coveredLines = ApprovalCanonicalJson.Serialize(
            PolicyExceptionTarget.CanonicalizeSet(binding.CoveredLines));
        if (!string.Equals(persisted.SubjectType, binding.SubjectType, StringComparison.Ordinal) ||
            persisted.SubjectId != binding.SubjectId ||
            persisted.SubjectVersion != binding.SubjectVersion ||
            !string.Equals(persisted.BaseResultDigest, binding.BaseResultDigest, StringComparison.OrdinalIgnoreCase) ||
            persisted.PolicyVersionId != binding.PolicyVersionId ||
            !string.Equals(persisted.PolicyContentDigest, binding.PolicyContentDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(persisted.ManifestDigest, binding.ManifestDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(persisted.TargetRequirementKey, binding.TargetRequirementKey, StringComparison.Ordinal) ||
            !string.Equals(persisted.CoveredLinesJson, coveredLines, StringComparison.Ordinal) ||
            persisted.From != binding.From ||
            persisted.To != binding.To ||
            persisted.Floor != binding.Floor ||
            persisted.ReferenceId != binding.ReferenceId ||
            persisted.RequesterId != binding.RequesterId ||
            persisted.OriginatorId != binding.OriginatorId ||
            persisted.WorkloadSubjectId != binding.WorkloadSubjectId ||
            persisted.RequestedAt != binding.RequestedAt ||
            persisted.RequestedValidTo != binding.RequestedValidTo ||
            !string.Equals(persisted.Nonce, binding.Nonce, StringComparison.Ordinal))
        {
            throw new DomainConflictException(
                "The verification request does not match the persisted policy exception.");
        }
    }

    private async Task<ApprovalPolicyExceptionVerificationRecord?> FindReplayAsync(
        PolicyExceptionBindingRequest binding,
        WorkflowVerificationRequest request,
        string evidenceDigest,
        CancellationToken cancellationToken)
    {
        var byNonce = await dbContext.ApprovalPolicyExceptionVerifications
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.OrganizationId == binding.OrganizationId &&
                record.VerifierType == PolicyExceptionContract.VerifierType &&
                record.Nonce == binding.Nonce,
                cancellationToken);
        var byDecision = await dbContext.ApprovalPolicyExceptionVerifications
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.OrganizationId == binding.OrganizationId &&
                record.WorkflowDecisionId == request.WorkflowDecisionId &&
                record.WorkflowDecisionVersion == request.WorkflowDecisionVersion,
                cancellationToken);
        if (byNonce is not null && byDecision is not null && byNonce.Id != byDecision.Id)
        {
            ApprovalTelemetry.RecordConflict("POLICY_EXCEPTION");
            throw new DomainConflictException(
                "The nonce and the workflow decision were already used by different policy exceptions.");
        }

        var winner = byNonce ?? byDecision;
        if (winner is null)
        {
            return null;
        }

        var bindingDigest = binding.ComputeBinding();
        if (!string.Equals(winner.Binding, bindingDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(winner.EvidenceDigest, evidenceDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(winner.EvidenceDigest, request.EvidenceDigest, StringComparison.OrdinalIgnoreCase) ||
            winner.WorkflowDecisionId != request.WorkflowDecisionId ||
            winner.WorkflowDecisionVersion != request.WorkflowDecisionVersion)
        {
            ApprovalTelemetry.RecordConflict("POLICY_EXCEPTION");
            throw new DomainConflictException(
                "The nonce or workflow decision was already used with a different policy exception.");
        }

        return winner;
    }

    private async Task<ApprovalPolicyExceptionVerificationRecord> CreateAsync(
        PolicyExceptionBindingRequest binding,
        WorkflowVerificationRequest request,
        ApprovalPolicyExceptionRequestRecord persisted,
        ApprovalDecisionRecord decision,
        ApprovalRequirementRecord requirement,
        string evidenceDigest,
        DateTimeOffset? revokedAt,
        string verifierReference,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken)
    {
        var record = new ApprovalPolicyExceptionVerificationRecord
        {
            Id = Guid.NewGuid(),
            Version = 1,
            OrganizationId = binding.OrganizationId,
            RequestId = persisted.Id,
            CaseId = decision.CaseId,
            RequirementId = requirement.Id,
            VerifierType = PolicyExceptionContract.VerifierType,
            WorkflowDecisionId = decision.Id,
            WorkflowDecisionVersion = decision.Version,
            WorkflowDecisionDigest = decision.DecisionDigest,
            EvidenceDigest = evidenceDigest,
            Binding = binding.ComputeBinding(),
            Nonce = binding.Nonce,
            Verified = true,
            FailureCode = string.Empty,
            RevocationReference = revokedAt is null ? null : verifierReference,
            VerifiedAt = verifiedAt
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var concurrent = await FindReplayAsync(binding, request, evidenceDigest, cancellationToken);
        if (concurrent is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return concurrent;
        }

        dbContext.ApprovalPolicyExceptionVerifications.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await FindReplayAsync(binding, request, evidenceDigest, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        return record;
    }

    private static DateTimeOffset MinValidity(DateTimeOffset? authorityValidTo, DateTimeOffset requestedValidTo) =>
        authorityValidTo is null || requestedValidTo <= authorityValidTo.Value
            ? requestedValidTo
            : authorityValidTo.Value;

    private static DateTimeOffset? AuthorityValidTo(string? eligibilityEvidenceJson)
    {
        if (string.IsNullOrWhiteSpace(eligibilityEvidenceJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(eligibilityEvidenceJson);
            if (document.RootElement.TryGetProperty("authority_grant_valid_to", out var validTo))
            {
                if (validTo.ValueKind == JsonValueKind.String)
                {
                    return validTo.GetDateTimeOffset();
                }

                if (validTo.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
            }

            // The canonical eligibility evidence keeps nullable properties present; a missing
            // grant validity means the authority itself is the only bound (fail-closed at callers).
            return null;
        }
        catch (JsonException)
        {
            throw new DomainException("The persisted eligibility evidence is not readable.");
        }
    }

    private static string Role(int role) => OrganizationContractCodes.Role((SystemRole)role);

    private static string Authority(AuthorityRequirement authority) =>
        authority.Type is null
            ? throw new DomainException("The policy exception requirement has no authority type.")
            : OrganizationContractCodes.Authority(authority.Type.Value);
}

/// <summary>
/// Request fields that do not reproduce the submitted digest are a contract mismatch, not a
/// technical failure: the caller must resolve them before the control can be reduced (SPEC 05 REQ-06).
/// </summary>
public sealed class PolicyExceptionBindingMismatchException(string message) : DomainException(message);
