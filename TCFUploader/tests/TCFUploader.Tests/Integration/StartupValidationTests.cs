using TCFUploader.Cli;
using TCFUploader.BrowserAuth;
using TCFUploader.Configuration;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.Integration;

[TestClass]
public sealed class StartupValidationTests
{
    [TestMethod]
    public async Task AC01_InvalidStartupInputs_ExitBeforeWatcherOrHttp()
    {
        using var paths = new TestPaths();
        var noToken = await RunInvalidAsync(
            ["--folder", paths.Watch], paths.Local, environmentToken: null, redirected: false);
        Assert.AreEqual(ExitCodes.Configuration, noToken.ExitCode);
        Assert.AreEqual(1, noToken.Coordinators);
        AssertZeroConstructionAndDiagnostic(noToken, "token");

        var missing = await RunInvalidAsync(
            ["--folder", Path.Combine(paths.Root, "missing")], paths.Local);
        Assert.AreEqual(ExitCodes.Configuration, missing.ExitCode);
        Assert.AreEqual(0, missing.Coordinators);
        AssertZeroConstructionAndDiagnostic(missing, "existing directory");

        var fileInsteadOfFolder = Path.Combine(paths.Root, "file.txt");
        await File.WriteAllTextAsync(fileInsteadOfFolder, "x");
        var nonDirectory = await RunInvalidAsync(["--folder", fileInsteadOfFolder], paths.Local);
        Assert.AreEqual(ExitCodes.Configuration, nonDirectory.ExitCode);
        Assert.AreEqual(0, nonDirectory.Coordinators);
        AssertZeroConstructionAndDiagnostic(nonDirectory, "existing directory");

        var clock = new FakeClock();
        var blockedLocal = Path.Combine(paths.Root, "blocked");
        await File.WriteAllTextAsync(blockedLocal, "not a directory");
        var unwritable = await RunInvalidAsync(["--folder", paths.Watch], blockedLocal);
        Assert.AreEqual(ExitCodes.State, unwritable.ExitCode);
        AssertZeroConstructionAndDiagnostic(unwritable, "state_unwritable");

        var overlap = Path.Combine(paths.Watch, "state");
        var overlapping = await RunInvalidAsync(["--folder", paths.Watch], overlap);
        Assert.AreEqual(ExitCodes.State, overlapping.ExitCode);
        AssertZeroConstructionAndDiagnostic(overlapping, "state_overlap");

        Assert.IsTrue(OperatingSystem.IsWindows());
        var physicalState = Path.Combine(paths.Watch, "physical-state");
        Directory.CreateDirectory(physicalState);
        var linkedLocal = Path.Combine(paths.Root, "linked-local");
        CreateJunction(linkedLocal, physicalState);
        var physicalOverlap = await RunInvalidAsync(["--folder", paths.Watch], linkedLocal);
        Assert.AreEqual(ExitCodes.State, physicalOverlap.ExitCode);
        AssertZeroConstructionAndDiagnostic(physicalOverlap, "state_overlap");
        RemoveJunction(linkedLocal);

        var held = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        var locked = await RunInvalidAsync(["--folder", paths.Watch], paths.Local);
        Assert.AreEqual(ExitCodes.State, locked.ExitCode);
        AssertZeroConstructionAndDiagnostic(locked, "state_locked");
        var stateFile = held.Repository.StateFile;
        await held.Repository.DisposeAsync();

        await File.WriteAllTextAsync(stateFile, "{bad");
        var corrupt = await RunInvalidAsync(["--folder", paths.Watch], paths.Local);
        Assert.AreEqual(ExitCodes.State, corrupt.ExitCode);
        AssertZeroConstructionAndDiagnostic(corrupt, "state_invalid");

        const string secret = "sensitive-startup-token";
        Assert.IsFalse(string.Join(
            '\n',
            noToken.Diagnostics,
            missing.Diagnostics,
            nonDirectory.Diagnostics,
            unwritable.Diagnostics,
            overlapping.Diagnostics,
            physicalOverlap.Diagnostics,
            locked.Diagnostics,
            corrupt.Diagnostics).Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StartupOrdering_StateLockAndValidation_PrecedeTokenAcquisition()
    {
        using var paths = new TestPaths();
        var held = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        var tokenReads = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runtime = new ProgramRuntime(
            () =>
            {
                tokenReads++;
                return "must-not-be-read";
            },
            new StringReader(string.Empty),
            false,
            output,
            error,
            () => new WatcherCoordinator(localAppData: paths.Local));

        Assert.AreEqual(ExitCodes.State, await Program.RunAsync(["--folder", paths.Watch], runtime));
        Assert.AreEqual(0, tokenReads);
        await held.Repository.DisposeAsync();
    }

    [TestMethod]
    public async Task BrowserLogin_StateLockAndValidation_PrecedeBrowserLaunch()
    {
        using var paths = new TestPaths();
        var held = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, new FakeClock(), paths.Local, default);
        var browser = new CountingBrowserLogin();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runtime = new ProgramRuntime(
            () => null,
            new StringReader(string.Empty),
            false,
            output,
            error,
            () => new WatcherCoordinator(localAppData: paths.Local),
            _ => browser);

        Assert.AreEqual(
            ExitCodes.State,
            await Program.RunAsync(["--folder", paths.Watch, "--browser-login"], runtime));
        Assert.AreEqual(0, browser.Calls);
        await held.Repository.DisposeAsync();
    }

    [TestMethod]
    public async Task BrowserLogin_FirstOrImmediateCancellationStopsLogin()
    {
        using var firstPaths = new TestPaths();
        using var first = new CancellationTokenSource();
        using var immediate = new CancellationTokenSource();
        var firstBrowser = new WaitingBrowserLogin();
        var firstRun = Program.RunAsync(
            ["--folder", firstPaths.Watch, "--browser-login"],
            BrowserRuntime(firstPaths.Local, firstBrowser),
            first.Token,
            immediate.Token);
        await firstBrowser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Cancel();
        Assert.AreEqual(ExitCodes.Success, await firstRun);
        var firstReopen = (StateOpenResult.Success)await StateRepository.OpenAsync(
            firstPaths.Watch, UploaderConstants.EventId, new FakeClock(), firstPaths.Local, default);
        await firstReopen.Repository.DisposeAsync();

        using var immediatePaths = new TestPaths();
        using var secondFirst = new CancellationTokenSource();
        using var secondImmediate = new CancellationTokenSource();
        var immediateBrowser = new WaitingBrowserLogin();
        var immediateRun = Program.RunAsync(
            ["--folder", immediatePaths.Watch, "--browser-login"],
            BrowserRuntime(immediatePaths.Local, immediateBrowser),
            secondFirst.Token,
            secondImmediate.Token);
        await immediateBrowser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondImmediate.Cancel();
        Assert.AreEqual(ExitCodes.ForcedShutdown, await immediateRun);
        var immediateReopen = (StateOpenResult.Success)await StateRepository.OpenAsync(
            immediatePaths.Watch, UploaderConstants.EventId, new FakeClock(), immediatePaths.Local, default);
        await immediateReopen.Repository.DisposeAsync();
    }

    [TestMethod]
    public async Task InvalidRuntimeCapacityConfiguration_ExitsAsConfigurationError()
    {
        using var paths = new TestPaths();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runtime = new ProgramRuntime(
            () => "token",
            new StringReader(string.Empty),
            false,
            output,
            error,
            () => throw new InvalidDataException(
                "TCFUPLOADER_MAX_SPOOL_BYTES must be a positive byte count."));

        Assert.AreEqual(ExitCodes.Configuration,
            await Program.RunAsync(["--folder", paths.Watch], runtime));
        StringAssert.Contains(error.ToString(), "TCFUPLOADER_MAX_SPOOL_BYTES");
    }

    [TestMethod]
    public void AC23_ProductionProject_HasNoPackageReferenceOrRuntimePackages()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TCFUploader.slnx")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        var project = Path.Combine(directory.FullName, "src", "TCFUploader", "TCFUploader.csproj");
        Assert.IsFalse(File.ReadAllText(project).Contains("<PackageReference", StringComparison.Ordinal));
        var assets = File.ReadAllText(Path.Combine(directory.FullName, "src", "TCFUploader", "obj", "project.assets.json"));
        using var json = System.Text.Json.JsonDocument.Parse(assets);
        var packages = json.RootElement.GetProperty("libraries").EnumerateObject()
            .Count(library => library.Value.GetProperty("type").GetString() == "package");
        Assert.AreEqual(0, packages);
    }

