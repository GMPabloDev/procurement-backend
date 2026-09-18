using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.PurchaseOrders;

/// <summary>Payment terms published by the vendor terms snapshot (<c>{code,net_days}</c>, REQ-09).</summary>
public sealed record VendorPaymentTerms
{
    public VendorPaymentTerms(string code, int netDays)
    {
        Code = PurchaseOrderCodes.Code(code, "Payment terms code");
        NetDays = PurchaseOrderCodes.NetDays(netDays);
    }

    public string Code { get; }
    public int NetDays { get; }
}

/// <summary>
/// One frozen catalogue snapshot reference of the vendor terms (<c>{digest,line_ref}</c>, REQ-09).
/// </summary>
public sealed record VendorCatalogSnapshotRef(PurchaseOrderContentRef LineRef, string Digest)
{
    public string Digest { get; } = PurchaseOrderCodes.Digest(Digest, "Catalog snapshot digest");
}

/// <summary>
/// One effective vendor terms snapshot (<c>vendor-terms-snapshot/v1</c>, REQ-09). It is built
/// server-side from versioned sources and never branches on a supplier name, tax id or id: the
/// Supplier content of SPEC 09, the award terms of SPEC 10 and their catalogue snapshots, or the
/// Purchase Request and its Policy bundle for a Direct Purchase.
/// </summary>
public sealed record VendorTermsSnapshot
{
    public const string ContractVersion = PurchaseOrderCodes.VendorTermsSnapshotContract;

