using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WillVault.Application.Interfaces.Services;

namespace WillVault.Infrastructure.Services;

public class EncryptionService : IEncryptionService
{
    private readonly byte[] _masterKey;
    private readonly ILogger<EncryptionService> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _keyStore = new();

    public EncryptionService(IConfiguration configuration, ILogger<EncryptionService> logger)
    {
        _logger = logger;

        var masterKeyBase64 = configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
        {
            _logger.LogWarning("No master encryption key configured. Generating ephemeral key — NOT suitable for production.");
            _masterKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _masterKey = Convert.FromBase64String(masterKeyBase64);
        }
    }

    public Task<byte[]> EncryptAsync(byte[] data, string keyId, CancellationToken cancellationToken = default)
    {
        var key = ResolveKey(keyId);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Pack: [nonce(12)][ciphertext][tag(16)]
        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, nonce.Length);
        tag.CopyTo(result, nonce.Length + ciphertext.Length);

        return Task.FromResult(result);
    }

    public Task<byte[]> DecryptAsync(byte[] encryptedData, string keyId, CancellationToken cancellationToken = default)
    {
        var key = ResolveKey(keyId);

        var nonce = encryptedData[..12];
        var tag = encryptedData[^16..];
        var ciphertext = encryptedData[12..^16];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Task.FromResult(plaintext);
    }

    public Task<string> GenerateKeyAsync(CancellationToken cancellationToken = default)
    {
        var keyId = Guid.NewGuid().ToString();
        var itemKey = RandomNumberGenerator.GetBytes(32);
        _keyStore[keyId] = itemKey;
        _logger.LogDebug("Generated new encryption key {KeyId}", keyId);
        return Task.FromResult(keyId);
    }

    private byte[] ResolveKey(string keyId)
    {
        if (_keyStore.TryGetValue(keyId, out var key))
            return key;

        throw new InvalidOperationException($"Encryption key '{keyId}' not found.");
    }
}
