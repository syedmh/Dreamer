namespace TCFUploader.Files;

internal static class ContentTypeMap
{
    private static readonly IReadOnlyDictionary<string, string> Types =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png"
        };

    internal static string Get(string extension) =>
        Types.TryGetValue(extension, out var type) ? type : "application/octet-stream";

    internal static bool IsCompatibleStoredContentType(string extension, string contentType) =>
        string.Equals(contentType, GetLegacy(extension), StringComparison.Ordinal);

    internal static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.' || extension.Length is < 2 or > 17)
        {
            return string.Empty;
        }
        return extension.AsSpan(1).ToArray().All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            ? extension.ToLowerInvariant()
            : string.Empty;
    }

    private static string GetLegacy(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
}
