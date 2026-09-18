using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>One requested Acceptance Responsibility of a Direct Purchase line (REQ-06, REQ-07).</summary>
public sealed record DirectPurchaseAssignmentRequest(
    Guid LineId,
    int LineVersion,
    string Kind,
    Guid UserId,
    int UserVersion,
    string? Reason)
{
    public string Kind { get; } = PurchaseOrderCodes.Code(Kind, "Responsibility kind");

    public int UserVersion { get; } = UserVersion >= 1
        ? UserVersion
        : throw new DomainValidationException("An acceptance assignment requires a positive user version.");

    public Guid LineId { get; } = LineId == Guid.Empty
        ? throw new DomainValidationException("An acceptance assignment requires its line.")
        : LineId;

    public int LineVersion { get; } = LineVersion >= 1
        ? LineVersion
        : throw new DomainValidationException("An acceptance assignment requires a positive line version.");

    public string CanonicalIdentity => $"{LineId:D}:{LineVersion}";
}

/// <summary>
/// One Direct Purchase authorization version (<c>direct-purchase-authorization/v1</c>, REQ-07). It
/// authorizes a maximum and never posts a <c>COMMIT</c>: the reservation stays <c>RESERVED</c> until a
/// future Invoice/Matching contract consumes it. Every state transition appends a successor version.
/// </summary>
public sealed record DirectPurchaseAuthorization
{
    public const string ContractVersion = PurchaseOrderCodes.DirectPurchaseAuthorizationContract;

