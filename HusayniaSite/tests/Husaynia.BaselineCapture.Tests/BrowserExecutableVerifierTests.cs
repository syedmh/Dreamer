using Husaynia.BaselineCapture;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class BrowserExecutableVerifierTests
{
    [Fact]
    public void ApprovedPackageMatchedExecutableProducesSandboxedSanitizedLaunch()
    {
        using var temp = EmptyTempDirectory();
        var result = BrowserExecutableVerifier.Verify(
            CaptureProfile.Approved,
            ChromiumExecutable(),
            AmbientEnvironment(),
            temp.Path,
            processIsElevated: false);

        Assert.True(result.ChromiumSandbox);
        Assert.Equal(result.ExpectedSha256, result.ActualSha256);
        Assert.Equal(temp.Path, result.WorkingDirectory);
        Assert.DoesNotContain(
            result.ChildEnvironment.Keys,
            key => key.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(temp.Path, result.ChildEnvironment["TEMP"]);
        Assert.Equal(temp.Path, result.ChildEnvironment["TMP"]);
    }

    [Fact]
    public async Task ChildLaunchedThroughIsolationScopeSeesOnlyAllowlistAndTempWorkingDirectory()
    {
        using var temp = EmptyTempDirectory();
        var ambient = AmbientEnvironment();
        ambient["PATH"] = "C:\\workspace\\attacker-bin";
        ambient["NODE_OPTIONS"] = "--require C:\\workspace\\hook.js";
        ambient["HTTPS_PROXY"] = "https://proxy.example";
        ambient["BUILD_SECRET"] = "do-not-inherit";
        ambient["GITHUB_WORKSPACE"] = "C:\\workspace";
        var verification = BrowserExecutableVerifier.Verify(
            CaptureProfile.Approved,
            ChromiumExecutable(),
            ambient,
            temp.Path,
            processIsElevated: false);

        var probe = await BrowserExecutableVerifier.ProbeChildIsolationAsync(
            verification,
            CancellationToken.None);

        Assert.Equal(temp.Path, probe.WorkingDirectory, ignoreCase: true);
        Assert.All(
            verification.ChildEnvironment,
            expected => Assert.Equal(expected.Value, probe.Environment[expected.Key]));
        Assert.Subset(
            verification.ChildEnvironment.Keys
                .Concat(["COMSPEC", "PATHEXT", "PROMPT"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            probe.Environment.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(probe.Environment.Keys, key =>
            key.Equals("PATH", StringComparison.OrdinalIgnoreCase)
            || key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
            || key.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("workspace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HashMismatchFailsBeforeLaunch()
    {
        using var temp = EmptyTempDirectory();
        var identity = CaptureProfile.Approved.GetCurrentBrowserIdentity() with
        {
            ExecutableSha256 = new string('0', 64)
        };
        var profile = CaptureProfile.Approved with
        {
            BrowserIdentities = [identity]
        };

        var exception = Assert.Throws<CaptureSafetyException>(() =>
            BrowserExecutableVerifier.Verify(
                profile,
                ChromiumExecutable(),
                AmbientEnvironment(),
                temp.Path,
                processIsElevated: false));

        Assert.Equal("browser-executable-hash-mismatch", exception.ReasonCode);
    }

    [Fact]
    public void ElevatedOrDirtyLaunchInputsAreRejected()
    {
        using var elevatedTemp = EmptyTempDirectory();
        var elevated = Assert.Throws<CaptureSafetyException>(() =>
            BrowserExecutableVerifier.Verify(
                CaptureProfile.Approved,
                ChromiumExecutable(),
                AmbientEnvironment(),
                elevatedTemp.Path,
                processIsElevated: true));
        Assert.Equal("elevated-process-not-allowed", elevated.ReasonCode);

        using var dirtyTemp = EmptyTempDirectory();
        File.WriteAllText(Path.Combine(dirtyTemp.Path, "unexpected.txt"), "dirty");
        var dirty = Assert.Throws<CaptureSafetyException>(() =>
            BrowserExecutableVerifier.Verify(
                CaptureProfile.Approved,
                ChromiumExecutable(),
                AmbientEnvironment(),
                dirtyTemp.Path,
                processIsElevated: false));
        Assert.Equal("browser-working-directory-not-empty", dirty.ReasonCode);

    }

    [Fact]
    public void DiagnosticProvenanceSerializesStructuralAttestationsWithoutAbsolutePaths()
    {
        const string userSentinel = "sentinel-user-9381";
        var executable = Path.Combine(
            "C:\\Users",
            userSentinel,
            "workspace-sentinel",
            "ms-playwright",
            "chromium-1234",
            "chrome.exe");
        var workingDirectory = Path.Combine(
            "C:\\Users",
            userSentinel,
            "AppData",
            "Local",
            "Temp",
            "temp-sentinel");
        var verification = new BrowserLaunchVerification(
            executable,
            new string('a', 64),
            new string('a', 64),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEMP"] = workingDirectory,
                ["TMP"] = workingDirectory,
                ["SystemRoot"] = "C:\\Windows",
                ["WINDIR"] = "C:\\Windows"
            },
            workingDirectory,
            true);
        var provenance = PlaywrightScreenshotCapture.CreateProvenance(
            "capture-sentinel",
            new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero),
            CaptureProfile.ChromiumVersion,
            verification,
            "lock-hash",
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase),
            []);

        var json = JsonSerializer.Serialize(provenance);

        Assert.DoesNotContain(userSentinel, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "workspace-sentinel",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "temp-sentinel",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.IsPathRooted(provenance.ChromiumExecutable));
        Assert.False(Path.IsPathRooted(provenance.BrowserWorkingDirectory));
        Assert.True(provenance.BrowserExecutableOutsideWorkspace);
        Assert.True(provenance.BrowserWorkingDirectoryOutsideWorkspace);
        Assert.True(provenance.BrowserWorkingDirectoryEmpty);
        Assert.True(provenance.ChildEnvironmentSanitized);
    }

    [Fact]
    public async Task CaptureCreationRecordsBootstrapBrowserLaunchEvidence()
    {
        using var output = EmptyTempDirectory();
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            new StableDnsResolver(),
            CancellationToken.None);
        var before = DateTimeOffset.UtcNow;

        await using var capture = await PlaywrightScreenshotCapture.CreateAsync(
            "bootstrap-launch-proof",
            output.Path,
            policy,
            CancellationToken.None);

        var launch = Assert.Single(capture.Provenance.BrowserLaunches);
        Assert.Equal("bootstrap", launch.Reason);
        Assert.Null(launch.PreviousBrowserInstanceId);
        Assert.InRange(launch.LaunchedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(
            TrustedEndpointPolicy.ComputeResolverMapSha256(
                policy.ChromiumHostResolverRules),
            launch.ResolverMapSha256);
        Assert.Equal(64, launch.ResolverMapSha256.Length);
    }

    private static Dictionary<string, string?> AmbientEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["ComSpec"] = Environment.GetEnvironmentVariable("ComSpec"),
            ["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT")
        };

    private static string ChromiumExecutable() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright",
            $"chromium-{CaptureProfile.ChromiumRevision}",
            "chrome-win64",
            "chrome.exe");

    private static TemporaryDirectory EmptyTempDirectory() => new();

    private sealed class StableDnsResolver : ITrustedDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [IPAddress.Parse("93.184.216.34")]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"husaynia-browser-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
