using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class BaselineArtifactTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Evidence = Environment.GetEnvironmentVariable("HUSAYNIA_BASELINE_EVIDENCE")
        ?? Path.Combine(Root, "evidence", "baseline");
    private static readonly bool CandidateEvidence =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HUSAYNIA_BASELINE_EVIDENCE"));
    private static readonly JsonDocument RouteManifest = Load("route-manifest.json");
    private static readonly int[] AllowedStatuses = [200, 301, 308, 404, 410];

    [Fact]
    public void RequiredMachineReadableArtifactsExist()
    {
        var required = new[]
        {
            "capture-summary.json",
            "route-manifest.json",
            "migration-import-manifest.json",
            "http-inventory.json",
            "metadata-inventory.json",
            "media-inventory.json",
            "navigation-inventory.json",
            "forms-widgets.json",
            "religious-content.json",
            "screenshots.json",
            "screenshot-network-policy.json",
            "screenshot-network-decisions.json",
            "screenshot-capture-provenance.json",
            "screenshot-determinism.json",
            "residual-risks.json",
            "checksums.sha256",
            "README.md"
        };

        foreach (var relative in required)
        {
            var path = Path.Combine(Evidence, relative);
            Assert.True(File.Exists(path), $"Missing {relative}");
            Assert.True(new FileInfo(path).Length > 0, $"Empty {relative}");
        }
    }

    [Fact]
    public void RouteManifestMatchesFrozenC1Contract()
    {
        var root = RouteManifest.RootElement;
        Assert.Equal("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("captureId").GetString()));
        Assert.True(root.GetProperty("capturedAtUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal("https://www.husaynia.org/", root.GetProperty("sourceBaseUrl").GetString());

        var routes = root.GetProperty("routes").EnumerateArray().ToArray();
        Assert.NotEmpty(routes);
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            AssertRequiredString(route, "routeId");
            var legacy = AssertRequiredString(route, "legacyPath");
            var canonical = AssertRequiredString(route, "canonicalPath");
            Assert.StartsWith("/", legacy, StringComparison.Ordinal);
            Assert.StartsWith("/", canonical, StringComparison.Ordinal);
            Assert.Equal(legacy, legacy.Normalize());
            Assert.Equal(canonical, canonical.Normalize());
            Assert.True(canonicalPaths.Add(canonical), $"Duplicate canonical path: {canonical}");

            var status = route.GetProperty("expectedStatus").GetInt32();
            Assert.Contains(status, AllowedStatuses);
            if (status is 301 or 308)
            {
                Assert.StartsWith("/", AssertRequiredString(route, "redirectTarget"), StringComparison.Ordinal);
            }
            else
            {
                Assert.False(route.TryGetProperty("redirectTarget", out _), $"Non-redirect route has redirectTarget: {legacy}");
            }

            Assert.Equal(JsonValueKind.Array, route.GetProperty("assetKeys").ValueKind);
            Assert.Equal(JsonValueKind.Array, route.GetProperty("dynamicRegionKeys").ValueKind);
            Assert.NotEmpty(route.GetProperty("evidenceRefs").EnumerateArray());
        }
    }

    [Fact]
    public void EverySitemapPageIsRepresentedByTheRouteManifest()
    {
        var manifestPaths = RouteManifest.RootElement.GetProperty("routes").EnumerateArray()
            .Select(x => x.GetProperty("legacyPath").GetString())
            .Where(x => x is not null)
            .ToHashSet(StringComparer.Ordinal);

        var sitemapPages = Directory.EnumerateFiles(Path.Combine(Evidence, "raw", "sitemaps"), "*.xml")
            .SelectMany(path => XDocument.Load(path).Descendants().Where(x => x.Name.LocalName == "url").Elements().Where(x => x.Name.LocalName == "loc"))
            .Select(x => x.Value.Trim())
            .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && !uri.AbsolutePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var uri = new Uri(x);
                return Uri.UnescapeDataString(uri.AbsolutePath) + uri.Query;
            })
            .ToArray();

        Assert.NotEmpty(sitemapPages);
        foreach (var page in sitemapPages)
        {
            Assert.Contains(page.Normalize(), manifestPaths);
        }
    }

    [Fact]
    public void CaptureIsNoSubmitAndFormsRemainObservational()
    {
        using var summary = Load("capture-summary.json");
        Assert.True(summary.RootElement.GetProperty("noSubmit").GetBoolean());

        using var forms = Load("forms-widgets.json");
        foreach (var form in forms.RootElement.GetProperty("forms").EnumerateArray())
        {
            Assert.False(form.GetProperty("submissionAttempted").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(form.GetProperty("riskClassification").GetString()));
        }
    }

    [Fact]
    public void SignificantQueryEndpointIsRetainedOutsideQueryFreeC1Manifest()
    {
        using var http = Load("http-inventory.json");
        Assert.Contains(http.RootElement.EnumerateArray(), row => row.GetProperty("path").GetString() == "/events/?ical=1");
        Assert.DoesNotContain(RouteManifest.RootElement.GetProperty("routes").EnumerateArray(), route => route.GetProperty("legacyPath").GetString()!.Contains('?', StringComparison.Ordinal));
    }

    [Fact]
    public void UploadAliasesAreFirstClassRouteContracts()
    {
        using var media = Load("media-inventory.json");
        var uploadPaths = media.RootElement.EnumerateArray()
            .Select(row => new Uri(row.GetProperty("url").GetString()!).AbsolutePath)
            .Where(path => path.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            .Select(Uri.UnescapeDataString)
            .ToHashSet(StringComparer.Ordinal);
        var routePaths = RouteManifest.RootElement.GetProperty("routes").EnumerateArray()
            .Select(route => route.GetProperty("legacyPath").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(uploadPaths);
        Assert.Subset(routePaths, uploadPaths);
    }

    [Fact]
    public void MetadataAndMediaInventoriesContainChecksummedEvidence()
    {
        using var metadata = Load("metadata-inventory.json");
        var metadataRows = metadata.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(metadataRows);
        Assert.All(metadataRows, row => Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("contentSha256").GetString()!));

        using var media = Load("media-inventory.json");
        var rows = media.RootElement.EnumerateArray().ToArray();
        Assert.Equal(rows.Length, rows.Select(row => row.GetProperty("url").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(rows, row => row.GetProperty("contentType").GetString()?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(rows.Where(row => row.GetProperty("kind").GetString() != "rejected"), row => Assert.InRange(row.GetProperty("status").GetInt32(), 200, 299));
        Assert.All(rows.Where(row => row.GetProperty("kind").GetString() == "rejected"), row => Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("error").GetString())));
        var retained = rows.Where(x => x.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String).ToArray();
        Assert.NotEmpty(retained);
        Assert.All(retained, row =>
        {
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("sha256").GetString()!);
            Assert.True(File.Exists(Path.Combine(Evidence, row.GetProperty("savedAs").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
        });
    }

    [Fact]
    public void Adr009ScreenshotsUseExactLiveBrowserGeometryAndReadiness()
    {
        using var screenshots = Load("screenshots.json");
        var rows = screenshots.RootElement.EnumerateArray().ToArray();
        Assert.Equal(36, rows.Length);
        foreach (var viewport in new[] { "desktop-1920x1080", "desktop-1440x900", "tablet-1024x768", "tablet-768x1024", "mobile-390x844", "mobile-320x568" })
        {
            Assert.Equal(6, rows.Count(row => row.GetProperty("viewport").GetString() == viewport));
        }

        foreach (var row in rows)
        {
            Assert.Equal("captured", row.GetProperty("status").GetString());
            Assert.Equal("pass", row.GetProperty("qualityStatus").GetString());
            Assert.True(row.GetProperty("ready").GetBoolean());
            Assert.False(row.GetProperty("layoutInjectionUsed").GetBoolean());
            Assert.True(row.GetProperty("fontsReady").GetBoolean());
            Assert.Equal(0, row.GetProperty("incompleteImageCount").GetInt32());
            Assert.Equal(0, row.GetProperty("inFlightAllowedRequests").GetInt32());
            var width = row.GetProperty("width").GetInt32();
            var height = row.GetProperty("height").GetInt32();
            Assert.Equal(width, row.GetProperty("actualViewportWidth").GetInt32());
            Assert.Equal(height, row.GetProperty("actualViewportHeight").GetInt32());
            Assert.Equal(width, row.GetProperty("actualScreenWidth").GetInt32());
            Assert.Equal(height, row.GetProperty("actualScreenHeight").GetInt32());
            Assert.Equal(1d, row.GetProperty("actualDevicePixelRatio").GetDouble());
            Assert.Equal(width, row.GetProperty("pngWidth").GetInt32());
            Assert.Equal(height, row.GetProperty("pngHeight").GetInt32());
            var horizontalOverflow = row.GetProperty("horizontalOverflow").GetBoolean();
            Assert.Equal(row.GetProperty("documentScrollWidth").GetInt32() > width, horizontalOverflow);
            if (CandidateEvidence)
            {
                AssertQualityPassDoesNotHideDocumentOverflow(
                    row.GetProperty("templateKey").GetString()!,
                    row.GetProperty("viewport").GetString()!,
                    row.GetProperty("qualityStatus").GetString()!,
                    horizontalOverflow,
                    row.GetProperty("documentScrollWidth").GetInt32(),
                    width);
            }

            if (horizontalOverflow)
            {
                Assert.NotEmpty(row.GetProperty("overflowSources").EnumerateArray());
            }
            else
            {
                Assert.Empty(row.GetProperty("overflowSources").EnumerateArray());
            }

            var samples = row.GetProperty("readinessSamples").EnumerateArray().ToArray();
            Assert.True(samples.Length >= 3);
            Assert.All(samples.TakeLast(3), sample =>
            {
                Assert.Equal(width, sample.GetProperty("innerWidth").GetInt32());
                Assert.Equal(height, sample.GetProperty("innerHeight").GetInt32());
                Assert.Equal(1d, sample.GetProperty("devicePixelRatio").GetDouble());
                Assert.True(sample.GetProperty("fontsReady").GetBoolean());
                Assert.Equal(0, sample.GetProperty("incompleteImageCount").GetInt32());
                Assert.Equal(0, sample.GetProperty("inFlightAllowedRequests").GetInt32());
            });

            var relative = row.GetProperty("savedAs").GetString()!;
            Assert.True(File.Exists(Path.Combine(Evidence, relative.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("sha256").GetString()!);
            Assert.True(row.GetProperty("textLength").GetInt32() >= 100);
            Assert.True(row.GetProperty("pngBytes").GetInt64() >= Math.Max(2_500, width * height / 80));
            Assert.True(row.GetProperty("byteEntropy").GetDouble() >= 5.0);
            Assert.False(row.GetProperty("giantSvgDetected").GetBoolean());
            Assert.False(row.GetProperty("loadingOnlyState").GetBoolean());
            var landmarks = row.GetProperty("visibleLandmarks").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("menu", landmarks);
            Assert.Contains("heading", landmarks);
            Assert.Contains("content", landmarks);
            var template = row.GetProperty("templateKey").GetString();
            if (template is "donation-form" or "contact-form")
            {
                Assert.Contains("form-shell", landmarks);
                Assert.True(row.GetProperty("formCount").GetInt32() > 0);
            }
            if (template == "donation-form")
            {
                Assert.Contains("embedded-form", landmarks);
            }
        }
    }

    [Fact]
    public void EventDetailTabletOverflowCannotBeMarkedQualityPass()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertQualityPassDoesNotHideDocumentOverflow(
                "event-detail",
                "tablet-768x1024",
                "pass",
                horizontalOverflow: true,
                documentScrollWidth: 850,
                viewportWidth: 768));
    }

    [Fact]
    public void ScreenshotDecisionLogIsCompleteCorrelatedAndRedacted()
    {
        using var policy = Load("screenshot-network-policy.json");
        var retainedPolicyVersion = policy.RootElement.GetProperty("policyVersion").GetString();
        if (CandidateEvidence)
        {
            Assert.Equal(ScreenshotNetworkPolicy.Version, retainedPolicyVersion);
        }

        using var decisions = Load("screenshot-network-decisions.json");
        var rows = decisions.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            AssertRequiredString(row, "captureKey");
            AssertRequiredString(row, "requestId");
            Assert.Equal(retainedPolicyVersion, row.GetProperty("policyVersion").GetString());
            var url = AssertRequiredString(row, "redactedUrl");
            Assert.DoesNotContain("token=secret", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password=", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessionid=abc", url, StringComparison.OrdinalIgnoreCase);
        });

        var requests = rows.Where(row => row.GetProperty("eventType").GetString() == "request").ToArray();
        Assert.NotEmpty(requests);
        Assert.Equal(requests.Length, requests.Select(row => row.GetProperty("requestId").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (var request in requests)
        {
            var id = request.GetProperty("requestId").GetString();
            var terminals = rows.Count(row =>
                row.GetProperty("requestId").GetString() == id
                && row.GetProperty("eventType").GetString() is "response" or "failure");
            Assert.Equal(1, terminals);
        }
    }

    [Fact]
    public void ScreenshotProvenancePinsToolingOnlyPlaywrightChromium()
    {
        using var policy = Load("screenshot-network-policy.json");
        using var provenance = Load("screenshot-capture-provenance.json");
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("toolingOnlyDependency").GetBoolean());
        Assert.Equal("1.62.0", AssertRequiredString(root, "playwrightPackageVersion"));
        AssertRequiredString(root, "chromiumVersion");
        AssertRequiredString(root, "chromiumExecutable");
        Assert.Contains("playwright.ps1 install --no-shell chromium", AssertRequiredString(root, "browserInstallCommand"), StringComparison.Ordinal);
        Assert.Equal(
            policy.RootElement.GetProperty("policyVersion").GetString(),
            root.GetProperty("networkPolicyVersion").GetString());
        if (CandidateEvidence)
        {
            Assert.Equal(ScreenshotNetworkPolicy.Version, root.GetProperty("networkPolicyVersion").GetString());
            Assert.Equal("1234", root.GetProperty("chromiumRevision").GetString());
            Assert.Equal("151.0.7922.34", root.GetProperty("chromiumVersion").GetString());
            Assert.True(root.GetProperty("chromiumSandbox").GetBoolean());
            Assert.Equal(
                PlaywrightScreenshotCapture.BrowserExecutableIdentity,
                root.GetProperty("chromiumExecutable").GetString());
            Assert.Equal(
                PlaywrightScreenshotCapture.BrowserWorkingDirectoryAttestation,
                root.GetProperty("browserWorkingDirectory").GetString());
            Assert.True(root.GetProperty(
                "browserExecutableOutsideWorkspace").GetBoolean());
            Assert.True(root.GetProperty(
                "browserWorkingDirectoryOutsideWorkspace").GetBoolean());
            Assert.True(root.GetProperty(
                "browserWorkingDirectoryEmpty").GetBoolean());
            Assert.True(root.GetProperty(
                "childEnvironmentSanitized").GetBoolean());
            Assert.Equal(
                root.GetProperty("expectedExecutableSha256").GetString(),
                root.GetProperty("actualExecutableSha256").GetString());
            Assert.False(root.GetProperty("processElevated").GetBoolean());
            Assert.False(root.GetProperty("cookiesUsed").GetBoolean());
            Assert.False(root.GetProperty("storageStateUsed").GetBoolean());
            Assert.False(root.GetProperty("permissionsGranted").GetBoolean());
            Assert.False(root.GetProperty("proxyUsed").GetBoolean());
            Assert.False(root.GetProperty("credentialsUsed").GetBoolean());
        }
        Assert.Equal(3, root.GetProperty("navigationAttempts").GetInt32());
        Assert.Equal(10, root.GetProperty("navigationAttemptTimeoutSeconds").GetInt32());
        Assert.Equal(30, root.GetProperty("readinessTimeoutSeconds").GetInt32());
        Assert.Equal(45, root.GetProperty("captureTimeoutSeconds").GetInt32());
        Assert.Equal(3_000, root.GetProperty("interCaptureDelayMilliseconds").GetInt32());
    }

    [Fact]
    public void ConsecutiveControlledScreenshotRunsHaveIdenticalDomMetrics()
    {
        using var determinism = Load("screenshot-determinism.json");
        var root = determinism.RootElement;
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(36, root.GetProperty("runAScreenshotCount").GetInt32());
        Assert.Equal(36, root.GetProperty("runBScreenshotCount").GetInt32());
        Assert.Equal(0, root.GetProperty("metricDifferenceCount").GetInt32());
        Assert.Empty(root.GetProperty("metricDifferences").EnumerateArray());
        Assert.True(root.GetProperty("pixelHashDifferenceCount").GetInt32() >= 0);
    }

    [Fact]
    public void MigrationManifestHasRightsProfileAndDryRunCandidates()
    {
        using var manifest = Load("migration-import-manifest.json");
        var root = manifest.RootElement;
        Assert.Equal("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.StartsWith("public-fidelity-evidence-only", root.GetProperty("rightsProfile").GetString(), StringComparison.Ordinal);
        Assert.Equal("dry-run", root.GetProperty("mode").GetString());
        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.NotEmpty(candidates);
        var allowedKinds = new[] { "content", "religious-content", "metadata", "redirect", "navigation", "form", "editable-setting", "endpoint", "media" };
        Assert.All(allowedKinds, kind => Assert.Contains(candidates, candidate => candidate.GetProperty("kind").GetString() == kind));
        Assert.Contains(candidates, candidate => candidate.GetProperty("decision").GetString() == "create");
        Assert.Contains(candidates, candidate => candidate.GetProperty("decision").GetString() == "update");
        Assert.Contains(candidates, candidate => candidate.GetProperty("decision").GetString() == "skip");
        Assert.Contains(candidates, candidate => candidate.GetProperty("decision").GetString() == "conflict");
        Assert.Equal(candidates.Length, candidates.Select(candidate => candidate.GetProperty("sourceKey").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(candidates, candidate =>
        {
            AssertRequiredString(candidate, "sourceKey");
            Assert.Matches("^[0-9a-f]{64}$", AssertRequiredString(candidate, "sourceChecksum"));
            AssertRequiredString(candidate, "payloadRef");
            if (candidate.TryGetProperty("canonicalPath", out var canonical))
            {
                Assert.DoesNotContain('?', canonical.GetString()!);
            }
        });

        var duplicateContentMedia = candidates
            .Where(candidate => candidate.TryGetProperty("sourceUri", out _))
            .GroupBy(candidate => candidate.GetProperty("sourceUri").GetString(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.GetProperty("kind").GetString()).Contains("media")
                && group.Any(item => item.GetProperty("kind").GetString() is "content" or "religious-content"))
            .ToArray();
        Assert.Empty(duplicateContentMedia);

        var mediaCandidates = candidates.Where(candidate => candidate.GetProperty("kind").GetString() == "media").ToArray();
        Assert.Equal(mediaCandidates.Length, mediaCandidates.Select(candidate => candidate.GetProperty("sourceKey").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(mediaCandidates.Length, mediaCandidates.Select(candidate => candidate.GetProperty("targetKey").GetString()).Distinct(StringComparer.Ordinal).Count());
        var mediaKeys = mediaCandidates.Select(candidate => candidate.GetProperty("sourceKey").GetString()!).ToHashSet(StringComparer.Ordinal);
        var mediaRefs = candidates
            .Where(candidate => candidate.GetProperty("kind").GetString() is "content" or "religious-content")
            .SelectMany(candidate => candidate.GetProperty("mediaRefs").EnumerateArray())
            .Select(mediaRef => mediaRef.GetString()!)
            .ToArray();
        Assert.NotEmpty(mediaRefs);
        Assert.All(mediaRefs, mediaRef => Assert.Contains(mediaRef, mediaKeys));

        var queryEndpoint = candidates.Single(candidate =>
            candidate.TryGetProperty("sourceUri", out var sourceUri)
            && sourceUri.GetString()!.Contains("/events/?ical=1", StringComparison.Ordinal));
        Assert.Equal("endpoint", queryEndpoint.GetProperty("kind").GetString());
        Assert.False(queryEndpoint.TryGetProperty("canonicalPath", out _));

        using var media = Load("media-inventory.json");
        if (media.RootElement.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "rejected"))
        {
            Assert.Contains(candidates, candidate => candidate.GetProperty("decision").GetString() == "reject");
        }
    }

    [Fact]
    public void NavigationInventoryContainsHierarchyAndObservableStates()
    {
        using var navigation = Load("navigation-inventory.json");
        var rows = navigation.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.GetProperty("parentNavigationKey").ValueKind == JsonValueKind.String);
        Assert.Contains(rows, row => row.GetProperty("depth").GetInt32() > 0);
        Assert.Contains(rows, row => row.GetProperty("isActive").GetBoolean());
        Assert.Contains(rows, row => row.GetProperty("isMobileControl").GetBoolean());
        Assert.Contains(rows, row => row.GetProperty("isParentControl").GetBoolean() && !row.GetProperty("isDestination").GetBoolean());
        var prayerTiming = rows.First(row => row.GetProperty("label").GetString() == "Prayer Timings"
            && row.GetProperty("parentLabel").ValueKind == JsonValueKind.String);
        Assert.Equal("Calendar", prayerTiming.GetProperty("parentLabel").GetString());
        var faraj = rows.First(row => row.GetProperty("label").GetString() == "Dua – e – Faraj"
            && row.GetProperty("ancestorLabels").EnumerateArray().Any(label => label.GetString() == "Important Supplications"));
        Assert.Equal("Duas", faraj.GetProperty("parentLabel").GetString());
        Assert.Contains(faraj.GetProperty("ancestorLabels").EnumerateArray(), label => label.GetString() == "Important Supplications");
        Assert.All(rows, row =>
        {
            Assert.True(row.GetProperty("isFocusable").GetBoolean());
            AssertRequiredString(row, "stateEvidence");
        });
    }

    [Fact]
    public void ReligiousFixturesPreserveStructuredRoundTrip()
    {
        using var religious = Load("religious-content.json");
        var fixtures = religious.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(fixtures);
        Assert.Contains(fixtures.SelectMany(fixture => fixture.GetProperty("paragraphs").EnumerateArray()), paragraph => paragraph.GetProperty("role").GetString() == "transliteration");
        Assert.All(fixtures, fixture =>
        {
            Assert.Equal("religious-content", fixture.GetProperty("templateKey").GetString());
            var paragraphs = fixture.GetProperty("paragraphs").EnumerateArray().ToArray();
            Assert.Contains(paragraphs, paragraph => paragraph.GetProperty("role").GetString() == "arabic");
            Assert.Contains(paragraphs, paragraph => paragraph.GetProperty("role").GetString() == "english-translation");
            Assert.Equal(Enumerable.Range(0, paragraphs.Length), paragraphs.Select(item => item.GetProperty("order").GetInt32()));
            Assert.All(paragraphs, paragraph => Assert.Matches("^[0-9a-f]{64}$", paragraph.GetProperty("checksum").GetString()!));
            Assert.Matches("^[0-9a-f]{64}$", fixture.GetProperty("fixtureChecksum").GetString()!);
        });
    }

    [Fact]
    public void CaptureCompletedWithinBoundAndUsedDeterministicCache()
    {
        using var summary = Load("capture-summary.json");
        var root = summary.RootElement;
        Assert.InRange(root.GetProperty("durationSeconds").GetDouble(), 0.1, 25 * 60);
        Assert.True(root.GetProperty("networkRequestCount").GetInt32() > 0);
        Assert.True(root.GetProperty("cacheHitCount").GetInt32() >= 5);
        Assert.True(root.GetProperty("networkRequestCount").GetInt32()
            < root.GetProperty("routeCount").GetInt32() + root.GetProperty("assetCount").GetInt32() + 25);
    }

    [Theory]
    [InlineData("route-manifest.json", "contracts/routes/route-manifest.schema.json")]
    [InlineData("migration-import-manifest.json", "contracts/migration/import-manifest.schema.json")]
    public async Task GeneratedManifestValidatesAgainstFrozenSchema(string instanceRelative, string schemaRelative)
    {
        var instance = Path.Combine(Evidence, instanceRelative);
        var schema = Path.Combine(Root, schemaRelative.Replace('/', Path.DirectorySeparatorChar));
        var script = $"$value = Get-Content -LiteralPath '{instance.Replace("'", "''", StringComparison.Ordinal)}' -Raw | Test-Json -SchemaFile '{schema.Replace("'", "''", StringComparison.Ordinal)}'; if (-not $value) {{ exit 1 }}";
        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"Schema validation failed. stdout={stdout} stderr={stderr}");
    }

    [Fact]
    public async Task ChecksumFileValidatesEveryRetainedArtifact()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(Evidence, "checksums.sha256"));
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            var split = line.Split("  ", 2, StringSplitOptions.None);
            Assert.Equal(2, split.Length);
            var path = Path.Combine(Evidence, split[1].Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Checksum references missing file {split[1]}");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            Assert.Equal(split[0], actual);
        }
    }

    private static JsonDocument Load(string relative)
    {
        var path = Path.Combine(Evidence, relative);
        Assert.True(File.Exists(path), $"Run the capture first; missing {path}");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static string AssertRequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetString();
        Assert.False(string.IsNullOrWhiteSpace(value), $"Missing {name}");
        return value!;
    }

    private static void AssertQualityPassDoesNotHideDocumentOverflow(
        string template,
        string viewport,
        string qualityStatus,
        bool horizontalOverflow,
        int documentScrollWidth,
        int viewportWidth)
    {
        Assert.False(
            qualityStatus == "pass"
            && (horizontalOverflow || documentScrollWidth > viewportWidth),
            $"{template}|{viewport} claims quality pass despite document overflow.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools", "Husaynia.BaselineCapture")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate HusayniaSite root.");
    }
}
