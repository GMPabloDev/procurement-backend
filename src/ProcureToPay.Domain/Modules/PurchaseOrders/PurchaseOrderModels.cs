using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary><c>content_ref={content_digest,id,version}</c> of one versioned artefact (Datos y contratos).</summary>
public sealed record PurchaseOrderContentRef
{
    public PurchaseOrderContentRef(Guid id, int version, string contentDigest)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("A reference requires its identity.")
            : id;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A reference requires a positive version.");
        ContentDigest = PurchaseOrderCodes.Digest(contentDigest, "Reference content digest");
    }

    public Guid Id { get; }
    public int Version { get; }
    public string ContentDigest { get; }

    public string CanonicalIdentity => $"{Id:D}:{Version}";

    public PurchaseOrderEntityRef Entity => new(Id, Version);

    internal static PurchaseOrderContentRef From(Sourcing.SourcingContentRef reference) =>
        new(reference.Id, reference.Version, reference.ContentDigest);
}

/// <summary><c>entity_ref={id,version}</c> of one externally owned entity (Datos y contratos).</summary>
public sealed record PurchaseOrderEntityRef
{
    public PurchaseOrderEntityRef(Guid id, int version)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException("An entity reference requires its identity.")
            : id;
        Version = version >= 1
            ? version
            : throw new DomainValidationException("An entity reference requires a positive version.");
    }

    public Guid Id { get; }
    public int Version { get; }

    internal static PurchaseOrderEntityRef From(Sourcing.SourcingEntityRef reference) =>
        new(reference.Id, reference.Version);
}

/// <summary>
/// One delivery commitment of a Purchase Order version (<c>delivery-commitment/v1</c>, REQ-02). The
/// location is business-sensitive and never reaches logs, telemetry or health.
/// </summary>
public sealed record DeliveryCommitment
{
    public const string ContractVersion = PurchaseOrderCodes.DeliveryCommitmentContract;

    public DeliveryCommitment(DateOnly deliveryDate, string deliveryLocation)
    {
        DeliveryDate = deliveryDate;
        DeliveryLocation = PurchaseOrderCodes.Location(deliveryLocation);
    }

    public DateOnly DeliveryDate { get; }
    public string DeliveryLocation { get; }

    /// <summary>REQ-02: the date must be in the future when the version is presented.</summary>
    public void RequireFuture(DateOnly today)
    {
        if (DeliveryDate <= today)
        {
            throw new PurchaseOrderUnprocessableException(
                "The delivery date must be a future calendar date when the order is presented.");
        }
    }
}

/// <summary>
/// One Acceptance Responsibility of a Purchase Order line (<c>acceptance-responsibility/v1</c>,
/// REQ-06). It is a process responsibility, never a global role or an approval authority.
/// </summary>
public sealed record AcceptanceResponsibility
{
    public const string ContractVersion = PurchaseOrderCodes.AcceptanceResponsibilityContract;

    public AcceptanceResponsibility(
        string kind,
        PurchaseOrderContentRef lineRef,
        PurchaseOrderEntityRef principal,
        PurchaseOrderEntityRef candidateUserRef,
        Guid assignedByUserId,
        string reason,
        DateTimeOffset assignedAt,
        int version)
    {
        Kind = PurchaseOrderCodes.Code(kind, "Responsibility kind");
        if (Kind is not (PurchaseOrderCodes.ResponsibilityGoodsReceipt or
            PurchaseOrderCodes.ResponsibilityServiceAcceptance or
            PurchaseOrderCodes.ResponsibilitySubscriptionProvisioning or
            PurchaseOrderCodes.ResponsibilitySubscriptionAccessConfirmation))
        {
            throw new DomainValidationException("The acceptance responsibility kind is not recognized.");
        }

        LineRef = lineRef ?? throw new DomainValidationException("A responsibility requires its line.");
        Principal = principal ?? throw new DomainValidationException("A responsibility requires its principal.");
        CandidateUserRef = candidateUserRef ??
            throw new DomainValidationException("A responsibility requires its candidate reference.");
        if (assignedByUserId == Guid.Empty)
        {
            throw new DomainValidationException("A responsibility requires the assigning user.");
        }

        AssignedByUserId = assignedByUserId;
        Reason = PurchaseOrderCodes.Reason(reason);
        AssignedAt = assignedAt.ToUniversalTime();
        Version = version >= 1
            ? version
            : throw new DomainValidationException("A responsibility version must be positive.");
    }

