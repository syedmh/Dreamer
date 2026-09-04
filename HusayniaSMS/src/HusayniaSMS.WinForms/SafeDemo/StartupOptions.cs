namespace HusayniaSMS.WinForms.SafeDemo;

public enum StartupMode
{
    Production,
    SafeDemo
}

public enum SafeDemoScenario
{
    AllSuccess,
    Mixed,
    AuthFailure,
    Delayed
}

public sealed record StartupOptions(StartupMode Mode, SafeDemoScenario? Scenario);

public sealed record StartupParseResult(
    bool Succeeded,
    StartupOptions? Options,
    string? SafeError);

public static class StartupOptionsParser
{
    public static StartupParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return Success(new(StartupMode.Production, null));
        }

        var safeDemo = false;
        string? scenarioValue = null;
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--safe-demo", StringComparison.OrdinalIgnoreCase))
            {
                if (safeDemo)
                {
                    return Failure();
                }

                safeDemo = true;
                continue;
            }

            const string scenarioPrefix = "--scenario=";
            if (argument.StartsWith(scenarioPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (scenarioValue is not null)
                {
                    return Failure();
                }

                scenarioValue = argument[scenarioPrefix.Length..];
                if (scenarioValue.Length == 0)
                {
                    return Failure();
                }

                continue;
            }

            return Failure();
        }

        if (!safeDemo)
        {
            return Failure();
        }

        var scenario = scenarioValue?.ToLowerInvariant() switch
        {
            null => SafeDemoScenario.AllSuccess,
            "all-success" => SafeDemoScenario.AllSuccess,
            "mixed" => SafeDemoScenario.Mixed,
            "auth-failure" => SafeDemoScenario.AuthFailure,
            "delayed" => SafeDemoScenario.Delayed,
            _ => (SafeDemoScenario?)null
        };
        return scenario is null
            ? Failure()
            : Success(new(StartupMode.SafeDemo, scenario));
    }

    private static StartupParseResult Success(StartupOptions options) =>
        new(true, options, null);

    private static StartupParseResult Failure() =>
        new(false, null,
            "Invalid startup options. Use no arguments for production or --safe-demo with an optional supported --scenario value.");
}
