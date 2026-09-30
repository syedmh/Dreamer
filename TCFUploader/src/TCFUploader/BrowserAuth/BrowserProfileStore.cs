using TCFUploader.Files;

namespace TCFUploader.BrowserAuth;

internal abstract record BrowserProfileResult
{
    internal sealed record Success(string Path) : BrowserProfileResult;
    internal sealed record Error(string Code, string Message) : BrowserProfileResult;
}

internal interface IBrowserProfileStore
{
    BrowserProfileResult Create();
    Task<bool> DeleteAsync(string profilePath, CancellationToken cancellationToken);
}

internal sealed class BrowserProfileStore(string localAppData) : IBrowserProfileStore
{
    // Chromium subprocesses can retain profile handles briefly after the browser process exits.
    private const int DeleteAttempts = 41;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly string root = Path.Combine(localAppData, "TCFUploader", "browser-auth");

    public BrowserProfileResult Create()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(localAppData))
                return Failure();

            if (Directory.Exists(root))
            {
                if (!PathSecurity.HasNoReparsePointComponents(root) ||
                    !PathSecurity.HasPrivateDirectoryAcl(root))
                    return Failure();
            }
            else
            {
                Directory.CreateDirectory(root);
                PathSecurity.HardenPrivateDirectory(root);
            }

            var profilePath = Path.Combine(root, $"profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profilePath);
            PathSecurity.HardenPrivateDirectory(profilePath);
            if (!PathSecurity.HasNoReparsePointComponents(profilePath) ||
                !PathSecurity.HasPrivateDirectoryAcl(profilePath))
                return Failure();
            return new BrowserProfileResult.Success(profilePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
            ArgumentException or NotSupportedException)
        {
            return Failure();
        }
    }

    public async Task<bool> DeleteAsync(string profilePath, CancellationToken cancellationToken)
    {
        if (!IsExactProfilePath(profilePath))
            return false;

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(profilePath))
                    return true;
                Directory.Delete(profilePath, recursive: true);
                if (!Directory.Exists(profilePath))
                    return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }

            if (attempt + 1 < DeleteAttempts)
                await Task.Delay(DeleteRetryDelay, cancellationToken);
        }

        return !Directory.Exists(profilePath);
    }

    private bool IsExactProfilePath(string profilePath)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath));
            return string.Equals(Path.GetDirectoryName(fullProfile), fullRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullProfile).StartsWith("profile-", StringComparison.Ordinal) &&
                Path.GetFileName(fullProfile).Length == "profile-".Length + 32;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static BrowserProfileResult Failure() =>
        new BrowserProfileResult.Error(
            "browser_profile_failed",
            "A private browser profile could not be created securely. Use a noninteractive token source.");
}
