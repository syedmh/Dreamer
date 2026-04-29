namespace WillVault.Application.Interfaces.Services;

/// <summary>
/// Abstraction for data encryption and decryption operations.
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// Encrypts the provided data using the specified key.
    /// </summary>
    /// <param name="data">The plaintext data to encrypt.</param>
    /// <param name="keyId">The identifier of the encryption key to use.</param>
    /// <returns>The encrypted data as a byte array.</returns>
    Task<byte[]> EncryptAsync(byte[] data, string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts the provided data using the specified key.
    /// </summary>
    /// <param name="encryptedData">The encrypted data to decrypt.</param>
    /// <param name="keyId">The identifier of the encryption key to use.</param>
    /// <returns>The decrypted data as a byte array.</returns>
    Task<byte[]> DecryptAsync(byte[] encryptedData, string keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a new encryption key and returns its identifier.
    /// </summary>
    /// <returns>The identifier of the newly generated key.</returns>
    Task<string> GenerateKeyAsync(CancellationToken cancellationToken = default);
}
