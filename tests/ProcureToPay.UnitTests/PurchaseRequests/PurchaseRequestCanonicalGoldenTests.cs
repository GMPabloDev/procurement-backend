using System.Text.Json;
using ProcureToPay.Domain.Modules.Policy;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.PurchaseRequests;

/// <summary>
/// Independent regression of the SPEC 06 golden vectors published in the contract (CA-07): the
/// expected bytes and SHA-256 values are written by hand, never derived from production helpers.
/// </summary>
public sealed class PurchaseRequestCanonicalGoldenTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LegalEntityId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset AttestedAt =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reference_attestation_golden_bytes_and_digest_are_reproducible()
    {
        var attestation = PurchaseRequestReferenceAttestation.Create(
            RequestId,
            1,
            OrganizationId,
            AttestedAt,
            [
                new PurchaseRequestReferenceAssertion(
                    PurchaseRequestAssertionType.ActiveInOrganization,
                    "organization-db/v1",
                    "organization-domain",
                    PurchaseRequestAttestedRef.ForEntity(
                        PurchaseRequestReferenceType.LegalEntity, LegalEntityId, 1),
                    "ACTIVE",
                    null)
            ]);

        Assert.Equal(
            "e00b6c3fb9488a27cee654c6c2633e00fc280ffd626dfb8ac29f063b0ad19194",
            attestation.Digest);
        Assert.Equal(
            "{\"assertions\":[{\"assertion_type\":\"ACTIVE_IN_ORGANIZATION\"," +
            "\"owner_contract_version\":\"organization-db/v1\",\"owner_id\":\"organization-domain\"," +
            "\"source_ref\":{\"code\":null,\"digest\":null,\"id\":\"44444444-4444-4444-4444-444444444444\"," +
            "\"kind\":\"ENTITY\",\"type\":\"LEGAL_ENTITY\",\"version\":1},\"status\":\"ACTIVE\"," +
            "\"target_ref\":null}],\"attested_at\":\"2026-09-13T12:00:00.0000000Z\"," +
            "\"canonicalization_version\":\"policy-canonical-json/v1\"," +
            "\"contract_version\":\"purchase-request-reference-attestation/v1\"," +
            "\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"request_id\":\"22222222-2222-2222-2222-222222222222\",\"request_version\":1}",
            PurchaseRequestCanonicalizer.SerializeAttestation(attestation));
    }

    [Fact]
    public void Materiality_golden_bytes_and_digest_are_reproducible()
    {
        var digest = PurchaseRequestPolicyProjection.MaterialityDigest(
            SnapshotJson,
            LineId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GROSS_AMOUNT_BASE"] =
                    "pr://22222222-2222-2222-2222-222222222222/versions/1/lines/" +
                    "33333333-3333-3333-3333-333333333333/versions/1#base_amount"
            });

        Assert.Equal(
            "2a5dcf855c7ec1c9f18f51486a0ed6c26f9e4c4d0c4a334c62eb6968ac7c31f9",
            digest);
    }

    [Fact]
    public void Materiality_is_set_order_invariant_and_field_sensitive()
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GROSS_AMOUNT_BASE"] =
                "pr://22222222-2222-2222-2222-222222222222/versions/1/lines/" +
                "33333333-3333-3333-3333-333333333333/versions/1#base_amount",
            ["PURCHASE_TYPE"] =
                "pr://22222222-2222-2222-2222-222222222222/versions/1/lines/" +
                "33333333-3333-3333-3333-333333333333/versions/1#purchase_type"
        };
        var first = PurchaseRequestPolicyProjection.MaterialityDigest(SnapshotJsonWithPurchaseType, LineId, provenance);
        var reordered = PurchaseRequestPolicyProjection.MaterialityDigest(
            SnapshotJsonWithPurchaseTypeReorderedFacts, LineId, provenance);
        var changed = PurchaseRequestPolicyProjection.MaterialityDigest(
            SnapshotJsonWithPurchaseType.Replace("\"GOOD\"", "\"SERVICE\"", StringComparison.Ordinal),
            LineId,
            provenance);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Line_content_requires_fx_exactly_when_currencies_differ()
    {
        Assert.Throws<DomainValidationException>(() => LineContent(
            transactionCurrency: "USD",
            baseCurrency: "PEN",
            fx: null));
        Assert.Throws<DomainValidationException>(() => LineContent(
            transactionCurrency: "PEN",
            baseCurrency: "PEN",
            fx: PurchaseRequestFxRef.Create(Guid.NewGuid(), 1, "PEN", "USD", 3.75m, new DateOnly(2026, 9, 1))));
        var content = LineContent(
            transactionCurrency: "USD",
            baseCurrency: "PEN",
            fx: PurchaseRequestFxRef.Create(Guid.NewGuid(), 1, "PEN", "USD", 3.75m, new DateOnly(2026, 9, 1)));
        Assert.NotNull(content.FxAttestationRef);
    }

    [Fact]
    public void Line_content_rejects_preferred_and_required_product_together()
    {
        Assert.Throws<DomainValidationException>(() => new PurchaseRequestLineContent(
            100m, "PEN", 100m, "PEN", 2026, "GOOD", SpendCategory(), CostCenter(), Department(),
            Department(), User(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            null, Product(), Product(), false, false, "NONE", "Need", [], null));
    }

    private static PurchaseRequestLineContent LineContent(
        string transactionCurrency,
        string baseCurrency,
        PurchaseRequestFxRef? fx) =>
        new(
            100m,
            transactionCurrency,
            375m,
            baseCurrency,
            2026,
            "GOOD",
            SpendCategory(),
            CostCenter(),
            Department(),
            Department(),
            User(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            null,
            null,
            null,
            false,
            false,
            "NONE",
            "Need",
            [],
            fx);

    private static VersionedCodeRef SpendCategory() =>
        new("SPEND_CATEGORY", "HARDWARE", 1, new string('a', 64));

    private static VersionedEntityRef CostCenter() =>
        new("COST_CENTER", Guid.Parse("66666666-6666-6666-6666-666666666666"), 1);

    private static VersionedEntityRef Department() =>
        new("DEPARTMENT", Guid.Parse("77777777-7777-7777-7777-777777777777"), 1);

    private static VersionedEntityRef User(Guid id) => new("USER", id, 1);

    private static VersionedEntityRef Product() =>
        new("PRODUCT", Guid.Parse("88888888-8888-8888-8888-888888888888"), 1);

    private const string MoneyValue =
        "{\"catalog\":null,\"currency\":\"PEN\",\"digest\":null,\"entity_id\":null,\"kind\":\"MONEY\"," +
        "\"lower_bound\":null,\"lower_inclusive\":null,\"members\":null,\"question_code\":null," +
        "\"reference_type\":null,\"schema_version\":null,\"upper_bound\":null,\"upper_inclusive\":null," +
        "\"value\":\"100\",\"value_kind\":null,\"version\":null}";

    private const string SnapshotJson =
        "{\"base_currency\":\"PEN\",\"canonicalization_version\":\"policy-canonical-json/v1\"," +
        "\"evaluated_at_utc\":\"2026-09-13T12:00:00.0000000Z\",\"facts\":{}," +
        "\"legal_entity_id\":\"44444444-4444-4444-4444-444444444444\"," +
        "\"lines\":[{\"facts\":{\"GROSS_AMOUNT_BASE\":" + MoneyValue + "}," +
        "\"subject\":{\"id\":\"33333333-3333-3333-3333-333333333333\",\"version\":1}}]," +
        "\"organization_id\":\"11111111-1111-1111-1111-111111111111\"," +
        "\"subject\":{\"id\":\"22222222-2222-2222-2222-222222222222\",\"version\":1}}";

    private const string PurchaseTypeValue =
        "{\"catalog\":null,\"currency\":null,\"digest\":null,\"entity_id\":null,\"kind\":\"CODE\"," +
        "\"lower_bound\":null,\"lower_inclusive\":null,\"members\":null,\"question_code\":null," +
        "\"reference_type\":null,\"schema_version\":null,\"upper_bound\":null,\"upper_inclusive\":null," +
        "\"value\":\"GOOD\",\"value_kind\":null,\"version\":null}";

    private static readonly string SnapshotJsonWithPurchaseType =
        "{\"base_currency\":\"PEN\",\"facts\":{},\"legal_entity_id\":\"44444444-4444-4444-4444-444444444444\"," +
        "\"lines\":[{\"facts\":{\"GROSS_AMOUNT_BASE\":" + MoneyValue + ",\"PURCHASE_TYPE\":" +
        PurchaseTypeValue + "},\"subject\":{\"id\":\"33333333-3333-3333-3333-333333333333\",\"version\":1}}]," +
        "\"organization_id\":\"11111111-1111-1111-1111-111111111111\"}";

    private static readonly string SnapshotJsonWithPurchaseTypeReorderedFacts =
        "{\"base_currency\":\"PEN\",\"facts\":{},\"legal_entity_id\":\"44444444-4444-4444-4444-444444444444\"," +
        "\"lines\":[{\"facts\":{\"PURCHASE_TYPE\":" + PurchaseTypeValue + ",\"GROSS_AMOUNT_BASE\":" +
        MoneyValue + "},\"subject\":{\"id\":\"33333333-3333-3333-3333-333333333333\",\"version\":1}}]," +
        "\"organization_id\":\"11111111-1111-1111-1111-111111111111\"}";
}
