using Microsoft.EntityFrameworkCore;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

namespace ProcureToPay.Infrastructure.Persistence.Sourcing;

/// <summary>Command that confirms the governed catalogue route of a draft process (REQ-06).</summary>
public sealed record ConfirmCatalogRouteCommand(
    Guid OrganizationId,
    Guid ProcessId,
    int ExpectedProcessVersion,
    string CommandKey);

/// <summary>Confirmed catalogue route: the frozen snapshots and the activated process (REQ-06).</summary>
public sealed record SourcingCatalogRouteView(
    Guid ProcessId,
    int ProcessVersion,
    SourcingEntityRef SupplierRef,
    IReadOnlyList<SourcingCatalogSnapshot> Snapshots,
    string SnapshotsDigest,
    Guid ActorUserId,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// Governed catalogue route (SPEC 10 REQ-06): omitting the RFQ is only allowed while the Purchase
/// Request version declares one supplier for every line, the approved catalogue snapshots publish
/// <c>PREFERRED_SUPPLIER=true</c> and <c>EXTERNAL_AGREEMENT_STATUS=ACTIVE</c>, and the current Policy
/// evaluation generated no quotation requirement for those targets. The catalogue never removes a
/// prerequisite and never decides an exception.
/// </summary>
public sealed class SourcingCatalogRouteService(
    ProcureToPayDbContext dbContext,
    IApprovedSupplierFactOwner approvedSupplierFacts)
{
    private const string ConfirmCommand = "CONFIRM_CATALOG_ROUTE";

    public async Task<SourcingCatalogRouteView> ConfirmAsync(
        ConfirmCatalogRouteCommand command,
        Guid actorUserId,
        string correlationReference,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = SourcingCodes.Key(command.CommandKey, "Command key");
        var process = await dbContext.SourcingProcesses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.ProcessId &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The sourcing process does not exist in this organization.");
        var lines = await dbContext.SourcingProcessLines
            .AsNoTracking()
            .Where(line => line.ProcessId == process.Id)
            .OrderBy(line => line.LineId)
            .ToArrayAsync(cancellationToken);
        if (lines.Length == 0)
        {
            throw new SourcingDependencyUnavailableException("The sourcing process has no lines.");
        }

        var requestLines = await new PurchaseRequestPersistenceService(
                dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<PurchaseRequestPersistenceService>.Instance)
            .LoadLinesAsync(process.RequestId, process.RequestVersion, cancellationToken);
        // REQ-06: the current request version must already declare the same supplier for every line.
        var declared = lines
            .Select(line => requestLines.TryGetValue(line.LineId, out var requestLine)
                ? requestLine.Content.SupplierRef
                : null)
            .ToArray();
        if (declared.Any(reference => reference is null))
        {
            throw new DomainConflictException(
                "The catalogue route requires the request version to declare a supplier on every line.");
        }

        var supplierRef = declared[0]!;
        if (declared.Any(reference =>
                reference!.Id != supplierRef.Id || reference.Version != supplierRef.Version))
        {
            throw new DomainConflictException(
                "The catalogue route requires the same supplier on every line of the request version.");
        }

        var snapshots = new List<SourcingCatalogSnapshot>(lines.Length);
        foreach (var line in lines)
        {
            var requestLine = requestLines[line.LineId];
            var snapshot = await approvedSupplierFacts.ResolveAsync(
                new ApprovedSupplierFactQuery(
                    command.OrganizationId,
                    new Domain.Modules.Suppliers.SupplierFactSourceLine(
                        line.LineId, line.LineVersion, line.LineContentDigest),
                    supplierRef,
                    requestLine.Content.SpendCategoryRef,
                    requestLine.Content.RequiredProductRef ?? requestLine.Content.PreferredProductRef,
                    now),
                cancellationToken);
            if (!snapshot.PreferredSupplier ||
                !string.Equals(snapshot.AgreementStatus, "ACTIVE", StringComparison.Ordinal) ||
                snapshot.CatalogEntryRef is null)
            {
                // The catalogue must publish a preferred supplier with an active agreement; anything
                // else demands an RFQ or a waiver (REQ-06).
                throw new DomainConflictException(
                    "The approved catalogue does not publish a preferred supplier with an active agreement.");
            }

            snapshots.Add(new SourcingCatalogSnapshot(
                new SourcingContentRef(
                    snapshot.CatalogEntryRef.Id, snapshot.CatalogEntryRef.Version, snapshot.Digest),
                line.LineId,
                line.LineVersion,
                new SourcingEntityRef(snapshot.SupplierRef!.Id, snapshot.SupplierRef.Version),
                snapshot.CatalogContentDigest ?? snapshot.Digest));
        }

        // The current Policy evaluation must not have generated a quotation requirement for those
        // targets: the catalogue never removes an existing prerequisite (REQ-06, DEC-03).
        var bundle = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == command.OrganizationId &&
                record.SubjectId == process.RequestId &&
                record.SubjectVersion == process.RequestVersion)
            .OrderByDescending(record => record.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (bundle is not null)
        {
            var evaluation = PolicyEvaluationBundleRehydrator.FromJson(bundle.BundleJson);
            var blocked = evaluation.Controls
                .Where(control => control.Type == PolicyEffectType.RequireQuotations)
                .SelectMany(control => control.SubjectIds)
                .ToHashSet();
            if (lines.Any(line => blocked.Contains(line.LineId)))
            {
                throw new DomainConflictException(
                    "The current policy requires quotations for these lines; the catalogue cannot omit the RFQ.");
            }
        }

        var fingerprint = SourcingCommandFingerprints.Transition(
            ConfirmCommand, command.OrganizationId, actorUserId, command.ProcessId,
            command.ExpectedProcessVersion, supplierRef.Id.ToString("D"), null, command.CommandKey);
        var replay = await dbContext.SourcingCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.ActorUserId == actorUserId &&
                          record.CommandType == ConfirmCommand &&
                          record.CommandKey == command.CommandKey,
                cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException("The command key was reused with another catalogue route.");
            }

            return await GetRouteAsync(command.OrganizationId, command.ProcessId, cancellationToken);
        }

        if (process.State != (int)SourcingProcessState.Draft ||
            process.Version != command.ExpectedProcessVersion)
        {
            throw new DomainConflictException("Only a draft sourcing process can confirm the catalogue route.");
        }

        var digest = SourcingCatalogSnapshotSet.ComputeDigest(
            command.OrganizationId, process.Id, process.Version + 1, snapshots);
        var applied = await dbContext.SourcingProcesses
            .Where(record =>
                record.Id == process.Id &&
                record.State == (int)SourcingProcessState.Draft &&
                record.Version == command.ExpectedProcessVersion)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(record => record.State, (int)SourcingProcessState.Active)
                    .SetProperty(record => record.Version, command.ExpectedProcessVersion + 1)
                    .SetProperty(record => record.UpdatedAt, now),
                cancellationToken);
        if (applied == 0)
        {
            throw new DomainConflictException("The sourcing process changed concurrently.");
        }

        dbContext.SourcingTakeovers.Add(new SourcingTakeoverRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            RequestId = process.RequestId,
            RequestVersion = process.RequestVersion,
            ProcessId = process.Id,
            ProcessVersion = command.ExpectedProcessVersion + 1,
            ActorUserId = actorUserId,
            RegisteredAt = now
        });
        dbContext.SourcingCatalogRoutes.Add(new SourcingCatalogRouteRecord
        {
            ProcessId = process.Id,
            OrganizationId = command.OrganizationId,
            RequestId = process.RequestId,
            RequestVersion = process.RequestVersion,
            SupplierId = supplierRef.Id,
            SupplierVersion = supplierRef.Version,
            SnapshotsJson = SourcingSerialization.CatalogSnapshots(snapshots),
            SnapshotsDigest = digest,
            SnapshotsVersion = command.ExpectedProcessVersion + 1,
            ActorUserId = actorUserId,
            ConfirmedAt = now
        });
        dbContext.SourcingCommands.Add(new SourcingCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            ActorUserId = actorUserId,
            CommandType = ConfirmCommand,
            CommandKey = command.CommandKey,
            Fingerprint = fingerprint,
            ResultId = process.Id,
            ResultVersion = command.ExpectedProcessVersion + 1,
            CreatedAt = now
        });
        dbContext.SourcingAuditRecords.Add(new SourcingAuditRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            Action = SourcingCodes.ActionCatalogRouteConfirmed,
            ActorJson = $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{actorUserId:D}\"," +
                        "\"workload_client_id\":null,\"workload_issuer\":null}}",
            ChangedFieldsJson = "[\"state\",\"takeover\",\"catalog_snapshots\"]",
            CorrelationReference = correlationReference,
            EffectKey = $"catalog-route:{process.Id:D}:confirmed",
            OccurredAt = now,
            TargetJson = $"{{\"id\":\"{process.Id:D}\",\"type\":\"{SourcingCodes.TargetProcess}\"," +
                         $"\"version\":{command.ExpectedProcessVersion + 1}}}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetRouteAsync(command.OrganizationId, process.Id, cancellationToken);
    }

    public async Task<SourcingCatalogRouteView> GetRouteAsync(
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken = default)
    {
        var route = await dbContext.SourcingCatalogRoutes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ProcessId == processId && record.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The sourcing process has no catalogue route.");
        var snapshots = SourcingSerialization.ReadCatalogSnapshots(route.SnapshotsJson);
        if (!string.Equals(
                SourcingCatalogSnapshotSet.ComputeDigest(
                    organizationId, processId, route.SnapshotsVersion, snapshots),
                route.SnapshotsDigest,
                StringComparison.Ordinal))
        {
            // The stored bytes must reproduce the recorded digest: a tampered route never proposes.
            throw new SourcingDependencyUnavailableException("The stored catalogue route is corrupted.");
        }

        return new SourcingCatalogRouteView(
            route.ProcessId,
            route.SnapshotsVersion,
            new SourcingEntityRef(route.SupplierId, route.SupplierVersion),
            snapshots,
            route.SnapshotsDigest,
            route.ActorUserId,
            route.ConfirmedAt);
    }
}