    public string Kind { get; }
    public PurchaseOrderContentRef LineRef { get; }
    public PurchaseOrderEntityRef Principal { get; }
    public PurchaseOrderEntityRef CandidateUserRef { get; }
    public Guid AssignedByUserId { get; }
    public string Reason { get; }
    public DateTimeOffset AssignedAt { get; }
    public int Version { get; }

    /// <summary>Identity of one responsibility: a line and a kind carry exactly one current row.</summary>
    public string Identity => $"{LineRef.CanonicalIdentity}:{Kind}";

    /// <summary>The v1 contract only admits an explicit user principal (REQ-06, DEC-06).</summary>
    public static PurchaseOrderEntityRef UserPrincipal(Guid userId, int version) => new(userId, version);
}

/// <summary>
/// One Purchase Order line (<c>purchase-order-line/v1</c>, REQ-02): one award line produces exactly
/// one PO line. Release 1 copies the award commercial content verbatim and never reconstructs the
/// tax breakdown the award already lost, so subtotal equals the source gross total.
/// </summary>
public sealed record PurchaseOrderLine
{
    public const string ContractVersion = PurchaseOrderCodes.PurchaseOrderLineContract;

    public PurchaseOrderLine(
        Guid lineId,
        PurchaseOrderContentRef awardLineRef,
        PurchaseOrderContentRef requestLineRef,
        decimal quantity,
        string unitCode,
        decimal unitPrice,
        string sourceCurrency,
        decimal grossTotal,
        string baseCurrency,
        decimal baseGrossTotal,
        decimal subtotal,
        decimal taxes,
        decimal additionalCharges,
        decimal discounts,
        PurchaseOrderContentRef? fxSnapshotRef,
        IEnumerable<AcceptanceResponsibility>? acceptanceResponsibilities)
    {
        LineId = lineId == Guid.Empty
            ? throw new DomainValidationException("A purchase order line requires its identity.")
            : lineId;
        AwardLineRef = awardLineRef ??
            throw new DomainValidationException("A purchase order line requires its award line.");
        RequestLineRef = requestLineRef ??
            throw new DomainValidationException("A purchase order line requires its request line.");
        Quantity = PurchaseOrderCodes.Positive(quantity, "Line quantity");
        UnitCode = PurchaseOrderCodes.Code(unitCode, "Unit code");
        UnitPrice = PurchaseOrderCodes.Positive(unitPrice, "Line unit price");
        SourceCurrency = PurchaseOrderCodes.Currency(sourceCurrency, "Source currency");
        GrossTotal = PurchaseOrderCodes.NonNegative(grossTotal, "Source gross total");
        BaseCurrency = PurchaseOrderCodes.Currency(baseCurrency, "Base currency");
        BaseGrossTotal = PurchaseOrderCodes.NonNegative(baseGrossTotal, "Base gross total");
        Subtotal = PurchaseOrderCodes.NonNegative(subtotal, "Subtotal");
        Taxes = PurchaseOrderCodes.NonNegative(taxes, "Taxes");
        AdditionalCharges = PurchaseOrderCodes.NonNegative(additionalCharges, "Additional charges");
        Discounts = PurchaseOrderCodes.NonNegative(discounts, "Discounts");
        FxSnapshotRef = fxSnapshotRef;

        if (string.Equals(SourceCurrency, BaseCurrency, StringComparison.Ordinal) != (fxSnapshotRef is null))
        {
            throw new DomainValidationException(
                "An FX snapshot reference is required exactly when the two currencies differ.");
        }

        if (Subtotal + Taxes + AdditionalCharges - Discounts != GrossTotal)
        {
            throw new DomainValidationException(
                "gross_total must equal subtotal + taxes + additional_charges - discounts.");
        }

        var responsibilities = (acceptanceResponsibilities ?? []).ToImmutableArray();
        var duplicates = responsibilities
            .GroupBy(responsibility => responsibility.Kind, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new DomainConflictException("A line carries exactly one current responsibility per kind.");
        }

        if (responsibilities.Any(responsibility =>
                responsibility.LineRef.Id != lineId ||
                !string.Equals(responsibility.LineRef.ContentDigest, requestLineRef.ContentDigest,
                    StringComparison.Ordinal)))
        {
            throw new DomainValidationException(
                "Every responsibility of a line references that line and its attested content.");
        }

        AcceptanceResponsibilities = responsibilities
            .OrderBy(responsibility => responsibility.Kind, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public Guid LineId { get; }
    public PurchaseOrderContentRef AwardLineRef { get; }
    public PurchaseOrderContentRef RequestLineRef { get; }
    public decimal Quantity { get; }
    public string UnitCode { get; }
    public decimal UnitPrice { get; }
    public string SourceCurrency { get; }
    public decimal GrossTotal { get; }
    public string BaseCurrency { get; }
    public decimal BaseGrossTotal { get; }
    public decimal Subtotal { get; }
    public decimal Taxes { get; }
    public decimal AdditionalCharges { get; }
    public decimal Discounts { get; }
    public PurchaseOrderContentRef? FxSnapshotRef { get; }
    public IReadOnlyList<AcceptanceResponsibility> AcceptanceResponsibilities { get; }

    public string CanonicalIdentity => $"{RequestLineRef.Id:D}:{RequestLineRef.Version}";

    /// <summary>
    /// REQ-02: the deterministic commercial content copied from one award line. Quantity, unit,
    /// price, source gross, base gross and the FX snapshot are byte-equivalent to the award; the
    /// breakdown Release 1 publishes is <c>subtotal = source gross</c> with zero taxes, additional
    /// charges and discounts, because the award lost it.
    /// </summary>
    public static PurchaseOrderLine FromAwardLine(
        Sourcing.AwardLine awardLine,
        Guid lineId,
        PurchaseOrderContentRef requestLineRef,
        IEnumerable<AcceptanceResponsibility>? responsibilities = null) =>
        new(
            lineId,
            PurchaseOrderContentRef.From(awardLine.LineRef),
            requestLineRef,
            awardLine.Quantity,
            awardLine.UnitCode,
            awardLine.UnitPrice,
            awardLine.SourceCurrency,
            awardLine.SourceGrossTotal,
            awardLine.BaseCurrency,
            awardLine.BaseGrossTotal,
            subtotal: awardLine.SourceGrossTotal,
            taxes: 0m,
            additionalCharges: 0m,
            discounts: 0m,
            awardLine.FxSnapshotRef is null ? null : PurchaseOrderContentRef.From(awardLine.FxSnapshotRef),
            responsibilities);

    /// <summary>The same commercial content with a replacement responsibility set (REQ-05/06).</summary>
    public PurchaseOrderLine WithResponsibilities(IEnumerable<AcceptanceResponsibility> responsibilities) =>
        new(
            LineId,
            AwardLineRef,
            RequestLineRef,
            Quantity,
            UnitCode,
            UnitPrice,
            SourceCurrency,
            GrossTotal,
            BaseCurrency,
            BaseGrossTotal,
            Subtotal,
            Taxes,
            AdditionalCharges,
            Discounts,
            FxSnapshotRef,
            responsibilities);
}

/// <summary>Reference to one confirmed Budget operation of a Purchase Order version (REQ-04).</summary>
public sealed record PurchaseOrderBudgetOperationRef(
    string Operation,
    Guid OperationId,
    string OperationKey,
    Guid SourceId,
    int SourceVersion,
    string SourceDigest)
{
    public string Operation { get; } = PurchaseOrderCodes.Code(Operation, "Budget operation");

    public string OperationKey { get; } = PurchaseOrderCodes.Key(OperationKey, "operation_key");

    public string SourceDigest { get; } = PurchaseOrderCodes.Digest(SourceDigest, "Budget source digest");
}

/// <summary>
/// One published Purchase Order version (<c>purchase-order-version/v1</c>, REQ-02). It is
/// append-only: a <c>DRAFT</c> may be incomplete, and every later version is a full successor whose
/// digest is reproducible from its own properties.
/// </summary>
public sealed record PurchaseOrderVersion
{
    public const string ContractVersion = PurchaseOrderCodes.PurchaseOrderVersionContract;

    public PurchaseOrderVersion(
        Guid poId,
        int purchaseOrderVersion,
        int? predecessorVersion,
        Guid organizationId,
        string poNumber,
        PurchaseOrderState state,
        PurchaseOrderEntityRef legalEntityRef,
        PurchaseOrderEntityRef supplierRef,
        PurchaseOrderContentRef requestRef,
        PurchaseOrderContentRef proposalRef,
        PurchaseOrderContentRef awardRef,
        PurchaseOrderContentRef awardClaimRef,
        PurchaseOrderContentRef? orderingEvidenceRef,
        PurchaseOrderContentRef? approvalRef,
        PurchaseOrderContentRef? amendmentRef,
        VendorTermsSnapshot termsSnapshot,
        DeliveryCommitment? delivery,
        IEnumerable<PurchaseOrderLine> lines,
        IEnumerable<PurchaseOrderBudgetOperationRef>? budgetOperationRefs,
        decimal sourceAmount,
        string sourceCurrency,
        decimal baseAmount,
        string baseCurrency,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        DateTimeOffset? issuedAt)
    {
        if (poId == Guid.Empty)
        {
            throw new DomainValidationException("A purchase order version requires its root identity.");
        }

        PoId = poId;
        PurchaseOrderVersionNumber = purchaseOrderVersion >= 1
            ? purchaseOrderVersion
            : throw new DomainValidationException("A purchase order version must be positive.");
        if (purchaseOrderVersion == 1 != (predecessorVersion is null))
        {
            throw new DomainValidationException("Only the first purchase order version has no predecessor.");
        }

        PredecessorVersion = predecessorVersion;
        OrganizationId = organizationId == Guid.Empty
            ? throw new DomainValidationException("A purchase order requires its organization.")
            : organizationId;
        PoNumber = PurchaseOrderCodes.PoNumber(poNumber);
        State = state;
        LegalEntityRef = legalEntityRef ??
            throw new DomainValidationException("A purchase order requires its legal entity.");
        SupplierRef = supplierRef ?? throw new DomainValidationException("A purchase order requires its supplier.");
        RequestRef = requestRef ?? throw new DomainValidationException("A purchase order requires its request.");
        ProposalRef = proposalRef ?? throw new DomainValidationException("A purchase order requires its proposal.");
        AwardRef = awardRef ?? throw new DomainValidationException("A purchase order requires its award.");
        AwardClaimRef = awardClaimRef ?? throw new DomainValidationException("A purchase order requires its claim.");
        OrderingEvidenceRef = orderingEvidenceRef;
        ApprovalRef = approvalRef;
        AmendmentRef = amendmentRef;
        TermsSnapshot = termsSnapshot ?? throw new DomainValidationException("A purchase order requires its terms.");
        Delivery = delivery;
        SourceCurrency = PurchaseOrderCodes.Currency(sourceCurrency, "Source currency");
        BaseCurrency = PurchaseOrderCodes.Currency(baseCurrency, "Base currency");
        SourceAmount = PurchaseOrderCodes.NonNegative(sourceAmount, "Source amount");
        BaseAmount = PurchaseOrderCodes.NonNegative(baseAmount, "Base amount");
        if (actorUserId == Guid.Empty)
        {
            throw new DomainValidationException("A purchase order version requires its actor.");
        }

        ActorUserId = actorUserId;
        OccurredAt = occurredAt.ToUniversalTime();
        IssuedAt = issuedAt?.ToUniversalTime();

        var lineSet = (lines ?? []).ToImmutableArray();
        if (lineSet.Length == 0)
        {
            throw new DomainValidationException("A purchase order version requires at least one line.");
        }

        if (lineSet.Length > PurchaseOrderCodes.MaxLines)
        {
            throw new PurchaseOrderPayloadTooLargeException(
                $"A purchase order supports at most {PurchaseOrderCodes.MaxLines} lines.");
        }

        if (lineSet.Select(line => line.LineId).Distinct().Count() != lineSet.Length)
        {
            throw new DomainConflictException("A purchase order version cannot repeat a line identity.");
        }

        if (lineSet.Select(line => line.CanonicalIdentity).Distinct(StringComparer.Ordinal).Count() != lineSet.Length)
        {
            throw new DomainConflictException("A purchase order version cannot repeat an award line.");
        }

        Lines = lineSet
            .OrderBy(line => line.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        BudgetOperationRefs = (budgetOperationRefs ?? [])
            .OrderBy(reference => reference.Operation, StringComparer.Ordinal)
            .ThenBy(reference => reference.OperationKey, StringComparer.Ordinal)
            .ToImmutableArray();

        if (Delivery is not null && !string.Equals(TermsSnapshot.SourceCurrency, SourceCurrency, StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                "The vendor terms snapshot currency must match the purchase order source currency.");
        }

        switch (State)
        {
            case PurchaseOrderState.Draft or PurchaseOrderState.Cancelled:
                // Nullable delivery is the documented shape of a pre-submit version.
                break;
            case PurchaseOrderState.PendingApproval or PurchaseOrderState.Approved:
                if (Delivery is null)
                {
                    throw new PurchaseOrderUnprocessableException(
                        "A presented purchase order requires a complete delivery commitment.");
                }

                break;
            case PurchaseOrderState.Issued:
                if (Delivery is null || ApprovalRef is null || OrderingEvidenceRef is null || IssuedAt is null)
                {
                    throw new PurchaseOrderUnprocessableException(
                        "An issued purchase order requires delivery, approval, ordering evidence and its instant.");
                }

                if (BudgetOperationRefs.Count == 0)
                {
                    throw new PurchaseOrderUnprocessableException(
                        "An issued purchase order requires its confirmed budget operations.");
                }

                break;
            default:
                throw new DomainValidationException("The purchase order state is not recognized.");
        }

        Digest = PurchaseOrderCanonicalizer.Hash(CanonicalDocument());
    }

    public Guid PoId { get; }
    public int PurchaseOrderVersionNumber { get; }
    public int? PredecessorVersion { get; }
    public Guid OrganizationId { get; }
    public string PoNumber { get; }
    public PurchaseOrderState State { get; }
    public PurchaseOrderEntityRef LegalEntityRef { get; }
    public PurchaseOrderEntityRef SupplierRef { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public PurchaseOrderContentRef ProposalRef { get; }
    public PurchaseOrderContentRef AwardRef { get; }
    public PurchaseOrderContentRef AwardClaimRef { get; }
    public PurchaseOrderContentRef? OrderingEvidenceRef { get; }
    public PurchaseOrderContentRef? ApprovalRef { get; }
    public PurchaseOrderContentRef? AmendmentRef { get; }
    public VendorTermsSnapshot TermsSnapshot { get; }
    public DeliveryCommitment? Delivery { get; }
    public IReadOnlyList<PurchaseOrderLine> Lines { get; }
    public IReadOnlyList<PurchaseOrderBudgetOperationRef> BudgetOperationRefs { get; }
    public decimal SourceAmount { get; }
    public string SourceCurrency { get; }
    public decimal BaseAmount { get; }
    public string BaseCurrency { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset? IssuedAt { get; }

    /// <summary><c>purchase_order_content_digest</c> computed from the published properties (REQ-02).</summary>
    public string Digest { get; }

    public bool ObligationsComplete =>
        Delivery is not null && Lines.All(line => line.AcceptanceResponsibilities.Count > 0);

    public string CanonicalDocument() => PurchaseOrderCanonicalizer.PurchaseOrderDocument(this);

    /// <summary>A successor keeps every server-owned reference and replaces the mutable content.</summary>
    public PurchaseOrderVersion With(
        PurchaseOrderState state,
        IEnumerable<PurchaseOrderLine> lines,
        DeliveryCommitment? delivery,
        IEnumerable<PurchaseOrderBudgetOperationRef>? budgetOperationRefs,
        PurchaseOrderContentRef? approvalRef,
        PurchaseOrderContentRef? orderingEvidenceRef,
        PurchaseOrderContentRef? amendmentRef,
        Guid actorUserId,
        DateTimeOffset occurredAt,
        DateTimeOffset? issuedAt,
        VendorTermsSnapshot? termsSnapshot = null)
    {
        var successors = lines.ToImmutableArray();
        return new PurchaseOrderVersion(
            PoId,
            PurchaseOrderVersionNumber + 1,
            PurchaseOrderVersionNumber,
            OrganizationId,
            PoNumber,
            state,
            LegalEntityRef,
            SupplierRef,
            RequestRef,
            ProposalRef,
            AwardRef,
            AwardClaimRef,
            orderingEvidenceRef ?? OrderingEvidenceRef,
            approvalRef ?? ApprovalRef,
            amendmentRef ?? AmendmentRef,
            termsSnapshot ?? TermsSnapshot,
            delivery,
            successors,
            budgetOperationRefs ?? BudgetOperationRefs,
            successors.Sum(line => line.GrossTotal),
            SourceCurrency,
            successors.Sum(line => line.BaseGrossTotal),
            BaseCurrency,
            actorUserId,
            occurredAt,
            issuedAt);
    }
}
