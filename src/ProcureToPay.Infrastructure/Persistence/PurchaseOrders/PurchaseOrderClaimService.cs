using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;
using ProcureToPay.Infrastructure.Persistence.Sourcing;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>
/// Single-consumption claim of a current award (SPEC 11 REQ-01, DEC-02). The command consumes exactly
/// one award of SPEC 10, its complete line set and its content digest; Sourcing re-verifies
/// <c>award-consumption/v1</c> in the same transaction that persists the unique claim, transfers the
/// live line takeovers from <c>SOURCING</c> to <c>PURCHASE_ORDER</c> and creates the first
/// <c>DRAFT</c> version of the Purchase Order.
/// </summary>
public sealed class PurchaseOrderClaimService(
    ProcureToPayDbContext dbContext,
    IAwardConsumptionVerifier awardVerifier,
    PurchaseRequestLineTakeoverService takeovers)
{
    public const string ClaimCommandType = "AWARD_CLAIM";
    private const string ClaimConsumerType = "PURCHASE_ORDER_CLAIM";

    /// <summary>Consumes one award or replays the recorded outcome of the same preimage.</summary>
    public async Task<AwardConsumptionClaimResponse> ClaimAsync(
        AwardConsumptionClaimRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A claim requires its acting user.");
        }

        PurchaseOrderCodes.Key(request.WorkloadClientId, "workload_client_id");
        PurchaseOrderCodes.ContractId(request.WorkloadIssuer, "workload_issuer");
        var fingerprint = request.Fingerprint;
        var existing = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == request.OrganizationId &&
                          record.CommandType == ClaimCommandType &&
                          record.CommandKey == request.ClaimKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "The claim key was already used with a different preimage.");
            }

            var replayedClaimId = Guid.Parse(existing.ResultRef!);
            return await ReadClaimResponseAsync(
                request.OrganizationId, replayedClaimId, replayed: true, cancellationToken);
        }

        var awardRow = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AwardId == request.AwardRef.Id &&
                          record.Version == request.AwardRef.Version &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award is not visible.");
        if (string.Equals(awardRow.ContentDigest, request.AwardRef.ContentDigest, StringComparison.Ordinal) is false)
        {
            throw new DomainConflictException("The award digest does not match the published version.");
        }

        var currentAward = await dbContext.SourcingAwards
            .AsNoTracking()
            .SingleAsync(record => record.Id == awardRow.AwardId, cancellationToken);
        if (currentAward.CurrentVersion != request.AwardRef.Version)
        {
            throw new DomainConflictException("Only the current award version can be claimed.");
        }

        // The verified snapshot is the only commercial source: nothing of it is accepted from the
        // caller, and a stale supplier or a released takeover fails closed here.
        var snapshot = await awardVerifier.VerifyAsync(
            new AwardConsumptionRequest(
                request.OrganizationId,
                request.AwardRef.Id,
                request.AwardRef.Version,
                request.AwardRef.ContentDigest,
                awardRow.RequestId,
                awardRow.RequestVersion,
                request.CoveredLines
                    .Select(line => new SourcedRef(line.Id, line.Version))
                    .ToArray(),
                request.RequestedAt,
                request.WorkloadIssuer,
                request.WorkloadClientId),
            cancellationToken);
        await RequireEligibleSupplierAsync(
            request.OrganizationId, snapshot.SupplierRef.Id, snapshot.SupplierRef.Version,
            snapshot.SourceCurrency, cancellationToken);
        var requestVersion = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == awardRow.RequestId &&
                          record.Version == awardRow.RequestVersion &&
                          record.OrganizationId == request.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The awarded purchase request version is not visible.");
        var requestRow = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleAsync(record => record.Id == awardRow.RequestId, cancellationToken);
        if (requestRow.CurrentVersion != awardRow.RequestVersion)
        {
            throw new DomainConflictException("The awarded purchase request version is no longer current.");
        }

        var lineRows = await dbContext.PurchaseRequestLineVersions
            .AsNoTracking()
            .Where(line => line.RequestId == awardRow.RequestId &&
                           request.CoveredLines.Select(covered => covered.Id).Contains(line.LineId))
            .ToArrayAsync(cancellationToken);
        var lines = BuildLines(snapshot, request, lineRows);
        var alreadyOwned = false;

        // The claim fences the whole line set: every covered line must still be held by SOURCING for
        // the awarded process, and the transfer is recorded in the same transaction.
        var takeoverRows = await takeovers.ResolveSetAsync(
            request.OrganizationId,
            awardRow.RequestId,
            awardRow.RequestVersion,
            request.CoveredLines
                .Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                    line.Id, line.Version, line.ContentDigest))
                .ToArray(),
            cancellationToken);
        foreach (var row in takeoverRows)
        {
            var owner = (PurchaseRequestLineOwner)row.Owner;
            if (owner == PurchaseRequestLineOwner.Sourcing && row.ConsumerId == awardRow.ProcessId)
            {
                continue;
            }

            // A successor award of an already ordered line keeps the Purchase Order as owner.
            if (owner == PurchaseRequestLineOwner.PurchaseOrder && row.ConsumerId == request.PoId)
            {
                alreadyOwned = true;
                continue;
            }

            throw new DomainConflictException(
                "A covered line is not held by the sourcing process of this award.");
        }

        var predecessor = takeoverRows
            .Where(row => (PurchaseRequestLineOwner)row.Owner == PurchaseRequestLineOwner.Sourcing)
            .Select(row => (row.ConsumerId, row.ConsumerVersion))
            .Distinct()
            .ToArray();
        if (predecessor.Length > 1)
        {
            throw new DomainConflictException("The covered lines are held by different sourcing versions.");
        }

        var claimId = Guid.NewGuid();
        var occurredAt = request.RequestedAt.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var poNumber = await AllocatePoNumberAsync(request.OrganizationId, cancellationToken);
        var claimRef = new PurchaseOrderContentRef(claimId, 1, fingerprint);
        var terms = VendorTermsSnapshot.ForAward(
            await SupplierContentAsync(
                request.OrganizationId, snapshot.SupplierRef.Id, snapshot.SupplierRef.Version, cancellationToken),
            snapshot.Terms,
            request.AwardRef,
            snapshot.PolicyBundleRef,
            new PurchaseOrderContentRef(
                snapshot.RequestRef.Id, snapshot.RequestRef.Version, snapshot.RequestRef.ContentDigest),
            snapshot.CatalogSnapshots,
            snapshot.SourceCurrency,
            occurredAt);
        var version = new PurchaseOrderVersion(
            request.PoId,
            1,
            null,
            request.OrganizationId,
            poNumber,
            PurchaseOrderState.Draft,
            new PurchaseOrderEntityRef(requestVersion.LegalEntityId, requestVersion.LegalEntityVersion),
            new PurchaseOrderEntityRef(snapshot.SupplierRef.Id, snapshot.SupplierRef.Version),
            new PurchaseOrderContentRef(
                snapshot.RequestRef.Id, snapshot.RequestRef.Version, snapshot.RequestRef.ContentDigest),
            new PurchaseOrderContentRef(
                snapshot.ProposalRef.Id, snapshot.ProposalRef.Version, snapshot.ProposalRef.ContentDigest),
            request.AwardRef,
            claimRef,
            orderingEvidenceRef: null,
            approvalRef: null,
            amendmentRef: null,
            terms,
            delivery: null,
            lines,
            budgetOperationRefs: null,
            lines.Sum(line => line.GrossTotal),
            snapshot.SourceCurrency,
            lines.Sum(line => line.BaseGrossTotal),
            snapshot.BaseCurrency,
            actorUserId,
            occurredAt,
            issuedAt: null);

        dbContext.PurchaseOrders.Add(new PurchaseOrderRecord
        {
            Id = request.PoId,
            OrganizationId = request.OrganizationId,
            PoNumber = poNumber,
            RequestId = awardRow.RequestId,
            RequestVersion = awardRow.RequestVersion,
            SupplierId = snapshot.SupplierRef.Id,
            SupplierVersion = snapshot.SupplierRef.Version,
            LegalEntityId = requestVersion.LegalEntityId,
            LegalEntityVersion = requestVersion.LegalEntityVersion,
            CurrentVersion = 1,
            ActorUserId = actorUserId,
            CreatedAt = occurredAt
        });
        foreach (var line in lines)
        {
            dbContext.PurchaseOrderLinePointers.Add(new PurchaseOrderLinePointerRecord
            {
                RequestLineId = line.RequestLineRef.Id,
                RequestLineVersion = line.RequestLineRef.Version,
                PoId = request.PoId,
                PoVersion = 1,
                OrganizationId = request.OrganizationId,
                State = (int)PurchaseOrderState.Draft
            });
        }

        dbContext.AwardConsumptionClaims.Add(new AwardConsumptionClaimRecord
        {
            Id = claimId,
            OrganizationId = request.OrganizationId,
            AwardId = request.AwardRef.Id,
            AwardVersion = request.AwardRef.Version,
            AwardContentDigest = request.AwardRef.ContentDigest,
            ClaimKey = request.ClaimKey,
            Fingerprint = fingerprint,
            PoId = request.PoId,
            PoNumber = poNumber,
            CoveredLinesJson = PurchaseOrderClaimDocument.CoveredLines(request.CoveredLines),
            AwardSnapshotJson = PurchaseOrderCanonicalizer.AwardConsumptionDocumentJson(snapshot),
            WorkloadIssuer = request.WorkloadIssuer,
            WorkloadClientId = request.WorkloadClientId,
            ActorUserId = actorUserId,
            State = (int)AwardClaimState.Claimed,
            ClaimedAt = occurredAt
        });
        await PersistVersionAsync(version, lines, occurredAt, reason: null, cancellationToken);
        var transferred = alreadyOwned
            ? takeoverRows
            : await takeovers.AcquireAsync(
                request.OrganizationId,
                awardRow.RequestId,
                awardRow.RequestVersion,
                requestVersion.ContentDigest,
                lines.Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                        line.RequestLineRef.Id, line.RequestLineRef.Version, line.RequestLineRef.ContentDigest))
                    .ToArray(),
                PurchaseRequestLineOwner.PurchaseOrder,
                request.PoId,
                1,
                version.Digest,
                "PURCHASE_ORDER",
                predecessor[0].ConsumerId,
                predecessor[0].ConsumerVersion,
                actorUserId.ToString("D"),
                occurredAt,
                cancellationToken);
        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            CommandKey = request.ClaimKey,
            CommandType = ClaimCommandType,
            Fingerprint = fingerprint,
            ResultRef = claimId.ToString("D"),
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        });
        AddAudit(
            request.OrganizationId,
            actorUserId,
            PurchaseOrderCodes.ActionClaimCreated,
            PurchaseOrderCodes.TargetPurchaseOrder,
            request.PoId,
            1,
            ["claim", "lines", "takeover"],
            fingerprint,
            occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AwardConsumptionClaimResponse(
            PurchaseOrderCodes.AwardConsumptionClaimContract,
            claimRef,
            snapshot,
            new PurchaseOrderContentRef(request.PoId, 1, version.Digest),
            transferred.Select(row => new PurchaseOrderContentRef(row.Id, row.Version, TakeoverDigest(row)))
                .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray(),
            occurredAt,
            Replayed: false);
    }

    /// <summary>
    /// Recovers an unclaimed award that stopped being consumable (REQ-01). <c>REOPEN</c> returns the
    /// process to <c>ACTIVE</c> keeping its takeover so a successor proposal/award can be produced;
    /// <c>CANCEL</c> marks the award and the process terminal, abandons the owner attempts and
    /// releases the takeovers so the request can be revised or cancelled.
    /// </summary>
    public async Task<AwardRecoveryResponse> RecoverAsync(
        AwardRecoveryCommand command,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await dbContext.AwardConsumptionClaims
                .AsNoTracking()
                .AnyAsync(
                    claim => claim.OrganizationId == command.OrganizationId &&
                             claim.AwardId == command.AwardRef.Id,
                    cancellationToken))
        {
            throw new DomainConflictException("An award with a claim cannot be recovered.");
        }

        var awardRow = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AwardId == command.AwardRef.Id &&
                          record.Version == command.ExpectedAwardVersion &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award version is not visible.");
        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == awardRow.ProcessId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The sourcing process is not visible.");
        if (process.Version != command.ExpectedProcessVersion)
        {
            throw new DomainConflictException("The sourcing process version is stale.");
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var processState = string.Equals(command.Action, AwardRecoveryCommand.ActionReopen, StringComparison.Ordinal)
            ? SourcingProcessState.Active
            : SourcingProcessState.Cancelled;
        if (process.State == (int)SourcingProcessState.Cancelled)
        {
            throw new DomainConflictException("The sourcing process is already terminal.");
        }

        var applied = await dbContext.SourcingProcesses
            .Where(record => record.Id == process.Id && record.Version == command.ExpectedProcessVersion)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.State, (int)processState)
                    .SetProperty(record => record.Version, command.ExpectedProcessVersion + 1)
                    .SetProperty(record => record.UpdatedAt, occurredAt),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The sourcing process changed concurrently.");
        }

        var released = new List<PurchaseRequestLineTakeoverRecord>();
        if (processState == SourcingProcessState.Cancelled)
        {
            var award = await dbContext.SourcingAwards
                .SingleAsync(record => record.Id == awardRow.AwardId, cancellationToken);
            award.CurrentVersion += 0;
            var versionRow = await dbContext.SourcingAwardVersions
                .SingleAsync(
                    record => record.AwardId == awardRow.AwardId && record.Version == awardRow.Version,
                    cancellationToken);
            versionRow.Superseded = true;
            dbContext.SourcingCurrentAwardLines.RemoveRange(
                dbContext.SourcingCurrentAwardLines.Where(pointer => pointer.AwardId == awardRow.AwardId));
            var attempts = await dbContext.SourcingOwnerAttempts
                .Where(attempt => attempt.ProcessId == awardRow.ProcessId &&
                                  attempt.State != "ABANDONED" && attempt.State != "COMPLETED")
                .ToArrayAsync(cancellationToken);
            foreach (var attempt in attempts)
            {
                attempt.State = "ABANDONED";
                attempt.AbandonedAt = occurredAt;
                attempt.UpdatedAt = occurredAt;
            }

            released.AddRange(await takeovers.ReleaseRequestAsync(
                command.OrganizationId,
                awardRow.RequestId,
                awardRow.RequestVersion,
                TakeoverState.Released,
                command.Reason,
                occurredAt,
                cancellationToken));
        }

        AddAudit(
            command.OrganizationId,
            actorUserId,
            string.Equals(command.Action, AwardRecoveryCommand.ActionReopen, StringComparison.Ordinal)
                ? PurchaseOrderCodes.ActionAwardReopened
                : PurchaseOrderCodes.ActionAwardCancelled,
            PurchaseOrderCodes.TargetPurchaseOrder,
            awardRow.AwardId,
            awardRow.Version,
            ["process_state", "takeover"],
            command.RecoveryKey,
            occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AwardRecoveryResponse(
            command.AwardRef,
            command.Action,
            command.ExpectedProcessVersion + 1,
            processState == SourcingProcessState.Active ? "ACTIVE" : "CANCELLED",
            released
                .Select(row => new PurchaseOrderContentRef(row.Id, row.Version, TakeoverDigest(row)))
                .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray(),
            Replayed: false);
    }

    /// <summary>
    /// Releases the claim of a pre-issue cancellation and returns the line takeovers to the award
    /// (REQ-01). It is called by the lifecycle service inside its own transaction.
    /// </summary>
    public async Task ReleaseClaimAsync(
        Guid organizationId,
        Guid claimId,
        Guid actorUserId,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var claim = await dbContext.AwardConsumptionClaims
            .SingleOrDefaultAsync(
                record => record.Id == claimId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The award claim is not visible.");
        if ((AwardClaimState)claim.State != AwardClaimState.Claimed)
        {
            throw new DomainConflictException("Only a live claim can be released before issue.");
        }

        var version = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .Where(record => record.PoId == claim.PoId)
            .OrderByDescending(record => record.Version)
            .FirstAsync(cancellationToken);
        var awardRow = await dbContext.SourcingAwardVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.AwardId == claim.AwardId && record.Version == claim.AwardVersion,
                cancellationToken);
        claim.State = (int)AwardClaimState.Released;
        claim.ReleasedAt = occurredAt;
        claim.ReleaseReason = PurchaseOrderCodes.Reason(reason);
        await takeovers.ReleaseAsync(
            organizationId,
            "PURCHASE_ORDER",
            claim.PoId,
            TakeoverState.Released,
            reason,
            occurredAt,
            cancellationToken);
        var pointers = await dbContext.PurchaseOrderLinePointers
            .Where(pointer => pointer.PoId == claim.PoId)
            .ToArrayAsync(cancellationToken);
        foreach (var pointer in pointers)
        {
            pointer.State = (int)PurchaseOrderState.Cancelled;
        }

        await takeovers.AcquireAsync(
            organizationId,
            awardRow.RequestId,
            awardRow.RequestVersion,
            version.RequestContentDigest,
            pointers
                .Select(pointer => new PurchaseRequestLineTakeoverService.TakeoverLine(
                    pointer.RequestLineId, pointer.RequestLineVersion, LineDigest(version, pointer)))
                .ToArray(),
            PurchaseRequestLineOwner.Sourcing,
            awardRow.ProcessId,
            awardRow.ProcessId == Guid.Empty ? 1 : version.Version,
            version.AwardContentDigest,
            "SOURCING",
            predecessorConsumerId: claim.PoId,
            predecessorConsumerVersion: version.Version,
            actorUserId.ToString("D"),
            occurredAt,
            cancellationToken);
        AddAudit(
            organizationId,
            actorUserId,
            PurchaseOrderCodes.ActionClaimReleased,
            PurchaseOrderCodes.TargetPurchaseOrder,
            claim.PoId,
            version.Version,
            ["claim_state", "takeover"],
            claim.ClaimKey,
            occurredAt);
    }

    private static string LineDigest(PurchaseOrderVersionRecord version, PurchaseOrderLinePointerRecord pointer)
    {
        var lines = PurchaseOrderSerialization.ReadLines(version.LinesJson);
        return lines
            .Single(line => line.RequestLineRef.Id == pointer.RequestLineId &&
                            line.RequestLineRef.Version == pointer.RequestLineVersion)
            .RequestLineRef.ContentDigest;
    }

    private static string TakeoverDigest(PurchaseRequestLineTakeoverRecord row) =>
        PurchaseRequestLineTakeoverService.ToDomain(row).Digest;

    /// <summary>
    /// Rebuilds the recorded response of a claim. A replay never consumes the award again: the
    /// verified snapshot persisted at claim time is returned verbatim (REQ-01, NFR-03).
    /// </summary>
    private async Task<AwardConsumptionClaimResponse> ReadClaimResponseAsync(
        Guid organizationId,
        Guid claimId,
        bool replayed,
        CancellationToken cancellationToken)
    {
        var claim = await dbContext.AwardConsumptionClaims
            .AsNoTracking()
            .SingleAsync(record => record.Id == claimId, cancellationToken);
        var version = await dbContext.PurchaseOrderVersions
            .AsNoTracking()
            .Where(record => record.PoId == claim.PoId)
            .OrderBy(record => record.Version)
            .FirstAsync(cancellationToken);
        var snapshot = PurchaseOrderClaimDocument.ReadAwardSnapshot(
            claim.AwardSnapshotJson,
            claim.AwardId,
            claim.AwardVersion,
            claim.AwardContentDigest);
        var claimTakeovers = await dbContext.PurchaseRequestLineTakeovers
            .AsNoTracking()
            .Where(row => row.OrganizationId == organizationId &&
                          row.ConsumerType == "PURCHASE_ORDER" &&
                          row.ConsumerId == claim.PoId &&
                          row.State == (int)TakeoverState.Active)
            .ToArrayAsync(cancellationToken);
        return new AwardConsumptionClaimResponse(
            PurchaseOrderCodes.AwardConsumptionClaimContract,
            new PurchaseOrderContentRef(claim.Id, 1, claim.Fingerprint),
            snapshot,
            new PurchaseOrderContentRef(claim.PoId, version.Version, version.ContentDigest),
            claimTakeovers
                .Select(row => new PurchaseOrderContentRef(row.Id, row.Version, TakeoverDigest(row)))
                .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray(),
            claim.ClaimedAt,
            replayed);
    }

    private static IReadOnlyList<PurchaseOrderLine> BuildLines(
        AwardConsumptionResponse snapshot,
        AwardConsumptionClaimRequest request,
        PurchaseRequestLineVersionRecord[] lineRows)
    {
        var byIdentity = snapshot.AwardLines.ToDictionary(
            line => $"{line.LineRef.Id:D}:{line.LineRef.Version}", line => line);
        var requestedLines = request.CoveredLines
            .ToDictionary(line => $"{line.Id:D}:{line.Version}", line => line);
        if (byIdentity.Count != requestedLines.Count)
        {
            throw new DomainConflictException(
                "The claim must cover exactly the lines of the awarded request version.");
        }

        var lines = new List<PurchaseOrderLine>(snapshot.AwardLines.Count);
        foreach (var awardLine in snapshot.AwardLines.OrderBy(line => line.LineRef.Id))
        {
            var identity = $"{awardLine.LineRef.Id:D}:{awardLine.LineRef.Version}";
            if (!requestedLines.TryGetValue(identity, out var covered))
            {
                throw new DomainConflictException(
                    "The claim does not cover every line of the awarded request version.");
            }

            var requestLine = lineRows.SingleOrDefault(line =>
                line.LineId == awardLine.LineRef.Id && line.LineVersion == awardLine.LineRef.Version)
                ?? throw new DomainNotFoundException("A covered purchase request line is not visible.");
            if (!string.Equals(requestLine.ContentDigest, covered.ContentDigest, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A covered line does not match the attested request line digest.");
            }

            lines.Add(PurchaseOrderLine.FromAwardLine(
                awardLine,
                awardLine.LineRef.Id,
                new PurchaseOrderContentRef(
                    requestLine.LineId, requestLine.LineVersion, requestLine.ContentDigest)));
        }

        return lines;
    }

    private async Task RequireEligibleSupplierAsync(
        Guid organizationId,
        Guid supplierId,
        int supplierVersion,
        string currency,
        CancellationToken cancellationToken)
    {
        var supplier = await dbContext.Suppliers
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == supplierId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new PurchaseOrderUnprocessableException("The awarded supplier is not visible.");
        if (supplier.OperationalVersion != supplierVersion)
        {
            throw new PurchaseOrderUnprocessableException(
                "The awarded supplier version is stale; recover the award before claiming it.");
        }

        var version = await dbContext.SupplierVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.SupplierId == supplierId && record.Version == supplierVersion,
                cancellationToken)
            ?? throw new PurchaseOrderUnprocessableException("The awarded supplier version is not visible.");
        if ((SupplierOperationalStatus)version.Status != SupplierOperationalStatus.Active)
        {
            throw new PurchaseOrderUnprocessableException(
                "The awarded supplier is not active; recover the award before claiming it.");
        }

        var currencies = SupplierSerialization.ReadCurrencies(version.SupportedCurrenciesJson);
        if (!currencies.Contains(currency, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The supplier no longer supports the currency of the award; recover the award.");
        }
    }

    private async Task<SupplierContentSnapshot> SupplierContentAsync(
        Guid organizationId,
        Guid supplierId,
        int supplierVersion,
        CancellationToken cancellationToken)
    {
        var supplier = await dbContext.Suppliers
            .AsNoTracking()
            .SingleAsync(record => record.Id == supplierId && record.OrganizationId == organizationId,
                cancellationToken);
        if (supplier.OperationalVersion != supplierVersion)
        {
            throw new PurchaseOrderUnprocessableException("The awarded supplier version is stale.");
        }

        var version = await dbContext.SupplierVersions
            .AsNoTracking()
            .SingleAsync(
                record => record.SupplierId == supplierId && record.Version == supplierVersion,
                cancellationToken);
        if ((SupplierOperationalStatus)version.Status != SupplierOperationalStatus.Active)
        {
            throw new PurchaseOrderUnprocessableException("The awarded supplier is not active.");
        }

        return new SupplierContentSnapshot(
            supplierId,
            supplierVersion,
            version.ContentDigest,
            SupplierSerialization.ReadPaymentTerms(version.PaymentTermsJson),
            SupplierSerialization.ReadCurrencies(version.SupportedCurrenciesJson));
    }

    /// <summary>
    /// Allocates the next non-reusable PO number of the organization inside the claim transaction.
    /// The allocator is serialized with <c>HOLDLOCK</c>, so two concurrent claims never share a value
    /// and a rolled back claim consumes no number (REQ-02).
    /// </summary>
    private async Task<string> AllocatePoNumberAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            MERGE [PurchaseOrders].[PurchaseOrderNumberSequences] WITH (HOLDLOCK) AS target
            USING (SELECT {0} AS [OrganizationId]) AS source
                ON target.[OrganizationId] = source.[OrganizationId]
            WHEN MATCHED THEN UPDATE SET target.[LastValue] = target.[LastValue] + 1
            WHEN NOT MATCHED THEN INSERT ([OrganizationId], [LastValue]) VALUES (source.[OrganizationId], 1);
            """,
            [organizationId],
            cancellationToken);
        var next = await dbContext.Database
            .SqlQueryRaw<long>(
                "SELECT [LastValue] AS [Value] FROM [PurchaseOrders].[PurchaseOrderNumberSequences] WHERE [OrganizationId] = {0}",
                organizationId)
            .SingleAsync(cancellationToken);
        return $"PO-{next:D8}";
    }

    private async Task PersistVersionAsync(
        PurchaseOrderVersion version,
        IReadOnlyList<PurchaseOrderLine> lines,
        DateTimeOffset occurredAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        var parameters = await LineParametersAsync(version, cancellationToken);
        dbContext.PurchaseOrderVersions.Add(
            PurchaseOrderVersionWriter.Record(version, lines, parameters, occurredAt, reason));
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
                ?? throw new DomainNotFoundException("A covered purchase request line is not visible.");
            parameters.Add(new PurchaseOrderSerialization.LineParameter(
                line.RequestLineRef.Id.ToString("D"),
                row.PurchaseType,
                ReadRequestedFor(row.RequestedForUserJson),
                1));
        }

        return parameters;
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

/// <summary>Covered line set and verified award snapshot of one claim, with strict readers.</summary>
public static class PurchaseOrderClaimDocument
{
    public static string CoveredLines(IEnumerable<PurchaseOrderContentRef> lines) =>
        System.Text.Json.JsonSerializer.Serialize((lines ?? [])
            .OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal)
            .Select(line => new
            {
                content_digest = line.ContentDigest,
                id = line.Id,
                version = line.Version
            }));

    public static IReadOnlyList<PurchaseOrderContentRef> ReadCoveredLines(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var lines = new List<PurchaseOrderContentRef>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            lines.Add(new PurchaseOrderContentRef(
                element.GetProperty("id").GetGuid(),
                element.GetProperty("version").GetInt32(),
                element.GetProperty("content_digest").GetString()!));
        }

        return lines;
    }

    /// <summary>Serializes the verified <c>award-consumption/v1</c> response of the claim instant.</summary>
    public static string AwardSnapshot(AwardConsumptionResponse snapshot) =>
        PurchaseOrderCanonicalizer.AwardConsumptionDocumentJson(snapshot);

    /// <summary>
    /// Reads back the persisted verified snapshot, refusing a row whose award identity drifted from
    /// the claim it belongs to (NFR-01).
    /// </summary>
    public static AwardConsumptionResponse ReadAwardSnapshot(
        string json,
        Guid awardId,
        int awardVersion,
        string awardContentDigest)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var awardRef = root.GetProperty("award_ref");
        if (awardRef.GetProperty("id").GetGuid() != awardId ||
            awardRef.GetProperty("version").GetInt32() != awardVersion ||
            !string.Equals(awardRef.GetProperty("content_digest").GetString(), awardContentDigest,
                StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The recorded award snapshot does not match its claim.");
        }

        var lines = root.GetProperty("award_lines").EnumerateArray()
            .Select(element => new ProcureToPay.Domain.Modules.Sourcing.AwardLine(
                ReadContentRef(element.GetProperty("line_ref")),
                PurchaseOrderSerialization.ParseDecimal(element.GetProperty("quantity").GetString()!),
                element.GetProperty("unit_code").GetString()!,
                PurchaseOrderSerialization.ParseDecimal(element.GetProperty("unit_price").GetString()!),
                element.GetProperty("source_currency").GetString()!,
                PurchaseOrderSerialization.ParseDecimal(element.GetProperty("source_gross_total").GetString()!),
                element.GetProperty("base_currency").GetString()!,
                PurchaseOrderSerialization.ParseDecimal(element.GetProperty("base_gross_total").GetString()!),
                element.GetProperty("fx_snapshot_ref").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : ReadContentRef(element.GetProperty("fx_snapshot_ref"))))
            .ToArray();
        var covered = root.GetProperty("covered_lines").EnumerateArray()
            .Select(element => new ProcureToPay.Domain.Modules.Sourcing.SourcedRef(
                element.GetProperty("id").GetGuid(), element.GetProperty("version").GetInt32()))
            .ToArray();
        var snapshots = root.GetProperty("catalog_snapshots").EnumerateArray()
            .Select(element => new ProcureToPay.Domain.Modules.Sourcing.SourcingCatalogSnapshot(
                ReadContentRef(element.GetProperty("snapshot_ref")),
                element.GetProperty("line_id").GetGuid(),
                element.GetProperty("line_version").GetInt32(),
                new ProcureToPay.Domain.Modules.Sourcing.SourcingEntityRef(
                    element.GetProperty("supplier_ref").GetProperty("id").GetGuid(),
                    element.GetProperty("supplier_ref").GetProperty("version").GetInt32()),
                element.GetProperty("catalog_content_digest").GetString()!))
            .ToArray();
        var fx = root.GetProperty("fx_snapshots").EnumerateArray()
            .Select(ReadPolicyEvaluationRef)
            .ToArray();
        var termsElement = root.GetProperty("terms");
        return new AwardConsumptionResponse(
            ReadContentRef(root.GetProperty("award_ref")),
            ReadContentRef(root.GetProperty("proposal_ref")),
            ReadContentRef(root.GetProperty("request_ref")),
            new ProcureToPay.Domain.Modules.Sourcing.SourcingEntityRef(
                root.GetProperty("supplier_ref").GetProperty("id").GetGuid(),
                root.GetProperty("supplier_ref").GetProperty("version").GetInt32()),
            lines,
            covered,
            root.GetProperty("source_currency").GetString()!,
            PurchaseOrderSerialization.ParseDecimal(root.GetProperty("source_amount").GetString()!),
            root.GetProperty("base_currency").GetString()!,
            PurchaseOrderSerialization.ParseDecimal(root.GetProperty("base_amount").GetString()!),
            snapshots,
            root.GetProperty("catalog_snapshot_digest").GetString()!,
            fx,
            ReadPolicyEvaluationRef(root.GetProperty("policy_bundle_ref")),
            new ProcureToPay.Domain.Modules.Sourcing.CommercialTerms(
                termsElement.GetProperty("delivery_days").GetInt32(),
                termsElement.GetProperty("incoterm_code").ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : termsElement.GetProperty("incoterm_code").GetString(),
                termsElement.GetProperty("payment_terms_code").GetString()!,
                termsElement.GetProperty("warranty_days").GetInt32()),
            PurchaseOrderSerialization.ParseUtc(root.GetProperty("eligible_at").GetString()!));
    }

    private static ProcureToPay.Domain.Modules.Sourcing.SourcingContentRef ReadContentRef(System.Text.Json.JsonElement element) =>
        new(
            element.GetProperty("id").GetGuid(),
            element.GetProperty("version").GetInt32(),
            element.GetProperty("content_digest").GetString()!);

    private static ProcureToPay.Domain.Modules.Sourcing.SourcingPolicyEvaluationRef ReadPolicyEvaluationRef(
        System.Text.Json.JsonElement element) =>
        new(
            element.GetProperty("evaluation_sequence").GetInt64(),
            element.GetProperty("id").GetGuid(),
            element.GetProperty("facts_digest").GetString()!,
            element.GetProperty("input_digest").GetString()!,
            element.GetProperty("manifest_digest").GetString()!,
            element.GetProperty("policy_content_digest").GetString()!,
            element.GetProperty("result_digest").GetString()!);
}
