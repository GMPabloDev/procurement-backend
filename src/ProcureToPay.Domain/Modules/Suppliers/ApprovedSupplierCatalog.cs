using System.Collections.Immutable;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.Domain.Modules.Suppliers;

/// <summary>
/// Immutable content of one approved catalog version (REQ-06). The negotiated price, the external
/// agreement reference, the validity window and the confirmed attachment are the facts a Policy
/// evaluation may rely on; the entry never decides a waiver.
/// </summary>
public sealed record ApprovedCatalogVersionContent
{
    public ApprovedCatalogVersionContent(
        VersionedEntityRef supplierRef,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef? productRef,
        decimal negotiatedPrice,
        string currency,
        string unitCode,
        string externalContractReference,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ApprovedCatalogEntryStatus status,
        SupplierAgreementAttachmentView attachment)
    {
        SupplierRef = supplierRef ?? throw new DomainValidationException("A catalog version requires its supplier.");
        SpendCategoryRef = spendCategoryRef ??
            throw new DomainValidationException("A catalog version requires its spend category.");
        ProductRef = productRef;
        if (supplierRef.EntityType != SupplierCodes.SupplierCatalog)
        {
            throw new DomainValidationException("The catalog supplier reference must be a SUPPLIER.");
        }

        if (!string.Equals(spendCategoryRef.Catalog, "SPEND_CATEGORY", StringComparison.Ordinal))
        {
            throw new DomainValidationException("The catalog spend category must be a SPEND_CATEGORY reference.");
        }

        NegotiatedPrice = negotiatedPrice > 0
            ? decimal.Round(negotiatedPrice, 12)
            : throw new DomainValidationException("A negotiated price must be positive.");
        if (decimal.Round(negotiatedPrice, 12) != negotiatedPrice)
        {
            throw new DomainValidationException("A negotiated price admits at most twelve decimal places.");
        }

        Currency = SupplierCodes.Currency(currency, "Negotiated currency");
        UnitCode = SupplierCodes.Code(unitCode, "Unit code");
        ExternalContractReference = SupplierCodes.DisplayName(
            externalContractReference, "External contract reference", SupplierCodes.MaxExternalReferenceScalars);
        var from = validFrom.ToUniversalTime();
        var to = validTo.ToUniversalTime();
        if (to <= from)
        {
            throw new DomainValidationException("The agreement validity window is empty.");
        }

        ValidFrom = from;
        ValidTo = to;
        Status = status;
        // The caller only publishes a CONFIRMED attachment; the frozen metadata is what enters the
        // digest so a staged upload can never become part of a published version (REQ-07).
        Attachment = attachment ?? throw new DomainValidationException("An approved catalog version requires its attachment.");
    }

    public VersionedEntityRef SupplierRef { get; }
    public VersionedCodeRef SpendCategoryRef { get; }
    public VersionedEntityRef? ProductRef { get; }
    public decimal NegotiatedPrice { get; }
    public string Currency { get; }
    public string UnitCode { get; }
    public string ExternalContractReference { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset ValidTo { get; }
    public ApprovedCatalogEntryStatus Status { get; }
    public SupplierAgreementAttachmentView Attachment { get; }

    /// <summary>Exact selector of the entry: supplier plus category plus optional product (REQ-06).</summary>
    public string SelectorKey =>
        $"{EntityKey(SupplierRef)}|{CodeKey(SpendCategoryRef)}|" +
        (ProductRef is null ? "GENERAL" : EntityKey(ProductRef));

    /// <summary>Derived state at an instant; the stored version is never mutated by the clock.</summary>
    public string EffectiveStatus(DateTimeOffset at) =>
        SupplierCodes.EffectiveStatus(Status, ValidFrom, ValidTo, at);

    internal static string EntityKey(VersionedEntityRef reference) =>
        $"{reference.EntityType}|{reference.Id:D}|{reference.Version}";

    internal static string CodeKey(VersionedCodeRef reference) =>
        $"{reference.Catalog}|{reference.Code}|{reference.Version}|{reference.Digest}";
}

/// <summary>Administrative view of one append-only catalog version (REQ-06, REQ-11).</summary>
public sealed record ApprovedCatalogVersionView(
    Guid CatalogEntryId,
    int Version,
    ApprovedCatalogVersionContent Content,
    string ContentDigest,
    int? PredecessorVersion,
    Guid ActorUserId,
    DateTimeOffset OccurredAt,
    string Reason);

/// <summary>
/// Deterministic selection of the effective catalogue entry for one Purchase Request line (REQ-08).
/// The caller never supplies a boolean, a price or an agreement status: the matcher derives the two
/// Policy facts from the persisted versions and the frozen instant.
/// </summary>
public static class ApprovedSupplierCatalogMatcher
{
    public const string AgreementNone = "NONE";
    public const string AgreementActive = "ACTIVE";
    public const string AgreementInactive = "INACTIVE";
    public const string AgreementExpired = "EXPIRED";

