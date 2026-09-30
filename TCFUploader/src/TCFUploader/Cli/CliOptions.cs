namespace TCFUploader.Cli;

using TCFUploader.Files;

internal sealed record CliOptions(string WatchedRoot, bool BrowserLogin);

internal abstract record CliParseResult
{
    internal sealed record Run(CliOptions Options) : CliParseResult;
    internal sealed record ShowHelp : CliParseResult;
    internal sealed record ShowVersion : CliParseResult;
    internal sealed record Error(string Code, string Message) : CliParseResult;
}

internal static class CliOptionsParser
{
    internal const string Usage = "Usage: TCFUploader --folder <existing-directory> [--browser-login]";

    internal static CliParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            return new CliParseResult.ShowHelp();
        }

        if (args.Length == 1 && args[0] == "--version")
        {
            return new CliParseResult.ShowVersion();
        }

        string? folder = null;
        var browserLogin = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--folder" when folder is null && index + 1 < args.Length:
                    folder = args[++index];
                    break;
                case "--browser-login" when !browserLogin:
                    browserLogin = true;
                    break;
                default:
                    return new CliParseResult.Error("invalid_arguments", Usage);
            }
        }

        if (folder is null)
        {
            return new CliParseResult.Error("invalid_arguments", Usage);
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(fullPath))
            {
                return new CliParseResult.Error("folder_not_found", "The watch folder must be an existing directory.");
            }
            if (!PathSecurity.IsSafeWatchRoot(fullPath))
            {
                return new CliParseResult.Error("folder_reparse", "The watch folder cannot be a reparse point.");
            }

            _ = Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToArray();
            return new CliParseResult.Run(new CliOptions(fullPath, browserLogin));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException or IOException)
        {
            return new CliParseResult.Error("folder_invalid", "The watch folder is invalid or unreadable.");
        }
    }
}
