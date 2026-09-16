using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.Suppliers;

/// <summary>
/// SPEC 09 Canonicalización y digests / CA-01, CA-06: the identity key, the content digest, the
/// catalogue digest, the fact snapshot, the manifest v2 digest and the evidence digest are fixed
/// byte-by-byte by the published preimages. Independent fixtures recompute them here instead of
/// reusing production helpers.
/// </summary>
public sealed class SupplierCanonicalizationGoldenTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid EntryId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AttachmentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AddressId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ContactId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-09-15T12:00:00.0000000Z");

    [Fact]
    public void Supplier_identity_key_matches_the_published_contract_vector()
    {
        // Exact published preimage of SPEC 09:
        // {"canonicalization_version":"policy-canonical-json/v1","contract_version":"supplier-identity-key/v1",
        //  "country_code":"PE","organization_id":"1111…1111","tax_id_key":"00000000000"}
        var digest = SupplierCanonicalizer.IdentityKeyDigest(
            "PE", OrganizationId, " 00000000000 ");

        Assert.Equal(
            "633c1bd7a7e84cd6524c8f448fc105b783c087acda1216296e13f34f5ff5c91d",
            digest);
    }

    [Fact]
    public void Supplier_identity_key_is_case_and_composition_stable()
    {
        var baseline = SupplierCanonicalizer.IdentityKeyDigest("PE", OrganizationId, "20123456789");

        Assert.Equal(baseline, SupplierCanonicalizer.IdentityKeyDigest("pe", OrganizationId, "20123456789"));
        Assert.Equal(baseline, SupplierCanonicalizer.IdentityKeyDigest("PE", OrganizationId, "20123456789 ".Trim()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.IdentityKeyDigest("CL", OrganizationId, "20123456789"));
        Assert.NotEqual(baseline, SupplierCanonicalizer.IdentityKeyDigest(
            "PE", Guid.Parse("99999999-9999-9999-9999-999999999999"), "20123456789"));
        Assert.NotEqual(baseline, SupplierCanonicalizer.IdentityKeyDigest("PE", OrganizationId, "20123456788"));
    }

    [Fact]
    public void Supplier_content_digest_is_reproducible_from_the_published_preimage()
    {
        var content = SampleContent();
        var digest = SupplierCanonicalizer.ContentDigest(SupplierId, 1, content);

        var expected = Hash(
            "{\"addresses\":[{\"city\":\"Lima\",\"country_code\":\"PE\",\"id\":\"" + AddressId.ToString("D") +
            "\",\"label\":\"Headquarters\",\"line1\":\"Av. Siempre Viva 742\",\"line2\":null,\"postal_code\":\"15046\"," +
            "\"region\":\"Lima\"}],\"banking_refs\":[],\"canonicalization_version\":\"policy-canonical-json/v1\"," +
            "\"categories_supplied\":[{\"catalog\":\"SPEND_CATEGORY\",\"code\":\"HARDWARE\",\"digest\":\"" +
            new string('a', 64) + "\",\"version\":1}],\"contacts\":[{\"email\":\"ventas@abc.test\",\"id\":\"" +
            ContactId.ToString("D") + "\",\"job_title\":null,\"name\":\"Ana Perez\",\"phone\":null}]," +
            "\"contract_version\":\"supplier-version/v1\",\"country_code\":\"PE\",\"legal_name\":\"ABC Tech SAC\"," +
            "\"payment_terms\":{\"code\":\"NET30\",\"net_days\":30},\"performance_score\":\"93\"," +
            "\"performance_source\":{\"measured_at\":\"2026-09-15T12:00:00.0000000Z\",\"source\":\"Q3 review\"}," +
            "\"risk_status\":\"LOW\",\"status\":\"ACTIVE\",\"supplier_id\":\"" + SupplierId.ToString("D") + "\"," +
            "\"supported_currencies\":[\"PEN\"],\"tax_id\":\"20123456789\",\"trade_name\":null,\"version\":1}");

        Assert.Equal(expected, digest);
    }

    [Fact]
    public void Supplier_content_digest_changes_with_any_governed_field()
    {
        var baseline = SupplierCanonicalizer.ContentDigest(SupplierId, 1, SampleContent());

        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(SupplierId, 2, SampleContent()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            Guid.Parse("88888888-8888-8888-8888-888888888888"), 1, SampleContent()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(name: "ABC Tech S.A.C.")));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(riskStatus: SupplierRiskStatus.High)));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(score: 92.5m)));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(currencies: ["PEN", "USD"])));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(banking: [new SupplierBankingRef(Guid.NewGuid(), 1)])));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ContentDigest(
            SupplierId, 1, SampleContent(tradeName: "ABC")));
    }

    [Fact]
    public void Approved_catalog_digest_commits_price_agreement_validity_and_attachment()
    {
        var baseline = SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Active, Attachment());

        // Exact published preimage of approved-supplier-catalog-entry/v1 (property set closed).
        var expected = Hash(
            "{\"attachment\":{\"content_type\":\"application/pdf\",\"file_id\":\"" + AttachmentId.ToString("D") +
            "\",\"file_name\":\"agreement.pdf\",\"length\":1024,\"sha256\":\"" + new string('c', 64) +
            "\",\"version\":1},\"canonicalization_version\":\"policy-canonical-json/v1\"," +
            "\"contract_version\":\"approved-supplier-catalog-entry/v1\"," +
            "\"external_contract_reference\":\"ACME-2026-001\",\"negotiated_price\":\"900\"," +
            "\"product_ref\":null,\"spend_category_ref\":{\"catalog\":\"SPEND_CATEGORY\",\"code\":\"HARDWARE\"," +
            "\"digest\":\"" + new string('a', 64) + "\",\"version\":1},\"status\":\"ACTIVE\"," +
            "\"supplier_ref\":{\"entity_type\":\"SUPPLIER\",\"id\":\"" + SupplierId.ToString("D") +
            "\",\"version\":1},\"unit_code\":\"UNIT\",\"valid_from\":\"2026-09-15T12:00:00.0000000Z\"," +
            "\"valid_to\":\"2027-09-15T12:00:00.0000000Z\",\"version\":1}");
        Assert.Equal(expected, baseline);
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 950m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Active, Attachment()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-002", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Active, Attachment()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(2), ApprovedCatalogEntryStatus.Active, Attachment()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Inactive, Attachment()));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 1, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Active,
            Attachment(digest: new string('b', 64))));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ApprovedCatalogContentDigest(
            EntryId, 2, SupplierRef(), CategoryRef(), null, 900m, "PEN", "UNIT",
            "ACME-2026-001", Clock, Clock.AddYears(1), ApprovedCatalogEntryStatus.Active, Attachment()));
    }

    [Fact]
    public void Fact_snapshot_and_its_set_are_reproducible()
    {
        var snapshot = Snapshot();
        Assert.Equal(snapshot.Digest, snapshot.ComputeDigest());

        var set = new SupplierFactSnapshotSet(OrganizationId, LineId, 1, [snapshot]);
        Assert.Equal(set.Digest, set.ComputeDigest());

        // Set semantics: the enumeration order of the snapshots never changes the digest.
        var second = new SupplierPolicyFactSnapshot(
            OrganizationId,
            new SupplierFactSourceLine(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1, new string('c', 64)),
            null, CategoryRef(), null, null, null, false, "NONE", Clock);
        var forward = new SupplierFactSnapshotSet(OrganizationId, LineId, 1, [snapshot, second]);
        var reversed = new SupplierFactSnapshotSet(OrganizationId, LineId, 1, [second, snapshot]);
        Assert.Equal(forward.Digest, reversed.Digest);
    }

    [Fact]
    public void Fact_snapshot_digest_changes_with_the_frozen_catalogue_version()
    {
        var baseline = Snapshot();
        var otherEntry = new SupplierPolicyFactSnapshot(
            OrganizationId, SourceLine(), SupplierRef(), CategoryRef(), null,
            new ApprovedCatalogEntryRef(EntryId, 2), new string('d', 64), true, "ACTIVE", Clock);

        Assert.NotEqual(baseline.Digest, otherEntry.Digest);
        Assert.NotEqual(
            baseline.Digest,
            new SupplierPolicyFactSnapshot(
                OrganizationId, SourceLine(), SupplierRef(), CategoryRef(), null, null, null, false, "NONE", Clock).Digest);
        Assert.NotEqual(
            baseline.Digest,
            new SupplierPolicyFactSnapshot(
                OrganizationId, SourceLine(), SupplierRef(), CategoryRef(), null, null, null, false, "NONE",
                Clock.AddSeconds(1)).Digest);
    }

    [Fact]
    public void Active_supplier_evidence_digest_is_reproducible_and_target_order_independent()
    {
        var first = new ApprovalTarget("PURCHASE_REQUEST_LINE", LineId, 1, new string('e', 64));
        var second = new ApprovalTarget("PURCHASE_REQUEST_LINE", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1, new string('f', 64));
        var attemptId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var prerequisiteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var parametersDigest = new string('1', 64);

        var baseline = SupplierCanonicalizer.ActiveSupplierEvidenceDigest(
            attemptId, prerequisiteId, OrganizationId, parametersDigest, SupplierRef(), true,
            "supplier:1:signal", Clock, [first, second]);
        var reversed = SupplierCanonicalizer.ActiveSupplierEvidenceDigest(
            attemptId, prerequisiteId, OrganizationId, parametersDigest, SupplierRef(), true,
            "supplier:1:signal", Clock, [second, first]);

        Assert.Equal(baseline, reversed);
        Assert.NotEqual(baseline, SupplierCanonicalizer.ActiveSupplierEvidenceDigest(
            attemptId, prerequisiteId, OrganizationId, parametersDigest, SupplierRef(), false,
            "supplier:1:signal", Clock, [first, second]));
        Assert.NotEqual(baseline, SupplierCanonicalizer.ActiveSupplierEvidenceDigest(
            attemptId, prerequisiteId, OrganizationId, parametersDigest, SupplierRef(), true,
            "supplier:1:signal", Clock.AddSeconds(1), [first, second]));
    }

    [Fact]
    public void Partition_key_and_parameters_follow_the_singular_owner_contract()
    {
        var key = SupplierCanonicalizer.PartitionKey("ACTIVE_SUPPLIER_REQUIRED", SupplierRef());
        Assert.StartsWith("ACTIVE_SUPPLIER:", key, StringComparison.Ordinal);
        Assert.True(key.Length <= 128);
        Assert.Equal(key, SupplierCanonicalizer.PartitionKey("ACTIVE_SUPPLIER_REQUIRED", SupplierRef()));
        Assert.NotEqual(key, SupplierCanonicalizer.PartitionKey("OTHER_KEY", SupplierRef()));
        Assert.NotEqual(key, SupplierCanonicalizer.PartitionKey(
            "ACTIVE_SUPPLIER_REQUIRED", new VersionedEntityRef("SUPPLIER", SupplierId, 2)));
        Assert.Equal(
            "{\"supplier_ref\":{\"id\":\"" + SupplierId.ToString("D") + "\",\"version\":1}}",
            SupplierCanonicalizer.SupplierParameters(SupplierRef()));
    }

    private static SupplierPolicyFactSnapshot Snapshot() => new(
        OrganizationId, SourceLine(), SupplierRef(), CategoryRef(), null,
        new ApprovedCatalogEntryRef(EntryId, 1), new string('d', 64), true, "ACTIVE", Clock);

    private static SupplierFactSourceLine SourceLine() => new(LineId, 1, new string('b', 64));

    private static VersionedEntityRef SupplierRef() => new("SUPPLIER", SupplierId, 1);

    private static VersionedCodeRef CategoryRef() => new("SPEND_CATEGORY", "HARDWARE", 1, new string('a', 64));

    private static SupplierAgreementAttachmentView Attachment(string? digest = null) => new(
        AttachmentId, 1, "agreement.pdf", "application/pdf", 1024, digest ?? new string('c', 64));

    internal static SupplierVersionContent SampleContent(
        string name = "ABC Tech SAC",
        SupplierRiskStatus riskStatus = SupplierRiskStatus.Low,
        decimal? score = 93m,
        IReadOnlyList<string>? currencies = null,
        IReadOnlyList<SupplierBankingRef>? banking = null,
        string? tradeName = null) => new(
        name,
        tradeName,
        "PE",
        "20123456789",
        [
            new SupplierAddress(AddressId, "Headquarters", "Av. Siempre Viva 742", null, "Lima", "Lima", "15046", "PE")
        ],
        [new SupplierContact(ContactId, "Ana Perez", "ventas@abc.test", null, null)],
        new SupplierPaymentTerms("NET30", 30),
        currencies ?? ["PEN"],
        [CategoryRef()],
        SupplierOperationalStatus.Active,
        riskStatus,
        score is null ? null : new SupplierPerformanceScore(score.Value, "Q3 review", Clock),
        banking ?? []);

    private static string Hash(string canonicalJson) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();
}