    /// <summary>
    /// Selects at most one effective entry. Product-specific entries win over the general entry of
    /// the same supplier and category; an ambiguous or corrupted set fails closed so the caller
    /// never observes an arbitrary winner.
    /// </summary>
    public static ApprovedSupplierSelection Select(
        VersionedEntityRef? supplierRef,
        VersionedCodeRef spendCategoryRef,
        VersionedEntityRef? productRef,
        IEnumerable<ApprovedCatalogVersionView> versions,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(spendCategoryRef);
        var materialized = (versions ?? []).ToImmutableArray();
        if (supplierRef is null)
        {
            return ApprovedSupplierSelection.None;
        }

        var scoped = materialized
            .Where(version =>
                version.Content.SupplierRef.Id == supplierRef.Id &&
                version.Content.SupplierRef.Version == supplierRef.Version &&
                string.Equals(
                    ApprovedCatalogVersionContent.CodeKey(version.Content.SpendCategoryRef),
                    ApprovedCatalogVersionContent.CodeKey(spendCategoryRef),
                    StringComparison.Ordinal))
            .ToImmutableArray();

        var specific = productRef is null
            ? []
            : scoped
                .Where(version => version.Content.ProductRef is not null &&
                                  version.Content.ProductRef.Id == productRef.Id &&
                                  version.Content.ProductRef.Version == productRef.Version)
                .ToImmutableArray();
        var level = specific.Length > 0
            ? specific
            : scoped.Where(version => version.Content.ProductRef is null).ToImmutableArray();
        if (level.Length == 0)
        {
            return ApprovedSupplierSelection.None;
        }

        var active = level
            .Where(version =>
                string.Equals(version.Content.EffectiveStatus(at), AgreementActive, StringComparison.Ordinal))
            .ToImmutableArray();
        if (active.Length > 1)
        {
            // Two effective entries for one selector are corruption, never a selection criteria.
            throw new SupplierDependencyUnavailableException(
                "Two approved catalog entries are effective for the same selector.");
        }

        if (active.Length == 1)
        {
            return new ApprovedSupplierSelection(active[0], PreferredSupplier: true, AgreementActive);
        }

        // No effective entry: the most recent version of the most specific level documents why the
        // supplier is not preferred, without inventing a state the data does not support.
        var latestFrom = level.Max(version => version.Content.ValidFrom);
        var latest = level.Where(version => version.Content.ValidFrom == latestFrom).ToImmutableArray();
        if (latest.Length != 1)
        {
            throw new SupplierDependencyUnavailableException(
                "The approved catalog entries for the selector are ambiguous.");
        }

        return new ApprovedSupplierSelection(
            latest[0], PreferredSupplier: false, latest[0].Content.EffectiveStatus(at));
    }
}

/// <summary>Frozen result of one catalogue match; <c>Entry</c> is null when nothing applies.</summary>
public sealed record ApprovedSupplierSelection(
    ApprovedCatalogVersionView? Entry,
    bool PreferredSupplier,
    string AgreementStatus)
{
    public static ApprovedSupplierSelection None { get; } =
        new(null, PreferredSupplier: false, ApprovedSupplierCatalogMatcher.AgreementNone);
}

/// <summary>Fail-closed dependency error of the supplier domain (projected as 503).</summary>
public sealed class SupplierDependencyUnavailableException(string message)
    : DomainException(message);