    private static void AssertZeroConstructionAndDiagnostic(StartupResult result, string expected)
    {
        Assert.AreEqual(0, result.Handlers);
        Assert.AreEqual(0, result.Feeds);
        StringAssert.Contains(result.Diagnostics, expected);
        Assert.IsFalse(result.Diagnostics.Contains("sensitive-startup-token", StringComparison.Ordinal));
    }

    private static async Task<StartupResult> RunInvalidAsync(
        string[] args,
        string localAppData,
        string? environmentToken = "sensitive-startup-token",
        bool redirected = false)
    {
        var coordinators = 0;
        var handlers = 0;
        var feeds = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            var runtime = new ProgramRuntime(
                () => environmentToken,
                new StringReader(string.Empty),
                redirected,
                output,
                error,
                () =>
                {
                    coordinators++;
                    return new WatcherCoordinator(
                        handlerFactory: () =>
                        {
                            handlers++;
                            return new ScriptedHttpMessageHandler();
                        },
                        localAppData: localAppData,
                        changeFeedFactory: _ =>
                        {
                            feeds++;
                            return new FakeChangeFeed();
                        });
                });
            var exit = await Program.RunAsync(args, runtime);
            return new StartupResult(
                exit,
                coordinators,
                handlers,
                feeds,
                string.Join(Environment.NewLine, output, error, console));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private sealed record StartupResult(
        int ExitCode,
        int Coordinators,
        int Handlers,
        int Feeds,
        string Diagnostics);

    private sealed class CountingBrowserLogin : IBrowserLogin
    {
        internal int Calls { get; private set; }

        public Task<BrowserLoginResult> AcquireAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<BrowserLoginResult>(
                new BrowserLoginResult.Error("unexpected", "unexpected"));
        }
    }

    private static ProgramRuntime BrowserRuntime(string localAppData, IBrowserLogin browser) =>
        new(
            () => null,
            new StringReader(string.Empty),
            false,
            TextWriter.Null,
            TextWriter.Null,
            () => new WatcherCoordinator(localAppData: localAppData),
            _ => browser);

    private sealed class WaitingBrowserLogin : IBrowserLogin
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BrowserLoginResult> AcquireAsync(CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                return new BrowserLoginResult.Cancelled();
            }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode,
            $"mklink failed: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
    }

    private static void RemoveJunction(string link)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c rmdir \"{link}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }
}
