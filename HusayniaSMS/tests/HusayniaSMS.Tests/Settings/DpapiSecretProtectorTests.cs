using HusayniaSMS.WinForms.Infrastructure.Settings;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.Tests.TestDoubles;

namespace HusayniaSMS.Tests.Settings;

[TestClass]
public sealed class DpapiSecretProtectorTests
{
    [TestMethod]
    public void CurrentWindowsUserCanRoundTripWithoutPlaintextCiphertext()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI CurrentUser is Windows-only.");
        }

        const string secret = "test-only-secret-value";
        var protector = new DpapiSecretProtector();
        var protectedResult = protector.Protect(secret);
        Assert.IsTrue(protectedResult.Succeeded);
        Assert.IsFalse(protectedResult.ProtectedBase64!.Contains(secret));
        var unprotected = protector.TryUnprotect(protectedResult.ProtectedBase64);
        Assert.IsTrue(unprotected.Succeeded);
        Assert.AreEqual(secret, unprotected.Plaintext);
    }

    [TestMethod]
    public void CorruptCiphertextFailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI CurrentUser is Windows-only.");
        }

        var result = new DpapiSecretProtector().TryUnprotect("not-valid-base64");
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Plaintext);
    }

    [TestMethod]
    public void BlankPlaintextIsNotProtected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI CurrentUser is Windows-only.");
        }

        Assert.IsFalse(new DpapiSecretProtector().Protect(" ").Succeeded);
    }

    [TestMethod]
    public async Task SettingsServiceRoundTripStoresCiphertextAndReloadsForSameUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI CurrentUser is Windows-only.");
        }

        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var store = new JsonLocalSettingsStore(path);
        var protector = new DpapiSecretProtector();
        var validator = new SetupValidator(new E164PhoneNumberValidator());
        var service = new SettingsService(store, protector, validator);
        const string token = "integration-test-secret";
        var saved = await service.SaveAsync(new(
            "AC0123456789abcdef0123456789ABCDEF",
            TwilioSenderMode.FromPhoneNumber,
            "+15550100100",
            token,
            false), default);
        Assert.AreEqual(SaveSettingsStatus.Saved, saved.Status);
        Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains(token));
        var credentials = await service.LoadCredentialsAsync(default);
        Assert.AreEqual(CredentialLoadStatus.Ready, credentials.Status);
        Assert.AreEqual(token, credentials.Credentials!.AuthToken);
    }
}
