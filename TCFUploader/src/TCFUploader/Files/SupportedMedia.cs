namespace TCFUploader.Files;

internal static class SupportedMedia
{
    internal const string UnsupportedOutcomeCode = "unsupported_media_type";

    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png"
        };

    internal static bool IsSupportedPath(string path) =>
        IsSupportedExtension(Path.GetExtension(path));

    internal static bool IsSupportedExtension(string extension) =>
        Extensions.Contains(extension);
}
