using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.Organization;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseOrders;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Budget;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

/// <summary>Command that authorizes one set of Purchase Request lines as a Direct Purchase (REQ-07).</summary>
public sealed record CreateDirectPurchaseAuthorizationCommand(
    Guid OrganizationId,
    Guid RequestId,
    int ExpectedRequestVersion,
    string AuthorizationKey,
    Guid ActorUserId,
    IReadOnlyList<OrderingEvidenceTargetRef> CoveredTargets,
    IReadOnlyList<DirectPurchaseAssignmentRequest> AcceptanceResponsibilities);

/// <summary>Command that cancels one authorized Direct Purchase and releases its hold (REQ-07).</summary>
public sealed record CancelDirectPurchaseAuthorizationCommand(
    Guid OrganizationId,
    Guid AuthorizationId,
    int ExpectedVersion,
    string CancelKey,
    string Reason,
    Guid ActorUserId);

/// <summary>
/// Direct Purchase authorization (SPEC 11 REQ-07, DEC-05). It derives every control server-side from
/// the current policy bundle, the completed request case and the attested lines, keeps only one active
/// takeover per covered line, and never posts a <c>COMMIT</c>: the reservation stays <c>RESERVED</c>
/// until a future Invoice/Matching contract consumes it.
/// </summary>
public sealed class DirectPurchaseService(
    ProcureToPayDbContext dbContext,
    PurchaseRequestOrderingEvidenceService orderingEvidence,
    PurchaseRequestLineTakeoverService takeovers,
    BudgetReleaseService releases)
{
    public const string CreateCommandType = "AUTHORIZE_DIRECT_PURCHASE";
    public const string CancelCommandType = "CANCEL_DIRECT_PURCHASE";
    public const string DirectPurchaseTriggerContract = "direct-purchase-authorization/v1";

    /// <summary>Authorizes a non-empty set of lines of one current approved request (REQ-07).</summary>
    public async Task<DirectPurchaseAuthorization> AuthorizeAsync(
        CreateDirectPurchaseAuthorizationCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.AuthorizationKey, "authorization_key");
        var occurred = occurredAt.ToUniversalTime();
        var commandFingerprint = DirectPurchaseFingerprints.CommandFingerprint(
            command.OrganizationId,
            command.RequestId,
            command.ExpectedRequestVersion,
            command.AuthorizationKey,
            command.ActorUserId,
            command.CoveredTargets ?? [],
            command.AcceptanceResponsibilities ?? []);
        var replay = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.CommandType == CreateCommandType &&
                          record.CommandKey == command.AuthorizationKey,
                cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Fingerprint, commandFingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "The authorization key was reused with a different preimage.");
            }

            var parts = replay.ResultRef!.Split(':');
            return await ReadAuthorizationAsync(
                command.OrganizationId, Guid.Parse(parts[0]), int.Parse(parts[1]), cancellationToken);
        }

        var targets = (command.CoveredTargets ?? [])
            .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0)
        {
            throw new PurchaseOrderUnprocessableException("A Direct Purchase requires at least one target.");
        }

        if (targets.Length > PurchaseOrderCodes.MaxLines)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A Direct Purchase covers at most {PurchaseOrderCodes.MaxLines} lines.");
        }

        var request = await dbContext.PurchaseRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == command.RequestId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (request.CurrentVersion != command.ExpectedRequestVersion)
        {
            throw new DomainConflictException("The purchase request changed under the command; reload and retry.");
        }

        if ((PurchaseRequestStatus)request.Status is not (PurchaseRequestStatus.Approved or
            PurchaseRequestStatus.PartiallyApproved))
        {
            throw new PurchaseOrderUnprocessableException("Only an approved purchase request is ordered directly.");
        }

        var covered = await CoveredLinesAsync(command, targets, cancellationToken);
        var evidence = await orderingEvidence.ProduceAsync(
            new OrderingEvidenceRequest(
                command.OrganizationId,
                command.RequestId,
                command.ExpectedRequestVersion,
                targets,
                occurred,
                PurchaseOrderCodes.DomainWorkloadIssuer,
                PurchaseOrderCodes.DomainWorkloadClientId),
            cancellationToken);
        await RequireRouteAsync(command, covered, evidence, cancellationToken);

        var supplier = RequireSupplier(covered);
        var sourceCurrency = RequireCurrency(covered, line => line.TransactionCurrency, "source");
        var baseCurrency = RequireCurrency(covered, line => line.BaseCurrency, "base");
        var expectedBaseCurrency = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == command.OrganizationId)
            .Select(organization => organization.BaseCurrency)
            .SingleAsync(cancellationToken);
        if (!string.Equals(baseCurrency, expectedBaseCurrency, StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The covered lines do not share the base currency of the organization.");
        }

        var supplierContent = await PurchaseOrderSupplierAccess.ContentAsync(
            dbContext, command.OrganizationId, supplier.Id, supplier.Version, cancellationToken);
        if (!supplierContent.SupportedCurrencies.Contains(sourceCurrency, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The supplier does not support the currency of the covered lines.");
        }

        var requestRef = new PurchaseOrderContentRef(
            command.RequestId, command.ExpectedRequestVersion, covered[0].RequestContentDigest);
        var terms = VendorTermsSnapshot.ForDirectPurchase(
            supplierContent, requestRef, evidence.PolicyBundleRef, sourceCurrency, occurred);
        var responsibilities = await BuildResponsibilitiesAsync(
            command, covered, occurred, cancellationToken);
        var documentEvidence = await DocumentEvidenceAsync(
            command.OrganizationId, evidence.CaseRef.CaseId, cancellationToken);
        var maximumSource = PurchaseOrderCodes.Decimal12(covered.Sum(line => line.EstimatedGrossAmount));
        var maximumBase = PurchaseOrderCodes.Decimal12(covered.Sum(line => line.BaseAmount));
        var fingerprint = DirectPurchaseFingerprints.AuthorizationFingerprint(
            command.OrganizationId,
            command.RequestId,
            command.ExpectedRequestVersion,
            command.AuthorizationKey,
            command.ActorUserId,
            covered.Select(line => new PurchaseOrderContentRef(
                line.LineId, line.LineVersion, line.ContentDigest)),
            responsibilities,
            evidence.PolicyBundleRef,
            new PurchaseOrderContentRef(
                command.RequestId, command.ExpectedRequestVersion, evidence.Digest),
            supplier,
            maximumSource,
            maximumBase,
            terms.Digest);
        var existingKey = await dbContext.DirectPurchaseAuthorizations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.AuthorizationKey == command.AuthorizationKey,
                cancellationToken);
        if (existingKey is not null)
        {
            throw new DomainConflictException(
                string.Equals(existingKey.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? "The Direct Purchase authorization already exists; retry with the same key."
                    : "The authorization key was reused with a different preimage.");
        }

        var authorization = new DirectPurchaseAuthorization(
            Guid.NewGuid(),
            version: 1,
            predecessorVersion: null,
            command.OrganizationId,
            command.AuthorizationKey,
            fingerprint,
            command.ExpectedRequestVersion,
            requestRef,
            new PurchaseOrderContentRef(
                evidence.CaseRef.CaseId, evidence.CaseRef.CaseVersion, evidence.CaseRef.ContentDigest),
            new PurchaseOrderContentRef(
                command.RequestId, command.ExpectedRequestVersion, evidence.Digest),
            evidence.PolicyBundleRef,
            supplier,
            covered.Select(line => new PurchaseOrderContentRef(
                line.LineId, line.LineVersion, line.ContentDigest)).ToArray(),
            maximumSource,
            sourceCurrency,
            maximumBase,
            terms,
            responsibilities,
            documentEvidence,
            DirectPurchaseState.Authorized,
            command.ActorUserId,
            occurred);
        dbContext.DirectPurchaseAuthorizations.Add(DirectPurchaseAuthorizationWriter.Record(
            authorization, evidence.CoveredTargets));
        await takeovers.AcquireAsync(
            command.OrganizationId,
            command.RequestId,
            command.ExpectedRequestVersion,
            covered[0].RequestContentDigest,
            covered.Select(line => new PurchaseRequestLineTakeoverService.TakeoverLine(
                line.LineId, line.LineVersion, line.ContentDigest)).ToArray(),
            PurchaseRequestLineOwner.DirectPurchase,
            authorization.AuthorizationId,
            authorization.Version,
            authorization.Digest,
            PurchaseOrderCodes.TakeoverConsumerDirectPurchase,
            predecessorConsumerId: null,
            predecessorConsumerVersion: null,
            command.ActorUserId.ToString("D"),
            occurred,
            cancellationToken);
        foreach (var line in covered)
        {
            await ProjectAsync(
                command.OrganizationId, line, authorization, occurred, cancellationToken);
        }

        dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            CommandKey = command.AuthorizationKey,
            CommandType = CreateCommandType,
            Fingerprint = commandFingerprint,
            ResultRef = $"{authorization.AuthorizationId:D}:{authorization.Version}",
            ActorUserId = command.ActorUserId,
            OccurredAt = occurred
        });
        AddAudit(
            command.OrganizationId,
            command.ActorUserId,
            PurchaseOrderCodes.ActionDirectPurchaseAuthorized,
            PurchaseOrderCodes.TargetDirectPurchase,
            authorization.AuthorizationId,
            authorization.Version,
            ["covered_lines", "maximum_source_amount", "maximum_base_amount", "takeover"],
            command.AuthorizationKey,
            occurred);
        await dbContext.SaveChangesAsync(cancellationToken);
        return authorization;
    }

    /// <summary>
    /// Cancels one authorized Direct Purchase: the hold is released through the
    /// <c>DIRECT_PURCHASE_CANCELLED</c> trigger and the takeovers return to the request (REQ-07,
    /// REQ-10).
    /// </summary>
    public async Task<DirectPurchaseAuthorization> CancelAsync(
        CancelDirectPurchaseAuthorizationCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PurchaseOrderCodes.Key(command.CancelKey, "cancel_key");
        var occurred = occurredAt.ToUniversalTime();
        var replay = await dbContext.PurchaseOrderCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.CommandType == CancelCommandType &&
                          record.CommandKey == command.CancelKey,
                cancellationToken);
        if (replay is not null)
        {
            var parts = replay.ResultRef!.Split(':');
            return await ReadAuthorizationAsync(
                command.OrganizationId, Guid.Parse(parts[0]), int.Parse(parts[1]), cancellationToken);
        }

        var current = await ReadAuthorizationAsync(
            command.OrganizationId, command.AuthorizationId, command.ExpectedVersion, cancellationToken);
        if (current.State == DirectPurchaseState.Cancelled)
        {
            return current;
        }

        var evidence = await dbContext.PurchaseRequestOrderingEvidence
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.OrganizationId == command.OrganizationId &&
                          record.ContentDigest == current.OrderingEvidenceRef.ContentDigest,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The ordering evidence of the Direct Purchase is not available.");
        var targets = PurchaseOrderSerialization.ReadTargets(evidence.CoveredTargetsJson);
        var release = await releases.ReleaseAsync(
            new BudgetReleaseRequest(
                command.OrganizationId,
                current.RequestApprovalCaseRef.Id,
                current.RequestRef.Id,
                current.RequestRef.Version,
                $"direct-purchase-cancel:{current.AuthorizationId:D}:{current.Version}",
                "DIRECT_PURCHASE_CANCELLED",
                BudgetReleaseTrigger.DirectPurchaseCancelled,
                new BudgetTriggerEvent(DirectPurchaseTriggerContract, current.AuthorizationId),
                targets
                    .Select(target => new BudgetTarget(
                        target.TargetId,
                        target.TargetVersion,
                        target.MaterialSnapshotDigest,
                        PurchaseOrderCodes.ApprovalTargetType))
                    .ToArray()),
            BudgetActor.ForUser(command.ActorUserId),
            command.CancelKey,
            cancellationToken);
        var cancelled = current.With(DirectPurchaseState.Cancelled, command.ActorUserId, occurred);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            dbContext.DirectPurchaseAuthorizations.Add(DirectPurchaseAuthorizationWriter.Record(
                cancelled, targets));
            await takeovers.ReleaseAsync(
                command.OrganizationId,
                PurchaseOrderCodes.TakeoverConsumerDirectPurchase,
                current.AuthorizationId,
                TakeoverState.Released,
                "DIRECT_PURCHASE_CANCELLED",
                occurred,
                cancellationToken);
            dbContext.PurchaseOrderCommands.Add(new PurchaseOrderCommandRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                CommandKey = command.CancelKey,
                CommandType = CancelCommandType,
                Fingerprint = cancelled.Digest,
                ResultRef = $"{cancelled.AuthorizationId:D}:{cancelled.Version}",
                ActorUserId = command.ActorUserId,
                OccurredAt = occurred
            });
            AddAudit(
                command.OrganizationId,
                command.ActorUserId,
                PurchaseOrderCodes.ActionDirectPurchaseCancelled,
                PurchaseOrderCodes.TargetDirectPurchase,
                cancelled.AuthorizationId,
                cancelled.Version,
                ["state", "budget_release"],
                command.CancelKey,
                occurred);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _ = release;
        return cancelled;
    }

    /// <summary>Reads one authorization version, rehashing its document (NFR-01).</summary>
    public async Task<DirectPurchaseAuthorization> ReadAuthorizationAsync(
        Guid organizationId,
        Guid authorizationId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.DirectPurchaseAuthorizations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == authorizationId &&
                             candidate.Version == version &&
                             candidate.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The Direct Purchase authorization is not visible.");
        return PurchaseOrderSerialization.ReadDirectPurchase(record.DocumentJson, record.ContentDigest);
    }

    /// <summary>
    /// REQ-07: the current bundle must allow a Direct Purchase for every covered target and must not
    /// require a Purchase Order nor block any of them.
    /// </summary>
    private async Task RequireRouteAsync(
        CreateDirectPurchaseAuthorizationCommand command,
        IReadOnlyList<CoveredLine> covered,
        PurchaseRequestOrderingEvidence evidence,
        CancellationToken cancellationToken)
    {
        var bundle = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.Id == evidence.PolicyBundleRef.Id &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the authorized request version is not available.");
        PolicyEvaluationBundle rehydrated;
        try
        {
            rehydrated = PolicyEvaluationBundleRehydrator.FromJson(bundle.BundleJson);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                             InvalidOperationException or FormatException)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the authorized request version is corrupted.");
        }

        if (!string.Equals(rehydrated.ResultDigest, evidence.PolicyBundleRef.ResultDigest, StringComparison.Ordinal))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "The policy evaluation of the authorized request version is not reproducible.");
        }

        // REQ-07: every requirement and prerequisite of the completed case must be completed; a
        // rejected decision or a waiting, failed or cancelled prerequisite never authorizes it.
        if (evidence.ResultRefs.Any(reference =>
                !string.Equals(reference.Result, "APPROVED", StringComparison.Ordinal)))
        {
            throw new PurchaseOrderUnprocessableException(
                "The authorized request case is not fully approved.");
        }

        var incomplete = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .AnyAsync(
                record => record.CaseId == evidence.CaseRef.CaseId &&
                          record.Status != (int)PrerequisiteStatus.Satisfied,
                cancellationToken);
        if (incomplete)
        {
            throw new PurchaseOrderUnprocessableException(
                "The completed request case carries an unsatisfied prerequisite.");
        }

        foreach (var line in covered)
        {
            var controls = rehydrated.Controls.Where(control => control.Covers(line.LineId)).ToArray();
            if (controls.Any(control => control.Type == PolicyEffectType.Block))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The policy evaluation blocks one covered line.");
            }

            if (controls.Any(control => control.Type == PolicyEffectType.RequirePo))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The policy evaluation requires a Purchase Order for one covered line.");
            }

            if (!controls.Any(control => control.Type == PolicyEffectType.AllowDirectPurchase))
            {
                throw new PurchaseOrderUnprocessableException(
                    "The policy evaluation does not allow a Direct Purchase for one covered line.");
            }
        }
    }

    private async Task<IReadOnlyList<CoveredLine>> CoveredLinesAsync(
        CreateDirectPurchaseAuthorizationCommand command,
        IReadOnlyList<OrderingEvidenceTargetRef> targets,
        CancellationToken cancellationToken)
    {
        var version = await dbContext.PurchaseRequestVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == command.RequestId &&
                          record.Version == command.ExpectedRequestVersion &&
                          record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new PurchaseOrderDependencyUnavailableException(
                "The authorized purchase request version is not available.");
        var manifest = await dbContext.PurchaseRequestVersionLines
            .AsNoTracking()
            .Where(record => record.RequestId == command.RequestId &&
                             record.RequestVersion == command.ExpectedRequestVersion)
            .ToArrayAsync(cancellationToken);
        var lines = new List<CoveredLine>(targets.Count);
        foreach (var target in targets)
        {
            if (!manifest.Any(record => record.LineId == target.TargetId &&
                                        record.LineVersion == target.TargetVersion))
            {
                throw new PurchaseOrderUnprocessableException(
                    "A covered target does not belong to the authorized request version.");
            }

            var line = await dbContext.PurchaseRequestLineVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.LineId == target.TargetId &&
                              record.LineVersion == target.TargetVersion &&
                              record.RequestId == command.RequestId &&
                              record.OrganizationId == command.OrganizationId,
                    cancellationToken)
                ?? throw new PurchaseOrderDependencyUnavailableException(
                    "A covered line is not available.");
            lines.Add(new CoveredLine(
                line.LineId,
                line.LineVersion,
                line.ContentDigest,
                line.RequestId,
                version.ContentDigest,
                line.PurchaseType,
                ReadEntity(line.SupplierJson, "supplier"),
                ReadEntity(line.RequestedForUserJson, "requested for user"),
                line.EstimatedGrossAmount,
                line.TransactionCurrency,
                line.BaseAmount,
                line.BaseCurrency));
        }

        return lines;
    }

    /// <summary>Every covered line shares one attested supplier and one source currency (REQ-07).</summary>
    private static PurchaseOrderEntityRef RequireSupplier(IReadOnlyList<CoveredLine> covered)
    {
        var first = covered[0].Supplier
            ?? throw new PurchaseOrderUnprocessableException(
                "A Direct Purchase requires an attested supplier on every covered line.");
        if (covered.Any(line => line.Supplier is null ||
                                line.Supplier.Id != first.Id ||
                                line.Supplier.Version != first.Version))
        {
            throw new PurchaseOrderUnprocessableException(
                "Every covered line of a Direct Purchase must share the same supplier version.");
        }

        return first;
    }

    private static string RequireCurrency(
        IReadOnlyList<CoveredLine> covered,
        Func<CoveredLine, string> selector,
        string concept)
    {
        var first = selector(covered[0]);
        if (covered.Any(line => !string.Equals(selector(line), first, StringComparison.Ordinal)))
        {
            throw new PurchaseOrderUnprocessableException(
                $"Every covered line of a Direct Purchase must share the same {concept} currency.");
        }

        return first;
    }

    private async Task<IReadOnlyList<AcceptanceResponsibility>> BuildResponsibilitiesAsync(
        CreateDirectPurchaseAuthorizationCommand command,
        IReadOnlyList<CoveredLine> covered,
        DateTimeOffset occurred,
        CancellationToken cancellationToken)
    {
        var requested = command.AcceptanceResponsibilities ?? [];
        var responsibilities = new List<AcceptanceResponsibility>();
        foreach (var line in covered)
        {
            var lineRef = new PurchaseOrderContentRef(line.LineId, line.LineVersion, line.ContentDigest);
            var identity = $"{line.LineId:D}:{line.LineVersion}";
            var assignments = requested
                .Where(assignment => string.Equals(
                    assignment.CanonicalIdentity, identity, StringComparison.Ordinal))
                .Select(assignment => new AcceptanceAssignmentRequest(
                    assignment.Kind, assignment.UserId, assignment.UserVersion, assignment.Reason))
                .ToArray();
            if (requested.Any(assignment => !covered.Any(candidate => string.Equals(
                    assignment.CanonicalIdentity,
                    $"{candidate.LineId:D}:{candidate.LineVersion}",
                    StringComparison.Ordinal))))
            {
                throw new PurchaseOrderUnprocessableException(
                    "An acceptance responsibility references a line the authorization does not cover.");
            }

            await RequireActiveUsersAsync(
                command.OrganizationId,
                assignments.Select(assignment => (assignment.UserId, assignment.UserVersion))
                    .Append((line.RequestedFor.Id, line.RequestedFor.Version)),
                cancellationToken);
            responsibilities.AddRange(AcceptanceResponsibilityBuilder.Build(
                lineRef,
                line.PurchaseType,
                line.RequestedFor,
                assignments,
                command.ActorUserId,
                occurred));
        }

        return responsibilities;
    }

    private async Task RequireActiveUsersAsync(
        Guid organizationId,
        IEnumerable<(Guid UserId, int Version)> users,
        CancellationToken cancellationToken)
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

    /// <summary>
    /// REQ-07/REQ-08: the satisfied supporting document prerequisites of the completed case supply the
    /// evidence references the authorization publishes; a missing evidence row fails closed.
    /// </summary>
    private async Task<IReadOnlyList<PurchaseOrderContentRef>> DocumentEvidenceAsync(
        Guid organizationId,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var prerequisiteIds = await dbContext.ApprovalPrerequisites
            .AsNoTracking()
            .Where(record => record.CaseId == caseId &&
                             record.OwnerAdapterId == PurchaseOrderCodes.SupportingDocumentOwnerAdapterId &&
                             record.OwnerAdapterVersion == PurchaseOrderCodes.OwnerAdapterVersion &&
                             record.Status == (int)PrerequisiteStatus.Satisfied)
            .Select(record => record.Id)
            .ToArrayAsync(cancellationToken);
        if (prerequisiteIds.Length == 0)
        {
            return [];
        }

        var evidence = await dbContext.SupportingDocumentOwnerEvidence
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             prerequisiteIds.Contains(record.PrerequisiteId))
            .Select(record => new { record.Id, record.ContentDigest })
            .ToArrayAsync(cancellationToken);
        if (evidence.Length != prerequisiteIds.Length)
        {
            throw new PurchaseOrderDependencyUnavailableException(
                "A satisfied supporting document prerequisite has no evidence.");
        }

        return evidence
            .Select(record => new PurchaseOrderContentRef(record.Id, 1, record.ContentDigest))
            .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task ProjectAsync(
        Guid organizationId,
        CoveredLine line,
        DirectPurchaseAuthorization authorization,
        DateTimeOffset occurred,
        CancellationToken cancellationToken)
    {
        var projection = await dbContext.PurchaseRequestLineProjections
            .SingleOrDefaultAsync(
                record => record.RequestId == line.RequestId &&
                          record.RequestVersion == authorization.ExpectedRequestVersion &&
                          record.LineId == line.LineId &&
                          record.LineVersion == line.LineVersion,
                cancellationToken);
        if (projection is null)
        {
            dbContext.PurchaseRequestLineProjections.Add(new PurchaseRequestLineProjectionRecord
            {
                RequestId = line.RequestId,
                RequestVersion = authorization.ExpectedRequestVersion,
                LineId = line.LineId,
                LineVersion = line.LineVersion,
                OrganizationId = organizationId,
                Projection = (int)PurchaseRequestLineProjection.DirectPurchaseAuthorized,
                ConsumerId = authorization.AuthorizationId,
                ConsumerVersion = authorization.Version,
                ConsumerDigest = authorization.Digest,
                ConsumerType = PurchaseOrderCodes.TargetDirectPurchase,
                ConsumerState = (int)DirectPurchaseState.Authorized,
                OccurredAt = occurred
            });
            return;
        }

        if (projection.ConsumerVersion <= authorization.Version &&
            projection.ConsumerId == authorization.AuthorizationId)
        {
            projection.Projection = (int)PurchaseRequestLineProjection.DirectPurchaseAuthorized;
            projection.ConsumerVersion = authorization.Version;
            projection.ConsumerDigest = authorization.Digest;
            projection.ConsumerState = (int)DirectPurchaseState.Authorized;
            projection.OccurredAt = occurred;
        }
    }

    private static PurchaseOrderEntityRef? ReadEntity(string? json, string concept)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("version", out var version))
        {
            throw new PurchaseOrderDependencyUnavailableException(
                $"The attested {concept} of a covered line is not readable.");
        }

        return new PurchaseOrderEntityRef(id.GetGuid(), version.GetInt32());
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
            ChangedFieldsJson = JsonSerializer.Serialize(changedFields),
            CorrelationReference = correlation.Length <= 256 ? correlation : correlation[..256],
            OccurredAt = occurredAt
        });

    /// <summary>One attested line the authorization covers, as the request persisted it.</summary>
    private sealed record CoveredLine(
        Guid LineId,
        int LineVersion,
        string ContentDigest,
        Guid RequestId,
        string RequestContentDigest,
        string PurchaseType,
        PurchaseOrderEntityRef? Supplier,
        PurchaseOrderEntityRef RequestedFor,
        decimal EstimatedGrossAmount,
        string TransactionCurrency,
        decimal BaseAmount,
        string BaseCurrency);
}

