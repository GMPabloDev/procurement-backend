using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Approval;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>Idempotent presentation command; the actor and organization come from the token (REQ-11).</summary>
public sealed record PurchaseRequestSubmissionCommand(
    Guid RequestId,
    int ExpectedVersion,
    string SubmissionKey,
    string Reason,
    Guid OrganizationId,
    Guid RequesterId,
    string CorrelationReference);

public sealed record PurchaseRequestSubmissionOutcome(
    Guid RequestId,
    int Version,
    PurchaseRequestStatus Status,
    PurchaseRequestSubmissionStatus AttemptStatus,
    Guid? PolicyEvaluationBundleId,
    Guid? ApprovalCaseId,
    bool Replayed);

/// <summary>
/// Closed supersession plan of one presented version: the delta covering both manifests and the
/// previous identities whose materiality proof the owner verified equal (REQ-08).
/// </summary>
public sealed record PurchaseRequestSupersessionPlan(
    IReadOnlyList<ApprovalSupersessionDeltaEntry> Entries,
    IReadOnlySet<string> VerifiedMaterialityIdentities);

/// <summary>A blocked Policy evaluation is a contract violation of the request, not a dependency (REQ-11).</summary>
public sealed class PurchaseRequestPolicyBlockedException(string message) : DomainException(message)
{
}

