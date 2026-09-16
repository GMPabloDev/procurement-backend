using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ProcureToPay.Application.Abstractions;
using ProcureToPay.Domain.Modules.Suppliers;

namespace ProcureToPay.Infrastructure.Persistence.Suppliers;

/// <summary>
/// AES-256-GCM provider of the supplier banking data (SPEC 09 REQ-05). The key comes from
/// deployment configuration, not from the database, and every ciphertext is bound to its exact row
/// identity through the associated data. A missing, malformed or wrong-sized key fails closed with
/// the supplier dependency error so the caller never degrades to plaintext.
/// </summary>
public sealed class ConfigurationSupplierBankingKeyProvider : ISupplierBankingKeyProvider
{
    public const string KeyVersionSetting = "Supplier:Banking:KeyVersion";
    public const string KeySetting = "Supplier:Banking:KeyBase64";
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly byte[] key;

    public ConfigurationSupplierBankingKeyProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[KeySetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SupplierDependencyUnavailableException(
                "No supplier banking key is configured for this deployment.");
        }

        try
        {
            key = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            throw new SupplierDependencyUnavailableException("The configured supplier banking key is invalid.");
        }

        if (key.Length != 32)
        {
            throw new SupplierDependencyUnavailableException(
                "The supplier banking key must be a 256-bit key.");
        }

        var version = configuration[KeyVersionSetting];
        KeyVersion = string.IsNullOrWhiteSpace(version) ? "v1" : version.Trim();
    }

    public string KeyVersion { get; }

    public byte[] Encrypt(string plaintext, byte[] associatedData, out byte[] nonce, out byte[] tag)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);
        nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        tag = new byte[TagSize];
        using var cipher = new AesGcm(key, TagSize);
        cipher.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), ciphertext, tag, associatedData);
        return ciphertext;
    }

    public string Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(associatedData);
        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new SupplierDependencyUnavailableException("The stored banking envelope is corrupted.");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException)
        {
            // A tampered or rebound envelope is never revealed and is never accepted as data.
            throw new SupplierDependencyUnavailableException(
                "The stored banking envelope failed its authentication.");
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
