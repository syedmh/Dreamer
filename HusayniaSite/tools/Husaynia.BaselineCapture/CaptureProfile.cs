using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Husaynia.BaselineCapture;

public sealed record BrowserIdentityProfile(
    string OperatingSystem,
    string Architecture,
    string PlaywrightVersion,
    string ChromiumRevision,
    string ChromiumVersion,
    string ExecutableSha256);

public sealed record CaptureViewport(string Name, int Width, int Height);

public sealed record StaticResourceRule(string Host, IReadOnlySet<string> ResourceTypes);

public sealed record CaptureProfile(
    string PolicyVersion,
    Uri PrimaryOrigin,
    IReadOnlyDictionary<string, Uri> Representatives,
    IReadOnlyList<CaptureViewport> Viewports,
    IReadOnlyList<StaticResourceRule> StaticResources,
    IReadOnlyList<BrowserIdentityProfile> BrowserIdentities)
{
    public const string PlaywrightVersion = "1.62.0";
    public const string ChromiumRevision = "1234";
    public const string ChromiumVersion = "151.0.7922.34";

    private static readonly IReadOnlySet<string> StylesheetResourceTypes =
        new[] { "stylesheet" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> FontImageResourceTypes =
        new[] { "font", "image" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> StaticCdnResourceTypes =
        new[] { "stylesheet", "font", "image" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static CaptureProfile Approved { get; } = CreateApproved();

    public IReadOnlySet<string> ExpectedScreenshotKeys =>
        Representatives.Keys
            .SelectMany(template => Viewports.Select(viewport => $"{template}|{viewport.Name}"))
            .ToFrozenSet(StringComparer.Ordinal);

    public bool IsApprovedOrigin(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.IsNullOrEmpty(uri.UserInfo)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(PrimaryOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    public BrowserIdentityProfile GetCurrentBrowserIdentity()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return BrowserIdentities.SingleOrDefault(identity =>
                   identity.OperatingSystem.Equals(os, StringComparison.OrdinalIgnoreCase)
                   && identity.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase)
                   && identity.PlaywrightVersion == PlaywrightVersion
                   && identity.ChromiumRevision == ChromiumRevision
                   && identity.ChromiumVersion == ChromiumVersion)
               ?? throw new InvalidOperationException("browser-identity-profile-missing");
    }

    private static CaptureProfile CreateApproved()
    {
        var identities = LoadBrowserIdentities();
        var representatives = new ReadOnlyDictionary<string, Uri>(
            new Dictionary<string, Uri>(StringComparer.Ordinal)
            {
                ["home"] = new("https://www.husaynia.org:443/"),
                ["content-page"] = new("https://www.husaynia.org:443/announcements/"),
                ["contact-form"] = new("https://www.husaynia.org:443/contact-us/"),
                ["donation-form"] = new("https://www.husaynia.org:443/donate-construction/"),
                ["event-detail"] = new("https://www.husaynia.org:443/event/%E2%9A%94%EF%B8%8F-battle-of-badr/"),
                ["religious-content"] = new("https://www.husaynia.org:443/duas-dua-e-tawassul/")
            });
        var viewports = Array.AsReadOnly<CaptureViewport>(
        [
            new("desktop-1440x900", 1440, 900),
            new("desktop-1920x1080", 1920, 1080),
            new("tablet-1024x768", 1024, 768),
            new("tablet-768x1024", 768, 1024),
            new("mobile-390x844", 390, 844),
            new("mobile-320x568", 320, 568)
        ]);
        var staticResources = Array.AsReadOnly<StaticResourceRule>(
        [
            new("fonts.googleapis.com", StylesheetResourceTypes),
            new("fonts.gstatic.com", FontImageResourceTypes),
            new("cdnjs.cloudflare.com", StaticCdnResourceTypes),
            new("cdn.jsdelivr.net", StaticCdnResourceTypes)
        ]);
        return new CaptureProfile(
            "adr-009-v3",
            new Uri("https://www.husaynia.org:443/"),
            representatives,
            viewports,
            staticResources,
            Array.AsReadOnly(identities.ToArray()));
    }

    private static ReadOnlyCollection<BrowserIdentityProfile> LoadBrowserIdentities()
    {
        var path = FindIdentityPath();
        if (path is null)
        {
            throw new InvalidOperationException("browser-identity-profile-file-missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return Array.AsReadOnly(document.RootElement.GetProperty("identities")
            .EnumerateArray()
            .Select(item => new BrowserIdentityProfile(
                item.GetProperty("operatingSystem").GetString()!,
                item.GetProperty("architecture").GetString()!,
                item.GetProperty("playwrightVersion").GetString()!,
                item.GetProperty("chromiumRevision").GetString()!,
                item.GetProperty("chromiumVersion").GetString()!,
                item.GetProperty("executableSha256").GetString()!))
            .ToArray());
    }

    private static string? FindIdentityPath()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "chromium-executable-sha256.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tools",
                "Husaynia.BaselineCapture",
                "chromium-executable-sha256.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
