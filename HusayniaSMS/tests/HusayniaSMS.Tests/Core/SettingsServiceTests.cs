using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.Tests.TestDoubles;

namespace HusayniaSMS.Tests.Core;

[TestClass]
public sealed class SettingsServiceTests
{
    private const string Sid = "AC0123456789abcdef0123456789ABCDEF";
    private const string MessagingServiceSid = "MG0123456789abcdef0123456789ABCDEF";

    [TestMethod]
    public async Task MissingSettingsProduceIncompleteDescriptorAndCredentials()
    {
        var service = Create(new FakeSettingsStore(), new FakeSecretProtector());
        Assert.AreEqual(TokenState.Missing,
            (await service.LoadDescriptorAsync(default)).TokenState);
        Assert.AreEqual(CredentialLoadStatus.Incomplete,
            (await service.LoadCredentialsAsync(default)).Status);
    }

    [TestMethod]
    public async Task SaveTrimsConfigurationAndProtectsNewToken()
    {
        var store = new FakeSettingsStore();
        var protector = new FakeSecretProtector();
        var result = await Create(store, protector).SaveAsync(
            new(
                $" {Sid} ",
                TwilioSenderMode.FromPhoneNumber,
                " +15550100100 ",
                " new-token ",
                false), default);

        Assert.AreEqual(SaveSettingsStatus.Saved, result.Status);
        Assert.AreEqual(Sid, store.SavedSettings!.AccountSid);
        Assert.AreEqual("+15550100100", store.SavedSettings.SenderNumber);
        Assert.AreEqual(TwilioSenderMode.FromPhoneNumber, store.SavedSettings.SenderMode);
        Assert.AreEqual("new-token", protector.LastProtectedPlaintext);
        Assert.IsFalse(store.SavedSettings.ProtectedAuthToken.Contains("new-token"));
    }

