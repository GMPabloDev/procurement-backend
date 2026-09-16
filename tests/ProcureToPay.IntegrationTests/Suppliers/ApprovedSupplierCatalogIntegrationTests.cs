using Microsoft.EntityFrameworkCore;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.IntegrationTests.Suppliers;

/// <summary>
/// SQL Server evidence of the Approved Supplier Catalog (SPEC 09 REQ-06, REQ-07): staged and
/// confirmed attachments, append-only versions behind the current pointer, deterministic matching
/// and the clock-derived expiration.
/// </summary>
public sealed class ApprovedSupplierCatalogIntegrationTests
{
    [Fact]
    public async Task An_approved_entry_is_published_with_its_agreement_and_expires_by_clock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        var supplierVersion = await OperationalVersionAsync(harness, supplierId, cancellationToken);

        Guid entryId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var attachment = await StageAndConfirmAsync(harness, context, services, supplierId, cancellationToken);
            var outcome = await services.Catalog.SaveAsync(
                harness.OrganizationId,
                harness.BuyerId,
                catalogEntryId: null,
                expectedVersion: null,
                new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                Category(harness),
                productRef: null,
                negotiatedPrice: 900m,
                currency: "PEN",
                unitCode: "UNIT",
                externalContractReference: "ACME-2026-001",
                validFrom: DateTimeOffset.UtcNow.AddDays(-1),
                validTo: DateTimeOffset.UtcNow.AddDays(1),
                ApprovedCatalogEntryStatus.Active,
                attachment.Id,
                attachment.Version,
                "Negotiated price for standard hardware",
                "catalog-1",
                "corr-catalog",
                cancellationToken);
            entryId = outcome.Version.CatalogEntryId;
            Assert.True(outcome.RequiresApproval);

            // Nothing is effective before the decision: the pointer is still at version zero.
            Assert.Empty(await services.Catalog.ListEffectiveAsync(
                harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken));

