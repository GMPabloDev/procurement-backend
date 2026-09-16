using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Suppliers;

/// <summary>
/// SPEC 09 REQ-08 / CA-06: the effective catalogue selection is deterministic, prefers the
/// product-specific entry of the same supplier and category, derives the two Policy facts from the
/// frozen instant and fails closed when the data is ambiguous.
/// </summary>
public sealed class ApprovedSupplierCatalogMatcherTests
{
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CategoryEntryId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ProductEntryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ProductId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-15T12:00:00.0000000Z");

    [Fact]
    public void Without_a_declared_supplier_nothing_is_preferred()
    {
        var selection = ApprovedSupplierCatalogMatcher.Select(
            null, Category(), null, [Version(CategoryEntryId, product: null)], Clock);

        Assert.Null(selection.Entry);
        Assert.False(selection.PreferredSupplier);
        Assert.Equal("NONE", selection.AgreementStatus);
    }

    [Fact]
    public void An_effective_entry_of_the_category_is_preferred_and_active()
    {
        var selection = ApprovedSupplierCatalogMatcher.Select(
            Supplier(), Category(), null, [Version(CategoryEntryId, product: null)], Clock);

        Assert.NotNull(selection.Entry);
        Assert.True(selection.PreferredSupplier);
        Assert.Equal("ACTIVE", selection.AgreementStatus);
    }

    [Fact]
    public void A_product_entry_wins_over_the_general_entry()
    {
        var general = Version(CategoryEntryId, product: null, price: 1000m);
        var specific = Version(ProductEntryId, product: new VersionedEntityRef("PRODUCT", ProductId, 1), price: 900m);

        var selection = ApprovedSupplierCatalogMatcher.Select(
            Supplier(), Category(), new VersionedEntityRef("PRODUCT", ProductId, 1), [general, specific], Clock);

        Assert.Same(specific, selection.Entry);
        Assert.Equal(900m, selection.Entry!.Content.NegotiatedPrice);
    }

    [Fact]
    public void A_product_entry_of_another_version_does_not_apply()
    {
        var general = Version(CategoryEntryId, product: null);
        var specific = Version(ProductEntryId, product: new VersionedEntityRef("PRODUCT", ProductId, 2));

        var selection = ApprovedSupplierCatalogMatcher.Select(
            Supplier(), Category(), new VersionedEntityRef("PRODUCT", ProductId, 1), [general, specific], Clock);

        Assert.Same(general, selection.Entry);
    }

    [Fact]
    public void An_expired_entry_is_not_preferred_but_still_documents_the_agreement()
    {
        var expired = Version(CategoryEntryId, product: null, validTo: Clock.AddDays(-1));

        var selection = ApprovedSupplierCatalogMatcher.Select(
            Supplier(), Category(), null, [expired], Clock);

        Assert.Same(expired, selection.Entry);
        Assert.False(selection.PreferredSupplier);
        Assert.Equal("EXPIRED", selection.AgreementStatus);
    }

    [Fact]
    public void An_inactive_or_future_entry_is_reported_as_inactive()
    {
        var inactive = Version(CategoryEntryId, product: null, status: ApprovedCatalogEntryStatus.Inactive);
        var future = Version(ProductEntryId, product: null, validFrom: Clock.AddDays(1));

        Assert.Equal(
            "INACTIVE",
            ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [inactive], Clock).AgreementStatus);
        Assert.Equal(
            "INACTIVE",
            ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [future], Clock).AgreementStatus);
    }

    [Fact]
    public void An_entry_of_another_supplier_category_or_version_never_applies()
    {
        var otherSupplier = Version(
            CategoryEntryId, product: null, supplierRef: new VersionedEntityRef("SUPPLIER", SupplierId, 2));
        var otherCategory = Version(
            CategoryEntryId, product: null, category: new VersionedCodeRef("SPEND_CATEGORY", "SOFTWARE", 1, new string('a', 64)));

        Assert.Null(ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [otherSupplier], Clock).Entry);
        Assert.Null(ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [otherCategory], Clock).Entry);
    }

    [Fact]
    public void Two_effective_entries_for_one_selector_fail_closed()
    {
        var first = Version(CategoryEntryId, product: null, validTo: Clock.AddDays(10));
        var second = Version(ProductEntryId, product: null, validTo: Clock.AddDays(20));

        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [first, second], Clock));
    }

    [Fact]
    public void Two_versions_of_the_same_root_with_the_same_start_are_ambiguous()
    {
        var first = Version(CategoryEntryId, product: null, validTo: Clock.AddDays(-1));
        var second = Version(CategoryEntryId, product: null, validTo: Clock.AddDays(-2), version: 2);

        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            ApprovedSupplierCatalogMatcher.Select(Supplier(), Category(), null, [first, second], Clock));
    }

    private static VersionedEntityRef Supplier() => new("SUPPLIER", SupplierId, 1);

    private static VersionedCodeRef Category() => new("SPEND_CATEGORY", "HARDWARE", 1, new string('a', 64));

    private static ApprovedCatalogVersionView Version(
        Guid id,
        VersionedEntityRef? product,
        decimal price = 900m,
        int version = 1,
        ApprovedCatalogEntryStatus status = ApprovedCatalogEntryStatus.Active,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        VersionedEntityRef? supplierRef = null,
        VersionedCodeRef? category = null) => new(
        id,
        version,
        new ApprovedCatalogVersionContent(
            supplierRef ?? Supplier(),
            category ?? Category(),
            product,
            price,
            "PEN",
            "UNIT",
            "ACME-2026-001",
            validFrom ?? Clock.AddDays(-10),
            validTo ?? Clock.AddDays(10),
            status,
            new SupplierAgreementAttachmentView(
                Guid.NewGuid(), 1, "agreement.pdf", "application/pdf", 1024, new string('c', 64))),
        new string('d', 64),
        PredecessorVersion: null,
        Guid.NewGuid(),
        Clock,
        "Reviewed");
}
