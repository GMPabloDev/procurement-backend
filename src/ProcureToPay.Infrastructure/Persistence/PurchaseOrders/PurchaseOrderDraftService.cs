using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>One line of a draft update: the assignments the Buyer selected for it.</summary>
public sealed record PurchaseOrderLineAssignments(
    Guid LineId,
    int LineVersion,
    IReadOnlyList<AcceptanceAssignmentRequest> Assignments);

/// <summary>
/// Command of one draft successor (<c>update-purchase-order-draft</c>, REQ-02, REQ-06). The Buyer may
/// only change the delivery commitment, the Acceptance Responsibilities and the non commercial
/// operational references: quantity, unit, price, gross totals, supplier, currencies and terms stay
/// byte-equivalent to the award.
/// </summary>
public sealed record UpdatePurchaseOrderDraftCommand(
    Guid OrganizationId,
    Guid PoId,
    int ExpectedPoVersion,
    DeliveryCommitment? Delivery,
    IReadOnlyList<PurchaseOrderLineAssignments> Assignments,
    string DraftKey,
    string Reason);

/// <summary>
/// Draft lifecycle of a Purchase Order (REQ-02): the claim creates an incomplete <c>DRAFT</c> and the
/// Buyer completes it through append-only successors before presenting it. No version is ever
/// updated in place.
/// </summary>
public sealed class PurchaseOrderDraftService(
    ProcureToPayDbContext dbContext,
    PurchaseRequestLineTakeoverService takeovers)
{
    public const string CommandType = "UPDATE_DRAFT";

    /// <summary>Appends one <c>DRAFT</c> successor with the requested delivery and assignments.</summary>
    public async Task<PurchaseOrderVersion> UpdateDraftAsync(
        UpdatePurchaseOrderDraftCommand command,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.DraftKey, "draft_key");
        var reason = PurchaseOrderCodes.Reason(command.Reason);
        var fingerprint = DraftFingerprint(command);
        var replay = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.CommandType == CommandType &&
                          record.CommandKey == command.DraftKey,
                cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The draft key was reused with a different preimage.");
            }

            var replayedVersion = int.Parse(replay.ResultRef!, System.Globalization.CultureInfo.InvariantCulture);
            var replayed = await ReadVersionAsync(command.OrganizationId, command.PoId, replayedVersion, cancellationToken);
            return PurchaseOrderSerialization.ReadDocument(replayed.DocumentJson, replayed.ContentDigest);
        }

        var order = await dbContext.PurchaseOrders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.PoId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase order is not visible.");
        if (order.CurrentVersion != command.ExpectedPoVersion)
        {
            throw new DomainConflictException("The purchase order changed under the command; reload and retry.");
        }

        var currentRecord = await ReadVersionAsync(
            command.OrganizationId, command.PoId, order.CurrentVersion, cancellationToken);
        if ((PurchaseOrderState)currentRecord.State != PurchaseOrderState.Draft)
        {
            throw new DomainConflictException("Only a draft purchase order can be completed or cancelled.");
        }

        var current = PurchaseOrderSerialization.ReadDocument(currentRecord.DocumentJson, currentRecord.ContentDigest);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(currentRecord.LineParametersJson);
        var lines = new List<PurchaseOrderLine>(current.Lines.Count);
        foreach (var line in current.Lines)
        {
            var parameter = parameters.SingleOrDefault(entry =>
                string.Equals(entry.LineId, line.RequestLineRef.Id.ToString("D"), StringComparison.Ordinal))
                ?? throw new PurchaseOrderDependencyUnavailableException(
                    "The attested purchase type of a line is missing.");
            var requested = command.Assignments?.SingleOrDefault(assignment =>
                assignment.LineId == line.RequestLineRef.Id &&
                assignment.LineVersion == line.RequestLineRef.Version);
            // An omitted line keeps the assignments it already had.
            if (requested is null)
            {
                lines.Add(line);
                continue;
            }

            await RequireActiveUsersAsync(
                command.OrganizationId,
                requested.Assignments.Select(assignment => (assignment.UserId, assignment.UserVersion))
                    .Append((parameter.CandidateUserId, parameter.CandidateVersion)),
                cancellationToken);
            var candidate = new PurchaseOrderEntityRef(parameter.CandidateUserId, parameter.CandidateVersion);
            lines.Add(line.WithResponsibilities(AcceptanceResponsibilityBuilder.Build(
                line.RequestLineRef,
                parameter.PurchaseType,
                candidate,
                requested.Assignments,
                actorUserId,
                occurredAt.ToUniversalTime(),
                line.AcceptanceResponsibilities.Count == 0
                    ? 1
                    : line.AcceptanceResponsibilities.Max(responsibility => responsibility.Version) + 1)));
        }

        var successor = current.With(
            PurchaseOrderState.Draft,
            lines,
            command.Delivery ?? current.Delivery,
            budgetOperationRefs: null,
            approvalRef: null,
            orderingEvidenceRef: null,
            amendmentRef: null,
            actorUserId,
            occurredAt.ToUniversalTime(),
            issuedAt: null);
        await PersistVersionAsync(
            successor, lines, occurredAt.ToUniversalTime(), reason, cancellationToken);
        await AdvancePointerAsync(
            command.OrganizationId, command.PoId, order.CurrentVersion, successor, lines, cancellationToken);
        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.DraftKey,
            CommandType = CommandType,
            Fingerprint = fingerprint,
            ResultRef = successor.PurchaseOrderVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ActorUserId = actorUserId,
            OccurredAt = occurredAt.ToUniversalTime()
        });
        AddAudit(
            command.OrganizationId,
            actorUserId,
            PurchaseOrderCodes.ActionDraftUpdated,
            PurchaseOrderCodes.TargetPurchaseOrder,
            command.PoId,
            successor.PurchaseOrderVersionNumber,
            ["delivery", "acceptance_responsibilities"],
            command.DraftKey,
            occurredAt.ToUniversalTime());
        await dbContext.SaveChangesAsync(cancellationToken);
        _ = takeovers;
        return successor;
    }

    /// <summary>
    /// REQ-06: every selected principal must be an active user of the same organization, and a stale
    /// or foreign reference fails before any successor exists.
    /// </summary>
    public async Task RequireActiveUsersAsync(
        Guid organizationId,
        IEnumerable<(Guid UserId, int Version)> users,
        CancellationToken cancellationToken = default)
    {
        foreach (var (userId, version) in users.Distinct())
        {
            var active = await dbContext.UserProfiles
                .AsNoTracking()
                .AnyAsync(
                    record => record.Id == userId &&
                              record.OrganizationId == organizationId &&
                              record.Status == (int)UserProfileStatus.Active &&
                              record.Version == version,
                    cancellationToken);
            if (!active)
            {
                throw new PurchaseOrderUnprocessableException(
                    "An acceptance responsibility references an inactive, stale or foreign user.");
            }
        }
    }

    /// <summary>Reads one stored version of the order, rehashing its document (NFR-01).</summary>
    public async Task<PurchaseOrderVersionRecord> ReadVersionAsync(
        Guid organizationId,
        Guid poId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.PoId == poId &&
                             candidate.Version == version &&
                             candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase order version is not visible.");
        return record;
    }

        /// <summary>
        /// Fingerprint of one draft successor: the expected version, the delivery commitment, the exact
        /// assignment set and the key. It is an internal idempotency preimage, not a published digest.
        /// </summary>
        public static string DraftFingerprint(UpdatePurchaseOrderDraftCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["assignments"] = PurchaseOrderCanonicalizer.Set((command.Assignments ?? [])
                    .OrderBy(assignment => assignment.LineId)
                    .ThenBy(assignment => assignment.LineVersion)
                    .Select(assignment => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["assignments"] = PurchaseOrderCanonicalizer.Set(
                            (assignment.Assignments ?? []).OrderBy(entry => entry.Kind, StringComparer.Ordinal)
                            .Select(entry => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["kind"] = entry.Kind,
                                ["reason"] = entry.Reason,
                                ["user_id"] = entry.UserId.ToString("D"),
                                ["user_version"] = entry.UserVersion
                            })),
                        ["line_id"] = assignment.LineId.ToString("D"),
                        ["line_version"] = assignment.LineVersion
                    })),
                ["canonicalization_version"] = PolicyCanonicalizer.Version,
                ["contract_version"] = "update-purchase-order-draft/v1",
                ["delivery"] = command.Delivery is null
                    ? null
                    : PurchaseOrderCanonicalizer.DeliveryPreimage(command.Delivery),
                ["draft_key"] = PurchaseOrderCodes.Key(command.DraftKey, "draft_key"),
                ["expected_po_version"] = command.ExpectedPoVersion,
                ["organization_id"] = command.OrganizationId.ToString("D"),
                ["po_id"] = command.PoId.ToString("D"),
                ["reason"] = PurchaseOrderCodes.Reason(command.Reason)
            };
            return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
        }

    /// <summary>Rehydrates one stored version, refusing a document that no longer matches its digest.</summary>
    public static PurchaseOrderVersion ReadDocument(PurchaseOrderVersionRecord record) =>
        PurchaseOrderSerialization.ReadDocument(record.DocumentJson, record.ContentDigest);

    private async Task PersistVersionAsync(
        PurchaseOrderVersion version,
        IReadOnlyList<PurchaseOrderLine> lines,
        DateTimeOffset occurredAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var parameters = await LineParametersAsync(version, cancellationToken);
        dbContext.PurchaseOrderVersions.Add(PurchaseOrderVersionWriter.Record(version, lines, parameters, occurredAt, reason));
    }

    private async Task<IReadOnlyList<PurchaseOrderSerialization.LineParameter>> LineParametersAsync(
        PurchaseOrderVersion version,
        CancellationToken cancellationToken)
    {
        var parameters = new List<PurchaseOrderSerialization.LineParameter>();
        foreach (var line in version.Lines)
        {
            var row = await dbContext.PurchaseRequestLineVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.LineId == line.RequestLineRef.Id &&
                              record.LineVersion == line.RequestLineRef.Version,
                    cancellationToken)
                ?? throw new PurchaseOrderDependencyUnavailableException(
                    "A covered purchase request line is not visible.");
            parameters.Add(new PurchaseOrderSerialization.LineParameter(
                line.RequestLineRef.Id.ToString("D"),
                row.PurchaseType,
                ReadRequestedFor(row.RequestedForUserJson),
                1));
        }

        return parameters;
    }

    private async Task AdvancePointerAsync(
        Guid organizationId,
        Guid poId,
        int previousVersion,
        PurchaseOrderVersion successor,
        IReadOnlyList<PurchaseOrderLine> lines,
        CancellationToken cancellationToken)
    {
        var applied = await dbContext.PurchaseOrders
            .Where(record => record.Id == poId && record.CurrentVersion == previousVersion)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(record => record.CurrentVersion, successor.PurchaseOrderVersionNumber),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The purchase order changed concurrently.");
        }

        foreach (var line in lines)
        {
            var pointer = await dbContext.PurchaseOrderLinePointers
                .SingleOrDefaultAsync(
                    record => record.RequestLineId == line.RequestLineRef.Id &&
                              record.RequestLineVersion == line.RequestLineRef.Version &&
                              record.PoId == poId,
                    cancellationToken);
            if (pointer is not null)
            {
                pointer.PoVersion = successor.PurchaseOrderVersionNumber;
                pointer.State = (int)successor.State;
            }
        }

        _ = organizationId;
    }

    private static Guid ReadRequestedFor(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private void AddAudit(
        Guid organizationId,
        Guid actorUserId,
        string action,
        string targetType,
        Guid targetId,
        int targetVersion,
        string[] changedFields,
        string correlation,
        DateTimeOffset occurredAt) =>
        dbContext.PurchaseOrderAuditRecords.Add(new PurchaseOrderAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetVersion = targetVersion,
            ChangedFieldsJson = System.Text.Json.JsonSerializer.Serialize(changedFields),
            CorrelationReference = correlation.Length <= 256 ? correlation : correlation[..256],
            OccurredAt = occurredAt
        });
}

