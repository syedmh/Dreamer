using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Infrastructure.Settings;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public SecretProtectResult Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return new(false, null);
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return new(true, Convert.ToBase64String(protectedBytes));
        }
        catch (CryptographicException)
        {
            return new(false, null);
        }
        catch (PlatformNotSupportedException)
        {
            return new(false, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public SecretUnprotectResult TryUnprotect(string protectedBase64)
    {
        byte[]? protectedBytes = null;
        byte[]? plaintextBytes = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedBase64);
            plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return new(true, Encoding.UTF8.GetString(plaintextBytes));
        }
        catch (FormatException)
        {
            return new(false, null);
        }
        catch (CryptographicException)
        {
            return new(false, null);
        }
        catch (PlatformNotSupportedException)
        {
            return new(false, null);
        }
        finally
        {
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plaintextBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
    }
}
