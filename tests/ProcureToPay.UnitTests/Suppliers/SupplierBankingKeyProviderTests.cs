using Microsoft.Extensions.Configuration;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Infrastructure.Persistence.Suppliers;

namespace ProcureToPay.UnitTests.Suppliers;

/// <summary>
/// SPEC 09 REQ-05 / CA-04: the banking envelope is authenticated, bound to its exact row identity and
/// refuses to reveal anything when the key material is absent or the ciphertext was tampered.
/// </summary>
public sealed class SupplierBankingKeyProviderTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DetailId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ISupplierBankingKeyProvider Provider(string? key = null) =>
        new ConfigurationSupplierBankingKeyProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Supplier:Banking:KeyBase64"] = key ?? new string('A', 43) + "=",
                    ["Supplier:Banking:KeyVersion"] = "unit-v1"
                })
                .Build());

    [Fact]
    public void A_round_trip_recovers_the_exact_plaintext_and_a_different_binding_does_not()
    {
        var provider = Provider();
        var binding = SupplierBankingService.AssociatedData(OrganizationId, SupplierId, DetailId, 1);
        var ciphertext = provider.Encrypt("{\"account_number\":\"00123456789012345678\"}", binding, out var nonce, out var tag);

        Assert.Equal("unit-v1", provider.KeyVersion);
        Assert.Equal(
            "{\"account_number\":\"00123456789012345678\"}",
            provider.Decrypt(ciphertext, nonce, tag, binding));

        // The same envelope rebound to another supplier, detail or version is rejected.
        var otherBinding = SupplierBankingService.AssociatedData(
            OrganizationId, Guid.NewGuid(), DetailId, 1);
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(ciphertext, nonce, tag, otherBinding));
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(ciphertext, nonce, tag, SupplierBankingService.AssociatedData(
                OrganizationId, SupplierId, DetailId, 2)));
    }

    [Fact]
    public void A_tampered_ciphertext_tag_or_nonce_is_never_revealed()
    {
        var provider = Provider();
        var binding = SupplierBankingService.AssociatedData(OrganizationId, SupplierId, DetailId, 1);
        var ciphertext = provider.Encrypt("secret", binding, out var nonce, out var tag);

        var tampered = (byte[])ciphertext.Clone();
        tampered[0] ^= 0x01;
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(tampered, nonce, tag, binding));

        var badTag = (byte[])tag.Clone();
        badTag[0] ^= 0x01;
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(ciphertext, nonce, badTag, binding));

        var badNonce = (byte[])nonce.Clone();
        badNonce[0] ^= 0x01;
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(ciphertext, badNonce, tag, binding));

        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            provider.Decrypt(ciphertext, nonce, tag[..8], binding));
    }

    [Fact]
    public void A_missing_or_malformed_key_fails_closed()
    {
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            new ConfigurationSupplierBankingKeyProvider(
                new ConfigurationBuilder().AddInMemoryCollection([]).Build()));
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            new ConfigurationSupplierBankingKeyProvider(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Supplier:Banking:KeyBase64"] = "not-base64" })
                    .Build()));
        Assert.Throws<SupplierDependencyUnavailableException>(() =>
            new ConfigurationSupplierBankingKeyProvider(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Supplier:Banking:KeyBase64"] = Convert.ToBase64String(new byte[16])
                    })
                    .Build()));
    }

    [Fact]
    public void Two_encryptions_of_the_same_plaintext_never_repeat_the_nonce()
    {
        var provider = Provider();
        var binding = SupplierBankingService.AssociatedData(OrganizationId, SupplierId, DetailId, 1);
        provider.Encrypt("same", binding, out var first, out _);
        provider.Encrypt("same", binding, out var second, out _);

        Assert.NotEqual(first, second);
    }
}
