using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Sourcing;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>Command that creates one amendment lineage of an issued Purchase Order (REQ-05).</summary>
public sealed record CreatePurchaseOrderAmendmentCommand(
    Guid OrganizationId,
    Guid PoId,
    int ExpectedPoVersion,
    string AmendmentKey,
    PurchaseOrderContentRef? SuccessorAwardRef,
    IReadOnlyList<AmendmentLineDelta> LineDeltas,
    IReadOnlyList<ResponsibilityChange> ResponsibilityChanges,
    DeliveryCommitment? ReplacementDelivery,
    string Reason,
    Guid ActorUserId,
    string CorrelationReference);

/// <summary>Command that presents an amendment for Procurement Approval (REQ-05).</summary>
public sealed record SubmitPurchaseOrderAmendmentCommand(
    Guid OrganizationId,
    Guid AmendmentId,
    int ExpectedAmendmentVersion,
    string SubmissionKey,
    Guid OriginatorId,
    string CorrelationReference);

/// <summary>Command that applies an approved (or approval-free) amendment (REQ-05).</summary>
public sealed record ApplyPurchaseOrderAmendmentCommand(
    Guid OrganizationId,
    Guid AmendmentId,
    int ExpectedAmendmentVersion,
    string ApplyKey,
    Guid ActorUserId,
    string CorrelationReference);

/// <summary>Command that cancels an amendment before it is applied (REQ-05).</summary>
public sealed record CancelPurchaseOrderAmendmentCommand(
    Guid OrganizationId,
    Guid AmendmentId,
    int ExpectedAmendmentVersion,
    string CancelKey,
    string Reason,
    Guid ActorUserId);

