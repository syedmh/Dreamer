using TCFUploader.Cli;

namespace TCFUploader.BrowserAuth;

internal abstract record BrowserLoginResult
{
    internal sealed record Success(string Token, string? UserId = null) : BrowserLoginResult;
    internal sealed record Cancelled : BrowserLoginResult;
    internal sealed record Error(string Code, string Message) : BrowserLoginResult;
}

internal interface IBrowserLogin
{
    Task<BrowserLoginResult> AcquireAsync(CancellationToken cancellationToken);
}

internal interface IBrowserTokenCapture
{
    Task<BrowserTokenCaptureResult> CaptureAsync(
        string profilePath,
        IBrowserProcess process,
        CancellationToken cancellationToken);
}

internal abstract record BrowserTokenCaptureResult
{
    internal sealed record Authentication(string Token, string UserId) : BrowserTokenCaptureResult;
    internal sealed record BrowserClosed : BrowserTokenCaptureResult;
}

internal sealed class BrowserLogin(
    TextWriter output,
    IBrowserExecutableLocator locator,
    IBrowserProfileStore profiles,
    IBrowserProcessLauncher launcher,
    IBrowserTokenCapture capture,
    Func<bool> isWindows,
    TimeSpan timeout) : IBrowserLogin
{
    internal static BrowserLogin CreateDefault(
        TextWriter output,
        Func<string, string?> readEnvironment,
        string localAppData)
    {
        var profiles = new BrowserProfileStore(localAppData);
        return new BrowserLogin(
            output,
            new BrowserExecutableLocator(readEnvironment),
            profiles,
            new BrowserProcessLauncher(),
            new ChromiumDevToolsClient(),
            OperatingSystem.IsWindows,
            TimeSpan.FromMinutes(10));
    }

    public async Task<BrowserLoginResult> AcquireAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new BrowserLoginResult.Cancelled();

        if (!isWindows())
        {
            return new BrowserLoginResult.Error(
                "browser_unsupported",
                "Browser login is available only on Windows. Use the environment variable or redirected input.");
        }

        var executable = locator.Locate();
        if (executable is BrowserExecutableResult.Error executableError)
            return new BrowserLoginResult.Error(executableError.Code, executableError.Message);

        var profile = profiles.Create();
        if (profile is BrowserProfileResult.Error profileError)
            return new BrowserLoginResult.Error(profileError.Code, profileError.Message);

        var profilePath = ((BrowserProfileResult.Success)profile).Path;
        IBrowserProcess? process = null;
        BrowserLoginResult result;
        var cleanupConfirmed = false;
        try
        {
            process = launcher.Launch(
                ((BrowserExecutableResult.Success)executable).Executable,
                profilePath);
            output.WriteLine("Browser launched. Sign in to LumaBooth to continue.");

            using var captureCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var captureTask = capture.CaptureAsync(
                profilePath,
                process,
                captureCancellation.Token);
            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(captureTask, timeoutTask, cancellationTask);

            if (completed == cancellationTask)
            {
                captureCancellation.Cancel();
                await ObserveCancellationAsync(captureTask);
                result = new BrowserLoginResult.Cancelled();
            }
            else if (completed == timeoutTask)
            {
                captureCancellation.Cancel();
                await ObserveCancellationAsync(captureTask);
                result = new BrowserLoginResult.Error(
                    "browser_login_timeout",
                    "Browser login timed out after 10 minutes. Retry or use the environment variable or redirected input.");
            }
            else
            {
                var captured = await captureTask;
                if (captured is BrowserTokenCaptureResult.BrowserClosed)
                {
                    result = new BrowserLoginResult.Error(
                        "browser_closed",
                        "The browser closed before authentication completed. Retry or use a noninteractive token source.");
                }
                else
                {
                    var authentication = (BrowserTokenCaptureResult.Authentication)captured;
                    var validated = TokenSource.Validate(authentication.Token);
                    result = validated switch
                    {
                        TokenReadResult.Success success when !string.IsNullOrWhiteSpace(authentication.UserId) =>
                            new BrowserLoginResult.Success(success.Token, authentication.UserId),
                        TokenReadResult.Success => new BrowserLoginResult.Error(
                            "browser_user_missing",
                            "Browser authentication did not provide the signed-in user identifier."),
                        TokenReadResult.Error error => new BrowserLoginResult.Error(error.Code, error.Message),
                        _ => throw new InvalidOperationException("Unknown token validation result.")
                    };
                    if (result is BrowserLoginResult.Success)
                        output.WriteLine("Authentication acquired. Closing browser.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new BrowserLoginResult.Cancelled();
        }
        catch (Exception)
        {
            result = new BrowserLoginResult.Error(
                "browser_login_failed",
                "Browser authentication failed. Retry or use the environment variable or redirected input.");
        }
        finally
        {
            var processClosed = process is null || await CloseProcessAsync(process);
            var profileDeleted = await profiles.DeleteAsync(profilePath, CancellationToken.None);
            cleanupConfirmed = processClosed && profileDeleted;
            process?.Dispose();
        }

        return cleanupConfirmed
            ? result
            : new BrowserLoginResult.Error(
                "browser_cleanup_failed",
                "Browser login cleanup could not be confirmed. Close the launched browser and remove its private profile before retrying.");
    }

    private static async Task<bool> CloseProcessAsync(IBrowserProcess process)
    {
        try
        {
            if (!process.HasExited)
                process.KillTree();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(wait.Token);
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
