using Husaynia.BaselineCapture;
using System.Text;
using System.Text.Json;

namespace Husaynia.BaselineCapture.Tests;

internal static class BaselineTestFixture
{
    public static async Task<string> CreateValidCandidateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"husaynia-candidate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "screenshots"));
        Directory.CreateDirectory(Path.Combine(root, "raw", "html"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        var repository = FindRepositoryRoot();
        var baselineScreenshots = Path.Combine(repository, "evidence", "baseline", "screenshots");
        var started = new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);
        var capturedAt = started.AddSeconds(2);
        const string captureId = "candidate-test-run-a";
        var screenshots = new List<ScreenshotRecord>();
        var decisions = new List<ScreenshotNetworkDecision>();
        foreach (var representative in CaptureProfile.Approved.Representatives)
        {
            foreach (var viewport in CaptureProfile.Approved.Viewports)
            {
                var relative = $"screenshots/{representative.Key}-{viewport.Name}.png";
                var sourcePng = Path.Combine(
                    baselineScreenshots,
                    $"{representative.Key}-{viewport.Name}.png");
                var png = await File.ReadAllBytesAsync(sourcePng);
                var inspection = PngArtifactInspector.Inspect(png);
                await File.WriteAllBytesAsync(
                    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)),
                    png);
                var formCount = representative.Key is "donation-form" or "contact-form" ? 1 : 0;
                var landmarks = new List<string> { "content", "heading", "menu" };
                if (formCount > 0)
                {
                    landmarks.Add("form-shell");
                }

                if (representative.Key == "donation-form")
                {
                    landmarks.Add("embedded-form");
                }

                screenshots.Add(new ScreenshotRecord(
                    representative.Key,
                    representative.Value.AbsoluteUri,
                    viewport.Name,
                    viewport.Width,
                    viewport.Height,
                    relative,
                    inspection.Sha256,
                    "captured",
                    null,
                    true,
                    500,
                    false,
                    formCount,
                    inspection.Length,
                    inspection.Width,
                    inspection.Height,
                    representative.Key,
                    "pass",
                    inspection.ByteEntropy,
                    false,
                    false,
                    landmarks.Order(StringComparer.Ordinal).ToArray(),
                    viewport.Width,
                    viewport.Height,
                    viewport.Width,
                    viewport.Height,
                    1,
                    viewport.Width,
                    viewport.Height * 2,
                    viewport.Width,
                    viewport.Height * 2,
                    true,
                    0,
                    0,
                    [],
                    [
                        Readiness(viewport, capturedAt),
                        Readiness(viewport, capturedAt.AddMilliseconds(250)),
                        Readiness(viewport, capturedAt.AddMilliseconds(500))
                    ],
                    false,
                    "screenshot-network-decisions.json",
                    "screenshot-capture-provenance.json",
                    [],
                    1,
                    [],
                    representative.Key == "donation-form" ? formCount : null));

                var key = $"{representative.Key}|{viewport.Name}";
                var requestId = $"{key}:attempt:1:request:1";
                var request = new ScreenshotNetworkDecision(
                    key,
                    requestId,
                    capturedAt,
                    "request",
                    "GET",
                    representative.Value.AbsoluteUri,
                    "document",
                    true,
                    "allow",
                    "primary-origin-get-head",
                    null,
                    null,
                    CaptureProfile.Approved.PolicyVersion,
                    1,
                    true,
                    "93.184.216.34")
                {
                    DnsEpoch = decisions.Count / 2 + 1,
                    BrowserInstanceId = "fixture-browser",
                    BrowserContextId = $"fixture-context-{decisions.Count / 2 + 1}"
                };
                decisions.Add(request);
                decisions.Add(request with
                {
                    EventType = "response",
                    ReasonCode = "response-complete",
                    ResponseStatus = 200
                });
            }
        }

        var completed = started.AddSeconds(10);
        const string fixtureUrl = "https://www.husaynia.org/fixture/";
        const string rawRelative = "raw/html/fixture.html";
        var rawBytes = Encoding.UTF8.GetBytes("<html><body><h1>Fixture</h1></body></html>");
        var rawSha = CaptureIO.Sha256(rawBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(root, rawRelative.Replace('/', Path.DirectorySeparatorChar)),
            rawBytes);
        const string assetRelative = "assets/fixture.png";
        var assetBytes = await File.ReadAllBytesAsync(Path.Combine(
            root,
            screenshots[0].SavedAs!.Replace('/', Path.DirectorySeparatorChar)));
        var assetInspection = PngArtifactInspector.Inspect(assetBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(root, assetRelative.Replace('/', Path.DirectorySeparatorChar)),
            assetBytes);
        var assetKey = CaptureIO.StableKey(
            "asset",
            "https://www.husaynia.org/fixture.png");
        var mediaSourceKey = BaselineCaptureService.MediaCandidateKey(
            "https://www.husaynia.org/fixture.png");
        var contentSourceKey = CaptureIO.StableKey(
            "source",
            $"content:{fixtureUrl}");
        var httpRecord = new HttpRecord(
            fixtureUrl,
            "/fixture/",
            200,
            fixtureUrl,
            [],
            "text/html; charset=UTF-8",
            rawBytes.LongLength,
            rawSha,
            rawRelative,
            null,
            new Dictionary<string, string>());
        var metadataRecord = new MetadataRecord(
            fixtureUrl,
            "Fixture",
            null,
            fixtureUrl,
            null,
            null,
            null,
            null,
            null,
            [],
            "en-US",
            "ltr",
            "Fixture",
            rawSha);
        var assetRecord = new AssetRecord(
            assetKey,
            "https://www.husaynia.org/fixture.png",
            fixtureUrl,
            "image",
            "image",
            "same-origin-upload",
            200,
            "image/png",
            assetBytes.LongLength,
            assetInspection.Sha256,
            assetRelative,
            null);
        var navigationItem = new NavigationItem(
            CaptureIO.StableKey("navigation", $"{fixtureUrl}:fixture"),
            fixtureUrl,
            "Fixture",
            fixtureUrl,
            null,
            null,
            [],
            [],
            0,
            "primary",
            0,
            false,
            true,
            false,
            true,
            true,
            false,
            true,
            true,
            "fixture");
        var formRecord = new FormObservation(
            fixtureUrl,
            "fixture-form",
            "POST",
            fixtureUrl,
            [],
            false,
            "fixture");
        var formsDocument = new
        {
            noSubmit = true,
            forms = new[] { formRecord },
            dynamicRegions = Array.Empty<DynamicRegionObservation>()
        };
        var summary = new CaptureSummary(
            captureId,
            started,
            completed,
            CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
            true,
            0,
            1,
            1,
            0,
            1,
            1,
            1,
            1,
            0,
            36,
            36,
            36,
            0,
            10,
            1,
            0);
        var identity = CaptureProfile.Approved.GetCurrentBrowserIdentity();
        var pinnedAnswers = CaptureProfile.Approved.StaticResources
            .Select(rule => rule.Host)
            .Append(CaptureProfile.Approved.PrimaryOrigin.Host)
            .ToDictionary(
                host => host,
                _ => (IReadOnlyList<string>)["93.184.216.34"],
                StringComparer.OrdinalIgnoreCase);
        var provenance = new ScreenshotCaptureProvenance(
            captureId,
            capturedAt,
            "Microsoft Windows test",
            "X64",
            "10.0.400",
            CaptureProfile.PlaywrightVersion,
            CaptureProfile.ChromiumVersion,
            PlaywrightScreenshotCapture.BrowserExecutableIdentity,
            "pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium",
            "test-cache-key",
            CaptureProfile.Approved.PolicyVersion,
            3,
            10,
            30,
            45,
            3000,
            true,
            CaptureProfile.ChromiumRevision,
            identity.ExecutableSha256,
            identity.ExecutableSha256,
            true,
            OperatingSystem.IsWindows()
                ? ["SystemRoot", "TEMP", "TMP", "WINDIR"]
                : ["HOME", "TMPDIR"],
            PlaywrightScreenshotCapture.BrowserWorkingDirectoryAttestation,
            false,
            CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority),
            pinnedAnswers,
            pinnedAnswers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"MAP {pair.Key} {pair.Value[0]}")
                .Append("MAP * ~NOTFOUND")
                .ToArray(),
            false,
            false,
            false,
            false,
            false)
        {
            BrowserExecutableOutsideWorkspace = true,
            BrowserWorkingDirectoryOutsideWorkspace = true,
            BrowserWorkingDirectoryEmpty = true,
            ChildEnvironmentSanitized = true,
            BrowserLaunches =
            [
                new BrowserLaunchEvidence(
                    "fixture-browser",
                    capturedAt.AddSeconds(-1),
                    TrustedEndpointPolicy.ComputeResolverMapSha256(
                        pinnedAnswers
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"MAP {pair.Key} {pair.Value[0]}")
                            .Append("MAP * ~NOTFOUND")
                            .ToArray()),
                    null,
                    "bootstrap")
            ],
            DnsContextEpochs = screenshots
                .Select((screenshot, index) => new DnsContextEpochEvidence(
                    index + 1,
                    $"{screenshot.TemplateKey}|{screenshot.Viewport}",
                    1,
                    "fixture-browser",
                    $"fixture-context-{index + 1}",
                    capturedAt,
                    capturedAt,
                    capturedAt,
                    capturedAt,
                    pinnedAnswers
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new DnsPinBindingEvidence(
                            pair.Key,
                            pair.Key == CaptureProfile.Approved.PrimaryOrigin.Host
                                ? nameof(TrustedHostClass.PrimaryOrigin)
                                : nameof(TrustedHostClass.StaticResource),
                            pair.Value[0],
                            pair.Value,
                            CaptureIO.Sha256(
                                Encoding.UTF8.GetBytes(pair.Value[0])),
                            capturedAt,
                            false))
                        .ToArray()))
                .ToArray()
        };
        var importCandidates = new[]
        {
            new ImportCandidate(
                contentSourceKey,
                captureId,
                rawSha,
                "content",
                fixtureUrl,
                CaptureIO.StableKey("target-content", fixtureUrl),
                "/fixture/",
                rawRelative,
                [mediaSourceKey],
                [],
                null,
                "create",
                null),
            new ImportCandidate(
                CaptureIO.StableKey(
                    "source",
                    "editable-setting:site-settings"),
                captureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            BaseUri = new Uri(
                                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri),
                            Navigation = 1,
                            DynamicRegions = 0
                        },
                        CaptureIO.JsonOptions)),
                "editable-setting",
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                CaptureIO.StableKey(
                    "target-editable-setting",
                    "site-settings"),
                "/",
                "forms-widgets.json",
                [],
                [],
                null,
                "conflict",
                "Provider credentials, payment configuration, form destinations and widget settings are not observable from public GET evidence."),
            new ImportCandidate(
                CaptureIO.StableKey("source", "form:fixture-form"),
                captureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        formRecord,
                        CaptureIO.JsonOptions)),
                "form",
                fixtureUrl,
                CaptureIO.StableKey("target-form", "fixture-form"),
                "/fixture/",
                "forms-widgets.json",
                [],
                [],
                null,
                "conflict",
                "Public markup is observable, but destination, anti-abuse, consent and success workflow require authorized export/non-production evidence."),
            new ImportCandidate(
                mediaSourceKey,
                captureId,
                assetInspection.Sha256,
                "media",
                assetRecord.Url,
                CaptureIO.StableKey("target-media", assetRecord.Url),
                null,
                assetRelative,
                [],
                [],
                null,
                "create",
                null),
            new ImportCandidate(
                CaptureIO.StableKey("source", $"navigation:{fixtureUrl}"),
                captureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new[] { navigationItem },
                        CaptureIO.JsonOptions)),
                "navigation",
                fixtureUrl,
                CaptureIO.StableKey("target-navigation", fixtureUrl),
                "/fixture/",
                "navigation-inventory.json",
                [],
                [],
                null,
                "update",
                "Hierarchy, ordering, active, focusable, desktop and mobile states require explicit editorial reconciliation."),
            new ImportCandidate(
                CaptureIO.StableKey("source", $"metadata:{fixtureUrl}"),
                captureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        metadataRecord,
                        CaptureIO.JsonOptions)),
                "metadata",
                fixtureUrl,
                CaptureIO.StableKey("target-metadata", fixtureUrl),
                "/fixture/",
                "metadata-inventory.json",
                [],
                [contentSourceKey],
                null,
                "update",
                "SEO metadata is reconciled independently from body content.")
        }
            .OrderBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var runBStarted = capturedAt.AddMinutes(1).AddSeconds(-1);
        var runBCaptured = capturedAt.AddMinutes(1);
        var runBCompleted = capturedAt.AddMinutes(1).AddSeconds(1);
        var determinism = new ScreenshotDeterminismEvidence(
            runBCompleted.AddSeconds(1),
            captureId,
            "candidate-test-run-b",
            capturedAt,
            runBCaptured,
            36,
            36,
            0,
            0,
            [],
            [],
            [],
            "not-required",
            true)
        {
            RunAStartedAtUtc = started,
            RunACompletedAtUtc = completed,
            RunBStartedAtUtc = runBStarted,
            RunBCompletedAtUtc = runBCompleted,
            ScreenshotHashes = screenshots
                .Select(item => new ScreenshotHashEvidence(
                    $"{item.TemplateKey}|{item.Viewport}",
                    item.Sha256!,
                    item.Sha256!))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray()
        };
        await CaptureIO.WriteJsonAsync(Path.Combine(root, "capture-summary.json"), summary, CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "route-manifest.json"),
            new RouteManifest(
                "1.0.0",
                captureId,
                started,
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                [
                    new RouteEntry(
                        CaptureIO.StableKey("route", "/fixture/"),
                        "/fixture/",
                        "/fixture/",
                        200,
                        null,
                        "content-page",
                        CaptureIO.StableKey("content", "/fixture/"),
                        true,
                        false,
                        CaptureIO.StableKey("metadata", "/fixture/"),
                        [assetKey],
                        [],
                        [rawRelative, "http-inventory.json", "metadata-inventory.json"],
                        rawSha)
                ]),
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "migration-import-manifest.json"),
            new ImportManifest(
                "1.0.0",
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                captureId,
                started,
                "public-fidelity-evidence-only",
                "dry-run",
                importCandidates),
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "http-inventory.json"),
            new[] { httpRecord },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "metadata-inventory.json"),
            new[] { metadataRecord },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "media-inventory.json"),
            new[] { assetRecord },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "navigation-inventory.json"),
            new[] { navigationItem },
            CancellationToken.None);
        foreach (var file in new[]
                 {
                     "religious-content.json", "residual-risks.json"
                 })
        {
            await CaptureIO.WriteJsonAsync(
                Path.Combine(root, file),
                Array.Empty<object>(),
                CancellationToken.None);
        }

        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "forms-widgets.json"),
            formsDocument,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(Path.Combine(root, "screenshots.json"), screenshots, CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "screenshot-network-policy.json"),
            new
            {
                policyVersion = CaptureProfile.Approved.PolicyVersion,
                allowedMethods = ScreenshotNetworkPolicy.AllowedMethods,
                allowedOrigins = new[] { CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority) },
                allowedStaticHosts = ScreenshotNetworkPolicy.StaticHosts.Order().ToArray(),
                allowedStaticResourceTypes = ScreenshotNetworkPolicy.AllowedStaticResourceTypes,
                blockedCapabilities = ScreenshotNetworkPolicy.BlockedCapabilities,
                pinnedDnsAnswers = pinnedAnswers,
                chromiumHostResolverRules = provenance.ChromiumHostResolverRules,
                navigationAttempts = 3,
                navigationAttemptTimeoutSeconds = 10,
                readinessTimeoutSeconds = 30,
                captureTimeoutSeconds = 45,
                interCaptureDelayMilliseconds = 3000,
                enforcement = "fixture"
            },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "screenshot-network-decisions.json"),
            decisions,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "screenshot-capture-provenance.json"),
            provenance,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(root, "screenshot-determinism.json"),
            determinism,
            CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Candidate test evidence\n");
        var finalized = await new BaselineEvidenceValidator().FinalizeAsync(root, CancellationToken.None);
        if (!finalized.Passed)
        {
            throw new InvalidOperationException(string.Join(',', finalized.ReasonCodes));
        }

        return root;
    }

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "tools", "Husaynia.BaselineCapture")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate HusayniaSite root.");
    }

    private static ScreenshotReadinessSample Readiness(
        CaptureViewport viewport,
        DateTimeOffset capturedAt) =>
        new(
            capturedAt,
            viewport.Width,
            viewport.Height,
            1,
            viewport.Width,
            viewport.Height * 2,
            viewport.Width,
            viewport.Height * 2,
            true,
            0,
            0);
}
