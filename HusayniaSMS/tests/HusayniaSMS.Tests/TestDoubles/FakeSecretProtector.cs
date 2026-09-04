using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeSecretProtector : ISecretProtector
{
    public bool ProtectSucceeds { get; set; } = true;
    public bool UnprotectSucceeds { get; set; } = true;
    public string Plaintext { get; set; } = "saved-token";
    public string? LastProtectedPlaintext { get; private set; }
    public int ProtectCallCount { get; private set; }
    public int UnprotectCallCount { get; private set; }

    public SecretProtectResult Protect(string plaintext)
    {
        ProtectCallCount++;
        LastProtectedPlaintext = plaintext;
        return ProtectSucceeds
            ? new(true, $"protected:{plaintext.Length}")
            : new(false, null);
    }

    public SecretUnprotectResult TryUnprotect(string protectedBase64)
    {
        UnprotectCallCount++;
        return UnprotectSucceeds ? new(true, Plaintext) : new(false, null);
    }
}
