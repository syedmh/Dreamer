using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Playwright;

namespace Husaynia.BaselineCapture;

public sealed record BrowserLaunchVerification(
    string ExecutablePath,
    string ExpectedSha256,
    string ActualSha256,
    IReadOnlyDictionary<string, string> ChildEnvironment,
    string WorkingDirectory,
    bool ChromiumSandbox);

public sealed record ChildProcessIsolationProbe(
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public static class BrowserExecutableVerifier
{
    private static readonly SemaphoreSlim ProcessEnvironmentLock = new(1, 1);
    private static readonly string[] ForbiddenEnvironmentFragments =
    [
        "proxy", "token", "secret", "password", "cookie", "credential", "client_cert",
        "extra_headers", "workspace", "repository", "storage_state"
    ];

    public static BrowserLaunchVerification Verify(
        CaptureProfile profile,
        string executablePath,
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        string emptyRunTempDirectory,
        bool processIsElevated)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(ambientEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyRunTempDirectory);

        if (processIsElevated)
        {
            throw new CaptureSafetyException("elevated-process-not-allowed");
        }

        var workingDirectory = Path.GetFullPath(emptyRunTempDirectory);
        if (!Directory.Exists(workingDirectory)
            || Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            throw new CaptureSafetyException("browser-working-directory-not-empty");
        }

        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is not null && IsSubpathOf(workingDirectory, repositoryRoot))
        {
            throw new CaptureSafetyException("browser-working-directory-inside-workspace");
        }

        if (PathContainsReparsePoint(workingDirectory))
        {
            throw new CaptureSafetyException("browser-working-directory-reparse-point");
        }

        var identity = profile.GetCurrentBrowserIdentity();
        var resolvedPath = Path.GetFullPath(executablePath);
        if (!File.Exists(resolvedPath))
        {
            throw new CaptureSafetyException("browser-executable-missing");
        }

        if (PathContainsReparsePoint(resolvedPath))
        {
            throw new CaptureSafetyException("browser-executable-reparse-point");
        }

        if (!resolvedPath.Contains(
                $"chromium-{identity.ChromiumRevision}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureSafetyException("browser-revision-mismatch");
        }

        var actualVersion = FileVersionInfo.GetVersionInfo(resolvedPath).ProductVersion;
        if (!string.Equals(actualVersion, identity.ChromiumVersion, StringComparison.Ordinal))
        {
            throw new CaptureSafetyException("browser-version-mismatch");
        }

        var resolvedPackageVersion = ReadPlaywrightPackageVersion();
        if (!string.Equals(resolvedPackageVersion, identity.PlaywrightVersion, StringComparison.Ordinal))
        {
            throw new CaptureSafetyException("playwright-version-mismatch");
        }

        using var executable = File.OpenRead(resolvedPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        if (!string.Equals(actualHash, identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureSafetyException("browser-executable-hash-mismatch");
        }

        var childEnvironment = BuildChildEnvironment(ambientEnvironment, workingDirectory);
        if (childEnvironment.Any(pair =>
                ForbiddenEnvironmentFragments.Any(fragment =>
                    pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                || pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CaptureSafetyException("browser-child-environment-not-sanitized");
        }

        return new BrowserLaunchVerification(
            resolvedPath,
            identity.ExecutableSha256,
            actualHash,
            childEnvironment,
            workingDirectory,
            true);
    }

    public static string ResolveExpectedExecutablePath(CaptureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = profile.GetCurrentBrowserIdentity();
        var browserRoot = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Caches",
                    "ms-playwright")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache",
                    "ms-playwright");
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(
                browserRoot,
                $"chromium-{identity.ChromiumRevision}",
                "chrome-win64",
                "chrome.exe")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(
                    browserRoot,
                    $"chromium-{identity.ChromiumRevision}",
                    RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                        ? "chrome-mac-arm64"
                        : "chrome-mac",
                    "Chromium.app",
                    "Contents",
                    "MacOS",
                    "Chromium")
                : Path.Combine(
                    browserRoot,
                    $"chromium-{identity.ChromiumRevision}",
                    "chrome-linux",
                    "chrome");
        return Path.GetFullPath(executable);
    }

    public static async Task<ChildProcessIsolationProbe> ProbeChildIsolationAsync(
        BrowserLaunchVerification verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        return await RunIsolatedAsync(
            verification,
            async token =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows()
                        ? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "cmd.exe")
                        : "/bin/sh",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (OperatingSystem.IsWindows())
                {
                    startInfo.ArgumentList.Add("/d");
                    startInfo.ArgumentList.Add("/c");
                    startInfo.ArgumentList.Add("cd & set");
                }
                else
                {
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add("pwd; env");
                }

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("isolation-probe-start-failed");
                var output = await process.StandardOutput.ReadToEndAsync(token);
                var error = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"isolation-probe-failed:{error}");
                }

                var lines = output
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                var childEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf('=');
                    if (separator > 0)
                    {
                        childEnvironment[line[..separator]] = line[(separator + 1)..];
                    }
                }

                return new ChildProcessIsolationProbe(lines[0], childEnvironment);
            },
            cancellationToken);
    }

    internal static async Task<T> RunIsolatedAsync<T>(
        BrowserLaunchVerification verification,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(action);
        await ProcessEnvironmentLock.WaitAsync(cancellationToken);
        var originalDirectory = Environment.CurrentDirectory;
        var originalEnvironment = SnapshotEnvironment();
        try
        {
            ReplaceProcessEnvironment(verification.ChildEnvironment);
            Environment.CurrentDirectory = verification.WorkingDirectory;
            return await action(cancellationToken);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            ReplaceProcessEnvironment(originalEnvironment);
            ProcessEnvironmentLock.Release();
        }
    }

    public static bool IsProcessElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? GetEffectiveUserId() == 0
            : true;
    }

    private static Dictionary<string, string> BuildChildEnvironment(
        IReadOnlyDictionary<string, string?> ambientEnvironment,
        string workingDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windowsDirectory = Directory.GetParent(systemDirectory)?.FullName
                ?? throw new CaptureSafetyException("windows-directory-unavailable");
            environment["SystemRoot"] = windowsDirectory;
            environment["WINDIR"] = windowsDirectory;
            environment["TEMP"] = workingDirectory;
            environment["TMP"] = workingDirectory;
        }
        else
        {
            environment["HOME"] = workingDirectory;
            environment["TMPDIR"] = workingDirectory;
        }

        return environment;
    }

    private static Dictionary<string, string> SnapshotEnvironment() =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is not null && entry.Value is not null)
            .ToDictionary(
                entry => entry.Key.ToString()!,
                entry => entry.Value!.ToString()!,
                StringComparer.OrdinalIgnoreCase);

    private static void ReplaceProcessEnvironment(IReadOnlyDictionary<string, string> replacement)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        foreach (var pair in replacement)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static string? ReadPlaywrightPackageVersion()
    {
        var lockPath = FindRepositoryFile(
            Path.Combine("tools", "Husaynia.BaselineCapture", "packages.lock.json"));
        if (lockPath is null)
        {
            return typeof(Playwright).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?.Split('+', 2)[0];
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        return document.RootElement.GetProperty("dependencies")
            .EnumerateObject()
            .Select(framework => framework.Value)
            .Where(framework => framework.TryGetProperty("Microsoft.Playwright", out _))
            .Select(framework => framework.GetProperty("Microsoft.Playwright").GetProperty("resolved").GetString())
            .FirstOrDefault();
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "Husaynia.BaselineCapture")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindRepositoryFile(string relative)
    {
        var root = FindRepositoryRoot();
        if (root is not null)
        {
            var candidate = Path.Combine(root, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var direct = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(relative));
        return File.Exists(direct) ? direct : null;
    }

    private static bool IsSubpathOf(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool PathContainsReparsePoint(string path)
    {
        var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo;
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }

        return false;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEffectiveUserId() => geteuid();
}

internal sealed class VerifiedBrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly string _temporaryDirectory;

    private VerifiedBrowserSession(
        IPlaywright playwright,
        IBrowser browser,
        BrowserLaunchVerification verification,
        string temporaryDirectory,
        IReadOnlyList<string> chromiumHostResolverRules)
    {
        _playwright = playwright;
        Browser = browser;
        Verification = verification;
        _temporaryDirectory = temporaryDirectory;
        InstanceId = Guid.NewGuid().ToString("N");
        LaunchedAtUtc = DateTimeOffset.UtcNow;
        ResolverMapSha256 = TrustedEndpointPolicy.ComputeResolverMapSha256(
            chromiumHostResolverRules);
    }

    public IBrowser Browser { get; }

    public BrowserLaunchVerification Verification { get; }

    public string InstanceId { get; }

    public DateTimeOffset LaunchedAtUtc { get; }

    public string ResolverMapSha256 { get; }

    public static async Task<VerifiedBrowserSession> CreateAsync(
        CaptureProfile profile,
        IReadOnlyList<string> chromiumHostResolverRules,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            var ambient = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(
                    entry => entry.Key.ToString()!,
                    entry => entry.Value?.ToString(),
                    StringComparer.OrdinalIgnoreCase);
            var executablePath = BrowserExecutableVerifier.ResolveExpectedExecutablePath(profile);
            var verification = BrowserExecutableVerifier.Verify(
                profile,
                executablePath,
                ambient,
                temporaryDirectory,
                BrowserExecutableVerifier.IsProcessElevated());
            var arguments = new List<string>
            {
                "--no-proxy-server",
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-default-apps",
                "--disable-domain-reliability",
                "--disable-features=WebTransport,WebTransportDeveloperMode,MediaRouter,DnsOverHttps",
                "--disable-quic",
                "--disable-sync",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--metrics-recording-only",
                "--no-first-run"
            };
            if (chromiumHostResolverRules.Count > 0)
            {
                arguments.Add($"--host-resolver-rules={string.Join(',', chromiumHostResolverRules)}");
            }

            var launched = await BrowserExecutableVerifier.RunIsolatedAsync(
                verification,
                async token =>
                {
                    var isolatedPlaywright = await Playwright.CreateAsync().WaitAsync(token);
                    try
                    {
                        var isolatedBrowser = await isolatedPlaywright.Chromium.LaunchAsync(
                            new BrowserTypeLaunchOptions
                            {
                                Headless = true,
                                ExecutablePath = verification.ExecutablePath,
                                ChromiumSandbox = true,
                                Env = verification.ChildEnvironment.ToDictionary(
                                    pair => pair.Key,
                                    pair => pair.Value),
                                Args = arguments
                            }).WaitAsync(token);
                        return (Playwright: isolatedPlaywright, Browser: isolatedBrowser);
                    }
                    catch
                    {
                        isolatedPlaywright.Dispose();
                        throw;
                    }
                },
                cancellationToken);
            playwright = launched.Playwright;
            browser = launched.Browser;

            if (!string.Equals(browser.Version, CaptureProfile.ChromiumVersion, StringComparison.Ordinal))
            {
                throw new CaptureSafetyException("launched-browser-version-mismatch");
            }

            return new VerifiedBrowserSession(
                playwright,
                browser,
                verification,
                temporaryDirectory,
                chromiumHostResolverRules);
        }
        catch
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }

            playwright?.Dispose();
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright.Dispose();
        TryDeleteDirectory(_temporaryDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort after the verified browser process has exited.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup is best effort after the verified browser process has exited.
        }
    }
}