            await services.Catalog.SubmitAsync(
                harness.OrganizationId,
                harness.BuyerId,
                entryId,
                outcome.Version.Version,
                "submit-catalog-1",
                "corr-submit-catalog",
                cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var effective = await services.Catalog.ListEffectiveAsync(
                harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken);
            var entry = Assert.Single(effective);
            Assert.Equal(entryId, entry.CatalogEntryId);
            Assert.Equal(900m, entry.NegotiatedPrice);
            Assert.Equal("ACTIVE", entry.EffectiveStatus);

            // The clock alone expires the entry: the stored version is never mutated (REQ-07).
            Assert.Empty(await services.Catalog.ListEffectiveAsync(
                harness.OrganizationId, DateTimeOffset.UtcNow.AddDays(2), cancellationToken));

            // The fact lookup of one line freezes the entry, the price digest and both Policy facts.
            var snapshot = await services.FactOwner.ResolveAsync(
                new Application.Abstractions.ApprovedSupplierFactQuery(
                    harness.OrganizationId,
                    new SupplierFactSourceLine(Guid.NewGuid(), 1, new string('b', 64)),
                    new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                    Category(harness),
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            Assert.True(snapshot.PreferredSupplier);
            Assert.Equal("ACTIVE", snapshot.AgreementStatus);
            Assert.NotNull(snapshot.CatalogEntryRef);
            Assert.Equal(snapshot.Digest, snapshot.ComputeDigest());

            // A supplier that is not referenced has no agreement and is never preferred.
            var none = await services.FactOwner.ResolveAsync(
                new Application.Abstractions.ApprovedSupplierFactQuery(
                    harness.OrganizationId,
                    new SupplierFactSourceLine(Guid.NewGuid(), 1, new string('c', 64)),
                    null,
                    Category(harness),
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            Assert.False(none.PreferredSupplier);
            Assert.Equal("NONE", none.AgreementStatus);
        }
    }

    [Fact]
    public async Task A_product_entry_prevails_and_an_unconfirmed_attachment_cannot_be_published()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        var supplierVersion = await OperationalVersionAsync(harness, supplierId, cancellationToken);
        var productId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var general = await StageAndConfirmAsync(harness, context, services, supplierId, cancellationToken);

            // A staged attachment is not publishable.
            var staged = await services.Catalog.StageAttachmentAsync(
                harness.OrganizationId, supplierId, harness.BuyerId, "draft.pdf", "application/pdf", 8,
                new MemoryStream("12345678"u8.ToArray()), cancellationToken);
            await Assert.ThrowsAsync<Domain.SharedKernel.DomainValidationException>(() => services.Catalog.SaveAsync(
                harness.OrganizationId, harness.BuyerId, null, null,
                new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                Category(harness), null, 900m, "PEN", "UNIT", "ACME-2026-001",
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
                ApprovedCatalogEntryStatus.Active, staged.Id, staged.Version,
                "Staged only", "catalog-staged", "corr-staged", cancellationToken));

            foreach (var (product, price, key) in new (Guid?, decimal, string)[]
                     {
                         (null, 1000m, "catalog-general"),
                         (productId, 900m, "catalog-product")
                     })
            {
                var outcome = await services.Catalog.SaveAsync(
                    harness.OrganizationId, harness.BuyerId, null, null,
                    new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                    Category(harness),
                    product is null ? null : new Domain.Modules.Policy.VersionedEntityRef("PRODUCT", product.Value, 1),
                    price, "PEN", "UNIT", "ACME-2026-002",
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
                    ApprovedCatalogEntryStatus.Active, general.Id, general.Version,
                    "Negotiated price", key, $"corr-{key}", cancellationToken);
                await services.Catalog.SubmitAsync(
                    harness.OrganizationId, harness.BuyerId, outcome.Version.CatalogEntryId,
                    outcome.Version.Version, $"submit-{key}", $"corr-submit-{key}", cancellationToken);
                await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);
            }

            // The product entry of the same supplier and category wins over the general one.
            var generalEntry = await services.Catalog.ListEffectiveAsync(
                harness.OrganizationId, DateTimeOffset.UtcNow, cancellationToken);
            Assert.Equal(2, generalEntry.Count);
            var snapshot = await services.FactOwner.ResolveAsync(
                new Application.Abstractions.ApprovedSupplierFactQuery(
                    harness.OrganizationId,
                    new SupplierFactSourceLine(Guid.NewGuid(), 1, new string('d', 64)),
                    new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                    Category(harness),
                    new Domain.Modules.Policy.VersionedEntityRef("PRODUCT", productId, 1),
                    DateTimeOffset.UtcNow),
                cancellationToken);
            Assert.Equal(900m, await PriceOfAsync(context, snapshot.CatalogEntryRef!.Id, cancellationToken));

            // A different product version does not match, so the general entry applies.
            var fallback = await services.FactOwner.ResolveAsync(
                new Application.Abstractions.ApprovedSupplierFactQuery(
                    harness.OrganizationId,
                    new SupplierFactSourceLine(Guid.NewGuid(), 1, new string('e', 64)),
                    new Domain.Modules.Policy.VersionedEntityRef("SUPPLIER", supplierId, supplierVersion),
                    Category(harness),
                    new Domain.Modules.Policy.VersionedEntityRef("PRODUCT", productId, 2),
                    DateTimeOffset.UtcNow),
                cancellationToken);
            Assert.Equal(1000m, await PriceOfAsync(context, fallback.CatalogEntryRef!.Id, cancellationToken));
        }
    }

    [Fact]
    public async Task The_agreement_download_is_audited_and_the_attachment_metadata_is_immutable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await SupplierHarness.StartAsync(cancellationToken);
        var supplierId = await ApprovedSupplierAsync(harness, cancellationToken);
        await using var context = harness.CreateContext();
        var services = harness.CreateServices(context);
        var attachment = await StageAndConfirmAsync(harness, context, services, supplierId, cancellationToken);

        var url = await services.Catalog.CreateDownloadUrlAsync(
            harness.OrganizationId, attachment.Id, harness.BuyerId, "corr-download", cancellationToken);
        Assert.Contains("agreements.test", url.ToString(), StringComparison.Ordinal);
        Assert.True(await context.SupplierAuditRecords.AsNoTracking().AnyAsync(
            record => record.Action == SupplierCodes.ActionAttachmentDownloaded, cancellationToken));

