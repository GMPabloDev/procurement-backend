using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Api.Controllers;

public sealed record ApprovalSubmissionBody(
    Guid OrganizationId,
    string SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    string Operation,
    string? ContractVersion,
    string SubmissionKey,
    Guid? RequesterId,
    Guid OriginatorId);

public sealed record ApprovalSignalBody(
    bool Satisfied,
    string SignalKey,
    int ExpectedVersion,
    string? EvidenceReference,
    string? EvidenceDigest);

public sealed record ApprovalCancelBody(string Reason, int ExpectedVersion);

public sealed record ApprovalReconcileBody(string ReconciliationKey);

public sealed record ApprovalDecisionBody(
    string Action,
    string Reason,
    string DecisionKey,
    int ExpectedTaskVersion);

public sealed record ApprovalSubmissionResponse(Guid CaseId, string Status, int Version, bool Replayed);

public sealed record ApprovalTargetBody(string Type, Guid Id, int Version, string MaterialSnapshotDigest);

public sealed record ApprovalDelegationCreateBody(
    Guid DelegatorUserId,
    Guid DelegateeUserId,
    string Role,
    string ScopeJson,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string Reason,
    string DelegationCommandKey);

public sealed record ApprovalDelegationRevokeBody(int ExpectedVersion, string Reason, string DelegationCommandKey);

public sealed record ApprovalSupersessionMappingBody(
    ApprovalTargetBody Previous,
    ApprovalTargetBody Replacement,
    string MaterialitySchemaVersion,
    string MaterialityDigest);

public sealed record ApprovalSupersessionBody(
    int ExpectedPreviousCaseVersion,
    string SupersessionKey,
    string SubmissionKey,
    string? ContractVersion,
    IReadOnlyList<ApprovalSupersessionMappingBody> TargetMapping);

public sealed record ApprovalEvidenceRevocationBody(
    int ExpectedEvidenceVersion,
    string RevocationKey,
    string Reason,
    string? ReasonCode);

public sealed record ApprovalWorkloadResponse(
    Guid CaseId,
    string Status,
    int Version,
    bool Replayed,
    IReadOnlyList<Guid> OutboxEventIds);

