using TCFUploader.Configuration;

namespace TCFUploader.Tests.Configuration;

[TestClass]
public sealed class RuntimeOptionsTests
{
    [TestMethod]
    public void SpoolCapacityEnvironmentSettings_ParseAndRejectUnsafeValues()
    {
        var values = new Dictionary<string, string?>
        {
            ["TCFUPLOADER_MAX_SPOOL_BYTES"] = "1048576",
            ["TCFUPLOADER_MIN_FREE_BYTES"] = "65536"
        };
        var configured = RuntimeOptions.FromEnvironment(
            name => values.TryGetValue(name, out var value) ? value : null);
        Assert.AreEqual(1_048_576L, configured.AggregateSpoolLimitBytes);
        Assert.AreEqual(65_536L, configured.MinimumFreeSpaceReserveBytes);

        values["TCFUPLOADER_MAX_SPOOL_BYTES"] = "0";
        Assert.ThrowsExactly<InvalidDataException>(() => RuntimeOptions.FromEnvironment(
            name => values.TryGetValue(name, out var value) ? value : null));
        values["TCFUPLOADER_MAX_SPOOL_BYTES"] = "1048576";
        values["TCFUPLOADER_MIN_FREE_BYTES"] = "-1";
        Assert.ThrowsExactly<InvalidDataException>(() => RuntimeOptions.FromEnvironment(
            name => values.TryGetValue(name, out var value) ? value : null));
    }
}