/// <summary>Single writer of one append-only Direct Purchase authorization row (REQ-07).</summary>
public static class DirectPurchaseAuthorizationWriter
{
    public static DirectPurchaseAuthorizationRecord Record(
        DirectPurchaseAuthorization authorization,
        IReadOnlyList<OrderingEvidenceTarget> targets) =>
        new()
        {
            Id = authorization.AuthorizationId,
            Version = authorization.Version,
            OrganizationId = authorization.OrganizationId,
            RequestId = authorization.RequestRef.Id,
            RequestVersion = authorization.RequestRef.Version,
            AuthorizationKey = authorization.AuthorizationKey,
            Fingerprint = authorization.Fingerprint,
            SupplierId = authorization.SupplierRef.Id,
            SupplierVersion = authorization.SupplierRef.Version,
            PolicyBundleId = authorization.PolicyBundleRef.Id,
            PolicyBundleVersion = (int)authorization.PolicyBundleRef.EvaluationSequence,
            PolicyBundleDigest = authorization.PolicyBundleRef.ResultDigest,
            CoveredLinesJson = PurchaseOrderSerialization.Targets(authorization.CoveredLines
                .Select(line => new OrderingEvidenceTarget(line.Id, line.Version, line.ContentDigest))),
            CoveredTargetsJson = PurchaseOrderSerialization.Targets(targets),
            State = (int)authorization.State,
            DocumentJson = authorization.CanonicalDocument(),
            ContentDigest = authorization.Digest,
            ActorUserId = authorization.AuthorizedByUserId,
            AuthorizedAt = authorization.AuthorizedAt,
            CancelledAt = authorization.State == DirectPurchaseState.Cancelled
                ? authorization.AuthorizedAt
                : null
        };
}