    public VendorTermsSnapshot(
        string sourceKind,
        PurchaseOrderEntityRef supplierRef,
        PurchaseOrderContentRef supplierContentRef,
        IReadOnlyList<string> supplierSupportedCurrencies,
        PurchaseOrderContentRef requestRef,
        SourcingPolicyEvaluationRef policyBundleRef,
        PurchaseOrderContentRef? awardRef,
        CommercialTerms? awardTerms,
        IReadOnlyList<VendorCatalogSnapshotRef> catalogSnapshotRefs,
        VendorPaymentTerms paymentTerms,
        string sourceCurrency,
        DateTimeOffset evaluatedAt)
    {
        SourceKind = PurchaseOrderCodes.TermsSourceKind(sourceKind);
        SupplierRef = supplierRef ?? throw new DomainValidationException("Vendor terms require their supplier.");
        SupplierContentRef = supplierContentRef ??
            throw new DomainValidationException("Vendor terms require the supplier content reference.");
        SupplierSupportedCurrencies = (supplierSupportedCurrencies ?? [])
            .Select(currency => PurchaseOrderCodes.Currency(currency, "Supplier supported currency"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(currency => currency, StringComparer.Ordinal)
            .ToImmutableArray();
        RequestRef = requestRef ?? throw new DomainValidationException("Vendor terms require the request reference.");
        PolicyBundleRef = policyBundleRef ??
            throw new DomainValidationException("Vendor terms require the policy bundle reference.");
        AwardRef = awardRef;
        AwardTerms = awardTerms;
        CatalogSnapshotRefs = (catalogSnapshotRefs ?? [])
            .OrderBy(snapshot => snapshot.LineRef.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        PaymentTerms = paymentTerms ?? throw new DomainValidationException("Vendor terms require payment terms.");
        SourceCurrency = PurchaseOrderCodes.Currency(sourceCurrency, "Source currency");
        EvaluatedAt = evaluatedAt.ToUniversalTime();

        // REQ-09: AWARD requires award/terms/request/policy references; DIRECT_PURCHASE requires a
        // null award and terms with the request and policy references present.
        switch (SourceKind)
        {
            case PurchaseOrderCodes.TermsSourceAward:
                if (AwardRef is null || AwardTerms is null)
                {
                    throw new DomainValidationException(
                        "Award vendor terms require the award and its commercial terms.");
                }

                break;
            case PurchaseOrderCodes.TermsSourceDirectPurchase:
                if (AwardRef is not null || AwardTerms is not null || CatalogSnapshotRefs.Count != 0)
                {
                    throw new DomainValidationException(
                        "Direct purchase vendor terms carry no award, terms or catalogue snapshots.");
                }

                break;
            default:
                throw new DomainValidationException("The vendor terms source kind is not recognized.");
        }

        if (!SupplierSupportedCurrencies.Contains(SourceCurrency, StringComparer.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The supplier does not support the currency of the purchase order.");
        }
    }

    public string SourceKind { get; }
    public PurchaseOrderEntityRef SupplierRef { get; }
    public PurchaseOrderContentRef SupplierContentRef { get; }
    public IReadOnlyList<string> SupplierSupportedCurrencies { get; }
    public PurchaseOrderContentRef RequestRef { get; }
    public SourcingPolicyEvaluationRef PolicyBundleRef { get; }
    public PurchaseOrderContentRef? AwardRef { get; }
    public CommercialTerms? AwardTerms { get; }
    public IReadOnlyList<VendorCatalogSnapshotRef> CatalogSnapshotRefs { get; }
    public VendorPaymentTerms PaymentTerms { get; }
    public string SourceCurrency { get; }
    public DateTimeOffset EvaluatedAt { get; }

    /// <summary><c>vendor_terms_snapshot_digest</c> of the published preimage (REQ-09).</summary>
    public string Digest => PurchaseOrderCanonicalizer.Hash(PurchaseOrderCanonicalizer.VendorTermsDocument(this));

    /// <summary>
    /// REQ-09: for a Purchase Order the snapshot must reproduce the award terms and payment code
    /// exactly. A contradictory supplier payment code is a business conflict, not a schema error.
    /// </summary>
    public void RequireRestatesAward(CommercialTerms awardTerms, string awardPaymentCode)
    {
        ArgumentNullException.ThrowIfNull(awardTerms);
        if (AwardTerms is null ||
            AwardTerms.DeliveryDays != awardTerms.DeliveryDays ||
            !string.Equals(AwardTerms.IncotermCode, awardTerms.IncotermCode, StringComparison.Ordinal) ||
            !string.Equals(AwardTerms.PaymentTermsCode, awardTerms.PaymentTermsCode, StringComparison.Ordinal) ||
            AwardTerms.WarrantyDays != awardTerms.WarrantyDays)
        {
            throw new PurchaseOrderUnprocessableException(
                "The vendor terms snapshot does not reproduce the award commercial terms.");
        }

        if (!string.Equals(PaymentTerms.Code, awardPaymentCode, StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The supplier payment terms contradict the payment terms of the award.");
        }
    }

    /// <summary>
    /// Server-side construction of the effective terms of an award consumption (REQ-09). The
    /// supplier content is authoritative for payment net days and supported currencies; the award
    /// is authoritative for delivery, warranty, incoterm and payment code.
    /// </summary>
    public static VendorTermsSnapshot ForAward(
        SupplierContentSnapshot supplier,
        CommercialTerms awardTerms,
        PurchaseOrderContentRef awardRef,
        SourcingPolicyEvaluationRef policyBundleRef,
        PurchaseOrderContentRef requestRef,
        IReadOnlyList<SourcingCatalogSnapshot> catalogSnapshots,
        string sourceCurrency,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(awardTerms);
        // REQ-09: a supplier whose current payment code no longer restates the award is a
        // contradiction, not a silent override; the Buyer must recover the award instead.
        if (!string.Equals(supplier.PaymentTerms.Code, awardTerms.PaymentTermsCode, StringComparison.Ordinal))
        {
            throw new PurchaseOrderUnprocessableException(
                "The supplier payment terms contradict the payment terms of the award.");
        }

        var snapshot = new VendorTermsSnapshot(
            PurchaseOrderCodes.TermsSourceAward,
            new PurchaseOrderEntityRef(supplier.SupplierId, supplier.Version),
            new PurchaseOrderContentRef(supplier.SupplierId, supplier.Version, supplier.ContentDigest),
            supplier.SupportedCurrencies,
            requestRef,
            policyBundleRef,
            awardRef,
            awardTerms,
            (catalogSnapshots ?? [])
                .Select(catalog => new VendorCatalogSnapshotRef(
                    new PurchaseOrderContentRef(catalog.SnapshotRef.Id, catalog.SnapshotRef.Version,
                        catalog.SnapshotRef.ContentDigest),
                    catalog.CatalogContentDigest))
                .ToArray(),
            new VendorPaymentTerms(awardTerms.PaymentTermsCode, supplier.PaymentTerms.NetDays),
            sourceCurrency,
            evaluatedAt);
        snapshot.RequireRestatesAward(awardTerms, awardTerms.PaymentTermsCode);
        return snapshot;
    }

    /// <summary>
    /// Effective terms of a Direct Purchase (REQ-09): no award, no commercial terms and no catalogue
    /// snapshot; the payment terms derive from the current Supplier content.
    /// </summary>
    public static VendorTermsSnapshot ForDirectPurchase(
        SupplierContentSnapshot supplier,
        PurchaseOrderContentRef requestRef,
        SourcingPolicyEvaluationRef policyBundleRef,
        string sourceCurrency,
        DateTimeOffset evaluatedAt) =>
        new(
            PurchaseOrderCodes.TermsSourceDirectPurchase,
            new PurchaseOrderEntityRef(supplier.SupplierId, supplier.Version),
            new PurchaseOrderContentRef(supplier.SupplierId, supplier.Version, supplier.ContentDigest),
            supplier.SupportedCurrencies,
            requestRef,
            policyBundleRef,
            awardRef: null,
            awardTerms: null,
            catalogSnapshotRefs: [],
            new VendorPaymentTerms(supplier.PaymentTerms.Code, supplier.PaymentTerms.NetDays),
            sourceCurrency,
            evaluatedAt);
}

/// <summary>
/// Read-only projection of the current Supplier content that the vendor terms builder consumes
/// (SPEC 09 <c>supplier_content_ref</c>). It keeps the module free of a second copy of the Supplier
/// contract while still revalidating the versioned reference server-side.
/// </summary>
public sealed record SupplierContentSnapshot(
    Guid SupplierId,
    int Version,
    string ContentDigest,
    SupplierPaymentTerms PaymentTerms,
    IReadOnlyList<string> SupportedCurrencies);
