namespace ProcureToPay.Application.Abstractions;

/// <summary>
/// External key provider of the encrypted supplier banking data (SPEC 09 REQ-05). The key material
/// never lives in the database: SQL only stores the ciphertext, its nonce and tag, the key version
/// and the non-secret metadata.
/// </summary>
public interface ISupplierBankingKeyProvider
{
    /// <summary>Version of the key currently used to encrypt; it travels with each version row.</summary>
    string KeyVersion { get; }

    /// <summary>Authenticated encryption of one canonical plaintext bound to its row identity.</summary>
    byte[] Encrypt(string plaintext, byte[] associatedData, out byte[] nonce, out byte[] tag);

    /// <summary>Authenticated decryption; a tampered ciphertext, nonce, tag or binding fails.</summary>
    string Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, byte[] associatedData);
}