/// <summary>Single writer of one <c>PurchaseOrderVersionRecord</c> row from a published version.</summary>
public static class PurchaseOrderVersionWriter
{
    public static PurchaseOrderVersionRecord Record(
        PurchaseOrderVersion version,
        IReadOnlyList<PurchaseOrderLine> lines,
        IReadOnlyList<PurchaseOrderSerialization.LineParameter> parameters,
        DateTimeOffset occurredAt,
        string? reason) =>
        new()
        {
            PoId = version.PoId,
            Version = version.PurchaseOrderVersionNumber,
            PredecessorVersion = version.PredecessorVersion,
            OrganizationId = version.OrganizationId,
            PoNumber = version.PoNumber,
            State = (int)version.State,
            RequestId = version.RequestRef.Id,
            RequestVersion = version.RequestRef.Version,
            RequestContentDigest = version.RequestRef.ContentDigest,
            ProposalId = version.ProposalRef.Id,
            ProposalVersion = version.ProposalRef.Version,
            ProposalContentDigest = version.ProposalRef.ContentDigest,
            AwardId = version.AwardRef.Id,
            AwardVersion = version.AwardRef.Version,
            AwardContentDigest = version.AwardRef.ContentDigest,
            ClaimId = version.AwardClaimRef.Id,
            SupplierId = version.SupplierRef.Id,
            SupplierVersion = version.SupplierRef.Version,
            LegalEntityId = version.LegalEntityRef.Id,
            LegalEntityVersion = version.LegalEntityRef.Version,
            SourceAmount = version.SourceAmount,
            SourceCurrency = version.SourceCurrency,
            BaseAmount = version.BaseAmount,
            BaseCurrency = version.BaseCurrency,
            DeliveryJson = version.Delivery is null ? null : PurchaseOrderSerialization.Delivery(version.Delivery),
            TermsJson = PurchaseOrderSerialization.Terms(version.TermsSnapshot),
            LinesJson = PurchaseOrderSerialization.Lines(lines),
            BudgetOperationRefsJson = PurchaseOrderSerialization.BudgetOperationRefs(version.BudgetOperationRefs),
            LineParametersJson = PurchaseOrderSerialization.LineParameters(parameters),
            ApprovalCaseId = version.ApprovalRef?.Id,
            ApprovalCaseVersion = version.ApprovalRef?.Version,
            ApprovalDigest = version.ApprovalRef?.ContentDigest,
            OrderingEvidenceDigest = version.OrderingEvidenceRef?.ContentDigest,
            AmendmentId = version.AmendmentRef?.Id,
            AmendmentVersion = version.AmendmentRef?.Version,
            IssuedAt = version.IssuedAt,
            DocumentJson = version.CanonicalDocument(),
            ContentDigest = version.Digest,
            ActorUserId = version.ActorUserId,
            OccurredAt = occurredAt,
            Reason = reason
        };
}
