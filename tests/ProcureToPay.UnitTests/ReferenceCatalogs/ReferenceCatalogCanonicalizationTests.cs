using ProcureToPay.Domain.Modules.ReferenceCatalogs;
using ProcureToPay.Domain.SharedKernel;

namespace ProcureToPay.UnitTests.ReferenceCatalogs;

/// <summary>
/// SPEC 07 REQ-03 / CA-01: the Spend Category digest is reproducible from its exact
/// <c>spend-category/v1</c> preimage under <c>policy-canonical-json/v1</c>.
/// </summary>
public sealed class ReferenceCatalogCanonicalizationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Spend_category_digest_matches_the_published_contract_vector()
    {
        var digest = ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "HARDWARE", "Hardware", OrganizationId, 1);

        Assert.Equal(
            "4ed4dc1356c8d2b3dfeacdec46ee7743b9fcfb927d94c75dea8b15c950ed30a1",
            digest);
    }

    [Fact]
    public void Spend_category_digest_changes_with_any_material_field()
    {
        var baseline = ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "HARDWARE", "Hardware", OrganizationId, 1);

        Assert.NotEqual(baseline, ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "SOFTWARE", "Hardware", OrganizationId, 1));
        Assert.NotEqual(baseline, ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "HARDWARE", "Hardware and accessories", OrganizationId, 1));
        Assert.NotEqual(baseline, ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "HARDWARE", "Hardware", Guid.Parse("22222222-2222-2222-2222-222222222222"), 1));
        Assert.NotEqual(baseline, ReferenceCatalogCanonicalizer.SpendCategoryDigest(
            "HARDWARE", "Hardware", OrganizationId, 2));
        Assert.True(ReferenceCatalogCanonicalizer.MatchesSpendCategoryDigest(
            "HARDWARE", "Hardware", OrganizationId, 1, baseline));
        Assert.False(ReferenceCatalogCanonicalizer.MatchesSpendCategoryDigest(
            "HARDWARE", "Hardware", OrganizationId, 1, new string('a', 64)));
    }

    [Fact]
    public void Codes_and_names_are_normalized_and_validated()
    {
        Assert.Equal("CC-IT-DEV", ReferenceCatalogCodes.RequireCode(" cc-it-dev ", "Code"));
        Assert.Equal("Hardware", ReferenceCatalogCodes.RequireName(" Hardware ", "Name"));
        Assert.Throws<DomainValidationException>(() => ReferenceCatalogCodes.RequireCode("1CC", "Code"));
        Assert.Throws<DomainValidationException>(() => ReferenceCatalogCodes.RequireCode("CC IT", "Code"));
        Assert.Throws<DomainValidationException>(() => ReferenceCatalogCodes.RequireCode(
            new string('A', ReferenceCatalogCodes.MaxCodeLength + 1), "Code"));
        Assert.Throws<DomainValidationException>(() => ReferenceCatalogCodes.RequireName(
            new string('x', ReferenceCatalogCodes.MaxNameScalars + 1), "Name"));
        Assert.Throws<DomainValidationException>(() => ReferenceCatalogCodes.RequireDigest("abc"));
        Assert.Equal(new string('a', 64), ReferenceCatalogCodes.RequireDigest(new string('A', 64)));
    }
}
