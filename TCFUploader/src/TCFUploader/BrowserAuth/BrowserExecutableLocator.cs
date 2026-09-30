using TCFUploader.Configuration;

namespace TCFUploader.BrowserAuth;

internal enum BrowserKind
{
    Edge,
    Chrome
}

internal sealed record BrowserExecutable(string Path, BrowserKind Kind);

internal abstract record BrowserExecutableResult
{
    internal sealed record Success(BrowserExecutable Executable) : BrowserExecutableResult;
    internal sealed record Error(string Code, string Message) : BrowserExecutableResult;
}

internal interface IBrowserExecutableLocator
{
    BrowserExecutableResult Locate();
}

internal sealed class BrowserExecutableLocator(Func<string, string?> readEnvironment)
    : IBrowserExecutableLocator
{
    public BrowserExecutableResult Locate()
    {
        var configured = readEnvironment(UploaderConstants.BrowserPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var exactPath = Path.GetFullPath(configured);
                return File.Exists(exactPath)
                    ? new BrowserExecutableResult.Success(
                        new BrowserExecutable(exactPath, InferKind(exactPath)))
                    : InvalidOverride();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return InvalidOverride();
            }
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate.Path))
                return new BrowserExecutableResult.Success(candidate);
        }

        return new BrowserExecutableResult.Error(
            "browser_not_found",
            $"Microsoft Edge or Google Chrome was not found. Install one or set {UploaderConstants.BrowserPathEnvironmentVariable}.");
    }

    private static BrowserExecutableResult InvalidOverride() =>
        new BrowserExecutableResult.Error(
            "browser_path_invalid",
            $"{UploaderConstants.BrowserPathEnvironmentVariable} must name an existing browser executable.");

    private static BrowserKind InferKind(string path) =>
        Path.GetFileName(path).Contains("edge", StringComparison.OrdinalIgnoreCase)
            ? BrowserKind.Edge
            : BrowserKind.Chrome;

    private static IEnumerable<BrowserExecutable> Candidates()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var root in new[] { programFilesX86, programFiles, localAppData }
                     .Where(root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new BrowserExecutable(
                Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"),
                BrowserKind.Edge);
        }
        foreach (var root in new[] { programFiles, programFilesX86, localAppData }
                     .Where(root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new BrowserExecutable(
                Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"),
                BrowserKind.Chrome);
        }
    }
}
