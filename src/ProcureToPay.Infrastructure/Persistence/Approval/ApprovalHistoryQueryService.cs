using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

/// <summary>Minimized delegation record of the approver's own history (SPEC 04 REQ-07).</summary>
public sealed record ApprovalDelegationView(
    Guid DelegationId,
    Guid DelegatorUserId,
    Guid DelegateeUserId,
    string Role,
    string Status,
    int Version,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string DecisionScopeJson,
    Guid? RootAuditId);

/// <summary>Provenance of one derived decision inside the visible case chain (SPEC 04 REQ-07).</summary>
public sealed record ApprovalCarryForwardLinkView(
    Guid NewDecisionId,
    Guid SourceDecisionId,
    Guid RootHumanDecisionId,
    Guid EvidenceId,
    int EvidenceVersion,
    string EvidenceStatus,
    Guid RequirementId,
    string RequirementKey,
    string ProofDigest,
    DateTimeOffset CreatedAt);

/// <summary>Irreversible revocation visible to the owning workload, originator or AUDITOR.</summary>
public sealed record ApprovalEvidenceRevocationView(
    Guid RevocationId,
    Guid EvidenceId,
    Guid DecisionId,
    string ActorType,
    Guid? ActorUserId,
    string? ActorWorkloadIssuer,
    string? ActorWorkloadClientId,
    string ReasonCode,
    DateTimeOffset CreatedAt);

/// <summary>Version chain of one subject with its supersessions, carry-forward and revocations.</summary>
public sealed record ApprovalCaseChainView(
    Guid CaseId,
    Guid? PreviousCaseId,
    Guid? NextCaseId,
    string Status,
    int Version,
    IReadOnlyList<ApprovalCarryForwardLinkView> CarryForwards,
    IReadOnlyList<ApprovalEvidenceRevocationView> Revocations);

/// <summary>
/// History surfaces of SPEC 04 REQ-07: the approver reads own and delegated assignments, the
/// originator or owning workload reads the case chain, and AUDITOR reads the organizational
/// delegations, carry-forward and revocations. Everything is organization scoped, so anything
/// outside the visible scope is absent instead of leaking a partial record.
/// </summary>
public sealed class ApprovalHistoryQueryService(ProcureToPayDbContext dbContext)
{
    /// <summary>Delegations where the actor is the delegator or the delegatee (REQ-07).</summary>
    public async Task<IReadOnlyList<ApprovalDelegationView>> GetMyDelegationsAsync(
        Guid organizationId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                (record.DelegatorUserId == actorUserId || record.DelegateeUserId == actorUserId))
            .OrderBy(record => record.ValidFrom)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        return records.Select(ToView).ToArray();
    }

    /// <summary>Organizational AUDITOR read of every delegation of the organization (REQ-07).</summary>
    public async Task<IReadOnlyList<ApprovalDelegationView>> GetOrganizationDelegationsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.ApprovalDelegations
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId)
            .OrderBy(record => record.ValidFrom)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        return records.Select(ToView).ToArray();
    }

    /// <summary>
    /// Version chain of one case: previous and next case, its carry-forward provenance and the
    /// revocations of its linked evidence. An invisible case is a 404 (REQ-07).
    /// </summary>
    public async Task<ApprovalCaseChainView> GetCaseChainAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        var caseRecord = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == caseId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");

        var supersessions = await dbContext.CaseSupersessions
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                (record.NewCaseId == caseId || record.PreviousCaseId == caseId))
            .ToArrayAsync(cancellationToken);
        var previousCaseId = supersessions
            .Where(record => record.NewCaseId == caseId)
            .Select(record => (Guid?)record.PreviousCaseId)
            .SingleOrDefault();
        var nextCaseId = supersessions
            .Where(record => record.PreviousCaseId == caseId)
            .Select(record => (Guid?)record.NewCaseId)
            .SingleOrDefault();

        var carryForwards = await dbContext.DecisionCarryForwardEntries
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.NewCaseId == caseId)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);
        var evidenceIds = carryForwards.Select(record => record.EvidenceId).Distinct().ToArray();
        var evidenceStatuses = (await dbContext.DecisionAuthorityEvidences
                .AsNoTracking()
                .Where(record => evidenceIds.Contains(record.Id))
                .Select(record => new { record.Id, record.Status })
                .ToArrayAsync(cancellationToken))
            .ToDictionary(entry => entry.Id, entry => entry.Status);

        var revocations = await dbContext.DecisionEvidenceRevocations
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId && record.CaseId == caseId)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToArrayAsync(cancellationToken);

        return new ApprovalCaseChainView(
            caseRecord.Id,
            previousCaseId,
            nextCaseId,
            ((ApprovalCaseStatus)caseRecord.Status).ToString().ToUpperInvariant(),
            caseRecord.Version,
            carryForwards
                .Select(record => new ApprovalCarryForwardLinkView(
                    record.NewDecisionId,
                    record.SourceDecisionId,
                    record.RootHumanDecisionId,
                    record.EvidenceId,
                    record.EvidenceVersion,
                    evidenceStatuses.TryGetValue(record.EvidenceId, out var status) ? status : "UNKNOWN",
                    record.NewRequirementId,
                    record.NewRequirementKey,
                    record.ProofDigest,
                    record.CreatedAt))
                .ToArray(),
            revocations
                .Select(record => new ApprovalEvidenceRevocationView(
                    record.Id,
                    record.EvidenceId,
                    record.DecisionId,
                    record.ActorType,
                    record.ActorUserId,
                    record.ActorWorkloadIssuer,
                    record.ActorWorkloadClientId,
                    record.ReasonCode,
                    record.CreatedAt))
                .ToArray());
    }

    /// <summary>
    /// Version chain for the owning workload (REQ-07): a case of another workload is a 404, so the
    /// command identity never widens the visible scope.
    /// </summary>
    public async Task<ApprovalCaseChainView> GetCaseChainForWorkloadAsync(
        ApprovalWorkloadIdentity workload,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workload);
        var organizationId = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record => record.Id == caseId &&
                             record.WorkloadIssuer == workload.Issuer &&
                             record.WorkloadClientId == workload.ClientId)
            .Select(record => (Guid?)record.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainNotFoundException("The approval case is not visible.");
        return await GetCaseChainAsync(organizationId, caseId, cancellationToken);
    }

    private static ApprovalDelegationView ToView(ApprovalDelegationRecord record) => new(
        record.Id,
        record.DelegatorUserId,
        record.DelegateeUserId,
        ((SystemRole)record.Role).ToString().ToUpperInvariant(),
        record.Status,
        record.Version,
        record.ValidFrom,
        record.ValidTo,
        record.DecisionScopeJson,
        record.RootAuditId);
}