/// <summary>
/// Durable presentation orchestration (REQ-06, DEC-05): attestation, Policy evaluation by
/// reference and approval case opening or supersession advance one immutable
/// <c>PurchaseRequestSubmissionAttempt</c> under deterministic internal keys, so a retry after a
/// partial failure recovers the confirmed effects instead of duplicating them, and a failure never
/// declares success or leaves data half-written inside a module.
/// </summary>
public sealed class PurchaseRequestSubmissionService(
    ProcureToPayDbContext dbContext,
    PurchaseRequestPersistenceService persistence,
    PurchaseRequestAttestationService attestation,
    PolicyEvaluationService policyEvaluations,
    ApprovalSubmissionService approvalSubmissions,
    ApprovalSupersessionService approvalSupersessions,
    ILogger<PurchaseRequestSubmissionService> logger)
{
    /// <summary>Trusted in-process workload of the purchase request domain (REQ-05, REQ-06).</summary>
    public static readonly ApprovalWorkloadIdentity Workload =
        new("internal://procure-to-pay", PurchaseRequestCodes.ProviderId);

    public const string AdapterContractVersion = PolicyApprovalAdapter.ContractVersion;

    public async Task<PurchaseRequestSubmissionOutcome> SubmitAsync(
        PurchaseRequestSubmissionCommand command,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = PurchaseRequestLimits.RequireReason(command.Reason, "Submission reason");
        var submissionKey = PurchaseRequestLimits.RequireKey(command.SubmissionKey, "submission_key");
        var correlation = ApprovalLimits.RequireCorrelation(command.CorrelationReference);
        var utcNow = occurredAt.ToUniversalTime();
        if (command.OrganizationId == Guid.Empty || command.RequesterId == Guid.Empty)
        {
            throw new DomainValidationException("A submission requires the complete actor identity.");
        }

        var request = await dbContext.PurchaseRequests
            .SingleOrDefaultAsync(
                record => record.Id == command.RequestId && record.OrganizationId == command.OrganizationId,
                cancellationToken)
            ?? throw new DomainNotFoundException("The purchase request is not visible.");
        if (request.RequesterId != command.RequesterId)
        {
            throw new DomainForbiddenException("Only the requester may present this purchase request.");
        }

        if (request.CurrentVersion != command.ExpectedVersion)
        {
            throw new DomainConflictException(
                "The purchase request changed under the command; reload and retry.");
        }

        var status = (PurchaseRequestStatus)request.Status;
        if (status is PurchaseRequestStatus.Rejected or PurchaseRequestStatus.Cancelled)
        {
            throw new DomainConflictException("A terminal purchase request cannot be presented.");
        }

        // Owner attestation and the frozen manifest exist before anything is evaluated (REQ-04).
        var manifest = await attestation.EnsureAttestedAsync(
            request.Id, request.CurrentVersion, command.OrganizationId, utcNow, cancellationToken);
        var fingerprint = PurchaseRequestCanonicalizer.SubmissionFingerprint(
            request.Id,
            request.CurrentVersion,
            command.OrganizationId,
            command.RequesterId,
            manifest.RequestContentDigest,
            manifest.ReferenceAttestationDigest,
            manifest.PolicyManifestDigest,
            manifest.DomainAttestationDigest,
            submissionKey,
            reason);

        var attempt = await FindAttemptAsync(request.Id, request.CurrentVersion, cancellationToken);
        if (attempt is not null && !string.Equals(attempt.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new DomainConflictException("The submission key was already used with different content.");
        }

        if (attempt?.Status == (int)PurchaseRequestSubmissionStatus.ApprovalConfirmed)
        {
            return Replay(attempt, request.CurrentVersion, (PurchaseRequestStatus)request.Status);
        }

        if (attempt is null)
        {
            attempt = new PurchaseRequestSubmissionAttemptRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = command.OrganizationId,
                RequesterId = command.RequesterId,
                RequestId = request.Id,
                RequestVersion = request.CurrentVersion,
                SubmissionKey = submissionKey,
                Fingerprint = fingerprint,
                Status = (int)PurchaseRequestSubmissionStatus.Pending,
                ApprovalContractVersion = AdapterContractVersion,
                CreatedAt = utcNow,
                UpdatedAt = utcNow,
                AttemptCount = 0
            };
            dbContext.PurchaseRequestSubmissionAttempts.Add(attempt);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent identical presentation won the unique (request, version) row.
                dbContext.ChangeTracker.Clear();
                attempt = await FindAttemptAsync(request.Id, request.CurrentVersion, cancellationToken)
                    ?? throw new DomainConflictException(
                        "The purchase request version is already being presented by another command.");
                if (!string.Equals(attempt.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new DomainConflictException("The submission key was already used with different content.");
                }

                if (attempt.Status == (int)PurchaseRequestSubmissionStatus.ApprovalConfirmed)
                {
                    return Replay(attempt, request.CurrentVersion, (PurchaseRequestStatus)request.Status);
                }
            }
        }

        // (1) Policy evaluation by reference under a deterministic internal key (REQ-06).
        var evaluationKey = $"pr-submit-{request.Id:D}-v{request.CurrentVersion}-policy";
        PolicyEvaluationBundle bundle;
        try
        {
            bundle = await policyEvaluations.EvaluateEnterprisePurchaseRequestAsync(
                new PolicyFactRequest(
                    PurchaseRequestCodes.SubjectType,
                    request.Id,
                    request.CurrentVersion,
                    PurchaseRequestCodes.EvaluationOperation,
                    utcNow,
                    new PolicyWorkloadPrincipal(Workload.Issuer, Workload.ClientId),
                    correlation)
                {
                    OrganizationId = command.OrganizationId
                },
                evaluationKey,
                cancellationToken);
        }
        catch (Exception exception) when (exception is PolicyDependencyUnavailableException or
                                              PolicyConfigurationUnavailableException or PolicyPayloadTooLargeException)
        {
            await MarkFailureAsync(
                attempt, PurchaseRequestSubmissionStatus.DependencyFailed,
                PurchaseRequestSubmissionCodes.ErrorPolicyDependency, utcNow, cancellationToken);
            throw;
        }

        // (2) A blocked evaluation never creates a case and never projects success (REQ-06, REQ-11).
        if (bundle.Result == PolicyResult.Blocked)
        {
            await MarkFailureAsync(
                attempt, PurchaseRequestSubmissionStatus.Blocked,
                PurchaseRequestSubmissionCodes.ErrorPolicyBlocked, utcNow, cancellationToken);
            throw new PurchaseRequestPolicyBlockedException(
                "The active policy blocked the purchase request; it cannot be presented.");
        }

        var bundleRecord = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == bundle.Id, cancellationToken);
        attempt = await dbContext.PurchaseRequestSubmissionAttempts
            .SingleAsync(record => record.Id == attempt.Id, cancellationToken);
        attempt.PolicyEvaluationBundleId = bundle.Id;
        attempt.PolicyResultDigest = bundle.ResultDigest;
        attempt.PolicySetVersionId = bundleRecord?.PolicySetVersionId;
        attempt.Status = (int)PurchaseRequestSubmissionStatus.PolicyConfirmed;
        attempt.ErrorCode = null;
        attempt.UpdatedAt = utcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        // (3) Open, or supersede the previous case, through the trusted in-process adapter v2.
        var submissionKeyInternal = $"pr-submit-{request.Id:D}-v{request.CurrentVersion}";
        var previousCase = await FindPreviousCaseAsync(request.Id, command.OrganizationId, cancellationToken);
        Guid caseId;
        string caseStatus;
        try
        {
            if (previousCase is null || previousCase.SubjectVersion == request.CurrentVersion)
            {
                var outcome = await approvalSubmissions.SubmitAsync(
                    new ApprovalSubmissionCommand(
                        Workload,
                        command.OrganizationId,
                        PurchaseRequestCodes.SubjectType,
                        request.Id,
                        request.CurrentVersion,
                        PurchaseRequestCodes.SubmitOperation,
                        AdapterContractVersion,
                        submissionKeyInternal,
                        request.RequesterId,
                        request.RequesterId,
                        correlation),
                    utcNow,
                    cancellationToken);
                caseId = outcome.CaseId;
                caseStatus = outcome.Status;
            }
            else
            {
                var plan = await BuildSupersessionPlanAsync(
                    request.Id, command.OrganizationId, previousCase.SubjectVersion, bundle, cancellationToken);
                var outcome = await approvalSupersessions.SupersedeAsync(
                    new ApprovalSupersessionCommand(
                        Workload,
                        previousCase.Id,
                        previousCase.Version,
                        $"pr-supersede-{request.Id:D}-v{request.CurrentVersion}",
                        submissionKeyInternal,
                        AdapterContractVersion,
                        [],
                        correlation,
                        plan.Entries,
                        plan.VerifiedMaterialityIdentities,
                        NewSubjectVersion: request.CurrentVersion),
                    utcNow,
                    cancellationToken);
                caseId = outcome.CaseId;
                caseStatus = outcome.Status;
            }
        }
        catch (Exception exception) when (exception is ApprovalDependencyUnavailableException or
                                              PolicyDependencyUnavailableException)
        {
            await MarkFailureAsync(
                attempt, PurchaseRequestSubmissionStatus.PolicyConfirmed,
                PurchaseRequestSubmissionCodes.ErrorApprovalDependency, utcNow, cancellationToken);
            throw;
        }

        // (4) Durable confirmation plus the SUBMITTED → IN_APPROVAL|APPROVED projection (REQ-06).
        dbContext.ChangeTracker.Clear();
        var confirmed = await FindAttemptAsync(request.Id, request.CurrentVersion, cancellationToken)
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The submission attempt disappeared before confirmation.");
        var confirmedRequest = await dbContext.PurchaseRequests
            .SingleAsync(record => record.Id == request.Id, cancellationToken);
        confirmed.Status = (int)PurchaseRequestSubmissionStatus.ApprovalConfirmed;
        confirmed.ApprovalCaseId = caseId;
        confirmed.ApprovalContractVersion = AdapterContractVersion;
        confirmed.ErrorCode = null;
        confirmed.AttemptCount++;
        confirmed.UpdatedAt = utcNow;
        ProjectSubmission(confirmedRequest, caseStatus, command.RequesterId, correlation, utcNow);
        dbContext.PurchaseRequestCommands.Add(new PurchaseRequestCommandRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = command.OrganizationId,
            ActorUserId = command.RequesterId,
            CommandType = PurchaseRequestPersistenceService.CommandSubmit,
            CommandKey = submissionKey,
            Fingerprint = fingerprint,
            RequestId = request.Id,
            RequestVersion = request.CurrentVersion,
            CreatedAt = utcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Purchase request {RequestId} version {Version} was presented with policy bundle {BundleId} and case {CaseId}.",
            request.Id,
            request.CurrentVersion,
            bundle.Id,
            caseId);
        return new PurchaseRequestSubmissionOutcome(
            request.Id,
            request.CurrentVersion,
            (PurchaseRequestStatus)confirmedRequest.Status,
            PurchaseRequestSubmissionStatus.ApprovalConfirmed,
            bundle.Id,
            caseId,
            Replayed: false);
    }

    /// <summary>
    /// Delta covering the previous and the new manifest by stable line id (REQ-08): retained pairs
    /// carry the replacement materiality proof, and only pairs whose previous proof the owner
    /// verified equal enter the carry-forward set. Anything else keeps a new task.
    /// </summary>
    public async Task<PurchaseRequestSupersessionPlan> BuildSupersessionPlanAsync(
        Guid requestId,
        Guid organizationId,
        int previousVersion,
        PolicyEvaluationBundle replacement,
        CancellationToken cancellationToken = default)
    {
        var previousRecord = await dbContext.PolicyEvaluationBundles
            .AsNoTracking()
            .Where(record => record.OrganizationId == organizationId &&
                             record.SubjectId == requestId &&
                             record.SubjectVersion == previousVersion &&
                             record.Operation == PurchaseRequestCodes.EvaluationOperation)
            .OrderByDescending(record => record.EvaluationSequence)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The previous policy evaluation of the purchase request version is unavailable.");
        PolicyEvaluationBundle previous;
        try
        {
            previous = PolicyEvaluationBundleRehydrator.FromJson(previousRecord.BundleJson);
        }
        catch (Exception exception) when (exception is not DomainException)
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "The previous policy evaluation of the purchase request version is corrupted.");
        }

        var previousProjection = previous.MaterialProjection
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The previous evaluation has no material target projection.");
        var replacementProjection = replacement.MaterialProjection
            ?? throw new PurchaseRequestDependencyUnavailableException(
                "The presented evaluation has no material target projection.");
        if (string.IsNullOrWhiteSpace(previous.RequestSnapshotJson) ||
            string.IsNullOrWhiteSpace(replacement.RequestSnapshotJson))
        {
            throw new PurchaseRequestDependencyUnavailableException(
                "A materiality proof requires both persisted policy snapshots.");
        }

        var previousById = previousProjection.Targets.ToDictionary(target => target.Id);
        var replacementById = replacementProjection.Targets.ToDictionary(target => target.Id);
        var entries = new List<ApprovalSupersessionDeltaEntry>(replacementProjection.Targets.Count);
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in replacementProjection.Targets.OrderBy(target => target.Id))
        {
            var replacementTarget = ApprovalTarget(target);
            if (previousById.TryGetValue(target.Id, out var prior))
            {
                var previousTarget = ApprovalTarget(prior);
                // The published preimage keeps its exact provenance, but the carry-forward proof
                // compares material facts across immutable versions: request/line version segments
                // are not material and are normalized before the comparison (REQ-08, DEC-07).
                var previousDigest = MaterialityProof(prior.Id, previous.RequestSnapshotJson,
                    previousProjection, versionless: true);
                var replacementDigest = MaterialityProof(target.Id, replacement.RequestSnapshotJson,
                    replacementProjection, versionless: true);
                entries.Add(ApprovalSupersessionDeltaEntry.CreateRetained(
                    previousTarget,
                    replacementTarget,
                    PurchaseRequestCodes.MaterialityVersion,
                    replacementDigest));
                if (string.Equals(previousDigest, replacementDigest, StringComparison.Ordinal))
                {
                    verified.Add(previousTarget.CanonicalIdentity);
                }
            }
            else
            {
                entries.Add(ApprovalSupersessionDeltaEntry.CreateAdded(
                    replacementTarget,
                    PurchaseRequestCodes.MaterialityVersion,
                    MaterialityProof(target.Id, replacement.RequestSnapshotJson, replacementProjection, versionless: true)));
            }
        }

        foreach (var prior in previousProjection.Targets
                     .Where(target => !replacementById.ContainsKey(target.Id))
                     .OrderBy(target => target.Id))
        {
            entries.Add(ApprovalSupersessionDeltaEntry.CreateRemoved(
                ApprovalTarget(prior),
                PurchaseRequestCodes.MaterialityVersion,
                MaterialityProof(prior.Id, previous.RequestSnapshotJson, previousProjection, versionless: true)));
        }

        ApprovalSupersessionDeltaRules.Validate(entries);
        return new PurchaseRequestSupersessionPlan(entries, verified);
    }

    /// <summary>
    /// The bundle provenance keeps the line identity in the key and the snapshot versions in the
    /// value; the published materiality preimage uses the flat fact key of each line, and the
    /// carry-forward comparison drops the immutable version segments of the provenance reference
    /// (REQ-08, DEC-07).
    /// </summary>
    private static string MaterialityProof(
        Guid lineId,
        string canonicalRequestJson,
        PolicyMaterialProjection projection,
        bool versionless)
    {
        var provenance = FlatLineProvenance(projection, lineId);
        if (!versionless)
        {
            return PurchaseRequestPolicyProjection.MaterialityDigest(canonicalRequestJson, lineId, provenance);
        }

        var normalized = provenance.ToDictionary(
            pair => pair.Key,
            pair => NormalizedProvenanceReference(pair.Value),
            StringComparer.Ordinal);
        return PurchaseRequestPolicyProjection.MaterialityDigest(canonicalRequestJson, lineId, normalized);
    }

    /// <summary>
    /// The published preimage keeps the exact provenance reference; the carry-forward proof only
    /// needs the fact anchor, because request/line versions, attestation digests and manifests are
    /// snapshots, not material facts (REQ-08, DEC-07).
    /// </summary>
    private static string NormalizedProvenanceReference(string value)
    {
        var anchor = value.LastIndexOf('#');
        return anchor >= 0 ? value[anchor..] : value;
    }
    private static IReadOnlyDictionary<string, string> FlatLineProvenance(
        PolicyMaterialProjection projection,
        Guid lineId)
    {
        var prefix = $"{lineId:D}:";
        var keyed = projection.Provenance
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        return keyed.Length > 0
            ? keyed.ToDictionary(
                pair => pair.Key[prefix.Length..], pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(projection.Provenance, StringComparer.Ordinal);
    }

    private static ApprovalTarget ApprovalTarget(PolicyMaterialTarget target) =>
        new(PolicyApprovalTargets.TargetType, target.Id, target.Version, target.MaterialSnapshotDigest);

    private Task<PurchaseRequestSubmissionAttemptRecord?> FindAttemptAsync(
        Guid requestId,
        int requestVersion,
        CancellationToken cancellationToken) =>
        dbContext.PurchaseRequestSubmissionAttempts
            .SingleOrDefaultAsync(
                record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                cancellationToken);

    private Task<ApprovalCaseRecord?> FindPreviousCaseAsync(
        Guid requestId,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        dbContext.ApprovalCases
            .AsNoTracking()
            .Where(record =>
                record.OrganizationId == organizationId &&
                record.SubjectType == PurchaseRequestCodes.SubjectType &&
                record.SubjectId == requestId &&
                record.Operation == PurchaseRequestCodes.SubmitOperation &&
                record.Status != (int)ApprovalCaseStatus.Cancelled)
            .OrderByDescending(record => record.SubjectVersion)
            .ThenByDescending(record => record.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task MarkFailureAsync(
        PurchaseRequestSubmissionAttemptRecord attempt,
        PurchaseRequestSubmissionStatus status,
        string errorCode,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var current = await dbContext.PurchaseRequestSubmissionAttempts
            .SingleOrDefaultAsync(record => record.Id == attempt.Id, cancellationToken);
        if (current is null)
        {
            return;
        }

        current.Status = (int)status;
        current.ErrorCode = errorCode;
        current.AttemptCount++;
        current.UpdatedAt = occurredAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The request only projects SUBMITTED once Policy and Approval references are confirmed, and
    /// then IN_APPROVAL, or APPROVED when the new case already completed (REQ-06).
    /// </summary>
    private void ProjectSubmission(
        PurchaseRequestRecord request,
        string caseStatus,
        Guid requesterId,
        string correlation,
        DateTimeOffset occurredAt)
    {
        var before = request.Status;
        AppendLifecycle(
            request, PurchaseRequestSubmissionCodes.ActionSubmitted, before,
            (int)PurchaseRequestStatus.Submitted, requesterId, null, null,
            PurchaseRequestSubmissionCodes.ReasonCodeSubmit, correlation, occurredAt);
        var projected = string.Equals(
            caseStatus, ApprovalCaseStatus.Completed.ToString().ToUpperInvariant(), StringComparison.Ordinal)
            ? PurchaseRequestStatus.Approved
            : PurchaseRequestStatus.InApproval;
        // The second projection transition is one tick later so the append-only history is
        // deterministically ordered by its own instant, without depending on row identity.
        AppendLifecycle(
            request, projected == PurchaseRequestStatus.Approved
                ? PurchaseRequestSubmissionCodes.ActionApproved
                : PurchaseRequestSubmissionCodes.ActionInApproval,
            (int)PurchaseRequestStatus.Submitted,
            (int)projected,
            null,
            Workload.Issuer,
            Workload.ClientId,
            PurchaseRequestSubmissionCodes.ReasonCodeSubmit,
            correlation,
            occurredAt.AddTicks(1));
        request.Status = (int)projected;
        request.UpdatedAt = occurredAt;
    }

    private void AppendLifecycle(
        PurchaseRequestRecord request,
        string action,
        int beforeStatus,
        int afterStatus,
        Guid? actorUserId,
        string? workloadIssuer,
        string? workloadClientId,
        string reasonCode,
        string correlation,
        DateTimeOffset occurredAt) =>
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            RequestId = request.Id,
            RequestVersion = request.CurrentVersion,
            Action = action,
            BeforeStatus = beforeStatus,
            AfterStatus = afterStatus,
            ActorUserId = actorUserId,
            WorkloadIssuer = workloadIssuer,
            WorkloadClientId = workloadClientId,
            ReasonCode = reasonCode,
            Reason = "Purchase request presented",
            CorrelationReference = correlation,
            OccurredAt = occurredAt
        });

    private static PurchaseRequestSubmissionOutcome Replay(
        PurchaseRequestSubmissionAttemptRecord attempt,
        int version,
        PurchaseRequestStatus status) =>
        new(
            attempt.RequestId,
            version,
            status,
            (PurchaseRequestSubmissionStatus)attempt.Status,
            attempt.PolicyEvaluationBundleId,
            attempt.ApprovalCaseId,
            Replayed: true);
}
