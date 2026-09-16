using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// Idempotent consumer of <c>approval-result/v3</c> and <c>approval-case-lifecycle/v1</c> for the
/// Supplier Master and the Approved Supplier Catalog (SPEC 09 REQ-03, REQ-07). It verifies
/// organization, case, subject, target and material digest against the persisted proposal and
/// deduplicates by event and target, so a redelivered or forged event can never move a pointer.
/// </summary>
public sealed class SupplierApprovalResultConsumer(
    ProcureToPayDbContext dbContext,
    SupplierGovernanceService supplierGovernance,
    ApprovedSupplierCatalogGovernanceService catalogGovernance,
    ILogger<SupplierApprovalResultConsumer> logger,
    string contractVersion) : IApprovalResultConsumer
{
    public string ContractVersion { get; } = contractVersion;

    public async Task DeliverAsync(
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!string.Equals(delivery.ContractVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new DomainValidationException("The delivered result does not match the consumer contract.");
        }

        var materialized = SupplierResultEvent.Parse(delivery);
        if (materialized is null)
        {
            // The inbox is shared by every subject: an event of another domain is not ours.
            return;
        }

        var change = await VerifyAsync(materialized, cancellationToken);
        var delivered = await dbContext.SupplierApprovalResults
            .AsNoTracking()
            .AnyAsync(
                row => row.EventId == materialized.EventId &&
                       row.ContractVersion == materialized.ContractVersion &&
                       row.TargetId == materialized.TargetId &&
                       row.TargetVersion == materialized.TargetVersion,
                cancellationToken);
        if (delivered)
        {
            logger.LogInformation(
                "Supplier approval event {EventId} was already projected.", materialized.EventId);
            return;
        }

        var effectKey = $"approval:{materialized.EventId:D}:{materialized.TargetId:D}:{materialized.TargetVersion}";
        var cause = Cause(materialized);
        if (change.IsCatalog)
        {
            await catalogGovernance.ApplyApprovalAsync(
                materialized.OrganizationId,
                materialized.SubjectId,
                materialized.SubjectVersion,
                change.ProposalId,
                materialized.Result,
                materialized.DecisionActorUserId,
                cause,
                effectKey,
                materialized.OccurredAt,
                cancellationToken);
        }
        else
        {
            await supplierGovernance.ApplyApprovalAsync(
                materialized.OrganizationId,
                materialized.SubjectId,
                materialized.SubjectVersion,
                change.ProposalId,
                materialized.Result,
                materialized.DecisionActorUserId,
                change.RequirementKey,
                cause,
                effectKey,
                materialized.OccurredAt,
                cancellationToken);
        }

        dbContext.SupplierApprovalResults.Add(new SupplierApprovalResultRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = materialized.OrganizationId,
            EventId = materialized.EventId,
            ContractVersion = materialized.ContractVersion,
            CaseId = materialized.CaseId,
            TargetId = materialized.TargetId,
            TargetVersion = materialized.TargetVersion,
            Result = materialized.Result,
            ProposalId = change.ProposalId,
            OccurredAt = materialized.OccurredAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the proposal of one result and recomputes its material digest, so the decision only
    /// applies to the exact candidate the approver saw.
    /// </summary>
    private async Task<ProposalBinding> VerifyAsync(
        SupplierResultEvent materialized,
        CancellationToken cancellationToken)
    {
        if (materialized.EventId == Guid.Empty || materialized.OrganizationId == Guid.Empty ||
            materialized.SubjectId == Guid.Empty || materialized.SubjectVersion < 1 ||
            materialized.TargetId == Guid.Empty || materialized.TargetVersion < 1)
        {
            throw new DomainValidationException("The approval result is missing its immutable identities.");
        }

        if (materialized.TargetId != materialized.SubjectId ||
            materialized.TargetVersion != materialized.SubjectVersion)
        {
            throw new DomainValidationException(
                "The approval result target does not identify the submitted candidate.");
        }

        var approvalCase = await dbContext.ApprovalCases
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == materialized.CaseId, cancellationToken)
            ?? throw new SupplierDependencyUnavailableException("The approval case of the result does not exist.");
        if (approvalCase.OrganizationId != materialized.OrganizationId ||
            approvalCase.SubjectId != materialized.SubjectId ||
            !string.Equals(approvalCase.SubjectType, materialized.SubjectType, StringComparison.Ordinal))
        {
            throw new SupplierDependencyUnavailableException(
                "The approval case of the result does not match the supplier change.");
        }

        if (materialized.IsLifecycle && materialized.PreviousCaseId is null)
        {
            throw new DomainValidationException("A lifecycle event requires its previous case.");
        }

        var proposal = await dbContext.SupplierChangeProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.OrganizationId == materialized.OrganizationId &&
                       row.SupplierId == materialized.SubjectId &&
                       row.CandidateVersion == materialized.SubjectVersion &&
                       row.ApprovalCaseId == materialized.CaseId,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException(
                "The approval result does not match any persisted supplier proposal.");
        var expected = await SupplierGovernanceApprovalAdapter.ComputeMaterialDigestAsync(
            dbContext, proposal, materialized.TargetType, cancellationToken);
        if (!string.Equals(expected, materialized.MaterialSnapshotDigest, StringComparison.Ordinal))
        {
            throw new SupplierDependencyUnavailableException(
                "The material snapshot of the approval result is not reproducible.");
        }

        return new ProposalBinding(
            proposal.Id,
            proposal.RequirementKey,
            string.Equals(
                materialized.TargetType, SupplierCodes.CatalogApprovalTargetType, StringComparison.Ordinal));
    }

    private static string Cause(SupplierResultEvent materialized) =>
        $"{{\"contract_version\":\"{materialized.ContractVersion}\",\"event_id\":\"{materialized.EventId:D}\"," +
        $"\"source\":\"APPROVAL\"}}";

    private sealed record ProposalBinding(Guid ProposalId, string? RequirementKey, bool IsCatalog);

    /// <summary>Exact payload of one delivered supplier result or lifecycle event (REQ-03).</summary>
    private sealed record SupplierResultEvent(
        Guid EventId,
        string ContractVersion,
        Guid CaseId,
        Guid OrganizationId,
        string SubjectType,
        Guid SubjectId,
        int SubjectVersion,
        string TargetType,
        Guid TargetId,
        int TargetVersion,
        string MaterialSnapshotDigest,
        string Result,
        Guid? DecisionActorUserId,
        Guid? PreviousCaseId,
        DateTimeOffset OccurredAt)
    {
        public bool IsLifecycle =>
            string.Equals(
                ContractVersion, ApprovalEvolutionCodes.LifecycleContractVersion, StringComparison.Ordinal);

        /// <summary>Parses one delivery; returns null when the subject belongs to another domain.</summary>
        public static SupplierResultEvent? Parse(ApprovalResultDelivery delivery)
        {
            using var document = JsonDocument.Parse(delivery.PayloadJson);
            var root = document.RootElement;
            var contract = root.GetProperty("contract_version").GetString() ?? string.Empty;
            if (!string.Equals(contract, delivery.ContractVersion, StringComparison.Ordinal))
            {
                throw new DomainValidationException("The payload contract version does not match its delivery.");
            }

            var subjectType = root.GetProperty("subject_type").GetString() ?? string.Empty;
            if (subjectType is not (SupplierCodes.ApprovalSubjectType or SupplierCodes.CatalogApprovalSubjectType))
            {
                return null;
            }

            var target = root.GetProperty("target");
            return new SupplierResultEvent(
                root.GetProperty("event_id").GetGuid(),
                contract,
                root.GetProperty("case_id").GetGuid(),
                root.GetProperty("organization_id").GetGuid(),
                subjectType,
                root.GetProperty("subject_id").GetGuid(),
                root.GetProperty("subject_version").GetInt32(),
                target.GetProperty("type").GetString() ?? string.Empty,
                target.GetProperty("id").GetGuid(),
                target.GetProperty("version").GetInt32(),
                target.GetProperty("material_snapshot_digest").GetString() ?? string.Empty,
                root.GetProperty("result").GetString() ?? string.Empty,
                ReadGuid(root, "decided_by_user_id"),
                ReadGuid(root, "previous_case_id"),
                DateTimeOffset.Parse(
                    root.GetProperty("occurred_at").GetString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        private static Guid? ReadGuid(JsonElement root, string property) =>
            root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            Guid.TryParse(element.GetString(), out var value) &&
            value != Guid.Empty
                ? value
                : null;
    }
}

/// <summary>
/// Single consumer of the shared result contracts (SPEC 03 REQ-10 requires exactly one per contract
/// version): it routes every delivered event to the domain that owns the subject.
/// </summary>
public sealed class ApprovalResultRouter(
    PurchaseRequests.PurchaseRequestApprovalResultConsumer purchaseRequests,
    SupplierApprovalResultConsumer suppliers,
    ILogger<ApprovalResultRouter> logger,
    string contractVersion) : IApprovalResultConsumer
{
    public string ContractVersion { get; } = contractVersion;

    public async Task DeliverAsync(
        ApprovalResultDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var subjectType = ReadSubjectType(delivery.PayloadJson);
        if (subjectType is SupplierCodes.ApprovalSubjectType or SupplierCodes.CatalogApprovalSubjectType)
        {
            await suppliers.DeliverAsync(delivery, cancellationToken);
            return;
        }

        await purchaseRequests.DeliverAsync(delivery, cancellationToken);
    }

    private static string ReadSubjectType(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("subject_type", out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            // An unreadable payload is handed to the Purchase Requests consumer, which owns the
            // strict parsing of the shared inbox contract.
            return string.Empty;
        }
    }
}