        // The staged object is stored under an opaque key that never contains the file name.
        Assert.All(harness.Storage.Objects.Keys, key =>
            Assert.DoesNotContain("agreement.pdf", key, StringComparison.Ordinal));
        var stored = await context.SupplierAgreementAttachments
            .AsNoTracking()
            .SingleAsync(record => record.Id == attachment.Id, cancellationToken);
        Assert.Equal((int)SupplierAgreementAttachmentState.Confirmed, stored.State);
        Assert.Equal(attachment.Sha256, stored.Sha256);
    }

    private static async Task<decimal> PriceOfAsync(
        ProcureToPay.Infrastructure.Persistence.ProcureToPayDbContext context,
        Guid catalogEntryId,
        CancellationToken cancellationToken)
    {
        var entry = await context.ApprovedSupplierCatalogEntries
            .AsNoTracking()
            .SingleAsync(record => record.Id == catalogEntryId, cancellationToken);
        return await context.ApprovedSupplierCatalogVersions
            .AsNoTracking()
            .Where(record => record.CatalogEntryId == catalogEntryId && record.Version == entry.CurrentVersion)
            .Select(record => record.NegotiatedPrice)
            .SingleAsync(cancellationToken);
    }

    private static async Task<SupplierAgreementAttachmentView> StageAndConfirmAsync(
        SupplierHarness harness,
        ProcureToPay.Infrastructure.Persistence.ProcureToPayDbContext context,
        Services services,
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var bytes = "agreement-content"u8.ToArray();
        var attachment = await services.Catalog.StageAttachmentAsync(
            harness.OrganizationId, supplierId, harness.BuyerId, "agreement.pdf", "application/pdf",
            bytes.Length, new MemoryStream(bytes), cancellationToken);
        return await services.Catalog.ConfirmAttachmentAsync(
            harness.OrganizationId, attachment.Id, harness.BuyerId, attachment.Sha256, cancellationToken);
    }

    private static Domain.Modules.Policy.VersionedCodeRef Category(SupplierHarness harness) =>
        new("SPEND_CATEGORY", harness.SpendCategoryCode, harness.SpendCategoryVersion, harness.SpendCategoryDigest);

    private static async Task<Guid> ApprovedSupplierAsync(
        SupplierHarness harness,
        CancellationToken cancellationToken)
    {
        Guid supplierId;
        await using (var context = harness.CreateContext())
        {
            var services = harness.CreateServices(context);
            var outcome = await services.Governance.CreateAsync(
                harness.OrganizationId, harness.BuyerId,
                new SupplierVersionContent(
                    "ABC Tech SAC", null, "PE", "20123456789",
                    [
                        new SupplierAddress(
                            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "HQ", "Av. Siempre Viva 742",
                            null, "Lima", null, null, "PE")
                    ],
                    [new SupplierContact(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Ana", "ana@abc.test", null, null)],
                    new SupplierPaymentTerms("NET30", 30),
                    ["PEN"],
                    [Category(harness)],
                    SupplierOperationalStatus.Draft,
                    SupplierRiskStatus.Low,
                    null,
                    []),
                SupplierOperationalStatus.Active,
                "New supplier",
                $"create-{Guid.NewGuid():N}",
                "corr-create",
                cancellationToken);
            supplierId = outcome.Version.SupplierId;
            await services.Governance.SubmitAsync(
                harness.OrganizationId, harness.BuyerId, supplierId, outcome.Version.Version,
                "Submit", $"submit-{Guid.NewGuid():N}", "corr-submit", cancellationToken);
        }

        await harness.DecideAndDispatchAsync(ApprovalDecisionAction.Approve, cancellationToken);
        return supplierId;
    }

    private static async Task<int> OperationalVersionAsync(
        SupplierHarness harness,
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        await using var context = harness.CreateContext();
        var view = await harness.CreateServices(context).Persistence.FindOperationalAsync(
            harness.OrganizationId, supplierId, cancellationToken);
        return view!.Version;
    }
}