    public DirectPurchaseAuthorization(
        Guid authorizationId,
        int version,
        int? predecessorVersion,
        Guid organizationId,
        string authorizationKey,
        string fingerprint,
        int expectedRequestVersion,
        PurchaseOrderContentRef requestRef,
        PurchaseOrderContentRef requestApprovalCaseRef,
        PurchaseOrderContentRef orderingEvidenceRef,
        SourcingPolicyEvaluationRef policyBundleRef,
        PurchaseOrderEntityRef supplierRef,
        IReadOnlyList<PurchaseOrderContentRef> coveredLines,
        decimal maximumSourceAmount,
        string sourceCurrency,
        decimal maximumBaseAmount,
        VendorTermsSnapshot termsSnapshot,
        IReadOnlyList<AcceptanceResponsibility> acceptanceResponsibilities,
        IReadOnlyList<PurchaseOrderContentRef> documentEvidenceRefs,
        DirectPurchaseState state,
        Guid authorizedByUserId,
        DateTimeOffset authorizedAt)
    {
        AuthorizationId = authorizationId == Guid.Empty
            ? throw new DomainValidationException("A Direct Purchase requires its identity.")
            : authorizationId;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A Direct Purchase version must be positive.");
        if (version == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first Direct Purchase version has no predecessor.");
        }

        PredecessorVersion = predecessorVersion;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("A Direct Purchase requires its organization.")
            : organizationId;
        AuthorizationKey = PurchaseOrderCodes.Key(authorizationKey, "authorization_key");
        Fingerprint = PurchaseOrderCodes.Digest(fingerprint, "authorization_fingerprint");
        ExpectedRequestVersion = expectedRequestVersion >= 1
            ? expectedRequestVersion
            : throw new DomainValidationException("A Direct Purchase requires the expected request version.");
        RequestRef = requestRef ?? throw new DomainValidationException("A Direct Purchase requires its request.");
        RequestApprovalCaseRef = requestApprovalCaseRef ??
            throw new DomainValidationException("A Direct Purchase requires its request approval case.");
        OrderingEvidenceRef = orderingEvidenceRef ??
            throw new DomainValidationException("A Direct Purchase requires its ordering evidence.");
        PolicyBundleRef = policyBundleRef ??
            throw new DomainValidationException("A Direct Purchase requires its policy bundle.");
        SupplierRef = supplierRef ?? throw new DomainValidationException("A Direct Purchase requires its supplier.");
        SourceCurrency = PurchaseOrderCodes.Currency(sourceCurrency, "Source currency");
        MaximumSourceAmount = PurchaseOrderCodes.Positive(maximumSourceAmount, "Maximum source amount");
        MaximumBaseAmount = PurchaseOrderCodes.Positive(maximumBaseAmount, "Maximum base amount");
        TermsSnapshot = termsSnapshot ?? throw new DomainValidationException("A Direct Purchase requires its terms.");
        if (!string.Equals(TermsSnapshot.SourceKind, PurchaseOrderCodes.TermsSourceDirectPurchase, StringComparison.Ordinal))
        {
            throw new DomainValidationException("Direct Purchase terms must come from the direct source kind.");
        }

        if (!string.Equals(TermsSnapshot.SourceCurrency, SourceCurrency, StringComparison.Ordinal) ||
            !string.Equals(TermsSnapshot.SupplierRef.Id.ToString("D"), SupplierRef.Id.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                "Direct Purchase terms must restate the authorized currency and supplier.");
        }

        var lines = (coveredLines ?? [])
            .OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (lines.Length == 0)
        {
            throw new DomainValidationException("A Direct Purchase requires at least one covered line.");
        }

        if (lines.Select(line => line.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() != lines.Length)
        {
            throw new DomainConflictException("A Direct Purchase cannot repeat a covered line.");
        }

        CoveredLines = lines;
        var responsibilities = (acceptanceResponsibilities ?? [])
            .OrderBy(responsibility => responsibility.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(responsibility => responsibility.Kind, StringComparer.Ordinal)
            .ToImmutableArray();
        if (responsibilities.Any(responsibility => !lines.Any(line =>
                string.Equals(line.CanonicalIdentity, responsibility.LineRef.CanonicalIdentity, StringComparison.Ordinal))))
        {
            throw new DomainValidationException(
                "Every acceptance responsibility must belong to a covered line of the authorization.");
        }

        AcceptanceResponsibilities = responsibilities;
        DocumentEvidenceRefs = (documentEvidenceRefs ?? [])
            .OrderBy(reference => reference.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (authorizedByUserId == Guid.Empty)
        {
            throw new DomainValidationException("A Direct Purchase requires its authorizing user.");
        }

        AuthorizedByUserId = authorizedByUserId;
        AuthorizedAt = authorizedAt.ToUniversalTime();
        if (!Enum.IsDefined(State = state))
        {
            throw new DomainValidationException("The Direct Purchase state is not recognized.");
        }

        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid AuthorizationId { get; }
    public int Version { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public string AuthorizationKey { get; }
    public string Fingerprint { get; }
    public int ExpectedRequestVersion { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public PurchaseOrderContentRef RequestApprovalCaseRef { get; }
    public PurchaseOrderContentRef OrderingEvidenceRef { get; }
    public SourcingPolicyEvaluationRef PolicyBundleRef { get; }
    public PurchaseOrderEntityRef SupplierRef { get; }
    public IReadOnlyList<PurchaseOrderContentRef> CoveredLines { get; }
    public decimal MaximumSourceAmount { get; }
    public string SourceCurrency { get; }
    public decimal MaximumBaseAmount { get; }
    public VendorTermsSnapshot TermsSnapshot { get; }
    public IReadOnlyList<AcceptanceResponsibility> AcceptanceResponsibilities { get; }
    public IReadOnlyList<PurchaseOrderContentRef> DocumentEvidenceRefs { get; }
    public DirectPurchaseState State { get; }
    public Guid AuthorizedByUserId { get; }
    public DateTimeOffset AuthorizedAt { get; }

    /// <summary><c>direct_purchase_authorization_digest</c> of the published preimage (REQ-07).</summary>
    public string Digest { get; }

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.DirectPurchaseDocument(this);

    /// <summary>The cancelled successor of an authorized Direct Purchase (REQ-07).</summary>
    public DirectPurchaseAuthorization With(
        DirectPurchaseState state,
        Guid actorUserId,
        DateTimeOffset occurredAt) =>
        new(
            AuthorizationId,
            Version + 1,
            Version,
            OrganizationId,
            AuthorizationKey,
            Fingerprint,
            ExpectedRequestVersion,
            RequestRef,
            RequestApprovalCaseRef,
            OrderingEvidenceRef,
            PolicyBundleRef,
            SupplierRef,
            CoveredLines,
            MaximumSourceAmount,
            SourceCurrency,
            MaximumBaseAmount,
            TermsSnapshot,
            AcceptanceResponsibilities,
            DocumentEvidenceRefs,
            state,
            actorUserId,
            occurredAt);
}

/// <summary>
/// <c>direct_purchase_authorization_digest</c> and <c>authorization_fingerprint</c> preimages (REQ-07).
/// The fingerprint covers the public command properties plus the server-derived evidence, so a replay
/// of the same preimage is recognized while a different one under the same key is a conflict.
/// </summary>
public static class DirectPurchaseFingerprints
{
    /// <summary>Public properties of <c>direct-purchase-command/v1</c>.</summary>
    public static SortedDictionary<string, object?> CommandPreimage(
        Guid organizationId,
        Guid requestId,
        int expectedRequestVersion,
        string authorizationKey,
        IEnumerable<PurchaseOrderContentRef> coveredTargets,
        IEnumerable<AcceptanceResponsibility> acceptanceResponsibilities)
    {
        var responsibilities = (acceptanceResponsibilities ?? [])
            .OrderBy(responsibility => responsibility.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ThenBy(responsibility => responsibility.Kind, StringComparer.Ordinal)
            .Select(responsibility => (object?)PurchaseOrderCanonicalizer.ResponsibilityPreimage(responsibility));
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["acceptance_responsibilities"] = PurchaseOrderCanonicalizer.Set(responsibilities),
            ["authorization_key"] = PurchaseOrderCodes.Key(authorizationKey, "authorization_key"),
            ["contract_version"] = PurchaseOrderCodes.DirectPurchaseCommandContract,
            ["covered_targets"] = PurchaseOrderCanonicalizer.Set(
                (coveredTargets ?? []).Select(line => (object?)PurchaseOrderCanonicalizer.ContentRef(line))),
            ["expected_request_version"] = expectedRequestVersion,
            ["organization_id"] = organizationId.ToString("D"),
            ["request_id"] = requestId.ToString("D")
        };
    }

    /// <summary>
    /// Idempotency preimage of one authorization command. It covers only the caller-provided
    /// properties, so a replay is recognized before any server-derived evidence is recomputed while a
    /// different preimage under the same key is a conflict.
    /// </summary>
    public static string CommandFingerprint(
        Guid organizationId,
        Guid requestId,
        int expectedRequestVersion,
        string authorizationKey,
        Guid actorUserId,
        IEnumerable<OrderingEvidenceTargetRef> coveredTargets,
        IEnumerable<DirectPurchaseAssignmentRequest> assignments)
    {
        var preimage = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor_user_id"] = actorUserId.ToString("D"),
            ["assignments"] = PurchaseOrderCanonicalizer.Set((assignments ?? [])
                .OrderBy(assignment => assignment.CanonicalIdentity, StringComparer.Ordinal)
                .ThenBy(assignment => assignment.Kind, StringComparer.Ordinal)
                .Select(assignment => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = assignment.Kind,
                    ["line_id"] = assignment.LineId.ToString("D"),
                    ["line_version"] = assignment.LineVersion,
                    ["reason"] = assignment.Reason,
                    ["user_id"] = assignment.UserId.ToString("D"),
                    ["user_version"] = assignment.UserVersion
                })),
            ["authorization_key"] = PurchaseOrderCodes.Key(authorizationKey, "authorization_key"),
            ["canonicalization_version"] = PolicyCanonicalizer.Version,
            ["contract_version"] = "authorize-direct-purchase-command/v1",
            ["covered_targets"] = PurchaseOrderCanonicalizer.Set((coveredTargets ?? [])
                .OrderBy(target => target.CanonicalIdentity, StringComparer.Ordinal)
                .Select(target => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = target.TargetId.ToString("D"),
                    ["version"] = target.TargetVersion
                })),
            ["expected_request_version"] = expectedRequestVersion,
            ["organization_id"] = organizationId.ToString("D"),
            ["request_id"] = requestId.ToString("D")
        };
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }

    /// <summary>Public command properties plus every server-derived binding (REQ-07).</summary>
    public static string AuthorizationFingerprint(
        Guid organizationId,
        Guid requestId,
        int expectedRequestVersion,
        string authorizationKey,
        Guid actorUserId,
        IEnumerable<PurchaseOrderContentRef> coveredTargets,
        IEnumerable<AcceptanceResponsibility> acceptanceResponsibilities,
        SourcingPolicyEvaluationRef policyBundleRef,
        PurchaseOrderContentRef orderingEvidenceRef,
        PurchaseOrderEntityRef supplierRef,
        decimal maximumSourceAmount,
        decimal maximumBaseAmount,
        string termsSnapshotDigest)
    {
        ArgumentNullException.ThrowIfNull(policyBundleRef);
        ArgumentNullException.ThrowIfNull(orderingEvidenceRef);
        ArgumentNullException.ThrowIfNull(supplierRef);
        var preimage = CommandPreimage(
            organizationId, requestId, expectedRequestVersion, authorizationKey, coveredTargets,
            acceptanceResponsibilities);
        preimage["actor_user_id"] = actorUserId.ToString("D");
        preimage["canonicalization_version"] = PolicyCanonicalizer.Version;
        preimage["maximum_base_amount"] = PurchaseOrderCodes.Decimal(PurchaseOrderCodes.Decimal12(maximumBaseAmount));
        preimage["maximum_source_amount"] = PurchaseOrderCodes.Decimal(PurchaseOrderCodes.Decimal12(maximumSourceAmount));
        preimage["ordering_evidence_ref"] = PurchaseOrderCanonicalizer.ContentRef(orderingEvidenceRef);
        preimage["policy_bundle_ref"] = SourcingProposalVersion.EvaluationRefDocument(policyBundleRef);
        preimage["supplier_ref"] = PurchaseOrderCanonicalizer.EntityRef(supplierRef);
        preimage["terms_snapshot_digest"] = PurchaseOrderCodes.Digest(termsSnapshotDigest, "Terms snapshot digest");
        return PolicyCanonicalizer.Hash(PolicyCanonicalizer.SerializeCanonical(preimage));
    }
}
