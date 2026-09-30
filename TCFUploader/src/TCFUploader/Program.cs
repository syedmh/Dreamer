using System.Reflection;
using TCFUploader.BrowserAuth;
using TCFUploader.Cli;
using TCFUploader.Configuration;
using TCFUploader.Hosting;
using TCFUploader.State;

namespace TCFUploader;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var first = new CancellationTokenSource();
        using var immediate = new CancellationTokenSource();
        var signals = 0;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (Interlocked.Increment(ref signals) == 1) first.Cancel();
            else immediate.Cancel();
        };
        return await RunAsync(args, ProgramRuntime.CreateDefault(), first.Token, immediate.Token);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        ProgramRuntime runtime,
        CancellationToken firstSignal = default,
        CancellationToken immediateSignal = default)
    {
        var parsed = CliOptionsParser.Parse(args);
        if (parsed is CliParseResult.ShowHelp)
        {
            runtime.Output.WriteLine(CliOptionsParser.Usage);
            return ExitCodes.Success;
        }
        if (parsed is CliParseResult.ShowVersion)
        {
            runtime.Output.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
            return ExitCodes.Success;
        }
        if (parsed is CliParseResult.Error error)
        {
            runtime.Error.WriteLine(error.Message);
            return ExitCodes.Configuration;
        }

        try
        {
            var options = ((CliParseResult.Run)parsed).Options;
            var coordinator = runtime.CreateCoordinator();
            var open = await coordinator.OpenStateAsync(options.WatchedRoot, immediateSignal);
            if (open is StateOpenResult.Invalid invalid)
            {
                runtime.Error.WriteLine($"{invalid.Code}: {invalid.Message}");
                return ExitCodes.State;
            }
            if (open is StateOpenResult.Locked locked)
            {
                runtime.Error.WriteLine($"{locked.Code}: {locked.Message}");
                return ExitCodes.State;
            }

            var repository = ((StateOpenResult.Success)open).Repository;
            var coordinatorOwnsRepository = false;
            try
            {
                using var browserCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    firstSignal,
                    immediateSignal);
                var token = await TokenAcquirer.ReadAsync(
                    options.BrowserLogin,
                    runtime.ReadEnvironment,
                    runtime.Input,
                    runtime.IsInputRedirected,
                    runtime.CreateBrowserLogin ??
                        (_ => throw new InvalidOperationException("Browser login is unavailable.")),
                    runtime.Output,
                    options.BrowserLogin ? browserCancellation.Token : immediateSignal);
                if (token is TokenReadResult.Error tokenError)
                {
                    if (tokenError.Code == "browser_login_cancelled")
                        return immediateSignal.IsCancellationRequested
                            ? ExitCodes.ForcedShutdown
                            : ExitCodes.Success;
                    runtime.Error.WriteLine(tokenError.Message);
                    return ExitCodes.Configuration;
                }

                coordinatorOwnsRepository = true;
                var authentication = (TokenReadResult.Success)token;
                return await coordinator.RunAsync(
                    repository,
                    authentication.Token,
                    authentication.UserId,
                    firstSignal,
                    immediateSignal);
            }
            finally
            {
                if (!coordinatorOwnsRepository)
                    await repository.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (immediateSignal.IsCancellationRequested)
        {
            return ExitCodes.ForcedShutdown;
        }
        catch (InvalidDataException ex)
        {
            runtime.Error.WriteLine(ex.Message);
            return ExitCodes.Configuration;
        }
        catch (Exception)
        {
            runtime.Error.WriteLine("A fatal runtime error occurred.");
            return ExitCodes.Fatal;
        }
    }
}

internal sealed record ProgramRuntime(
    Func<string?> ReadEnvironment,
    TextReader Input,
    bool IsInputRedirected,
    TextWriter Output,
    TextWriter Error,
    Func<WatcherCoordinator> CreateCoordinator,
    Func<TextWriter, IBrowserLogin>? CreateBrowserLogin = null)
{
    internal static ProgramRuntime CreateDefault() => new(
        () => Environment.GetEnvironmentVariable(UploaderConstants.TokenEnvironmentVariable),
        Console.In,
        Console.IsInputRedirected,
        Console.Out,
        Console.Error,
        () => new WatcherCoordinator(RuntimeOptions.FromEnvironment(Environment.GetEnvironmentVariable)),
        output => BrowserLogin.CreateDefault(
            output,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
}
