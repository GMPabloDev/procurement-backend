using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Approval;

public sealed record ApprovalReconciliationOutcome(
    Guid OrganizationId,
    int Scanned,
    int Reassigned,
    int Unassigned,
    int Unchanged,
    DateTimeOffset CompletedAt);

/// <summary>
/// Idempotent authority reconciliation (REQ-04, NFR-03): re-evaluates every PENDING requirement
/// in an organization against the current profiles, roles, grants and scopes, and reassigns or
/// releases only the tasks whose assignee stopped being eligible. A decided task never changes.
/// Each case is one transaction so assignment, audit and status stay atomic (NFR-02).
/// </summary>
public sealed class ApprovalReconciliationService(
    ProcureToPayDbContext dbContext,
    ApprovalAssignmentEngine assignmentEngine,
    ILogger<ApprovalReconciliationService> logger)
{
    /// <summary>A due reconciliation must complete within 60 seconds (NFR-03).</summary>
    public static readonly TimeSpan MaxReconciliationAge = TimeSpan.FromSeconds(60);

    public async Task<bool> IsDueAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var last = await dbContext.ApprovalWorkflowStates
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId)
            .Select(record => (DateTimeOffset?)record.LastReconciliationCompletedAt)
            .SingleOrDefaultAsync(cancellationToken);
        return last is null || now.ToUniversalTime() - last.Value >= MaxReconciliationAge;
    }

    public async Task<ApprovalReconciliationOutcome> ReconcileAsync(
        Guid organizationId,
        string owner,
        string correlationReference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var utcNow = occurredAt.ToUniversalTime();
        var caseIds = await dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                (record.Status == (int)ApprovalCaseStatus.Open ||
                 record.Status == (int)ApprovalCaseStatus.Blocked))
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);

        var scanned = 0;
        var reassigned = 0;
        var unassigned = 0;
        var unchanged = 0;

        foreach (var caseId in caseIds.OrderBy(id => id.ToString("D"), StringComparer.Ordinal))
        {
            scanned++;
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var caseRecord = await dbContext.ApprovalCases
                .SingleOrDefaultAsync(record => record.Id == caseId, cancellationToken);
            if (caseRecord is null)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            var requirementRecords = await dbContext.ApprovalRequirements
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var prerequisiteRecords = await dbContext.ApprovalPrerequisites
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var taskRecords = await dbContext.ApprovalTasks
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);
            var assignmentRecords = await dbContext.ApprovalAssignments
                .Where(record => record.CaseId == caseId)
                .ToArrayAsync(cancellationToken);

            var approvalCase = ApprovalCaseHydrator.Hydrate(
                caseRecord, requirementRecords, prerequisiteRecords, taskRecords);
            if (approvalCase.Status is not (ApprovalCaseStatus.Open or ApprovalCaseStatus.Blocked))
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            var outcomes = await assignmentEngine.ReconcilePendingAsync(
                approvalCase, assignmentRecords, utcNow, correlationReference, cancellationToken);
            if (outcomes.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            ApprovalStateSync.Apply(
                approvalCase, caseRecord, requirementRecords, taskRecords, prerequisiteRecords);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            reassigned += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Reassigned);
            unassigned += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Unassigned);
            unchanged += outcomes.Count(outcome => outcome.Kind == ApprovalAssignmentOutcomeKind.Unchanged);
        }

        var state = await dbContext.ApprovalWorkflowStates
            .SingleOrDefaultAsync(record => record.OrganizationId == organizationId, cancellationToken);
        if (state is null)
        {
            state = new ApprovalWorkflowStateRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId
            };
            dbContext.ApprovalWorkflowStates.Add(state);
        }

        state.LastReconciliationCompletedAt = utcNow;
        state.LastReconciliationOwner = owner.Length > 120 ? owner[..120] : owner;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Approval reconciliation for organization {OrganizationId} scanned {Scanned} cases: " +
            "{Reassigned} reassigned, {Unassigned} unassigned, {Unchanged} unchanged.",
            organizationId,
            scanned,
            reassigned,
            unassigned,
            unchanged);

        ApprovalTelemetry.RecordReconciliation(reassigned, unassigned);

        return new ApprovalReconciliationOutcome(
            organizationId, scanned, reassigned, unassigned, unchanged, utcNow);
    }
}
