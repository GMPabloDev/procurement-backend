using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

/// <summary>
/// Owner attestation and completeness manifest of one presented request version (REQ-04):
/// exactly one owner answers every reference, the answers are frozen, and the manifest is written
/// before the Policy provider can serve the version.
/// </summary>
public sealed class PurchaseRequestAttestationService(
    ProcureToPayDbContext dbContext,
    IPurchaseRequestReferenceOwnerRegistry owners,
    PurchaseRequestPersistenceService persistence,
    ILogger<PurchaseRequestAttestationService> logger)
{
    public static readonly TimeSpan OwnerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Idempotent: an attested version keeps its frozen manifest and digest.</summary>
    public async Task<PurchaseRequestCompletenessManifest> EnsureAttestedAsync(
        Guid requestId,
        int requestVersion,
        Guid organizationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                cancellationToken);
        if (existing is not null)
        {
            return ReadManifest(existing);
        }

        var snapshot = await persistence.LoadVersionAsync(requestId, requestVersion, cancellationToken);
        if (snapshot.OrganizationId != organizationId)
        {
            throw new DomainNotFoundException("The purchase request version is not visible.");
        }

        var lines = await persistence.LoadLinesAsync(requestId, requestVersion, cancellationToken);
        var requestContentDigest = PurchaseRequestCanonicalizer.RequestContentDigest(snapshot);
        var slots = RequiredAssertions(snapshot, lines.Values);
        var assertions = new List<PurchaseRequestReferenceAssertion>(slots.Count);
        foreach (var slot in slots)
        {
            assertions.Add(await VerifyAsync(slot, organizationId, occurredAt, cancellationToken));
        }

        var attestation = PurchaseRequestReferenceAttestation.Create(
            requestId,
            requestVersion,
            organizationId,
            occurredAt,
            assertions);
        var references = snapshot.LineRefs.ToArray();
        var policyManifestDigest = PurchaseRequestCanonicalizer.PolicyManifestDigest(
            requestId, requestVersion, references);
        var domainAttestationDigest = PurchaseRequestCanonicalizer.DomainAttestationDigest(
            requestId,
            requestVersion,
            organizationId,
            requestContentDigest,
            attestation.Digest,
            policyManifestDigest,
            references);
        var manifest = new PurchaseRequestCompletenessManifest(
            requestId,
            requestVersion,
            organizationId,
            requestContentDigest,
            attestation.Digest,
            policyManifestDigest,
            domainAttestationDigest,
            references);

        var occurredAtUtc = occurredAt.ToUniversalTime();
        // Two concurrent presentations of the same version must produce exactly one attestation and
        // one manifest (NFR-03, CA-03): the rows are written in one transaction and the losing
        // writer resolves the winner's frozen manifest instead of writing a second one.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var concurrent = await dbContext.PurchaseRequestCompletenessManifests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                cancellationToken);
        if (concurrent is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ReadManifest(concurrent);
        }

        dbContext.PurchaseRequestReferenceAttestations.Add(new PurchaseRequestReferenceAttestationRecord
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            RequestVersion = requestVersion,
            OrganizationId = organizationId,
            AttestedAt = occurredAtUtc,
            AssertionsJson = PurchaseRequestSerialization.Assertions(attestation.Assertions),
            Digest = attestation.Digest,
            CreatedAt = occurredAtUtc
        });
        dbContext.PurchaseRequestCompletenessManifests.Add(new PurchaseRequestCompletenessManifestRecord
        {
            RequestId = requestId,
            RequestVersion = requestVersion,
            OrganizationId = organizationId,
            RequestContentDigest = requestContentDigest,
            ReferenceAttestationDigest = attestation.Digest,
            PolicyManifestDigest = policyManifestDigest,
            DomainAttestationDigest = domainAttestationDigest,
            LinesJson = PurchaseRequestSerialization.ManifestLines(references),
            CreatedAt = occurredAtUtc
        });
        dbContext.PurchaseRequestLifecycleEvents.Add(new PurchaseRequestLifecycleEventRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RequestId = requestId,
            RequestVersion = requestVersion,
            Action = "ATTESTED",
            BeforeStatus = null,
            AfterStatus = (int)(await persistence.FindStatusAsync(
                requestId, requestVersion, organizationId, cancellationToken) ?? PurchaseRequestStatus.Draft),
            ActorUserId = null,
            WorkloadIssuer = "internal://procure-to-pay",
            WorkloadClientId = PurchaseRequestCodes.ProviderId,
            ReasonCode = "REFERENCE_ATTESTATION",
            Reason = "Owner references attested for submission.",
            CorrelationReference = $"attestation-{requestVersion}",
            OccurredAt = occurredAtUtc
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The winner committed while this transaction was inserting: reuse its frozen rows.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The engine already aborted the transaction (for example on a deadlock victim).
            }

            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.PurchaseRequestCompletenessManifests
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.RequestId == requestId && record.RequestVersion == requestVersion,
                    cancellationToken)
                ?? throw new PurchaseRequestDependencyUnavailableException(
                    "The concurrent attestation of the purchase request version did not complete.");
            return ReadManifest(winner);
        }

        logger.LogInformation(
            "Purchase request {RequestId} version {Version} attested with {AssertionCount} assertion(s).",
            requestId,
            requestVersion,
            assertions.Count);
        return manifest;
    }

    internal static PurchaseRequestCompletenessManifest ReadManifest(
        PurchaseRequestCompletenessManifestRecord record) =>
        new(
            record.RequestId,
            record.RequestVersion,
            record.OrganizationId,
            record.RequestContentDigest,
            record.ReferenceAttestationDigest,
            record.PolicyManifestDigest,
            record.DomainAttestationDigest,
            PurchaseRequestSerialization.ReadLineRefs(record.LinesJson));

    /// <summary>
    /// Exact assertion cardinalities of REQ-04: activity for every referenced entity, one ownership
    /// relation per cost-center/department pair and one FX assertion per distinct FX reference.
    /// </summary>
    internal static IReadOnlyList<AssertionSlot> RequiredAssertions(
        PurchaseRequestSnapshot snapshot,
        IEnumerable<PurchaseRequestLineView> lines)
    {
        var slots = new List<AssertionSlot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(PurchaseRequestAssertionType type, PurchaseRequestAttestedRef source, PurchaseRequestAttestedRef? target)
        {
            var slot = new AssertionSlot(type, source, target);
            if (seen.Add(slot.Identity))
            {
                slots.Add(slot);
            }
        }

        Add(
            PurchaseRequestAssertionType.ActiveInOrganization,
            PurchaseRequestAttestedRef.ForEntity(
                PurchaseRequestReferenceType.LegalEntity,
                snapshot.LegalEntityRef.Id,
                snapshot.LegalEntityRef.Version),
            null);
        Add(
            PurchaseRequestAssertionType.ActiveInOrganization,
            PurchaseRequestAttestedRef.ForEntity(PurchaseRequestReferenceType.User, snapshot.RequesterId, 1),
            null);
        foreach (var line in lines)
        {
            var content = line.Content;
            Add(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.User, content.RequestedForUserRef.Id, content.RequestedForUserRef.Version),
                null);
            Add(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.Department,
                    content.BeneficiaryDepartmentRef.Id,
                    content.BeneficiaryDepartmentRef.Version),
                null);
            Add(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.CostCenter,
                    content.CostCenterRef.Id,
                    content.CostCenterRef.Version),
                null);
            Add(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.Department,
                    content.CostCenterDepartmentRef.Id,
                    content.CostCenterDepartmentRef.Version),
                null);
            Add(
                PurchaseRequestAssertionType.CostCenterOwnedByDepartment,
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.CostCenter,
                    content.CostCenterRef.Id,
                    content.CostCenterRef.Version),
                PurchaseRequestAttestedRef.ForEntity(
                    PurchaseRequestReferenceType.Department,
                    content.CostCenterDepartmentRef.Id,
                    content.CostCenterDepartmentRef.Version));
            Add(
                PurchaseRequestAssertionType.ActiveInOrganization,
                PurchaseRequestAttestedRef.ForCode(
                    PurchaseRequestReferenceType.SpendCategory,
                    content.SpendCategoryRef.Code,
                    content.SpendCategoryRef.Version,
                    content.SpendCategoryRef.Digest),
                null);
            foreach (var answer in content.RiskAnswers)
            {
                Add(
                    PurchaseRequestAssertionType.ActiveInOrganization,
                    RiskSchemaRef(answer),
                    null);
            }

            if (content.SupplierRef is not null)
            {
                Add(
                    PurchaseRequestAssertionType.ActiveInOrganization,
                    PurchaseRequestAttestedRef.ForEntity(
                        PurchaseRequestReferenceType.Supplier,
                        content.SupplierRef.Id,
                        content.SupplierRef.Version),
                    null);
            }

            foreach (var product in new[] { content.PreferredProductRef, content.RequiredProductRef })
            {
                if (product is not null)
                {
                    Add(
                        PurchaseRequestAssertionType.ActiveInOrganization,
                        PurchaseRequestAttestedRef.ForEntity(
                            PurchaseRequestReferenceType.Product, product.Id, product.Version),
                        null);
                }
            }

            if (content.FxAttestationRef is PurchaseRequestFxRef fx)
            {
                Add(
                    PurchaseRequestAssertionType.FxAttestationValid,
                    PurchaseRequestAttestedRef.ForCode(
                        PurchaseRequestReferenceType.Fx,
                        "FX_" + fx.AttestationId.ToString("N").ToUpperInvariant(),
                        fx.AttestationVersion,
                        PurchaseRequestAttestationService.FxSlotDigest(fx)),
                    null);
            }
        }

        return slots;
    }

    private static PurchaseRequestAttestedRef RiskSchemaRef(TypedAnswerRef answer) =>
        PurchaseRequestAttestedRef.ForQuestionSchema(
            answer.QuestionCode,
            answer.SchemaVersion,
            RiskSchemaDigest(answer));

    /// <summary>Stable digest of one FX attestation slot so the owner can echo the same binding.</summary>
    internal static string FxSlotDigest(PurchaseRequestFxRef fx) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{fx.AttestationId:D}:{fx.AttestationVersion}:{fx.BaseCurrency}:{fx.TransactionCurrency}")))
            .ToLowerInvariant();

    private static string RiskSchemaDigest(TypedAnswerRef answer)
    {
        // The owner answers the schema identity; the caller only declares the question and version.
        // The digest is the canonical digest of the declared answer kind so the slot is stable.
        var basis = $"{answer.QuestionCode}:{answer.SchemaVersion}:{answer.ValueKind}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(basis))).ToLowerInvariant();
    }

    private async Task<PurchaseRequestReferenceAssertion> VerifyAsync(
        AssertionSlot slot,
        Guid organizationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var owner = owners.ResolveExactlyOne(slot.AssertionType, slot.SourceRef.Type);
        var request = new PurchaseRequestVerificationRequest(
            PurchaseRequestCodes.Code(slot.AssertionType),
            organizationId.ToString("D"),
            slot.SourceRef,
            slot.TargetRef,
            occurredAt.ToUniversalTime());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OwnerTimeout);
        PurchaseRequestVerificationResponse response;
        try
        {
            response = await owner.VerifyAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PurchaseRequestDependencyUnavailableException("A reference owner timed out.");
        }
        catch (Exception exception) when (exception is not DomainException)
        {
            logger.LogWarning(exception, "A reference owner failed.");
            throw new PurchaseRequestDependencyUnavailableException("A reference owner is unavailable.");
        }

        if (response is null ||
            !string.Equals(response.AssertionType, request.AssertionType, StringComparison.Ordinal) ||
            !string.Equals(response.OrganizationId, request.OrganizationId, StringComparison.Ordinal) ||
            response.VerifiedAt.ToUniversalTime() != request.VerifiedAt ||
            !SameReference(response.SourceRef, request.SourceRef) ||
            !SameReference(response.TargetRef, request.TargetRef) ||
            string.IsNullOrWhiteSpace(response.OwnerContractVersion))
        {
            throw new DomainValidationException("A reference owner answered with a different binding.");
        }

        if (!response.Active)
        {
            throw new DomainValidationException("A referenced entity or code is not usable for this request.");
        }

        return new PurchaseRequestReferenceAssertion(
            slot.AssertionType,
            response.OwnerContractVersion,
            response.OwnerId,
            response.SourceRef,
            response.Status,
            response.TargetRef);
    }

    private static bool SameReference(PurchaseRequestAttestedRef? left, PurchaseRequestAttestedRef? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Kind == right.Kind &&
               left.Type == right.Type &&
               left.Id == right.Id &&
               left.Version == right.Version &&
               string.Equals(left.Code, right.Code, StringComparison.Ordinal) &&
               string.Equals(left.Digest, right.Digest, StringComparison.Ordinal);
    }

    /// <summary>One required owner answer: assertion type plus asserted reference (REQ-04).</summary>
    public sealed record AssertionSlot(
        PurchaseRequestAssertionType AssertionType,
        PurchaseRequestAttestedRef SourceRef,
        PurchaseRequestAttestedRef? TargetRef)
    {
        public string Identity =>
            $"{PurchaseRequestCodes.Code(AssertionType)}|{PurchaseRequestReferenceAssertion.DescribeReference(SourceRef)}|" +
            (TargetRef is null ? string.Empty : PurchaseRequestReferenceAssertion.DescribeReference(TargetRef));
    }
}