/// <summary>
/// Append-only amendment lifecycle of an issued Purchase Order (SPEC 11 REQ-05, DEC-04). The order is
/// never rewritten: every commercial or operational change appends an amendment version, a reduction
/// releases only the still-open committed remainder through <c>REVERSE</c>, an increase consumes the
/// additional reservations of a successor award, and the application produces exactly one successor
/// Purchase Order version.
/// </summary>
public sealed class PurchaseOrderAmendmentService(
    ProcureToPayDbContext dbContext,
    PurchaseOrderBudgetProducer budget,
    PurchaseRequestLineTakeoverService takeovers,
    ApprovalSubmissionService submissions)
{
    public const string CreateCommandType = "CREATE_AMENDMENT";
    public const string SubmitCommandType = "SUBMIT_AMENDMENT";
    public const string ApplyCommandType = "APPLY_AMENDMENT";
    public const string CancelCommandType = "CANCEL_AMENDMENT";

    /// <summary>Creates the first <c>DRAFT</c> version of one amendment lineage (REQ-05).</summary>
    public async Task<PurchaseOrderAmendmentVersion> CreateAsync(
        CreatePurchaseOrderAmendmentCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.AmendmentKey, "amendment_key");
        var reason = PurchaseOrderCodes.Reason(command.Reason);
        var occurred = occurredAt.ToUniversalTime();
        var fingerprint = AmendmentFingerprint(command);
        var replay = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.CommandType == CreateCommandType &&
                          record.CommandKey == command.AmendmentKey,
                cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The amendment key was reused with a different preimage.");
            }

            var replayParts = replay.ResultRef!.Split(':');
            return await ReadAmendmentAsync(
                command.OrganizationId, Guid.Parse(replayParts[0]), int.Parse(replayParts[1]), cancellationToken);
        }

        var order = await RequireIssuedOrderAsync(
            command.OrganizationId, command.PoId, command.ExpectedPoVersion, cancellationToken);
        var baseVersion = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.PoId == command.PoId && record.Version == command.ExpectedPoVersion,
                cancellationToken);
        var currentLines = PurchaseOrderSerialization.ReadLines(baseVersion.LinesJson);
        ValidateDeltas(command, currentLines);
        if (command.SuccessorAwardRef is not null)
        {
            await ValidateSuccessorAwardAsync(command, baseVersion, currentLines, cancellationToken);
        }

        await RequireNoLiveAmendmentAsync(command.OrganizationId, command.PoId, cancellationToken);
        // One Purchase Order owns one amendment lineage (unique root per PO): a later amendment
        // appends a new version of the same lineage instead of opening a parallel one.
        var root = await dbContext.PurchaseOrderAmendmentRoots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId && record.PoId == command.PoId,
                cancellationToken);
        if (root is not null)
        {
            var current = await ReadAmendmentAsync(
                command.OrganizationId, root.Id, root.CurrentVersion, cancellationToken);
            if (current.State is not (AmendmentState.Applied or AmendmentState.Cancelled))
            {
                throw new DomainConflictException("The purchase order already carries a live amendment.");
            }
        }

        var amendmentId = root?.Id ?? Guid.NewGuid();
        var version = (root?.CurrentVersion ?? 0) + 1;
        var descriptor = new PurchaseOrderAmendmentVersion(
            amendmentId,
            version,
            version == 1 ? null : version - 1,
            new PurchaseOrderContentRef(
                command.PoId, command.ExpectedPoVersion, baseVersion.ContentDigest),
            command.ExpectedPoVersion,
            AmendmentState.Draft,
            command.LineDeltas ?? [],
            command.ResponsibilityChanges ?? [],
            command.ReplacementDelivery,
            command.SuccessorAwardRef,
            evidenceRefs: null,
            approvalRef: null,
            command.OrganizationId,
            reason,
            command.ActorUserId,
            occurred);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            if (root is null)
            {
                dbContext.PurchaseOrderAmendmentRoots.Add(new PurchaseOrderAmendmentRootRecord
                {
                    Id = amendmentId,
                    OrganizationId = command.OrganizationId,
                    PoId = command.PoId,
                    CurrentVersion = version
                });
            }
            else
            {
                await AdvanceAmendmentAsync(
                    command.OrganizationId, amendmentId, root.CurrentVersion, descriptor, cancellationToken);
            }

            dbContext.PurchaseOrderAmendments.Add(
                PurchaseOrderAmendmentWriter.Record(descriptor, command.AmendmentKey, occurred));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.AmendmentKey,
            CommandType = CreateCommandType,
            Fingerprint = fingerprint,
            ResultRef = $"{amendmentId:D}:{version}",
            ActorUserId = command.ActorUserId,
            OccurredAt = occurred
        });
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionAmendmentCreated,
            PurchaseOrderCodes.TargetAmendment,
            amendmentId,
            version,
            ["line_deltas", "responsibility_changes", "replacement_delivery"],
            command.AmendmentKey,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        _ = order;
        return descriptor;
    }

    /// <summary>
    /// Presents an amendment with line deltas: <c>DRAFT→PENDING_APPROVAL</c> plus the exact-one
    /// Procurement requirement of the adapter (REQ-03, REQ-05).
    /// </summary>
    public async Task<PurchaseOrderAmendmentVersion> SubmitAsync(
        SubmitPurchaseOrderAmendmentCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.SubmissionKey, "submission_key");
        var occurred = occurredAt.ToUniversalTime();
        var current = await ReadAmendmentAsync(
            command.OrganizationId, command.AmendmentId, command.ExpectedAmendmentVersion, cancellationToken);
        if (current.State == AmendmentState.PendingApproval &&
            (await AmendmentRecordAsync(command.OrganizationId, command.AmendmentId, current.Version,
                cancellationToken)).ApprovalCaseId is not null)
        {
            return current;
        }

        if (current.State != AmendmentState.Draft)
        {
            throw new DomainConflictException("Only a draft amendment can be presented.");
        }

        if (current.LineDeltas.Count == 0)
        {
            throw new PurchaseOrderUnprocessableException(
                "An amendment without commercial line deltas does not require Procurement Approval.");
        }

        var presented = current.With(AmendmentState.PendingApproval, approvalRef: null, command.OriginatorId, occurred);
        var record = PurchaseOrderAmendmentWriter.Record(presented, command.SubmissionKey, occurred);
        // The presented version and its pointer advance commit together, so the adapter can only see
        // a pending amendment that is really current (REQ-05).
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            dbContext.PurchaseOrderAmendments.Add(record);
            await AdvanceAmendmentAsync(
                command.OrganizationId, command.AmendmentId, current.Version, presented, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var submission = await submissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                PurchaseOrderIssuanceService.Workload,
                command.OrganizationId,
                PurchaseOrderCodes.AmendmentSubjectType,
                command.AmendmentId,
                presented.Version,
                PurchaseOrderCodes.ApplyAmendmentOperation,
                PurchaseOrderCodes.ApprovalAdapterVersion,
                command.SubmissionKey,
                command.OriginatorId,
                command.OriginatorId,
                command.CorrelationReference),
            occurred,
            cancellationToken);
        // The case binding is an INSERT, never an UPDATE: the append-only trigger of the table
        // admits no rewrite, so the binding successor carries the case reference from the start.
        var binding = presented.With(
            AmendmentState.PendingApproval, approvalRef: null, command.OriginatorId, occurred);
        var bindingRecord = PurchaseOrderAmendmentWriter.Record(binding, binding.Digest, occurred);
        bindingRecord.ApprovalCaseId = submission.CaseId;
        bindingRecord.ApprovalCaseVersion = submission.Version;
        dbContext.PurchaseOrderAmendments.Add(bindingRecord);
        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.SubmissionKey,
            CommandType = SubmitCommandType,
            Fingerprint = presented.Digest,
            ResultRef = $"{command.AmendmentId:D}:{binding.Version}",
            ActorUserId = command.OriginatorId,
            OccurredAt = occurred
        });
        AddAudit(
            command.OrganizationId,
            command.OriginatorId,
            PurchaseOrderCodes.ActionAmendmentSubmitted,
            PurchaseOrderCodes.TargetAmendment,
            command.AmendmentId,
            binding.Version,
            ["state", "approval_ref"],
            command.SubmissionKey,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AdvanceAmendmentAsync(
            command.OrganizationId, command.AmendmentId, presented.Version, binding, cancellationToken);
        return binding;
    }

    /// <summary>
    /// Applies one amendment: the budget delta is confirmed first, and only then the successor
    /// Purchase Order version, the applied amendment version and the projection are written in one
    /// local transaction (REQ-05, NFR-05). An amendment without line deltas may be applied straight
    /// from <c>DRAFT</c> because REQ-05 does not require Procurement Approval for it.
    /// </summary>
    public async Task<PurchaseOrderAmendmentVersion> ApplyAsync(
        ApplyPurchaseOrderAmendmentCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.ApplyKey, "apply_key");
        var occurred = occurredAt.ToUniversalTime();
        var replay = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.CommandType == ApplyCommandType &&
                          record.CommandKey == command.ApplyKey,
                cancellationToken);
        if (replay is not null)
        {
            var parts = replay.ResultRef!.Split(':');
            return await ReadAmendmentAsync(
                command.OrganizationId, Guid.Parse(parts[0]), int.Parse(parts[1]), cancellationToken);
        }

        var current = await ReadAmendmentAsync(
            command.OrganizationId, command.AmendmentId, command.ExpectedAmendmentVersion, cancellationToken);
        var requiresApproval = current.LineDeltas.Count != 0;
        if (current.State == AmendmentState.Applied)
        {
            return current;
        }

        if (requiresApproval && current.State != AmendmentState.Approved)
        {
            throw new DomainConflictException("Only an approved amendment can be applied.");
        }

        if (!requiresApproval && current.State != AmendmentState.Draft)
        {
            throw new DomainConflictException("Only a draft amendment can be applied without approval.");
        }

        var amendment = current;

        var order = await RequireIssuedOrderAsync(
            command.OrganizationId, amendment.BasePoRef.Id, amendment.ExpectedPoVersion, cancellationToken);
        var baseVersion = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.PoId == order.Id && record.Version == amendment.ExpectedPoVersion,
                cancellationToken);
        var baseOrder = PurchaseOrderSerialization.ReadDocument(baseVersion.DocumentJson, baseVersion.ContentDigest);
        var parameters = PurchaseOrderSerialization.ReadLineParameters(baseVersion.LineParametersJson);
        var previousLines = baseOrder.Lines.ToDictionary(line => line.CanonicalIdentity, StringComparer.Ordinal);
        var successorLines = MaterializeLines(amendment, baseOrder.Lines);
        foreach (var line in successorLines)
        {
            var parameter = parameters.Single(entry =>
                string.Equals(entry.LineId, line.RequestLineRef.Id.ToString("D"), StringComparison.Ordinal));
            AcceptanceResponsibilityBuilder.RequireComplete(
                line, parameter.PurchaseType, line.RequestLineRef.CanonicalIdentity);
        }

        var (sourceDelta, baseDelta) = amendment.ComputeDeltas(previousLines);
        _ = sourceDelta;
        var budgetRefs = baseOrder.BudgetOperationRefs.ToList();
        if (baseDelta > 0m)
        {
            var deltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var line in successorLines)
            {
                var previous = previousLines[line.CanonicalIdentity];
                var delta = line.BaseGrossTotal - previous.BaseGrossTotal;
                if (delta > 0m)
                {
                    deltas[line.CanonicalIdentity] = delta;
                }
            }

            var increase = await budget.CommitAmendmentIncreaseAsync(
                command.OrganizationId,
                order.Id,
                amendment.ExpectedPoVersion,
                baseVersion.ContentDigest,
                command.AmendmentId,
                deltas,
                baseOrder,
                command.CorrelationReference,
                occurred,
                cancellationToken);
            budgetRefs.AddRange(increase.OperationRefs);
        }
        else if (baseDelta < 0m)
        {
            var reductions = new List<(Guid TargetId, int TargetVersion, decimal Amount)>();
            foreach (var line in baseOrder.Lines)
            {
                var successorLine = successorLines.FirstOrDefault(candidate =>
                    string.Equals(candidate.CanonicalIdentity, line.CanonicalIdentity, StringComparison.Ordinal));
                var delta = (successorLine?.BaseGrossTotal ?? 0m) - line.BaseGrossTotal;
                if (delta < 0m)
                {
                    reductions.Add((line.RequestLineRef.Id, line.RequestLineRef.Version, -delta));
                }
            }

            var release = await budget.ReleaseAmendmentReductionAsync(
                command.OrganizationId,
                order.Id,
                amendment.ExpectedPoVersion,
                baseVersion.ContentDigest,
                command.AmendmentId,
                reductions,
                command.CorrelationReference,
                occurred,
                cancellationToken);
            budgetRefs.AddRange(release.OperationRefs);
        }

        var applying = amendment.With(AmendmentState.Applying, amendment.ApprovalRef, command.ActorUserId, occurred);
        await using var applyTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.PurchaseOrderAmendments.Add(PurchaseOrderAmendmentWriter.Record(applying, command.ApplyKey, occurred));
        var totalCancellation = successorLines.Count == 0;
        // A published version always carries its line set, so the cancelled successor keeps the last
        // commercial content and expresses the total cancellation through its state and the amendment.
        var publishedLines = totalCancellation ? baseOrder.Lines : successorLines;
        var successor = baseOrder.With(
            totalCancellation ? PurchaseOrderState.Cancelled : PurchaseOrderState.Issued,
            publishedLines,
            amendment.ReplacementDelivery ?? baseOrder.Delivery,
            budgetRefs,
            approvalRef: amendment.ApprovalRef,
            orderingEvidenceRef: baseOrder.OrderingEvidenceRef,
            amendmentRef: new PurchaseOrderContentRef(command.AmendmentId, amendment.Version, amendment.Digest),
            command.ActorUserId,
            occurred,
            totalCancellation ? null : occurred,
            totalCancellation ? baseOrder.TermsSnapshot : await SuccessorTermsAsync(amendment, baseOrder, cancellationToken));
        dbContext.PurchaseOrderVersions.Add(
            PurchaseOrderVersionWriter.Record(successor, publishedLines, parameters, occurred, amendment.Reason));
        await AdvanceOrderAsync(command.OrganizationId, order.Id, amendment.ExpectedPoVersion, successor,
            publishedLines, cancellationToken);
        if (totalCancellation)
        {
            var claim = await dbContext.AwardConsumptionClaims
                .SingleOrDefaultAsync(
                    record => record.PoId == order.Id &&
                              record.OrganizationId == command.OrganizationId &&
                              record.State == (int)AwardClaimState.Issued,
                    cancellationToken);
            if (claim is not null)
            {
                claim.State = (int)AwardClaimState.ConsumedCancelled;
                claim.ReleaseReason = PurchaseOrderCodes.Reason(amendment.Reason);
            }

            await takeovers.ReleaseAsync(
                command.OrganizationId,
                PurchaseOrderCodes.TakeoverConsumerPurchaseOrder,
                order.Id,
                TakeoverState.Released,
                "PO_CANCELLED",
                occurred,
                cancellationToken);
        }
        else
        {
            await ProjectAppliedLinesAsync(command.OrganizationId, baseOrder, successor, occurred, cancellationToken);
        }

        var applied = applying.With(AmendmentState.Applied, amendment.ApprovalRef, command.ActorUserId, occurred);
        // The applied row is addressed by its own content digest: the command key already identifies
        // the APPLYING row of the same application (the unique index admits one row per key).
        dbContext.PurchaseOrderAmendments.Add(PurchaseOrderAmendmentWriter.Record(applied, applied.Digest, occurred));
        // The pointer advance is a compare-and-swap, never a tracked mutation: the root created by
        // this same context could otherwise override the row with a stale rowversion.
        await AdvanceAmendmentAsync(
            command.OrganizationId, command.AmendmentId, amendment.Version, applied, cancellationToken);
        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.ApplyKey,
            CommandType = ApplyCommandType,
            Fingerprint = applied.Digest,
            ResultRef = $"{command.AmendmentId:D}:{applied.Version}",
            ActorUserId = command.ActorUserId,
            OccurredAt = occurred
        });
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionAmendmentApplied,
            PurchaseOrderCodes.TargetAmendment,
            command.AmendmentId,
            applied.Version,
            ["state", "po_version"],
            command.ApplyKey,
            occurred);
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionDraftUpdated,
            PurchaseOrderCodes.TargetPurchaseOrder,
            order.Id,
            successor.PurchaseOrderVersionNumber,
            ["lines", "delivery", "acceptance_responsibilities", "budget_operation_refs"],
            command.ApplyKey,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        await applyTransaction.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>Cancels an amendment that has not been applied: <c>…→CANCELLED</c> (REQ-05).</summary>
    public async Task<PurchaseOrderAmendmentVersion> CancelAsync(
        CancelPurchaseOrderAmendmentCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.CancelKey, "cancel_key");
        var occurred = occurredAt.ToUniversalTime();
        var amendment = await ReadAmendmentAsync(
            command.OrganizationId, command.AmendmentId, command.ExpectedAmendmentVersion, cancellationToken);
        if (amendment.State == AmendmentState.Cancelled)
        {
            return amendment;
        }

        if (amendment.State is AmendmentState.Applying or AmendmentState.Applied)
        {
            throw new DomainConflictException("An applied amendment cannot be cancelled.");
        }

        var cancelled = amendment.With(AmendmentState.Cancelled, amendment.ApprovalRef, command.ActorUserId, occurred);
        await using var cancelTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.PurchaseOrderAmendments.Add(PurchaseOrderAmendmentWriter.Record(cancelled, command.CancelKey, occurred));
        await AdvanceAmendmentAsync(
            command.OrganizationId, command.AmendmentId, amendment.Version, cancelled, cancellationToken);
        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.CancelKey,
            CommandType = CancelCommandType,
            Fingerprint = cancelled.Digest,
            ResultRef = $"{command.AmendmentId:D}:{cancelled.Version}",
            ActorUserId = command.ActorUserId,
            OccurredAt = occurred
        });
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionAmendmentCancelled,
            PurchaseOrderCodes.TargetAmendment,
            command.AmendmentId,
            cancelled.Version,
            ["state"],
            command.CancelKey,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cancelTransaction.CommitAsync(cancellationToken);
        return cancelled;
    }

    /// <summary>Reads one amendment version, rehashing its document (NFR-01).</summary>
    public async Task<PurchaseOrderAmendmentVersion> ReadAmendmentAsync(
        Guid organizationId,
        Guid amendmentId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await AmendmentRecordAsync(organizationId, amendmentId, version, cancellationToken);
        return PurchaseOrderSerialization.ReadAmendment(
            record.DocumentJson, record.OrganizationId, record.ActorUserId, record.OccurredAt, record.ContentDigest);
    }

    /// <summary>The version pointer of one amendment lineage, or null when the lineage does not exist.</summary>
    public async Task<int?> CurrentAmendmentVersionAsync(
        Guid organizationId,
        Guid amendmentId,
        CancellationToken cancellationToken = default) =>
        await dbContext.PurchaseOrderAmendmentRoots
            .AsNoTracking()
            .Where(record => record.Id == amendmentId && record.OrganizationId == organizationId)
            .Select(record => (int?)record.CurrentVersion)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<PurchaseOrderAmendmentRecord> AmendmentRecordAsync(
        Guid organizationId,
        Guid amendmentId,
        int version,
        CancellationToken cancellationToken) =>
        await dbContext.PurchaseOrderAmendments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == amendmentId &&
                          record.Version == version &&
                          record.OrganizationId == organizationId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The purchase order amendment is not visible.");

    private async Task<PurchaseOrderRecord> RequireIssuedOrderAsync(
        Guid organizationId,
        Guid poId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.PurchaseOrders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == poId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase order is not visible.");
        if (order.CurrentVersion != expectedVersion)
        {
            throw new DomainConflictException("The purchase order changed under the command; reload and retry.");
        }

        var version = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.PoId == poId && record.Version == expectedVersion,
                cancellationToken);
        if ((PurchaseOrderState)version.State != PurchaseOrderState.Issued)
        {
            throw new DomainConflictException(
                "Only an issued purchase order is amended; a draft or cancelled one is completed or reopened.");
        }

        return order;
    }

    /// <summary>
    /// REQ-05: an issued Purchase Order carries at most one live amendment lineage. A second one is
    /// refused so two competing successors can never both believe they are current.
    /// </summary>
    private async Task RequireNoLiveAmendmentAsync(
        Guid organizationId,
        Guid poId,
        CancellationToken cancellationToken)
    {
        var live = await dbContext.PurchaseOrderAmendmentRoots
            .AsNoTracking()
            .Join(
                dbContext.PurchaseOrderAmendments.AsNoTracking(),
                root => new { Id = root.Id, Version = root.CurrentVersion },
                amendment => new { Id = amendment.Id, Version = amendment.Version },
                (root, amendment) => new { root, amendment })
            .Where(entry => entry.root.OrganizationId == organizationId &&
                            entry.root.PoId == poId &&
                            (entry.amendment.State == (int)AmendmentState.Draft ||
                             entry.amendment.State == (int)AmendmentState.PendingApproval ||
                             entry.amendment.State == (int)AmendmentState.Approved ||
                             entry.amendment.State == (int)AmendmentState.Applying))
            .AnyAsync(cancellationToken);
        if (live)
        {
            throw new DomainConflictException("The purchase order already carries a live amendment.");
        }
    }

    /// <summary>
    /// Validates every delta against the exact issued line set: the previous document must be
    /// byte-equivalent to the stored line, a reduction can only shrink, a cancellation replaces with
    /// nothing, and the award/request identity, currencies and FX snapshot never change (REQ-05).
    /// </summary>
    private static void ValidateDeltas(
        CreatePurchaseOrderAmendmentCommand command,
        IReadOnlyList<PurchaseOrderLine> currentLines)
    {
        var byIdentity = currentLines.ToDictionary(line => line.CanonicalIdentity, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var delta in command.LineDeltas ?? [])
        {
            var identity = delta.LineRef.CanonicalIdentity;
            if (!seen.Add(identity))
            {
                throw new DomainConflictException("An amendment cannot carry two deltas of the same line.");
            }

            if (!byIdentity.TryGetValue(identity, out var stored))
            {
                throw new DomainConflictException("A delta references a line the order does not carry.");
            }

            if (!string.Equals(
                    PurchaseOrderCanonicalizer.PurchaseOrderLineDocument(delta.PreviousLine),
                    PurchaseOrderCanonicalizer.PurchaseOrderLineDocument(stored),
                    StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "The previous line of a delta is not the line the order currently carries.");
            }

            if (delta.ChangeKind == PurchaseOrderCodes.ChangeCancel)
            {
                continue;
            }

            var replacement = delta.ReplacementLine
                ?? throw new DomainValidationException("A reduced or commercial delta requires its replacement line.");
            if (!string.Equals(
                    replacement.RequestLineRef.CanonicalIdentity, stored.RequestLineRef.CanonicalIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(replacement.SourceCurrency, stored.SourceCurrency, StringComparison.Ordinal) ||
                !string.Equals(replacement.BaseCurrency, stored.BaseCurrency, StringComparison.Ordinal) ||
                (replacement.FxSnapshotRef is null) != (stored.FxSnapshotRef is null))
            {
                throw new DomainConflictException(
                    "An amendment never changes the request line, the currencies or the FX snapshot.");
            }

            if (delta.ChangeKind == PurchaseOrderCodes.ChangeReduce)
            {
                if (replacement.Quantity > stored.Quantity ||
                    replacement.UnitPrice > stored.UnitPrice ||
                    replacement.GrossTotal > stored.GrossTotal ||
                    replacement.BaseGrossTotal > stored.BaseGrossTotal)
                {
                    throw new PurchaseOrderUnprocessableException(
                        "A reduction cannot grow a quantity, a price or an amount.");
                }

                if (replacement.Quantity == stored.Quantity &&
                    replacement.UnitPrice == stored.UnitPrice &&
                    replacement.GrossTotal == stored.GrossTotal &&
                    replacement.BaseGrossTotal == stored.BaseGrossTotal)
                {
                    throw new PurchaseOrderUnprocessableException("A reduction must actually reduce the line.");
                }
            }
            else
            {
                var grew = replacement.Quantity > stored.Quantity ||
                           replacement.UnitPrice > stored.UnitPrice ||
                           replacement.BaseGrossTotal > stored.BaseGrossTotal;
                var changedTerms = replacement.UnitCode != stored.UnitCode ||
                                   replacement.AwardLineRef.CanonicalIdentity !=
                                   stored.AwardLineRef.CanonicalIdentity ||
                                   replacement.GrossTotal != stored.GrossTotal;
                if (!grew && !changedTerms && command.SuccessorAwardRef is null)
                {
                    throw new PurchaseOrderUnprocessableException(
                        "A commercial change requires a successor award.");
                }
            }
        }

        if (command.SuccessorAwardRef is not null &&
            !(command.LineDeltas ?? []).Any(delta => delta.ChangeKind == PurchaseOrderCodes.ChangeCommercial))
        {
            throw new PurchaseOrderUnprocessableException(
                "Only a commercial change carries a successor award reference.");
        }
    }

    /// <summary>
    /// REQ-05: a commercial increase requires a current successor award of SPEC 10 that covers the
    /// whole line set without changing supplier, currency or request identity, and whose amounts match
    /// the replacement documents exactly.
    /// </summary>
    private async Task ValidateSuccessorAwardAsync(
        CreatePurchaseOrderAmendmentCommand command,
        PurchaseOrderVersionRecord baseVersion,
        IReadOnlyList<PurchaseOrderLine> currentLines,
        CancellationToken cancellationToken)
    {
        var reference = command.SuccessorAwardRef!;
        var root = await dbContext.SourcingAwards
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == reference.Id && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException("The successor award is not visible.");
        if (root.CurrentVersion != reference.Version)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "Only the current version of a successor award can back an amendment.");
        }

        var row = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AwardId == reference.Id && record.Version == reference.Version,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException("The successor award version is not visible.");
        if (!string.Equals(row.ContentDigest, reference.ContentDigest, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The successor award digest does not match the published version.");
        }

        if (row.SupplierId != baseVersion.SupplierId || row.SupplierVersion != baseVersion.SupplierVersion)
        {
            throw new PurchaseOrderUnprocessableException(
                "An amendment never changes the supplier of the order.");
        }

        if (row.RequestId != baseVersion.RequestId || row.RequestVersion != baseVersion.RequestVersion)
        {
            throw new PurchaseOrderUnprocessableException(
                "A successor award of an amendment must belong to the same request version.");
        }

        var content = SourcingAwardDocument.Read(row.DocumentJson, row.ContentDigest);
        var awardLines = content.AwardCandidate.Lines.ToDictionary(
            line => $"{line.LineRef.Id:D}:{line.LineRef.Version}", line => line, StringComparer.Ordinal);
        if (!string.Equals(content.AwardCandidate.SourceCurrency, baseVersion.SourceCurrency, StringComparison.Ordinal) ||
            !string.Equals(content.AwardCandidate.BaseCurrency, baseVersion.BaseCurrency, StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "An amendment never changes the currency of the order.");
        }

        var replacements = (command.LineDeltas ?? [])
            .Where(delta => delta.ChangeKind == PurchaseOrderCodes.ChangeCommercial)
            .ToDictionary(delta => delta.LineRef.CanonicalIdentity, delta => delta.ReplacementLine!, StringComparer.Ordinal);
        foreach (var line in currentLines)
        {
            var replacement = replacements.TryGetValue(line.CanonicalIdentity, out var requested)
                ? requested
                : line;
            if (!awardLines.TryGetValue(replacement.RequestLineRef.CanonicalIdentity, out var awardLine))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The successor award does not cover exactly the lines of the order.");
            }

            if (awardLine.Quantity != replacement.Quantity ||
                awardLine.UnitPrice != replacement.UnitPrice ||
                awardLine.SourceGrossTotal != replacement.GrossTotal ||
                awardLine.BaseGrossTotal != replacement.BaseGrossTotal ||
                !string.Equals(awardLine.UnitCode, replacement.UnitCode, StringComparison.Ordinal))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The successor award does not restate the replacement line exactly.");
            }
        }
    }

    /// <summary>The successor line set of an amendment, with its responsibility changes applied.</summary>
    private static List<PurchaseOrderLine> MaterializeLines(
        PurchaseOrderAmendmentVersion amendment,
        IReadOnlyList<PurchaseOrderLine> baseLines)
    {
        var deltas = amendment.LineDeltas.ToDictionary(delta => delta.LineRef.CanonicalIdentity, StringComparer.Ordinal);
        var changes = amendment.ResponsibilityChanges
            .GroupBy(change => change.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var lines = new List<PurchaseOrderLine>(baseLines.Count);
        foreach (var line in baseLines)
        {
            var identity = line.CanonicalIdentity;
            var current = deltas.TryGetValue(identity, out var delta) ? delta.ReplacementLine : line;
            if (current is null)
            {
                continue;
            }

            if (changes.TryGetValue(identity, out var replacements))
            {
                var responsibilities = current.AcceptanceResponsibilities.ToList();
                foreach (var change in replacements)
                {
                    var index = responsibilities.FindIndex(candidate => candidate.Kind == change.Kind);
                    if (index < 0)
                    {
                        throw new DomainConflictException(
                            "A responsibility change references a kind the line does not carry.");
                    }

                    responsibilities[index] = change.Replacement;
                }

                current = current.WithResponsibilities(responsibilities);
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>
    /// REQ-05: a commercial amendment adopts the terms of its successor award; any other amendment
    /// keeps the terms of the order byte-equivalent.
    /// </summary>
    private async Task<VendorTermsSnapshot> SuccessorTermsAsync(
        PurchaseOrderAmendmentVersion amendment,
        PurchaseOrderVersion order,
        CancellationToken cancellationToken)
    {
        if (amendment.SuccessorAwardRef is null)
        {
            return order.TermsSnapshot;
        }

        var row = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.AwardId == amendment.SuccessorAwardRef.Id &&
                          record.Version == amendment.SuccessorAwardRef.Version,
                cancellationToken);
        var content = SourcingAwardDocument.Read(row.DocumentJson, row.ContentDigest);
        return VendorTermsSnapshot.ForAward(
            await PurchaseOrderSupplierAccess.ContentAsync(
                dbContext, amendment.OrganizationId, row.SupplierId, row.SupplierVersion, cancellationToken),
            content.AwardCandidate.Terms,
            new PurchaseOrderContentRef(row.AwardId, row.Version, row.ContentDigest),
            content.PolicyBundleRef,
            order.RequestRef,
            content.CatalogSnapshots,
            order.SourceCurrency,
            amendment.OccurredAt);
    }

    /// <summary>Projects the still-ordered lines of the successor version without moving backwards.</summary>
    private async Task ProjectAppliedLinesAsync(
        Guid organizationId,
        PurchaseOrderVersion baseOrder,
        PurchaseOrderVersion successor,
        DateTimeOffset occurred,
        CancellationToken cancellationToken)
    {
        foreach (var line in successor.Lines)
        {
            var projection = await dbContext.PurchaseRequestLineProjections
                .SingleOrDefaultAsync(
                    record => record.RequestId == baseOrder.RequestRef.Id &&
                              record.RequestVersion == baseOrder.RequestRef.Version &&
                              record.LineId == line.RequestLineRef.Id &&
                              record.LineVersion == line.RequestLineRef.Version,
                    cancellationToken);
            if (projection is null)
            {
                dbContext.PurchaseRequestLineProjections.Add(new PurchaseRequestLineProjectionRecord
                {
                    RequestId = baseOrder.RequestRef.Id,
                    RequestVersion = baseOrder.RequestRef.Version,
                    LineId = line.RequestLineRef.Id,
                    LineVersion = line.RequestLineRef.Version,
                    OrganizationId = organizationId,
                    Projection = (int)PurchaseRequestLineProjection.Ordered,
                    ConsumerId = baseOrder.PoId,
                    ConsumerVersion = successor.PurchaseOrderVersionNumber,
                    ConsumerDigest = successor.Digest,
                    ConsumerType = PurchaseOrderCodes.TargetPurchaseOrder,
                    ConsumerState = (int)PurchaseOrderState.Issued,
                    OccurredAt = occurred
                });
                continue;
            }

            if (projection.ConsumerVersion <= successor.PurchaseOrderVersionNumber)
            {
                projection.Projection = (int)PurchaseRequestLineProjection.Ordered;
                projection.ConsumerVersion = successor.PurchaseOrderVersionNumber;
                projection.ConsumerDigest = successor.Digest;
                projection.ConsumerState = (int)PurchaseOrderState.Issued;
                projection.OccurredAt = occurred;
            }
        }
    }

    private async Task AdvanceOrderAsync(
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

        var pointers = await dbContext.PurchaseOrderLinePointers
            .Where(pointer => pointer.PoId == poId)
            .ToArrayAsync(cancellationToken);
        var retained = lines
            .Select(line => (line.RequestLineRef.Id, line.RequestLineRef.Version))
            .ToHashSet();
        foreach (var pointer in pointers)
        {
            if (!retained.Contains((pointer.RequestLineId, pointer.RequestLineVersion)))
            {
                pointer.State = (int)PurchaseOrderState.Cancelled;
                pointer.PoVersion = successor.PurchaseOrderVersionNumber;
                continue;
            }

            pointer.PoVersion = successor.PurchaseOrderVersionNumber;
            pointer.State = (int)successor.State;
        }

        _ = organizationId;
    }

    private async Task AdvanceAmendmentAsync(
        Guid organizationId,
        Guid amendmentId,
        int previousVersion,
        PurchaseOrderAmendmentVersion successor,
        CancellationToken cancellationToken)
    {
        var applied = await dbContext.PurchaseOrderAmendmentRoots
            .Where(record => record.Id == amendmentId &&
                             record.OrganizationId == organizationId &&
                             record.CurrentVersion == previousVersion)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(record => record.CurrentVersion, successor.Version),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The amendment changed concurrently.");
        }
    }

    /// <summary>Idempotency preimage of one amendment creation; it is internal, not a published digest.</summary>
    public static string AmendmentFingerprint(CreatePurchaseOrderAmendmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amendment_key"] = PurchaseOrderCodes.Key(command.AmendmentKey, "amendment_key"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = "create-purchase-order-amendment/v1",
            ["expected_po_version"] = command.ExpectedPoVersion,
            ["line_deltas"] = PurchaseOrderCanonicalizer.Set((command.LineDeltas ?? [])
                .Select(delta => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["change_kind"] = delta.ChangeKind,
                    ["line_ref"] = PurchaseOrderCanonicalizer.ContentRef(delta.LineRef),
                    ["previous_line"] = PurchaseOrderCanonicalizer.LinePreimage(delta.PreviousLine),
                    ["replacement_line"] = delta.ReplacementLine is null
                        ? null
                        : PurchaseOrderCanonicalizer.LinePreimage(delta.ReplacementLine)
                })),
            ["organization_id"] = command.OrganizationId.ToString("D"),
            ["po_id"] = command.PoId.ToString("D"),
            ["reason"] = PurchaseOrderCodes.Reason(command.Reason),
            ["replacement_delivery"] = command.ReplacementDelivery is null
                ? null
                : PurchaseOrderCanonicalizer.DeliveryPreimage(command.ReplacementDelivery),
            ["responsibility_changes"] = PurchaseOrderCanonicalizer.Set(
                (command.ResponsibilityChanges ?? [])
                .Select(change => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = change.Kind,
                    ["line_ref"] = PurchaseOrderCanonicalizer.ContentRef(change.LineRef),
                    ["previous_assignment"] = PurchaseOrderCanonicalizer.ResponsibilityPreimage(change.Previous),
                    ["replacement_assignment"] = PurchaseOrderCanonicalizer.ResponsibilityPreimage(change.Replacement)
                })),
            ["successor_award_ref"] = command.SuccessorAwardRef is null
                ? null
                : PurchaseOrderCanonicalizer.ContentRef(command.SuccessorAwardRef)
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
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

/// <summary>
/// Server-side access to the current supplier content the Purchase Orders module requires for its
/// terms snapshots (REQ-09). A stale or non active supplier version fails closed.
/// </summary>
public static class PurchaseOrderSupplierAccess
{
    public static async Task<SupplierContentSnapshot> ContentAsync(
        ProcureToPayDbContext dbContext,
        Guid organizationId,
        Guid supplierId,
        int supplierVersion,
        CancellationToken cancellationToken = default)
    {
        var supplier = await dbContext.Suppliers
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == supplierId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new PurchaseOrderUnprocessableException("The awarded supplier is not visible.");
        if (supplier.OperationalVersion != supplierVersion)
        {
            throw new PurchaseOrderUnprocessableException("The awarded supplier version is stale.");
        }

        var version = await dbContext.SupplierVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.SupplierId == supplierId && record.Version == supplierVersion,
                cancellationToken)
            ?? throw new PurchaseOrderUnprocessableException("The awarded supplier version is not visible.");
        if ((ProcureToPay.Domain.Modules.Suppliers.SupplierOperationalStatus)version.Status !=
            ProcureToPay.Domain.Modules.Suppliers.SupplierOperationalStatus.Active)
        {
            throw new PurchaseOrderUnprocessableException("The awarded supplier is not active.");
        }

        return new SupplierContentSnapshot(
            supplierId,
            supplierVersion,
            version.ContentDigest,
            ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierSerialization.ReadPaymentTerms(
                version.PaymentTermsJson),
            ProcureToPay.Infrastructure.Persistence.Suppliers.SupplierSerialization.ReadCurrencies(
                version.SupportedCurrenciesJson));
    }
}

/// <summary>Single writer of one append-only <c>PurchaseOrderAmendmentRecord</c> row (REQ-05).</summary>
public static class PurchaseOrderAmendmentWriter
{
    public static PurchaseOrderAmendmentRecord Record(
        PurchaseOrderAmendmentVersion version,
        string commandKey,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = version.AmendmentId,
            Version = version.Version,
            PredecessorVersion = version.PredecessorVersion,
            OrganizationId = version.OrganizationId,
            PoId = version.BasePoRef.Id,
            BasePoVersion = version.BasePoRef.Version,
            State = (int)version.State,
            SuccessorAwardId = version.SuccessorAwardRef?.Id,
            SuccessorAwardVersion = version.SuccessorAwardRef?.Version,
            SuccessorAwardDigest = version.SuccessorAwardRef?.ContentDigest,
            DocumentJson = version.CanonicalDocument(),
            ContentDigest = version.Digest,
            CommandKey = commandKey,
            ActorUserId = version.ActorUserId,
            OccurredAt = occurredAt,
            Reason = version.Reason
        };
}