[ApiController]
[Authorize]
[Route("api/v1/approval")]
public sealed class ApprovalController(
    ApprovalSubmissionService submissionService,
    ApprovalWorkflowService workflowService,
    // pi-lens-ignore: lsp:CS0246
    ApprovalDecisionService decisionService,
    ApprovalQueryService queryService,
    // pi-lens-ignore: lsp:CS0246
    ApprovalOperationsQueryService operationsQueryService,
    // pi-lens-ignore: lsp:CS0246
    ApprovalReconciliationService reconciliationService,
    // pi-lens-ignore: lsp:CS0246
    ApprovalOutboxAdministrationService outboxAdministrationService,
    // pi-lens-ignore: lsp:CS0246
    ApprovalInboxQueryService inboxQueryService,
    ApprovalDelegationService delegationService,
    ApprovalSupersessionService supersessionService,
    ApprovalEvidenceRevocationService evidenceRevocationService,
    ApprovalHistoryQueryService historyQueryService,
    ApprovalInstanceIdentity instanceIdentity,
    ProcureToPayDbContext dbContext,
    CurrentUserProvisioningService provisioningService) : ControllerBase
{
    private const string GlobalScopeJson = "[{\"dimension\":\"ORGANIZATION\",\"reference\":null}]";
    /// <summary>Workload-only ingestion: requirements are built by a trusted adapter (REQ-01).</summary>
    [HttpPost("submissions")]
    public async Task<ActionResult<ApprovalSubmissionResponse>> Submit(
        ApprovalSubmissionBody request,
        CancellationToken cancellationToken)
    {
        var workload = ResolveWorkload();
        var outcome = await submissionService.SubmitAsync(
            new ApprovalSubmissionCommand(
                workload,
                request.OrganizationId,
                request.SubjectType,
                request.SubjectId,
                request.SubjectVersion,
                request.Operation,
                request.ContractVersion,
                request.SubmissionKey,
                request.RequesterId,
                request.OriginatorId,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new ApprovalSubmissionResponse(outcome.CaseId, outcome.Status, outcome.Version, outcome.Replayed));
    }

    /// <summary>Owner workload signals a prerequisite (REQ-03).</summary>
    [HttpPost("prerequisites/{prerequisiteId:guid}/signals")]
    public async Task<ActionResult<ApprovalWorkloadResponse>> Signal(
        Guid prerequisiteId,
        ApprovalSignalBody request,
        CancellationToken cancellationToken)
    {
        var workload = ResolveWorkload();
        var outcome = await workflowService.SignalAsync(
            prerequisiteId,
            new ApprovalSignalCommand(
                workload,
                request.Satisfied,
                request.SignalKey,
                request.ExpectedVersion,
                request.EvidenceReference,
                request.EvidenceDigest,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new ApprovalWorkloadResponse(
            outcome.CaseId, outcome.Status, outcome.Version, outcome.Replayed, outcome.OutboxEventIds));
    }

    /// <summary>Owning workload cancels an open case (REQ-07).</summary>
    [HttpPost("cases/{caseId:guid}/cancel")]
    public async Task<ActionResult<ApprovalWorkloadResponse>> Cancel(
        Guid caseId,
        ApprovalCancelBody request,
        CancellationToken cancellationToken)
    {
        var workload = ResolveWorkload();
        var outcome = await workflowService.CancelAsync(
            caseId,
            workload,
            request.Reason,
            request.ExpectedVersion,
            Correlation(),
            DateTimeOffset.UtcNow,
            cancellationToken);
        return Ok(new ApprovalWorkloadResponse(
            outcome.CaseId, outcome.Status, outcome.Version, outcome.Replayed, outcome.OutboxEventIds));
    }

    /// <summary>Minimized case read for its originator, an owner workload, AUDITOR or ADMIN (REQ-09).</summary>
    [HttpGet("cases/{caseId:guid}")]
    public async Task<ActionResult<ApprovalCaseView>> GetCase(Guid caseId, CancellationToken cancellationToken)
    {
        var workload = TryResolveWorkload();
        if (workload is not null)
        {
            return Ok(await queryService.GetCaseForWorkloadAsync(workload, caseId, cancellationToken));
        }

        var actor = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (actor.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var isAuditor = await dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == actor.Id &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson &&
                          assignment.Role == (int)SystemRole.Auditor,
            cancellationToken);
        var visible = await queryService.GetCaseAsync(actor.OrganizationId, caseId, cancellationToken);
        if (!isAuditor && visible.OriginatorId != actor.Id)
        {
            throw new DomainNotFoundException("The approval case is not visible.");
        }

        return Ok(visible);
    }

    /// <summary>
    /// The current assignee decides a pending task (REQ-06). ADMIN is rejected here: restoring
    /// operation never substitutes for business authority (CA-05, CA-08).
    /// </summary>
    [HttpPost("tasks/{taskId:guid}/decisions")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalDecisionOutcome>> Decide(
        Guid taskId,
        ApprovalDecisionBody request,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        if (!Enum.TryParse<ApprovalDecisionAction>(request.Action, ignoreCase: true, out var action))
        {
            throw new DomainValidationException("The approval action is invalid.");
        }

        return Ok(await decisionService.DecideAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDecisionCommand(
                taskId,
                action,
                request.Reason,
                request.DecisionKey,
                request.ExpectedTaskVersion,
                profile.Id,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>
    /// The delegator creates a bounded delegation; ADMIN organization scope may act as operational
    /// relief with a reason (SPEC 04 REQ-01, CA-01).
    /// </summary>
    [HttpPost("delegations")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalDelegationOutcome>> CreateDelegation(
        ApprovalDelegationCreateBody request,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var actorType = ApprovalDelegationActorType.Delegator;
        if (profile.Id != request.DelegatorUserId)
        {
            await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
            actorType = ApprovalDelegationActorType.Admin;
        }

        if (!Enum.TryParse<SystemRole>(request.Role, ignoreCase: true, out var role))
        {
            throw new DomainValidationException("The delegated role is invalid.");
        }

        var scope = DecisionScopeDescriptor.Parse(request.ScopeJson, profile.OrganizationId);
        return Ok(await delegationService.CreateAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDelegationCreateCommand(
                actorType,
                profile.Id,
                profile.OrganizationId,
                request.DelegatorUserId,
                request.DelegateeUserId,
                role,
                scope,
                request.ValidFrom,
                request.ValidTo,
                request.Reason,
                request.DelegationCommandKey,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>The delegator or ADMIN revokes a scheduled or active delegation (SPEC 04 REQ-01).</summary>
    [HttpPost("delegations/{delegationId:guid}/revoke")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalDelegationOutcome>> RevokeDelegation(
        Guid delegationId,
        ApprovalDelegationRevokeBody request,
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var record = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == delegationId && candidate.OrganizationId == profile.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval delegation is not visible.");
        var actorType = record.DelegatorUserId == profile.Id
            ? ApprovalDelegationActorType.Delegator
            : ApprovalDelegationActorType.Admin;
        if (actorType == ApprovalDelegationActorType.Admin)
        {
            await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        }

        return Ok(await delegationService.RevokeAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalDelegationRevokeCommand(
                actorType,
                profile.Id,
                profile.OrganizationId,
                delegationId,
                request.ExpectedVersion,
                request.Reason,
                request.DelegationCommandKey,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>The approver reads delegations where they are delegator or delegatee (SPEC 04 REQ-07).</summary>
    [HttpGet("delegations")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalDelegationView>>> GetMyDelegations(
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        return Ok(await historyQueryService.GetMyDelegationsAsync(
            profile.OrganizationId, profile.Id, cancellationToken));
    }

    /// <summary>Organizational AUDITOR read of every delegation (SPEC 04 REQ-07).</summary>
    [HttpGet("delegations/organization")]
    public async Task<ActionResult<IReadOnlyList<ApprovalDelegationView>>> GetOrganizationDelegations(
        CancellationToken cancellationToken)
    {
        var profile = await RequireAuditorAsync(cancellationToken);
        return Ok(await historyQueryService.GetOrganizationDelegationsAsync(
            profile.OrganizationId, cancellationToken));
    }

    /// <summary>
    /// The owning workload opens a new immutable version that supersedes the previous case
    /// (SPEC 04 REQ-04). Only a bijective, complete mapping is accepted.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/supersessions")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalSupersessionOutcome>> Supersede(
        Guid caseId,
        ApprovalSupersessionBody request,
        CancellationToken cancellationToken)
    {
        var workload = ResolveWorkload();
        var mapping = (request.TargetMapping ?? [])
            .Select(entry => ApprovalSupersessionMapping.Create(
                new ApprovalTarget(
                    entry.Previous.Type,
                    entry.Previous.Id,
                    entry.Previous.Version,
                    entry.Previous.MaterialSnapshotDigest),
                new ApprovalTarget(
                    entry.Replacement.Type,
                    entry.Replacement.Id,
                    entry.Replacement.Version,
                    entry.Replacement.MaterialSnapshotDigest),
                entry.MaterialitySchemaVersion,
                entry.MaterialityDigest))
            .ToArray();
        return Ok(await supersessionService.SupersedeAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalSupersessionCommand(
                workload,
                caseId,
                request.ExpectedPreviousCaseVersion,
                request.SupersessionKey,
                request.SubmissionKey,
                request.ContractVersion,
                mapping,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>
    /// Version chain of a case for its originator, its owning workload or an organizational
    /// AUDITOR; anything outside the visible scope is a 404 (SPEC 04 REQ-07).
    /// </summary>
    [HttpGet("cases/{caseId:guid}/history")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalCaseChainView>> GetCaseHistory(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var workload = TryResolveWorkload();
        if (workload is not null)
        {
            return Ok(await historyQueryService.GetCaseChainForWorkloadAsync(
                workload, caseId, cancellationToken));
        }

        var profile = await RequireActiveProfileAsync(cancellationToken);
        var visible = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == caseId && record.OrganizationId == profile.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        var isAuditor = await dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson &&
                          assignment.Role == (int)SystemRole.Auditor,
            cancellationToken);
        if (!isAuditor && visible.OriginatorId != profile.Id)
        {
            throw new DomainNotFoundException("The approval case is not visible.");
        }

        return Ok(await historyQueryService.GetCaseChainAsync(
            profile.OrganizationId, caseId, cancellationToken));
    }

    /// <summary>
    /// The owning workload invalidates its binding; ADMIN only as incident containment with a
    /// reason (SPEC 04 REQ-06, CA-06).
    /// </summary>
    [HttpPost("evidence/{evidenceId:guid}/revocations")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalEvidenceRevocationOutcome>> RevokeEvidence(
        Guid evidenceId,
        ApprovalEvidenceRevocationBody request,
        CancellationToken cancellationToken)
    {
        var workload = TryResolveWorkload();
        if (workload is not null)
        {
            return Ok(await evidenceRevocationService.RevokeAsync(
                // pi-lens-ignore: lsp:CS0246
                new ApprovalEvidenceRevocationCommand(
                    EvidenceRevocationActorType.Workload,
                    Guid.Empty,
                    workload,
                    Guid.Empty,
                    evidenceId,
                    request.ExpectedEvidenceVersion,
                    request.RevocationKey,
                    EvidenceRevocationReasonCode.OwnerInvalidation,
                    request.Reason,
                    Correlation()),
                DateTimeOffset.UtcNow,
                cancellationToken));
        }

        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        var reasonCode = request.ReasonCode is null
            ? EvidenceRevocationReasonCode.IncidentContainment
            : ApprovalEvolutionCodes.ParseReasonCode(request.ReasonCode);
        return Ok(await evidenceRevocationService.RevokeAsync(
            // pi-lens-ignore: lsp:CS0246
            new ApprovalEvidenceRevocationCommand(
                EvidenceRevocationActorType.Admin,
                profile.Id,
                null,
                profile.OrganizationId,
                evidenceId,
                request.ExpectedEvidenceVersion,
                request.RevocationKey,
                reasonCode,
                request.Reason,
                Correlation()),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>ADMIN sees the requirements with no eligible candidate (REQ-09).</summary>
    [HttpGet("operations/unassigned")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalUnassignedView>>> GetUnassigned(
        CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await operationsQueryService.GetUnassignedAsync(profile.OrganizationId, cancellationToken));
    }

    /// <summary>Reconciliation state for ADMIN (NFR-03).</summary>
    [HttpGet("operations/reconciliation")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalReconciliationStateView>> GetReconciliationState(
        CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await operationsQueryService.GetReconciliationStateAsync(
            profile.OrganizationId, DateTimeOffset.UtcNow, cancellationToken));
    }

    /// <summary>
    /// ADMIN triggers an idempotent reconciliation with its required key (REQ-04, REQ-09). It
    /// restores operation but cannot choose an assignee, create authority evidence or change a
    /// terminal decision.
    /// </summary>
    [HttpPost("operations/reconciliation")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalReconciliationOutcome>> Reconcile(
        ApprovalReconcileBody request,
        CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await reconciliationService.ReconcileAsync(
            profile.OrganizationId,
            profile.Id,
            request.ReconciliationKey,
            instanceIdentity.Owner,
            Correlation(),
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>Outbox backlog and reconciliation state for ADMIN (REQ-10).</summary>
    [HttpGet("operations/outbox")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalOutboxBacklog>> GetOutboxBacklog(CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await outboxAdministrationService.GetBacklogAsync(
            profile.OrganizationId, DateTimeOffset.UtcNow, cancellationToken));
    }

    /// <summary>Dead letters awaiting an administrative decision for ADMIN (REQ-10).</summary>
    [HttpGet("operations/outbox/dead-letters")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalOutboxBacklogEntry>>> GetDeadLetters(
        CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await outboxAdministrationService.GetDeadLettersAsync(
            profile.OrganizationId, cancellationToken));
    }

    /// <summary>
    /// ADMIN replays a dead letter; the contractual payload is never edited (REQ-10).
    /// </summary>
    [HttpPost("operations/outbox/{eventId:guid}/replay")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<ApprovalOutboxBacklogEntry>> ReplayOutboxEvent(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireAdministrativeAsync(requireAdmin: true, cancellationToken);
        return Ok(await outboxAdministrationService.ReplayAsync(
            profile.OrganizationId,
            eventId,
            profile.Id,
            HttpContext.TraceIdentifier,
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    /// <summary>Minimized backlog of pending tasks of the signed-in assignee (REQ-09).</summary>
    [HttpGet("inbox")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalInboxItemView>>> GetInbox(CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        return Ok(await inboxQueryService.GetInboxAsync(
            profile.OrganizationId, profile.Id, cancellationToken));
    }

    /// <summary>Decisions already taken by the signed-in actor (REQ-09).</summary>
    [HttpGet("inbox/history")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalDecisionHistoryView>>> GetDecisionHistory(
        CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        return Ok(await inboxQueryService.GetDecisionHistoryAsync(
            profile.OrganizationId, profile.Id, cancellationToken));
    }

    /// <summary>Organizational AUDITOR read of the decisions of a case (REQ-09).</summary>
    [HttpGet("cases/{caseId:guid}/decisions")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalCaseDecisionView>>> GetCaseDecisions(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireAuditorAsync(cancellationToken);
        return Ok(await inboxQueryService.GetCaseDecisionsAsync(
            profile.OrganizationId, caseId, cancellationToken));
    }

    /// <summary>Organizational AUDITOR read of the assignment history of a case (REQ-09).</summary>
    [HttpGet("cases/{caseId:guid}/assignments")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalCaseAssignmentView>>> GetCaseAssignments(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireAuditorAsync(cancellationToken);
        return Ok(await inboxQueryService.GetCaseAssignmentsAsync(
            profile.OrganizationId, caseId, cancellationToken));
    }

    /// <summary>
    /// Organizational AUDITOR read of the audit trail of a case, including the actor union and the
    /// immutable causal chain of automatic effects (REQ-09, CA-08).
    /// </summary>
    [HttpGet("cases/{caseId:guid}/audit")]
    // pi-lens-ignore: lsp:CS0246
    public async Task<ActionResult<IReadOnlyList<ApprovalCaseAuditView>>> GetCaseAudit(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var profile = await RequireAuditorAsync(cancellationToken);
        return Ok(await inboxQueryService.GetCaseAuditAsync(
            profile.OrganizationId, caseId, cancellationToken));
    }

    private async Task<UserProfileRecord> RequireAuditorAsync(CancellationToken cancellationToken)
    {
        var profile = await RequireActiveProfileAsync(cancellationToken);
        var authorized = await dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson &&
                          assignment.Role == (int)SystemRole.Auditor,
            cancellationToken);
        if (!authorized)
        {
            throw new DomainForbiddenException(
                "Organizational approval evidence requires the auditor role; administration does not grant it.");
        }

        return profile;
    }

    /// <summary>
    /// Opaque server-projected correlation reference (REQ-08): the request cannot supply free text.
    /// </summary>
    private string Correlation()
    {
        var trace = HttpContext.TraceIdentifier;
        if (!string.IsNullOrWhiteSpace(trace) &&
            trace.Length <= 120 &&
            trace.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-'))
        {
            return trace;
        }

        return $"corr-{Guid.NewGuid():N}";
    }

    private async Task<UserProfileRecord> RequireActiveProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        return profile;
    }

    private async Task<UserProfileRecord> RequireAdministrativeAsync(
        bool requireAdmin,
        CancellationToken cancellationToken)
    {
        var profile = await provisioningService.EnsureProfileAsync(User, cancellationToken);
        if (profile.Status != (int)UserProfileStatus.Active)
        {
            throw new DomainForbiddenException("The local user profile is not active.");
        }

        var allowedRoles = requireAdmin
            ? new[] { (int)SystemRole.Admin }
            : new[] { (int)SystemRole.Admin, (int)SystemRole.Auditor };
        var authorized = await dbContext.RoleAssignments.AnyAsync(
            assignment => assignment.UserProfileId == profile.Id &&
                          assignment.Status == (int)AssignmentStatus.Active &&
                          assignment.ScopeJson == GlobalScopeJson &&
                          allowedRoles.Contains(assignment.Role),
            cancellationToken);
        if (!authorized)
        {
            throw new DomainForbiddenException("The user does not have the required administrative role.");
        }

        return profile;
    }

    private ApprovalWorkloadIdentity ResolveWorkload() =>
        TryResolveWorkload() ??
        throw new DomainForbiddenException("A workload client identity is required.");

    private ApprovalWorkloadIdentity? TryResolveWorkload()
    {
        var clientId = User.FindFirst("client_id")?.Value ?? User.FindFirst("azp")?.Value;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var issuer = User.FindFirst("iss")?.Value ?? "internal://procure-to-pay";
        return new ApprovalWorkloadIdentity(issuer, clientId);
    }
}
