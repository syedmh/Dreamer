using System.Text.Json;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.Tests.TestDoubles;
using HusayniaSMS.WinForms.Infrastructure.Settings;

namespace HusayniaSMS.Tests.Settings;

[TestClass]
public sealed class JsonLocalSettingsStoreTests
{
    private static readonly PersistedSettingsV1 Valid =
        new(
            1,
            "AC0123456789abcdef0123456789ABCDEF",
            "+15550100100",
            "ciphertext",
            @"C:\contacts\last.csv",
            TwilioSenderMode.FromPhoneNumber);

    [TestMethod]
    public async Task MissingAndLoadedSettingsAreTyped()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var store = new JsonLocalSettingsStore(path);
        Assert.AreEqual(SettingsFileStatus.Missing, (await store.LoadAsync(default)).Status);
        Assert.AreEqual(SettingsFileSaveStatus.Saved,
            (await store.SaveAsync(Valid, default)).Status);
        var loaded = await store.LoadAsync(default);
        Assert.AreEqual(SettingsFileStatus.Loaded, loaded.Status);
        Assert.AreEqual(Valid, loaded.Settings);
    }

    [TestMethod]
    public async Task JsonContainsOnlyApprovedShapeAndNoPlaintextToken()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        await new JsonLocalSettingsStore(path).SaveAsync(Valid, default);
        var json = await File.ReadAllTextAsync(path);
        Assert.IsTrue(json.Contains("\"schemaVersion\""));
        Assert.IsTrue(json.Contains("\"protectedAuthToken\""));
        Assert.IsTrue(json.Contains("\"lastCsvPath\""));
        Assert.IsTrue(json.Contains("\"senderMode\": \"fromPhoneNumber\""));
        Assert.IsFalse(json.Contains("test-only-secret"));
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.EnumerateObject()
            .Any(property => string.Equals(
                property.Name,
                "authToken",
                StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "accountSid",
                "senderNumber",
                "protectedAuthToken",
                "lastCsvPath",
                "senderMode"
            },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public async Task LegacySettingsWithoutCsvPathRemainReadable()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        await File.WriteAllTextAsync(path,
            """{"schemaVersion":1,"accountSid":"AC0123456789abcdef0123456789ABCDEF","senderNumber":"+15550100100","protectedAuthToken":"ciphertext"}""");

        var loaded = await new JsonLocalSettingsStore(path).LoadAsync(default);

        Assert.AreEqual(SettingsFileStatus.Loaded, loaded.Status);
        Assert.IsNull(loaded.Settings!.LastCsvPath);
        Assert.IsNull(loaded.Settings.SenderMode);
    }

    [TestMethod]
    public async Task BothSenderModesRoundTripAsCamelCaseStrings()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var store = new JsonLocalSettingsStore(path);

        foreach (var (mode, value, serializedName) in new[]
        {
            (TwilioSenderMode.FromPhoneNumber, "+15550100100", "fromPhoneNumber"),
            (TwilioSenderMode.MessagingServiceSid,
                "MG0123456789abcdef0123456789ABCDEF",
                "messagingServiceSid")
        })
        {
            var settings = Valid with
            {
                SenderNumber = value,
                SenderMode = mode
            };

            Assert.AreEqual(
                SettingsFileSaveStatus.Saved,
                (await store.SaveAsync(settings, default)).Status);
            Assert.AreEqual(settings, (await store.LoadAsync(default)).Settings);
            StringAssert.Contains(
                await File.ReadAllTextAsync(path),
                $"\"senderMode\": \"{serializedName}\"");
        }
    }

    [TestMethod]
    public async Task PathOnlySettingsRoundTripWithoutInventingCredentials()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var pathOnly = new PersistedSettingsV1(
            1,
            string.Empty,
            string.Empty,
            string.Empty,
            @"C:\contacts\last.csv");
        var store = new JsonLocalSettingsStore(path);

        Assert.AreEqual(
            SettingsFileSaveStatus.Saved,
            (await store.SaveAsync(pathOnly, default)).Status);
        var loaded = await store.LoadAsync(default);

        Assert.AreEqual(SettingsFileStatus.Loaded, loaded.Status);
        Assert.AreEqual(pathOnly, loaded.Settings);
        Assert.IsNull(loaded.Settings!.SenderMode);
    }

    [TestMethod]
    public async Task PathOnlySettingsRejectNonNullSenderMode()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        await File.WriteAllTextAsync(path,
            """{"schemaVersion":1,"accountSid":"","senderNumber":"","protectedAuthToken":"","lastCsvPath":"C:\\contacts\\last.csv","senderMode":"fromPhoneNumber"}""");

        var loaded = await new JsonLocalSettingsStore(path).LoadAsync(default);

        Assert.AreEqual(SettingsFileStatus.Corrupt, loaded.Status);
        Assert.IsNull(loaded.Settings);
    }

    [TestMethod]
    public async Task CorruptAndUnsupportedFilesFailSafely()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        await File.WriteAllTextAsync(path, "{not json");
        var store = new JsonLocalSettingsStore(path);
        Assert.AreEqual(SettingsFileStatus.Corrupt, (await store.LoadAsync(default)).Status);

        await File.WriteAllTextAsync(path,
            """{"schemaVersion":2,"accountSid":"a","senderNumber":"b","protectedAuthToken":"c"}""");
        Assert.AreEqual(SettingsFileStatus.UnsupportedVersion,
            (await store.LoadAsync(default)).Status);
    }

    [TestMethod]
    [DataRow("""{"schemaVersion":1,"accountSid":"AC0123456789abcdef0123456789ABCDEF","senderNumber":"+15550100100","protectedAuthToken":"ciphertext","senderMode":"unknown"}""")]
    [DataRow("""{"schemaVersion":1,"accountSid":"AC0123456789abcdef0123456789ABCDEF","senderNumber":"+15550100100","protectedAuthToken":"ciphertext","senderMode":42}""")]
    [DataRow("""{"schemaVersion":1,"accountSid":"AC0123456789abcdef0123456789ABCDEF","senderNumber":"+15550100100","protectedAuthToken":"ciphertext","senderMode":"42"}""")]
    [DataRow("""{"schemaVersion":1,"accountSid":"AC0123456789abcdef0123456789ABCDEF","senderNumber":"+15550100100","protectedAuthToken":"ciphertext","senderMode":{}}""")]
    public async Task UnknownNumericMalformedAndUndefinedModesAreCorrupt(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        await File.WriteAllTextAsync(path, json);

        var loaded = await new JsonLocalSettingsStore(path).LoadAsync(default);

        Assert.AreEqual(SettingsFileStatus.Corrupt, loaded.Status);
        Assert.IsNull(loaded.Settings);
    }

    [TestMethod]
    public async Task DirectoryPathMapsSaveAccessDenied()
    {
        using var temp = new TempDirectory();
        var store = new JsonLocalSettingsStore(temp.Path);
        Assert.AreEqual(SettingsFileSaveStatus.AccessDenied,
            (await store.SaveAsync(Valid, default)).Status);
    }

    [TestMethod]
    public async Task FailedReplacementPreservesLastGoodFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var store = new JsonLocalSettingsStore(path);
        await store.SaveAsync(Valid, default);
        await using (var locked = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await store.SaveAsync(
                Valid with { SenderNumber = "+15550100101" }, default);
            Assert.AreEqual(SettingsFileSaveStatus.AccessDenied, failed.Status);
        }

        Assert.AreEqual(Valid, (await store.LoadAsync(default)).Settings);
    }

    [TestMethod]
    public async Task ReplacementLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.v1.json");
        var store = new JsonLocalSettingsStore(path);
        await store.SaveAsync(Valid, default);
        await store.SaveAsync(Valid with { SenderNumber = "+15550100101" }, default);
        Assert.AreEqual(1, Directory.GetFiles(temp.Path).Length);
        Assert.AreEqual("+15550100101", (await store.LoadAsync(default)).Settings!.SenderNumber);
    }

    [TestMethod]
    public void ProductionAndSafeDemoPathsAreSeparatedAndLocal()
    {
        using var temp = new TempDirectory();
        var production = new LocalSettingsPathProvider(false, temp.Path);
        var demo = new LocalSettingsPathProvider(true, temp.Path);
        Assert.AreNotEqual(production.SettingsPath, demo.SettingsPath);
        StringAssert.Contains(production.SettingsPath, "HusayniaSMS");
        StringAssert.Contains(demo.SettingsPath, "SafeDemo");
        Assert.IsTrue(production.SettingsPath.StartsWith(temp.Path, StringComparison.OrdinalIgnoreCase));
    }
}
