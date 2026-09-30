using TCFUploader.Cli;

namespace TCFUploader.Tests.Cli;

[TestClass]
public sealed class TokenSourceTests
{
    [TestMethod]
    public async Task EnvironmentToken_WinsAndDiagnosticsAreSecretFree()
    {
        var result = await TokenSource.ReadAsync(() => "env-secret", new StringReader("stdin-secret"), true, default);
        Assert.AreEqual("env-secret", ((TokenReadResult.Success)result).Token);
    }

    [TestMethod]
    public async Task RedirectedInput_IsBoundedAndRejectsControls()
    {
        Assert.IsInstanceOfType<TokenReadResult.Error>(
            await TokenSource.ReadAsync(() => null, new StringReader("one\ntwo"), true, default));
        Assert.IsInstanceOfType<TokenReadResult.Error>(
            await TokenSource.ReadAsync(() => null, new StringReader(new string('x', 16 * 1024 + 1)), true, default));
        Assert.AreEqual(
            "token_too_long",
            ((TokenReadResult.Error)TokenSource.Validate(new string('x', 16 * 1024 + 1))).Code);
    }
}
