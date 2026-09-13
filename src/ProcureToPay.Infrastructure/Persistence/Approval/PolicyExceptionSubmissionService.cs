using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>
/// Workload-only command that opens a quotation waiver case (SPEC 05 REQ-05). It creates the
/// case, one ordinary <c>PROCUREMENT_APPROVER</c> requirement and the immutable
/// <c>policy-exception-request/v1</c> extension in a single transaction; a user or
/// <c>ADMIN</c> cannot invoke it because the workload allowlist is checked before any write.
/// </summary>
public sealed class PolicyExceptionSubmissionService(
    ProcureToPayDbContext dbContext,
    ApprovalSubmissionService submissionService,
    ApprovalWorkloadAllowlist allowlist,
    ILogger<PolicyExceptionSubmissionService> logger)
{
    public const string AdapterId = "policy-exception-adapter";

    public async Task<PolicyExceptionSubmissionOutcome> SubmitAsync(
        PolicyExceptionSubmission command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        allowlist.EnsureAllowed(command.Workload);
        var utcNow = occurredAt.ToUniversalTime();
        var binding = new PolicyExceptionBindingRequest(
            command.OrganizationId,
            command.SubjectType,
            command.SubjectId,
            command.SubjectVersion,
            command.BaseBundleId,
            command.BaseResultDigest,
            command.PolicyVersionId,
            command.PolicyContentDigest,
            command.ManifestDigest,
            command.TargetRequirementKey,
            command.CoveredLines,
            command.From,
            command.To,
            command.Floor,
            command.ReferenceId,
            command.RequesterId,
            command.OriginatorId,
            command.WorkloadSubjectId,
            command.RequestedAt,
            command.RequestedValidTo,
            command.Nonce);

        // The extension must reference a real evaluation of the same subject version: a
        // workload cannot invent bindings that no Policy evaluation can ever verify (REQ-05).
        var record = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(bundle =>
                bundle.Id == command.BaseBundleId &&
                bundle.OrganizationId == command.OrganizationId &&
                bundle.SubjectId == command.SubjectId &&
                bundle.SubjectVersion == command.SubjectVersion,
                cancellationToken);
        if (record is null)
        {
            throw new DomainConflictException("The referenced policy evaluation is not available.");
        }

        if (!string.Equals(record.ResultDigest, command.BaseResultDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.PolicyContentDigest, command.PolicyContentDigest, StringComparison.OrdinalIgnoreCase) ||
            record.PolicySetVersionId != command.PolicyVersionId)
        {
            throw new DomainConflictException(
                "The policy exception bindings do not match the referenced evaluation.");
        }

        var targets = command.CoveredLines
            .Select(target => new ApprovalTarget(target.Type, target.Id, target.Version, target.MaterialSnapshotDigest))
            .ToArray();
        var exclusions = ImmutableHashSet.CreateBuilder<Guid>();
        exclusions.Add(command.OriginatorId);
        exclusions.Add(command.WorkloadSubjectId);
        if (command.RequesterId is Guid requester && requester != Guid.Empty)
        {
            exclusions.Add(requester);
        }

        // The exception is decided organization-wide: the only decision scope available in this
        // release covers the union of every line the base evaluation may have evaluated.
        var scope = DecisionScopeDescriptor.Create(
            command.OrganizationId, [new DecisionScopeEntry(ScopeDimension.Organization, null, null)]);
        var requirement = new ApprovalRequirementDefinition(
            PolicyExceptionContract.ExceptionRequirementKey,
            PolicyExceptionContract.ExceptionRequirementStage,
            SystemRole.ProcurementApprover,
            AuthorityRequirement.Required(ApprovalAuthorityType.Procurement, 1, null, null),
            scope,
            // pi-lens-ignore: lsp:CS0117
            [ApprovalDecisionAction.Approve, ApprovalDecisionAction.Reject],
            exclusions.ToImmutable(),
            targets,
            []);

        var descriptor = new ApprovalAdapterDescriptor(
            AdapterId,
            command.SubjectType,
            PolicyExceptionContract.ExceptionCaseOperation,
            PolicyExceptionContract.SubmissionContractVersion,
            RequesterRequired: false);
        var submission = new ApprovalSubmission(
            command.SubmissionKey,
            command.OrganizationId,
            command.SubjectType,
            command.SubjectId,
            command.SubjectVersion,
            PolicyExceptionContract.ExceptionCaseOperation,
            binding.ComputeBinding(),
            command.RequesterId,
            command.OriginatorId,
            [requirement],
            []);
        var approvalCommand = new ApprovalSubmissionCommand(
            command.Workload,
            command.OrganizationId,
            command.SubjectType,
            command.SubjectId,
            command.SubjectVersion,
            PolicyExceptionContract.ExceptionCaseOperation,
            PolicyExceptionContract.SubmissionContractVersion,
            command.SubmissionKey,
            command.RequesterId,
            command.OriginatorId,
            command.CorrelationReference);

        var outcome = await submissionService.SubmitPreparedAsync(
            approvalCommand,
            descriptor,
            submission,
            utcNow,
            approvalCase => PersistExtension(approvalCase, binding, command, utcNow),
            cancellationToken);

        var extension = await dbContext.ApprovalPolicyExceptionRequests
            .AsNoTracking()
            .SingleAsync(request => request.CaseId == outcome.CaseId, cancellationToken);
        logger.LogInformation(
            "Policy exception {RequestId} for evaluation {BundleId} resolved as {State}.",
            extension.Id,
            command.BaseBundleId,
            outcome.Replayed ? "REPLAY" : "CREATED");
        ApprovalTelemetry.RecordPolicyException(outcome.Replayed ? "REPLAYED" : "CREATED");
        return new PolicyExceptionSubmissionOutcome(
            outcome.CaseId,
            extension.RequirementId,
            extension.RequirementKey,
            extension.Id,
            extension.Binding,
            outcome.Status,
            outcome.Version,
            outcome.Replayed);
    }

    private void PersistExtension(
        ApprovalCase approvalCase,
        PolicyExceptionBindingRequest binding,
        PolicyExceptionSubmission command,
        DateTimeOffset createdAt)
    {
        var requirement = approvalCase.Requirements.Single(
            item => string.Equals(
                item.SourceRequirementKey, PolicyExceptionContract.ExceptionRequirementKey, StringComparison.Ordinal));
        dbContext.ApprovalPolicyExceptionRequests.Add(new ApprovalPolicyExceptionRequestRecord
        {
            Id = Guid.NewGuid(),
            Version = 1,
            OrganizationId = command.OrganizationId,
            CaseId = approvalCase.Id,
            RequirementId = requirement.Id,
            RequirementKey = requirement.WorkflowRequirementKey,
            SubjectType = command.SubjectType,
            SubjectId = command.SubjectId,
            SubjectVersion = command.SubjectVersion,
            BaseBundleId = command.BaseBundleId,
            BaseResultDigest = command.BaseResultDigest,
            PolicyVersionId = command.PolicyVersionId,
            PolicyContentDigest = command.PolicyContentDigest,
            ManifestDigest = command.ManifestDigest,
            TargetRequirementKey = command.TargetRequirementKey,
            CoveredLinesJson = ApprovalCanonicalJson.Serialize(
                PolicyExceptionTarget.CanonicalizeSet(command.CoveredLines)),
            From = command.From,
            To = command.To,
            Floor = command.Floor,
            ReferenceId = command.ReferenceId,
            RequesterId = command.RequesterId,
            OriginatorId = command.OriginatorId,
            WorkloadSubjectId = command.WorkloadSubjectId,
            RequestedAt = command.RequestedAt.ToUniversalTime(),
            RequestedValidTo = command.RequestedValidTo.ToUniversalTime(),
            Nonce = binding.Nonce,
            Binding = binding.ComputeBinding(),
            CreatedAt = createdAt
        });
    }
}
