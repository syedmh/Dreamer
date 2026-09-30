using TCFUploader.BrowserAuth;
using TCFUploader.Cli;

namespace TCFUploader.Tests.Cli;

[TestClass]
public sealed class TokenAcquirerTests
{
    [TestMethod]
    public async Task BrowserLogin_PrecedenceUsesValidEnvironmentThenRedirectedInput()
    {
        var browser = new FakeBrowserLogin(new BrowserLoginResult.Success("browser-secret"));
        var environment = await ReadAsync("environment-secret", "stdin-secret", true, browser);
        Assert.AreEqual("environment-secret", ((TokenReadResult.Success)environment).Token);
        Assert.AreEqual(0, browser.Calls);

        var redirected = await ReadAsync(null, "stdin-secret", true, browser);
        Assert.AreEqual("stdin-secret", ((TokenReadResult.Success)redirected).Token);
        Assert.AreEqual(0, browser.Calls);
    }

    [TestMethod]
    public async Task BrowserLogin_RunsForMissingOrEmptyInput()
    {
        var browser = new FakeBrowserLogin(new BrowserLoginResult.Success("browser-secret"));
        var missing = await ReadAsync(null, string.Empty, false, browser);
        Assert.AreEqual("browser-secret", ((TokenReadResult.Success)missing).Token);
        Assert.AreEqual(1, browser.Calls);

        var invalid = await ReadAsync("bad\tsecret", string.Empty, false, browser);
        Assert.AreEqual("token_invalid", ((TokenReadResult.Error)invalid).Code);
        Assert.AreEqual(1, browser.Calls);

        var emptyRedirect = await ReadAsync(null, string.Empty, true, browser);
        Assert.AreEqual("browser-secret", ((TokenReadResult.Success)emptyRedirect).Token);
        Assert.AreEqual(2, browser.Calls);
    }

    private static Task<TokenReadResult> ReadAsync(
        string? environment,
        string stdin,
        bool redirected,
        FakeBrowserLogin browser) =>
        TokenAcquirer.ReadAsync(
            browserLogin: true,
            () => environment,
            new StringReader(stdin),
            redirected,
            _ => browser,
            TextWriter.Null,
            default);

    private sealed class FakeBrowserLogin(BrowserLoginResult result) : IBrowserLogin
    {
        internal int Calls { get; private set; }

        public Task<BrowserLoginResult> AcquireAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
