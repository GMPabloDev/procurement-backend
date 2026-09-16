using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>Outcome of one governed save: what was appended and whether approval is needed (REQ-02).</summary>
public sealed record SupplierSaveOutcome(
    SupplierVersionView Version,
    string ProjectedStatus,
    Guid? ProposalId,
    IReadOnlySet<string> SensitiveFields,
    bool RequiresApproval);

/// <summary>Outcome of one submission: the durable proposal and its approval case (REQ-03).</summary>
public sealed record SupplierSubmitOutcome(Guid ProposalId, int CandidateVersion, Guid CaseId, bool Replayed);

/// <summary>
/// Governed lifecycle of the Supplier Master (SPEC 09 REQ-02, REQ-03). Every write runs inside one
/// transaction on the supplier lock: the classification is computed server-side, a sensitive change
/// only materializes after an approved human decision with segregation of duties, and a cosmetic
/// change appends a successor version without an approval case.
/// </summary>
public sealed class SupplierGovernanceService(
    ProcureToPayDbContext dbContext,
    SupplierPersistenceService persistence,
    ApprovalSubmissionService approvalSubmissions,
    IConfiguration configuration,
    ILogger<SupplierGovernanceService> logger)
{
    /// <summary>Trusted in-process workload of the supplier domain (REQ-03).</summary>
    public static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", SupplierCodes.SupplierOwnerId);

    public const string RequirementKey = "SUPPLIER_MASTER_APPROVAL";
    public const string RequirementStage = "PROCUREMENT";

    /// <summary>Minimum SUPPLIER_MASTER rank accepted as approver; configurable, lowest level by default.</summary>
    public int MinimumAuthorityRank =>
        int.TryParse(configuration["Supplier:Governance:MinimumSupplierMasterRank"], out var rank) && rank >= 1
            ? rank
            : 1;

    /// <summary>Creates the root, its reserved fiscal identity, the first draft and its proposal.</summary>
    public async Task<SupplierSaveOutcome> CreateAsync(
        Guid organizationId,
        Guid actorUserId,
        SupplierVersionContent content,
        SupplierOperationalStatus requestedStatus,
        string reason,
        string changeKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedReason = SupplierCodes.Reason(reason);
        _ = SupplierCodes.Key(changeKey, "Change key");
        if (!SupplierChangeClassifier.IsAllowedTransition(null, requestedStatus))
        {
            throw new DomainValidationException(
                "A new supplier can only be proposed for activation.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var supplierId = Guid.NewGuid();
        await SupplierPersistenceService.AcquireSupplierLockAsync(
            dbContext, organizationId, supplierId, cancellationToken);
        var identityKey = SupplierCanonicalizer.IdentityKeyDigest(
            content.CountryCode, organizationId, content.TaxId);
        await EnsureIdentityIsFreeAsync(organizationId, identityKey, excluding: null, cancellationToken);

        var occurredAt = DateTimeOffset.UtcNow;
        var identity = new SupplierFiscalIdentityRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            SupplierId = supplierId,
            CountryCode = content.CountryCode,
            TaxIdKey = content.TaxId,
            TaxId = content.TaxId,
            IdentityKeyDigest = identityKey
        };
        var root = new SupplierRecord
        {
            Id = supplierId,
            OrganizationId = organizationId,
            FiscalIdentityId = identity.Id,
            OperationalVersion = null,
            WorkingProposalId = null
        };
        dbContext.Suppliers.Add(root);
        dbContext.SupplierFiscalIdentities.Add(identity);

        var candidate = content.WithStatus(SupplierOperationalStatus.Draft);
        var version = persistence.AppendVersion(
            organizationId, supplierId, 1, null, candidate, proposalId: null,
            SupplierApprovalTargets.ChangeKindCode(SupplierChangeKind.Create), changeKey, actorUserId,
            normalizedReason, occurredAt, correlationReference);
        var sensitive = SupplierChangeClassifier.SensitiveChanges(null, content);
        var proposal = new SupplierChangeProposalRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            SupplierId = supplierId,
            BaseVersion = null,
            CandidateVersion = 1,
            ChangeKind = (int)SupplierChangeKind.Create,
            SensitiveFieldsJson = SupplierPersistenceService.WriteSensitiveFields(sensitive),
            RequestedStatus = (int)requestedStatus,
            EditorUserId = actorUserId,
            State = (int)SupplierProposalState.Draft,
            ChangeKey = changeKey,
            Fingerprint = SupplierCanonicalizer.ChangeFingerprint(
                supplierId, organizationId, actorUserId, null, null,
                SupplierApprovalTargets.ChangeKindCode(SupplierChangeKind.Create),
                SupplierStatusCodes.Code(requestedStatus), changeKey,
                SupplierCanonicalizer.ContentDigest(supplierId, 1, candidate), normalizedReason),
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt
        };
        dbContext.SupplierChangeProposals.Add(proposal);
        version.ProposalId = proposal.Id;
        root.WorkingProposalId = proposal.Id;
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionCreated, Actor(actorUserId), null,
            SupplierChangeClassifier.ChangedFields(null, candidate), correlationReference,
            $"{changeKey}:create", Target(SupplierCodes.TargetSupplier, supplierId, 1), occurredAt);
        await persistence.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Supplier {SupplierId} drafted by {Actor}.", supplierId, actorUserId);

        return new SupplierSaveOutcome(
            SupplierSerialization.ReadView(version),
            SupplierStatusCodes.Code(SupplierOperationalStatus.Draft),
            proposal.Id,
            sensitive,
            RequiresApproval: true);
    }

    /// <summary>
    /// Appends one successor of the working draft or applies a cosmetic change directly. The
    /// classification comes from the persisted diff, never from the caller (REQ-02).
    /// </summary>
    public async Task<SupplierSaveOutcome> SaveAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid supplierId,
        int expectedVersion,
        SupplierVersionContent content,
        SupplierOperationalStatus? requestedStatus,
        string reason,
        string changeKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedReason = SupplierCodes.Reason(reason);
        _ = SupplierCodes.Key(changeKey, "Change key");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await SupplierPersistenceService.AcquireSupplierLockAsync(
            dbContext, organizationId, supplierId, cancellationToken);
        var root = await RequireRootAsync(organizationId, supplierId, cancellationToken);
        var latest = await LatestVersionAsync(supplierId, cancellationToken);
        if (latest != expectedVersion)
        {
            throw new DomainConflictException("The supplier was modified by another command.");
        }

        var operational = root.OperationalVersion is null
            ? null
            : SupplierSerialization.ReadView(
                await RequireVersionRowAsync(supplierId, root.OperationalVersion.Value, cancellationToken));
        var openProposal = await dbContext.SupplierChangeProposals
            .SingleOrDefaultAsync(
                row => row.SupplierId == supplierId &&
                       (row.State == (int)SupplierProposalState.Draft ||
                        row.State == (int)SupplierProposalState.Pending),
                cancellationToken);
        if (openProposal is not null && openProposal.State == (int)SupplierProposalState.Pending)
        {
            throw new DomainConflictException(
                "A pending proposal must be decided before the supplier can be edited again.");
        }

        var baseContent = operational?.Content;
        var effectiveStatus = requestedStatus ?? baseContent?.Status ?? SupplierOperationalStatus.Active;
        if (!SupplierChangeClassifier.IsAllowedTransition(baseContent?.Status, effectiveStatus))
        {
            throw new DomainValidationException("The requested supplier status transition is not allowed.");
        }

        var occurredAt = DateTimeOffset.UtcNow;
        // The candidate is classified with the status it would materialize, so a cosmetic edit of
        // an approved supplier is never mistaken for a governed status change (REQ-02).
        var sensitive = SupplierChangeClassifier.SensitiveChanges(
            baseContent, content.WithStatus(effectiveStatus));
        var requiresApproval = sensitive.Count > 0;
        if (!requiresApproval)
        {
            // Cosmetic change of an approved supplier: the successor keeps the operational status,
            // moves the pointer and leaves a change-keyed audit trail (REQ-02).
            var applied = content.WithStatus(effectiveStatus);
            var version = persistence.AppendVersion(
                organizationId, supplierId, latest + 1, latest, applied, proposalId: null,
                SupplierApprovalTargets.ChangeKindCode(SupplierChangeKind.NonSensitiveUpdate), changeKey,
                actorUserId, normalizedReason, occurredAt, correlationReference);
            // The append-only row is persisted before the pointer moves: the pointer trigger of the
            // root validates that the successor exists, so both steps share one ordered transaction.
            await persistence.SaveAsync(cancellationToken);
            root.OperationalVersion = latest + 1;
            await RecordIdentityAsync(root, applied, organizationId, latest + 1, cancellationToken);
            persistence.AddAudit(
                organizationId, SupplierCodes.ActionUpdated, Actor(actorUserId), null,
                SupplierChangeClassifier.ChangedFields(baseContent, applied), correlationReference,
                $"{changeKey}:apply", Target(SupplierCodes.TargetSupplier, supplierId, version.Version), occurredAt);
            await persistence.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Supplier {SupplierId} version {Version} applied without approval.", supplierId, version.Version);
            return new SupplierSaveOutcome(
                SupplierSerialization.ReadView(version),
                SupplierStatusCodes.Code(effectiveStatus),
                null,
                sensitive,
                RequiresApproval: false);
        }

        var identityKey = SupplierCanonicalizer.IdentityKeyDigest(content.CountryCode, organizationId, content.TaxId);
        await EnsureIdentityIsFreeAsync(organizationId, identityKey, root.FiscalIdentityId, cancellationToken);
        var candidate = content.WithStatus(SupplierOperationalStatus.Draft);
        var candidateVersion = latest + 1;
        var proposalId = openProposal?.Id ?? Guid.NewGuid();
        var changeKind = baseContent is null
            ? SupplierChangeKind.Create
            : effectiveStatus != baseContent.Status
                ? SupplierChangeKind.StatusChange
                : SupplierChangeKind.SensitiveUpdate;
        var record = persistence.AppendVersion(
            organizationId, supplierId, candidateVersion, latest, candidate, proposalId,
            SupplierApprovalTargets.ChangeKindCode(changeKind), changeKey, actorUserId,
            normalizedReason, occurredAt, correlationReference);
        if (openProposal is null)
        {
            dbContext.SupplierChangeProposals.Add(new SupplierChangeProposalRecord
            {
                Id = proposalId,
                OrganizationId = organizationId,
                SupplierId = supplierId,
                BaseVersion = root.OperationalVersion,
                CandidateVersion = candidateVersion,
                ChangeKind = (int)changeKind,
                SensitiveFieldsJson = SupplierPersistenceService.WriteSensitiveFields(sensitive),
                RequestedStatus = (int)effectiveStatus,
                EditorUserId = actorUserId,
                State = (int)SupplierProposalState.Draft,
                ChangeKey = changeKey,
                Fingerprint = SupplierCanonicalizer.ChangeFingerprint(
                    supplierId, organizationId, actorUserId, root.OperationalVersion, latest,
                    SupplierApprovalTargets.ChangeKindCode(changeKind), SupplierStatusCodes.Code(effectiveStatus),
                    changeKey, SupplierCanonicalizer.ContentDigest(supplierId, candidateVersion, candidate),
                    normalizedReason),
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt
            });
            root.WorkingProposalId = proposalId;
        }
        else
        {
            openProposal.CandidateVersion = candidateVersion;
            openProposal.ChangeKind = (int)changeKind;
            openProposal.SensitiveFieldsJson = SupplierPersistenceService.WriteSensitiveFields(sensitive);
            openProposal.RequestedStatus = (int)effectiveStatus;
            openProposal.ChangeKey = changeKey;
            openProposal.Fingerprint = SupplierCanonicalizer.ChangeFingerprint(
                supplierId, organizationId, actorUserId, root.OperationalVersion, latest,
                SupplierApprovalTargets.ChangeKindCode(changeKind), SupplierStatusCodes.Code(effectiveStatus),
                changeKey, SupplierCanonicalizer.ContentDigest(supplierId, candidateVersion, candidate),
                normalizedReason);
            openProposal.UpdatedAt = occurredAt;
        }

        persistence.AddAudit(
            organizationId, SupplierCodes.ActionUpdated, Actor(actorUserId), null,
            SupplierChangeClassifier.ChangedFields(baseContent, candidate), correlationReference,
            $"{changeKey}:candidate", Target(SupplierCodes.TargetSupplier, supplierId, candidateVersion), occurredAt);
        await persistence.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SupplierSaveOutcome(
            SupplierSerialization.ReadView(record),
            SupplierStatusCodes.Code(SupplierOperationalStatus.Draft),
            proposalId,
            sensitive,
            RequiresApproval: true);
    }

    /// <summary>
    /// Freezes the candidate and opens (or recovers) the approval case with the single
    /// PROCUREMENT_APPROVER + SUPPLIER_MASTER requirement that excludes the editor (REQ-03).
    /// </summary>
    public async Task<SupplierSubmitOutcome> SubmitAsync(
        Guid organizationId,
        Guid actorUserId,
        Guid supplierId,
        int candidateVersion,
        string reason,
        string submissionKey,
        string correlationReference,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = SupplierCodes.Reason(reason);
        _ = SupplierCodes.Key(submissionKey, "Submission key");
        var occurredAt = DateTimeOffset.UtcNow;
        Guid proposalId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await SupplierPersistenceService.AcquireSupplierLockAsync(
                dbContext, organizationId, supplierId, cancellationToken);
            var proposal = await dbContext.SupplierChangeProposals
                .SingleOrDefaultAsync(
                    row => row.OrganizationId == organizationId &&
                           row.SupplierId == supplierId &&
                           row.CandidateVersion == candidateVersion &&
                           (row.State == (int)SupplierProposalState.Draft ||
                            row.State == (int)SupplierProposalState.Pending),
                    cancellationToken)
                ?? throw new DomainNotFoundException("The supplier change proposal does not exist.");
            if (proposal.State == (int)SupplierProposalState.Pending && proposal.ApprovalCaseId is not null)
            {
                return new SupplierSubmitOutcome(
                    proposal.Id, candidateVersion, proposal.ApprovalCaseId.Value, Replayed: true);
            }

            if (proposal.EditorUserId != actorUserId)
            {
                throw new DomainForbiddenException("Only the editor of the proposal can submit it.");
            }

            if (proposal.State == (int)SupplierProposalState.Pending &&
                proposal.SubmissionKey is not null &&
                !string.Equals(proposal.SubmissionKey, submissionKey, StringComparison.Ordinal))
            {
                throw new DomainConflictException(
                    "A submission of this proposal is already in flight with another key.");
            }

            // The attempt is frozen before the case exists, so a crash between both steps is
            // recovered by a retry with the same key instead of creating a second case (REQ-03).
            proposal.State = (int)SupplierProposalState.Pending;
            proposal.SubmissionKey = submissionKey;
            proposal.RequirementKey = RequirementKey;
            proposal.CaseContractVersion = SupplierCodes.GovernanceAdapterVersion;
            proposal.UpdatedAt = occurredAt;
            persistence.AddAudit(
                organizationId, SupplierCodes.ActionSubmitted, Actor(actorUserId), null,
                SupplierPersistenceService.ReadSensitiveFields(proposal.SensitiveFieldsJson),
                correlationReference, $"{submissionKey}:submit",
                Target(SupplierCodes.TargetSupplier, supplierId, candidateVersion), occurredAt);
            await persistence.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            proposalId = proposal.Id;
        }

        var outcome = await approvalSubmissions.SubmitAsync(
            new ApprovalSubmissionCommand(
                Workload,
                organizationId,
                SupplierCodes.ApprovalSubjectType,
                supplierId,
                candidateVersion,
                SupplierCodes.SupplierApprovalOperation,
                SupplierCodes.GovernanceAdapterVersion,
                submissionKey,
                actorUserId,
                actorUserId,
                correlationReference),
            occurredAt,
            cancellationToken);
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var proposal = await dbContext.SupplierChangeProposals
                .SingleAsync(row => row.Id == proposalId, cancellationToken);
            proposal.ApprovalCaseId = outcome.CaseId;
            proposal.UpdatedAt = DateTimeOffset.UtcNow;
            await persistence.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Supplier {SupplierId} version {Version} submitted to approval as case {CaseId}.",
            supplierId, candidateVersion, outcome.CaseId);
        return new SupplierSubmitOutcome(proposalId, candidateVersion, outcome.CaseId, outcome.Replayed);
    }

    /// <summary>
    /// Applies one verified terminal approval decision (REQ-03). An approval materializes the
    /// requested status in a new append-only version, moves the approved pointer and publishes the
    /// status event atomically; any other terminal result only closes the proposal.
    /// </summary>
    public async Task ApplyApprovalAsync(
        Guid organizationId,
        Guid supplierId,
        int candidateVersion,
        Guid proposalId,
        string result,
        Guid? decisionActorUserId,
        string? requirementKey,
        string causeJson,
        string effectKey,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var proposal = await dbContext.SupplierChangeProposals
            .SingleOrDefaultAsync(
                row => row.Id == proposalId && row.OrganizationId == organizationId &&
                       row.SupplierId == supplierId,
                cancellationToken)
            ?? throw new SupplierDependencyUnavailableException("The supplier proposal does not exist.");
        if (proposal.CandidateVersion != candidateVersion)
        {
            throw new DomainConflictException("The approval result does not match the proposal candidate.");
        }

        if (proposal.State is (int)SupplierProposalState.Approved or (int)SupplierProposalState.Rejected or
            (int)SupplierProposalState.ChangesRequested or (int)SupplierProposalState.Cancelled)
        {
            return;
        }

        if (!string.Equals(result, "APPROVED", StringComparison.Ordinal))
        {
            proposal.State = result switch
            {
                "REJECTED" or "CANCELLED" => (int)SupplierProposalState.Rejected,
                "CHANGES_REQUESTED" => (int)SupplierProposalState.ChangesRequested,
                "SUPERSEDED" => (int)SupplierProposalState.Cancelled,
                _ => (int)SupplierProposalState.Rejected
            };
            proposal.UpdatedAt = occurredAt;
            persistence.AddAudit(
                organizationId,
                result switch
                {
                    "CHANGES_REQUESTED" => SupplierCodes.ActionChangesRequested,
                    _ => SupplierCodes.ActionRejected
                },
                Actor(decisionActorUserId ?? Guid.Empty),
                causeJson,
                SupplierPersistenceService.ReadSensitiveFields(proposal.SensitiveFieldsJson),
                "approval",
                effectKey,
                Target(SupplierCodes.TargetSupplier, supplierId, candidateVersion),
                occurredAt);
            await persistence.SaveAsync(cancellationToken);
            logger.LogInformation(
                "Supplier {SupplierId} proposal {ProposalId} closed as {Result}.",
                supplierId, proposalId, result);
            return;
        }

        var root = await RequireRootAsync(organizationId, supplierId, cancellationToken);
        var candidate = await RequireVersionRowAsync(supplierId, candidateVersion, cancellationToken);
        var requested = (SupplierOperationalStatus)(proposal.RequestedStatus
            ?? throw new SupplierDependencyUnavailableException("The proposal has no requested status."));
        var materialized = SupplierSerialization.ReadContent(candidate).WithStatus(requested);
        var latest = await LatestVersionAsync(supplierId, cancellationToken);
        var approved = persistence.AppendVersion(
            organizationId, supplierId, latest + 1, candidateVersion, materialized, proposalId: null,
            SupplierApprovalTargets.ChangeKindCode(SupplierChangeKind.StatusChange), proposal.ChangeKey,
            decisionActorUserId ?? Guid.Empty, candidate.Reason, occurredAt, "approval");
        approved.ApprovalCaseId = proposal.ApprovalCaseId;
        // The materialized version must exist before the approved pointer references it.
        await persistence.SaveAsync(cancellationToken);
        var previousStatus = (SupplierOperationalStatus?)null;
        if (root.OperationalVersion is not null)
        {
            previousStatus = (SupplierOperationalStatus)(
                await RequireVersionRowAsync(supplierId, root.OperationalVersion.Value, cancellationToken)).Status;
        }
        root.OperationalVersion = latest + 1;
        root.WorkingProposalId = null;
        proposal.State = (int)SupplierProposalState.Approved;
        proposal.UpdatedAt = occurredAt;
        await RecordIdentityAsync(root, materialized, organizationId, latest + 1, cancellationToken);
        dbContext.SupplierStatusOutbox.Add(new SupplierStatusOutboxRecord
        {
            EventId = Guid.NewGuid(),
            OrganizationId = organizationId,
            SupplierId = supplierId,
            SupplierVersion = latest + 1,
            PreviousStatus = previousStatus is null ? null : (int)previousStatus.Value,
            Status = (int)requested,
            OccurredAt = occurredAt,
            PayloadJson = new SupplierStatusChangedEvent(
                SupplierCodes.SupplierStatusChangedContract,
                Guid.NewGuid(),
                occurredAt,
                organizationId,
                previousStatus,
                supplierId,
                latest + 1,
                requested).CanonicalJson()
        });
        persistence.AddAudit(
            organizationId, SupplierCodes.ActionApproved, Actor(decisionActorUserId ?? Guid.Empty), causeJson,
            SupplierPersistenceService.ReadSensitiveFields(proposal.SensitiveFieldsJson), "approval",
            effectKey, Target(SupplierCodes.TargetSupplier, supplierId, latest + 1), occurredAt);
        await persistence.SaveAsync(cancellationToken);
        logger.LogInformation(
            "Supplier {SupplierId} version {Version} materialized as {Status}.",
            supplierId, latest + 1, SupplierStatusCodes.Code(requested));
    }

    /// <summary>Projected operational awareness of one supplier, including an open pending approval.</summary>
    public async Task<SupplierProjection> ReadProjectionAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var root = await dbContext.Suppliers
            .AsNoTracking()
            .SingleOrDefaultAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The supplier does not exist in this organization.");
        var open = await persistence.FindOpenProposalAsync(organizationId, supplierId, cancellationToken);
        SupplierVersionView? operational = root.OperationalVersion is null
            ? null
            : await persistence.LoadVersionAsync(supplierId, root.OperationalVersion.Value, cancellationToken);
        var state = open?.State == SupplierProposalState.Pending
            ? SupplierOperationalStatus.PendingApproval
            : operational?.Content.Status ?? SupplierOperationalStatus.Draft;
        return new SupplierProjection(supplierId, operational, open, SupplierStatusCodes.Code(state));
    }

    private async Task<SupplierRecord> RequireRootAsync(
        Guid organizationId,
        Guid supplierId,
        CancellationToken cancellationToken) =>
        await dbContext.Suppliers
            .Include(supplier => supplier.FiscalIdentity)
            .SingleOrDefaultAsync(
                supplier => supplier.OrganizationId == organizationId && supplier.Id == supplierId,
                cancellationToken)
        ?? throw new DomainNotFoundException("The supplier does not exist in this organization.");

    private Task<int> LatestVersionAsync(Guid supplierId, CancellationToken cancellationToken) =>
        dbContext.SupplierVersions
            .Where(row => row.SupplierId == supplierId)
            .MaxAsync(row => (int?)row.Version, cancellationToken)
            .ContinueWith(task => task.Result ?? 1, cancellationToken);

    private async Task<SupplierVersionRecord> RequireVersionRowAsync(
        Guid supplierId,
        int version,
        CancellationToken cancellationToken) =>
        await dbContext.SupplierVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.SupplierId == supplierId && row.Version == version,
                cancellationToken)
        ?? throw new SupplierDependencyUnavailableException("The supplier version does not exist.");

    /// <summary>
    /// Reserves the fiscal identity of an approved supplier: the pair (country, tax id) is unique per
    /// organization and an approved identity is never reassigned to another root (REQ-01).
    /// </summary>
    private async Task RecordIdentityAsync(
        SupplierRecord root,
        SupplierVersionContent content,
        Guid organizationId,
        int approvedVersion,
        CancellationToken cancellationToken)
    {
        var identityKey = SupplierCanonicalizer.IdentityKeyDigest(
            content.CountryCode, organizationId, content.TaxId);
        await EnsureIdentityIsFreeAsync(organizationId, identityKey, root.FiscalIdentityId, cancellationToken);
        var identity = root.FiscalIdentity;
        identity.CountryCode = content.CountryCode;
        identity.TaxIdKey = content.TaxId;
        identity.TaxId = content.TaxId;
        identity.IdentityKeyDigest = identityKey;
        identity.FirstApprovedVersion ??= approvedVersion;
        identity.LastApprovedVersion = approvedVersion;
    }

    private async Task EnsureIdentityIsFreeAsync(
        Guid organizationId,
        string identityKey,
        Guid? excluding,
        CancellationToken cancellationToken)
    {
        var conflict = await dbContext.SupplierFiscalIdentities
            .AsNoTracking()
            .AnyAsync(
                identity => identity.OrganizationId == organizationId &&
                            identity.IdentityKeyDigest == identityKey &&
                            identity.Id != (excluding ?? Guid.Empty),
                cancellationToken);
        if (conflict)
        {
            throw new DomainConflictException(
                "Another supplier already reserves that Country and Tax ID.");
        }
    }

    private static string Actor(Guid userId) =>
        $"{{\"system_id\":null,\"type\":\"USER\",\"user_id\":\"{userId:D}\",\"workload_client_id\":null,\"workload_issuer\":null}}";

    private static string Target(string type, Guid id, int version) =>
        $"{{\"id\":\"{id:D}\",\"type\":\"{type}\",\"version\":{version}}}";
}

/// <summary>Operational projection of one supplier: approved pointer, open proposal and status (REQ-11).</summary>
public sealed record SupplierProjection(
    Guid SupplierId,
    SupplierVersionView? Operational,
    SupplierProposalView? OpenProposal,
    string Status);
