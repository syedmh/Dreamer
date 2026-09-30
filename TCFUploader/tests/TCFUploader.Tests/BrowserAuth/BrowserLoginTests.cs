using TCFUploader.BrowserAuth;

namespace TCFUploader.Tests.BrowserAuth;

[TestClass]
public sealed class BrowserLoginTests
{
    [TestMethod]
    public async Task Cancellation_StopsCaptureClosesProcessAndDeletesProfile()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess();
        var profiles = new FakeProfiles(deleteResult: true);
        var login = CreateLogin(
            process,
            profiles,
            new BlockingCapture(),
            TimeSpan.FromMinutes(10));

        var task = login.AcquireAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.IsInstanceOfType<BrowserLoginResult.Cancelled>(await task);
        Assert.IsTrue(process.KillCalled);
        Assert.IsTrue(process.WaitCalled);
        Assert.AreEqual(1, profiles.DeleteCalls);
    }

    [TestMethod]
    public async Task CancellationBeforeStart_DoesNotCreateProfileOrLaunchBrowser()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var process = new FakeProcess();
        var profiles = new FakeProfiles(deleteResult: true);
        var launcher = new FakeLauncher(process);
        var login = new BrowserLogin(
            TextWriter.Null,
            new FakeLocator(),
            profiles,
            launcher,
            new BlockingCapture(),
            () => true,
            TimeSpan.FromMinutes(10));

        Assert.IsInstanceOfType<BrowserLoginResult.Cancelled>(
            await login.AcquireAsync(cancellation.Token));
        Assert.AreEqual(0, profiles.CreateCalls);
        Assert.AreEqual(0, launcher.Calls);
    }

    [TestMethod]
    public async Task BrowserClosure_FailsSecretFreeAndStillCleansUp()
    {
        var process = new FakeProcess { HasExitedValue = true };
        var profiles = new FakeProfiles(deleteResult: true);
        var login = CreateLogin(
            process,
            profiles,
            new ImmediateCapture(new BrowserTokenCaptureResult.BrowserClosed()),
            TimeSpan.FromMinutes(10));

        var result = await login.AcquireAsync(default);
        var error = (BrowserLoginResult.Error)result;
        Assert.AreEqual("browser_closed", error.Code);
        Assert.IsFalse(process.KillCalled);
        Assert.IsTrue(process.WaitCalled);
        Assert.AreEqual(1, profiles.DeleteCalls);
    }

    [TestMethod]
    public async Task Timeout_StopsCaptureAndCleansUp()
    {
        var process = new FakeProcess();
        var profiles = new FakeProfiles(deleteResult: true);
        var login = CreateLogin(
            process,
            profiles,
            new BlockingCapture(),
            TimeSpan.FromMilliseconds(20));

        var result = await login.AcquireAsync(default);
        Assert.AreEqual("browser_login_timeout", ((BrowserLoginResult.Error)result).Code);
        Assert.IsTrue(process.KillCalled);
        Assert.AreEqual(1, profiles.DeleteCalls);
    }

    [TestMethod]
    public async Task CleanupFailure_DiscardsCapturedTokenAndFailsClosed()
    {
        const string secret = "captured-secret";
        using var output = new StringWriter();
        var process = new FakeProcess();
        var profiles = new FakeProfiles(deleteResult: false);
        var login = CreateLogin(
            process,
            profiles,
            new ImmediateCapture(new BrowserTokenCaptureResult.Authentication(secret, "user-123")),
            TimeSpan.FromMinutes(10),
            output);

        var result = await login.AcquireAsync(default);
        var error = (BrowserLoginResult.Error)result;
        Assert.AreEqual("browser_cleanup_failed", error.Code);
        Assert.IsFalse(error.Message.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(output.ToString().Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CapturedToken_UsesExistingValidationRules()
    {
        var login = CreateLogin(
            new FakeProcess(),
            new FakeProfiles(deleteResult: true),
            new ImmediateCapture(new BrowserTokenCaptureResult.Authentication("bad\nsecret", "user-123")),
            TimeSpan.FromMinutes(10));

        var result = await login.AcquireAsync(default);
        Assert.AreEqual("token_invalid", ((BrowserLoginResult.Error)result).Code);
    }

    private static BrowserLogin CreateLogin(
        FakeProcess process,
        FakeProfiles profiles,
        IBrowserTokenCapture capture,
        TimeSpan timeout,
        TextWriter? output = null) =>
        new(
            output ?? TextWriter.Null,
            new FakeLocator(),
            profiles,
            new FakeLauncher(process),
            capture,
            () => true,
            timeout);

    private sealed class FakeLocator : IBrowserExecutableLocator
    {
        public BrowserExecutableResult Locate() =>
            new BrowserExecutableResult.Success(
                new BrowserExecutable(@"C:\browser.exe", BrowserKind.Edge));
    }

    private sealed class FakeLauncher(FakeProcess process) : IBrowserProcessLauncher
    {
        internal int Calls { get; private set; }

        public IBrowserProcess Launch(BrowserExecutable executable, string profilePath)
        {
            Calls++;
            return process;
        }
    }

    private sealed class FakeProfiles(bool deleteResult) : IBrowserProfileStore
    {
        internal int CreateCalls { get; private set; }
        internal int DeleteCalls { get; private set; }

        public BrowserProfileResult Create()
        {
            CreateCalls++;
            return new BrowserProfileResult.Success(
                @"C:\private\profile-00000000000000000000000000000000");
        }

        public Task<bool> DeleteAsync(string profilePath, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.FromResult(deleteResult);
        }
    }

    private sealed class FakeProcess : IBrowserProcess
    {
        internal bool HasExitedValue { get; set; }
        internal bool KillCalled { get; private set; }
        internal bool WaitCalled { get; private set; }
        public bool HasExited => HasExitedValue;

        public void KillTree()
        {
            KillCalled = true;
            HasExitedValue = true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalled = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateCapture(BrowserTokenCaptureResult result) : IBrowserTokenCapture
    {
        public Task<BrowserTokenCaptureResult> CaptureAsync(
            string profilePath,
            IBrowserProcess process,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class BlockingCapture : IBrowserTokenCapture
    {
        public async Task<BrowserTokenCaptureResult> CaptureAsync(
            string profilePath,
            IBrowserProcess process,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