    [TestMethod]
    public async Task MessagingServiceSetupSavesTrimmedValueAndExplicitMode()
    {
        var store = new FakeSettingsStore();
        var result = await Create(store, new FakeSecretProtector()).SaveAsync(
            new(
                Sid,
                TwilioSenderMode.MessagingServiceSid,
                $" {MessagingServiceSid} ",
                "token",
                false), default);

        Assert.AreEqual(SaveSettingsStatus.Saved, result.Status);
        Assert.AreEqual(MessagingServiceSid, store.SavedSettings!.SenderNumber);
        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, store.SavedSettings.SenderMode);
    }

    [TestMethod]
    public async Task PreserveSavedTokenReusesCiphertext()
    {
        var existing = new PersistedSettingsV1(
            1,
            Sid,
            "+15550100100",
            "ciphertext",
            @"C:\contacts\last.csv");
        var store = new FakeSettingsStore
        {
            LoadResult = new(SettingsFileStatus.Loaded, existing, null)
        };
        var protector = new FakeSecretProtector();
        var result = await Create(store, protector).SaveAsync(
            new(
                Sid,
                TwilioSenderMode.MessagingServiceSid,
                MessagingServiceSid,
                null,
                true), default);

        Assert.AreEqual(SaveSettingsStatus.Saved, result.Status);
        Assert.AreEqual("ciphertext", store.SavedSettings!.ProtectedAuthToken);
        Assert.AreEqual(existing.LastCsvPath, store.SavedSettings.LastCsvPath);
        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, store.SavedSettings.SenderMode);
        Assert.AreEqual(MessagingServiceSid, store.SavedSettings.SenderNumber);
        Assert.IsNull(protector.LastProtectedPlaintext);
    }

    [TestMethod]
    [DataRow(
        SettingsFileStatus.IoFailure,
        "Saved Twilio setup could not be read.",
        TwilioSenderMode.FromPhoneNumber,
        "+15550100101")]
    [DataRow(
        SettingsFileStatus.IoFailure,
        "Saved Twilio setup could not be read.",
        TwilioSenderMode.MessagingServiceSid,
        MessagingServiceSid)]
    [DataRow(
        SettingsFileStatus.AccessDenied,
        "Access to saved Twilio setup was denied.",
        TwilioSenderMode.FromPhoneNumber,
        "+15550100101")]
    [DataRow(
        SettingsFileStatus.AccessDenied,
        "Access to saved Twilio setup was denied.",
        TwilioSenderMode.MessagingServiceSid,
        MessagingServiceSid)]
    public async Task NonAuthoritativeReadPreventsSetupReplacement(
        SettingsFileStatus loadStatus,
        string loadMessage,
        TwilioSenderMode senderMode,
        string senderValue)
    {
        var existing = new PersistedSettingsV1(
            1,
            Sid,
            "+15550100100",
            "existing-ciphertext",
            @"C:\contacts\remembered.csv",
            TwilioSenderMode.FromPhoneNumber);
        var store = new ReadFailureSettingsStore(existing, loadStatus, loadMessage);
        var protector = new FakeSecretProtector();

        var result = await new SettingsService(
            store,
            protector,
            new SetupValidator(new E164PhoneNumberValidator())).SaveAsync(
            new(
                Sid,
                senderMode,
                senderValue,
                "replacement-token",
                PreserveSavedToken: false),
            default);

        Assert.AreEqual(SaveSettingsStatus.StorageFailed, result.Status);
        StringAssert.Contains(result.SafeMessage!, loadMessage);
        StringAssert.Contains(result.SafeMessage, "Settings were not changed.");
        Assert.IsNull(protector.LastProtectedPlaintext);
        Assert.AreEqual(0, protector.ProtectCallCount);
        Assert.AreEqual(0, protector.UnprotectCallCount);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(existing, store.CurrentSettings);
    }

    [TestMethod]
    [DataRow(
        SettingsFileStatus.IoFailure,
        "Saved Twilio setup could not be read.",
        TwilioSenderMode.FromPhoneNumber,
        "+15550100100")]
    [DataRow(
        SettingsFileStatus.IoFailure,
        "Saved Twilio setup could not be read.",
        TwilioSenderMode.MessagingServiceSid,
        MessagingServiceSid)]
    [DataRow(
        SettingsFileStatus.AccessDenied,
        "Access to saved Twilio setup was denied.",
        TwilioSenderMode.FromPhoneNumber,
        "+15550100100")]
    [DataRow(
        SettingsFileStatus.AccessDenied,
        "Access to saved Twilio setup was denied.",
        TwilioSenderMode.MessagingServiceSid,
        MessagingServiceSid)]
    public async Task NonAuthoritativeReadPreventsCsvPathReplacement(
        SettingsFileStatus loadStatus,
        string loadMessage,
        TwilioSenderMode senderMode,
        string senderValue)
    {
        var existing = new PersistedSettingsV1(
            1,
            Sid,
            senderValue,
            "existing-ciphertext",
            @"C:\contacts\remembered.csv",
            senderMode);
        var store = new ReadFailureSettingsStore(existing, loadStatus, loadMessage);
        var protector = new FakeSecretProtector();

        var result = await new SettingsService(
                store,
                protector,
                new SetupValidator(new E164PhoneNumberValidator()))
            .SaveLastCsvPathAsync(@"D:\imports\replacement.csv", default);

        Assert.AreEqual(SaveCsvPathStatus.StorageFailed, result.Status);
        StringAssert.Contains(result.SafeMessage!, loadMessage);
        Assert.AreEqual(0, protector.ProtectCallCount);
        Assert.AreEqual(0, protector.UnprotectCallCount);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(existing, store.CurrentSettings);
    }

    [TestMethod]
    public async Task LegacyMissingModeDefaultsToFromWithoutRewriting()
    {
        var existing = new PersistedSettingsV1(
            1,
            Sid,
            "+15550100100",
            "ciphertext",
            @"C:\contacts\last.csv");
        var store = new FakeSettingsStore
        {
            LoadResult = new(SettingsFileStatus.Loaded, existing, null)
        };
        var service = Create(store, new FakeSecretProtector());

        var descriptor = await service.LoadDescriptorAsync(default);
        var credentials = await service.LoadCredentialsAsync(default);

        Assert.AreEqual(TwilioSenderMode.FromPhoneNumber, descriptor.SenderMode);
        Assert.AreEqual("+15550100100", descriptor.SenderValue);
        Assert.AreEqual(CredentialLoadStatus.Ready, credentials.Status);
        Assert.AreEqual(TwilioSenderMode.FromPhoneNumber, credentials.Credentials!.SenderMode);
        Assert.AreEqual("+15550100100", credentials.Credentials.SenderValue);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task MessagingServiceModeSurvivesDescriptorAndCredentialReload()
    {
        var store = new FakeSettingsStore
        {
            LoadResult = new(
                SettingsFileStatus.Loaded,
                new PersistedSettingsV1(
                    1,
                    Sid,
                    MessagingServiceSid,
                    "ciphertext",
                    null,
                    TwilioSenderMode.MessagingServiceSid),
                null)
        };

        var service = Create(store, new FakeSecretProtector());
        var descriptor = await service.LoadDescriptorAsync(default);
        var credentials = await service.LoadCredentialsAsync(default);

        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, descriptor.SenderMode);
        Assert.AreEqual(MessagingServiceSid, descriptor.SenderValue);
        Assert.AreEqual(CredentialLoadStatus.Ready, credentials.Status);
        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, credentials.Credentials!.SenderMode);
        Assert.AreEqual(MessagingServiceSid, credentials.Credentials.SenderValue);
    }

    [TestMethod]
    public async Task LastCsvPathCanBeSavedBeforeTwilioSetupAndLoadedAfterRestart()
    {
        var store = new FakeSettingsStore();
        var service = Create(store, new FakeSecretProtector());

        var saved = await service.SaveLastCsvPathAsync(@"C:\contacts\last.csv", default);
        var restarted = Create(store, new FakeSecretProtector());
        var descriptor = await restarted.LoadDescriptorAsync(default);
        var credentials = await restarted.LoadCredentialsAsync(default);

        Assert.AreEqual(SaveCsvPathStatus.Saved, saved.Status);
        Assert.AreEqual(@"C:\contacts\last.csv", store.SavedSettings!.LastCsvPath);
        Assert.AreEqual(TokenState.Missing, descriptor.TokenState);
        Assert.AreEqual(@"C:\contacts\last.csv", descriptor.LastCsvPath);
        Assert.AreEqual(CredentialLoadStatus.Incomplete, credentials.Status);
    }

    [TestMethod]
    public async Task SavingLastCsvPathPreservesProtectedTwilioSetup()
    {
        var store = LoadedStore();
        var original = store.LoadResult.Settings!;

        var result = await Create(store, new FakeSecretProtector())
            .SaveLastCsvPathAsync(@"D:\imports\contacts.csv", default);

        Assert.AreEqual(SaveCsvPathStatus.Saved, result.Status);
        Assert.AreEqual(original.AccountSid, store.SavedSettings!.AccountSid);
        Assert.AreEqual(original.SenderNumber, store.SavedSettings.SenderNumber);
        Assert.AreEqual(original.SenderMode, store.SavedSettings.SenderMode);
        Assert.AreEqual(original.ProtectedAuthToken, store.SavedSettings.ProtectedAuthToken);
        Assert.AreEqual(@"D:\imports\contacts.csv", store.SavedSettings.LastCsvPath);
    }

    [TestMethod]
    public async Task SavingLastCsvPathPreservesMessagingServiceModeValueAndCiphertext()
    {
        var original = new PersistedSettingsV1(
            1,
            Sid,
            MessagingServiceSid,
            "exact-ciphertext",
            @"C:\contacts\old.csv",
            TwilioSenderMode.MessagingServiceSid);
        var store = new FakeSettingsStore
        {
            LoadResult = new(SettingsFileStatus.Loaded, original, null)
        };

        var result = await Create(store, new FakeSecretProtector())
            .SaveLastCsvPathAsync(@"D:\imports\contacts.csv", default);

        Assert.AreEqual(SaveCsvPathStatus.Saved, result.Status);
        Assert.AreEqual(original.AccountSid, store.SavedSettings!.AccountSid);
        Assert.AreEqual(original.SenderNumber, store.SavedSettings.SenderNumber);
        Assert.AreEqual(original.SenderMode, store.SavedSettings.SenderMode);
        Assert.AreEqual(original.ProtectedAuthToken, store.SavedSettings.ProtectedAuthToken);
        Assert.AreEqual(@"D:\imports\contacts.csv", store.SavedSettings.LastCsvPath);
    }

    [TestMethod]
    public async Task FailedLastCsvPathSavePreservesPreviousSettings()
    {
        var original = new PersistedSettingsV1(
            1,
            Sid,
            "+15550100100",
            "ciphertext",
            @"C:\contacts\good.csv");
        var store = new FakeSettingsStore
        {
            LoadResult = new(SettingsFileStatus.Loaded, original, null),
            SaveResult = new(SettingsFileSaveStatus.IoFailure, "safe path failure")
        };

        var result = await Create(store, new FakeSecretProtector())
            .SaveLastCsvPathAsync(@"C:\contacts\bad.csv", default);

        Assert.AreEqual(SaveCsvPathStatus.StorageFailed, result.Status);
        Assert.AreEqual(original, store.LoadResult.Settings);
        StringAssert.Contains(result.SafeMessage!, "safe path failure");
    }

    [TestMethod]
    public async Task CorruptTokenIsUnavailableAndCannotSend()
    {
        var store = LoadedStore();
        var protector = new FakeSecretProtector { UnprotectSucceeds = false };
        var service = Create(store, protector);
        Assert.AreEqual(TokenState.Unavailable,
            (await service.LoadDescriptorAsync(default)).TokenState);
        var credentials = await service.LoadCredentialsAsync(default);
        Assert.AreEqual(CredentialLoadStatus.SecretUnavailable, credentials.Status);
        Assert.IsNull(credentials.Credentials);
    }

    [TestMethod]
    public async Task CorruptSettingsRequireReentryInsteadOfExposingStorageDetails()
    {
        var store = new FakeSettingsStore
        {
            LoadResult = new(SettingsFileStatus.Corrupt, null, "safe corrupt message")
        };
        var result = await Create(store, new FakeSecretProtector())
            .LoadCredentialsAsync(default);
        Assert.AreEqual(CredentialLoadStatus.SecretUnavailable, result.Status);
        StringAssert.Contains(result.SafeMessage!, "Re-enter");
    }

    [TestMethod]
    public async Task ProtectionAndStorageFailuresAreTyped()
    {
        var protectionFailure = await Create(
            new FakeSettingsStore(),
            new FakeSecretProtector { ProtectSucceeds = false }).SaveAsync(
            new(
                Sid,
                TwilioSenderMode.FromPhoneNumber,
                "+15550100100",
                "token",
                false), default);
        Assert.AreEqual(SaveSettingsStatus.ProtectionFailed, protectionFailure.Status);

        var store = new FakeSettingsStore
        {
            SaveResult = new(SettingsFileSaveStatus.IoFailure, "safe storage failure")
        };
        var storageFailure = await Create(store, new FakeSecretProtector()).SaveAsync(
            new(
                Sid,
                TwilioSenderMode.FromPhoneNumber,
                "+15550100100",
                "token",
                false), default);
        Assert.AreEqual(SaveSettingsStatus.StorageFailed, storageFailure.Status);
        Assert.IsFalse(storageFailure.SafeMessage!.Contains("token"));
    }

    [TestMethod]
    public async Task ReadyCredentialsAreRedactedWhenFormatted()
    {
        var result = await Create(LoadedStore(), new FakeSecretProtector
        {
            Plaintext = "secret-value"
        }).LoadCredentialsAsync(default);

        Assert.AreEqual(CredentialLoadStatus.Ready, result.Status);
        Assert.AreEqual("[TwilioCredentials REDACTED]", result.Credentials!.ToString());
        Assert.IsFalse(result.Credentials.ToString().Contains("secret-value"));
    }

    private static SettingsService Create(
        FakeSettingsStore store,
        FakeSecretProtector protector) =>
        new(store, protector, new SetupValidator(new E164PhoneNumberValidator()));

    private static FakeSettingsStore LoadedStore() => new()
    {
        LoadResult = new(SettingsFileStatus.Loaded,
            new PersistedSettingsV1(
                1,
                Sid,
                "+15550100100",
                "ciphertext",
                null,
                TwilioSenderMode.FromPhoneNumber),
            null)
    };

    private sealed class ReadFailureSettingsStore(
        PersistedSettingsV1 currentSettings,
        SettingsFileStatus loadStatus,
        string safeMessage) : ILocalSettingsStore
    {
        public PersistedSettingsV1 CurrentSettings { get; private set; } = currentSettings;
        public int SaveCount { get; private set; }

        public Task<SettingsFileLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsFileLoadResult(loadStatus, null, safeMessage));

        public Task<SettingsFileSaveResult> SaveAsync(
            PersistedSettingsV1 settings,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            CurrentSettings = settings;
            return Task.FromResult(new SettingsFileSaveResult(
                SettingsFileSaveStatus.Saved,
                null));
        }
    }
}
