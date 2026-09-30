using System.Text;
using System.Text.Json;
using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class CaptureLogicTests
{
    [Theory]
    [InlineData("link", "rel=\"canonical\"", "/contact-us/", null)]
    [InlineData("link", "rel=\"alternate\" type=\"application/rss+xml\"", "/feed/", null)]
    [InlineData("link", "rel=\"stylesheet\"", "/theme.css", "stylesheet")]
    [InlineData("link", "rel=\"preload\" as=\"font\"", "/font.woff2", "preload-font")]
    [InlineData("a", "", "/about/", null)]
    [InlineData("a", "", "/wp-content/uploads/file.pdf", "download")]
    [InlineData("img", "", "/image.jpg", "image")]
    public void AssetRelationExcludesNavigationAndSeoLinks(string tag, string attributes, string path, string? expected)
    {
        Assert.Equal(expected, BaselineCaptureService.ClassifyAssetRelation(tag, attributes, path));
    }

    [Theory]
    [InlineData("text/html; charset=UTF-8", "/page/", false)]
    [InlineData("application/json", "/api", false)]
    [InlineData("image/webp", "/image.webp", true)]
    [InlineData("audio/mpeg", "/dua.mp3", true)]
    [InlineData("text/css", "/site.css", true)]
    public void MediaApprovalUsesResponseType(string contentType, string path, bool expected)
    {
        Assert.Equal(expected, BaselineCaptureService.IsApprovedAssetContentType(contentType, path));
    }

    [Fact]
    public void NavigationFixturePreservesHierarchyAndState()
    {
        var html = """
            <nav class="desktop-nav">
              <ul>
                <li class="current-menu-item"><a href="/">Home</a></li>
                <li><a href="#">Calendar</a>
                  <ul><li><a href="/prayer-timings/">Prayer Timings</a></li></ul>
                </li>
                <li><a href="#">Important Supplications</a>
                  <ul><li><a href="/duas-dua-faraj/">Dua – e – Faraj</a></li></ul>
                </li>
                <li class="mobile-menu-toggle"><a href="/menu/">Menu</a></li>
              </ul>
            </nav>
            """;

        var items = BaselineCaptureService.ExtractNavigation(html, "https://www.husaynia.org/");
        Assert.Equal(6, items.Count);
        Assert.True(items.Single(item => item.Label == "Home").IsActive);
        var child = items.Single(item => item.Label == "Prayer Timings");
        Assert.Equal("Calendar", child.ParentLabel);
        Assert.NotNull(child.ParentNavigationKey);
        Assert.Equal(1, child.Depth);
        var supplication = items.Single(item => item.Label == "Dua – e – Faraj");
        Assert.Equal("Important Supplications", supplication.ParentLabel);
        Assert.Contains("Important Supplications", supplication.AncestorLabels);
        Assert.True(items.Single(item => item.Label == "Calendar").IsParentControl);
        Assert.False(items.Single(item => item.Label == "Calendar").IsDestination);
        Assert.True(child.IsDestination);
        var mobile = items.Single(item => item.Label == "Menu");
        Assert.True(mobile.IsMobileControl);
        Assert.False(mobile.VisibleOnDesktop);
        Assert.True(mobile.VisibleOnMobile);
        Assert.All(items, item => Assert.True(item.IsFocusable));
    }

    [Theory]
    [InlineData("GET", "https://www.husaynia.org/contact-us/", "document", true, true, "same-origin-get-head")]
    [InlineData("GET", "https://www.husaynia.org/wp-content/themes/site.css", "stylesheet", false, true, "same-origin-get-head")]
    [InlineData("GET", "https://www.husaynia.org/donate/?giveDonationFormInIframe=1", "document", false, true, "same-origin-get-head")]
    [InlineData("HEAD", "https://fonts.gstatic.com/font.woff2", "font", false, true, "allowlisted-static-resource")]
    [InlineData("GET", "https://fonts.googleapis.com/css2?family=Roboto", "stylesheet", false, true, "allowlisted-static-resource")]
    [InlineData("GET", "https://fonts.gstatic.com/script.js", "script", false, false, "static-host-resource-type-not-allowed")]
    [InlineData("POST", "https://www.husaynia.org/contact-us/", "document", true, false, "method-not-allowed")]
    [InlineData("GET", "http://www.husaynia.org/contact-us/", "document", true, false, "invalid-or-non-https-url")]
    [InlineData("GET", "https://www.husaynia.org/other/", "document", true, false, "unexpected-document-navigation")]
    [InlineData("GET", "https://js.stripe.com/v3/", "script", false, false, "tracker-or-payment-host")]
    [InlineData("GET", "https://untrusted.example/image.png", "image", false, false, "origin-not-allowlisted")]
    public void ScreenshotNetworkPolicyIsFailClosed(
        string method,
        string resource,
        string resourceType,
        bool navigation,
        bool expectedAllowed,
        string expectedReason)
    {
        var result = ScreenshotNetworkPolicy.Decide(
            method,
            resource,
            resourceType,
            navigation,
            "https://www.husaynia.org/contact-us/");

        Assert.Equal(expectedAllowed, result.Allowed);
        Assert.Equal(expectedReason, result.ReasonCode);
    }

    [Fact]
    public void ScreenshotNetworkPolicyAllowsNestedStaticCssResources()
    {
        var stylesheet = ScreenshotNetworkPolicy.Decide(
            "GET",
            "https://fonts.googleapis.com/css2?family=Roboto",
            "stylesheet",
            false,
            "https://www.husaynia.org/");
        var nestedFont = ScreenshotNetworkPolicy.Decide(
            "GET",
            "https://fonts.gstatic.com/s/roboto/v1/font.woff2",
            "font",
            false,
            "https://www.husaynia.org/");

        Assert.True(stylesheet.Allowed);
        Assert.True(nestedFont.Allowed);
    }

    [Fact]
    public void ScreenshotDecisionUrlsRedactSensitiveValues()
    {
        var redacted = ScreenshotNetworkPolicy.RedactUrl(
            "https://alice:password@www.husaynia.org/resource?token=secret-value&view=calendar&sessionid=abc123#fragment");

        Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("password", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("fragment", redacted, StringComparison.Ordinal);
        Assert.Contains("REDACTED-SENSITIVE", Uri.UnescapeDataString(redacted), StringComparison.Ordinal);
        Assert.Contains("view=[REDACTED]", Uri.UnescapeDataString(redacted), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyExplicitSafeResponseHeadersAreRetained()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Server", "sensitive-fingerprint");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret");
        response.Headers.TryAddWithoutValidation(
            "Location",
            "https://www.husaynia.org/resource?X-Amz-Signature=secret");
        response.Content = new ByteArrayContent([]);
        response.Content.Headers.ContentType = new("text/html");
        response.Content.Headers.ContentLength = 0;

        var retained = EvidenceSanitizer.SelectSafeResponseHeaders(
            response.Headers,
            response.Content.Headers);

        Assert.DoesNotContain("Server", retained.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Cookie", retained.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", retained["Location"], StringComparison.Ordinal);
        Assert.Equal("text/html", retained["Content-Type"]);
        Assert.Equal("0", retained["Content-Length"]);

        using var relative = new HttpResponseMessage();
        relative.Headers.TryAddWithoutValidation(
            "Location",
            "/resource?unknown=value#access_token=secret");
        relative.Content = new ByteArrayContent([]);
        var relativeRetained = EvidenceSanitizer.SelectSafeResponseHeaders(
            relative.Headers,
            relative.Content.Headers);
        Assert.Equal(
            "/resource?unknown=[REDACTED]",
            Uri.UnescapeDataString(relativeRetained["Location"]));

        using var relativeUserInfo = new HttpResponseMessage();
        relativeUserInfo.Headers.TryAddWithoutValidation(
            "Location",
            "//alice:password@www.husaynia.org/resource?token=secret&view=calendar");
        relativeUserInfo.Content = new ByteArrayContent([]);
        var userInfoRetained = EvidenceSanitizer.SelectSafeResponseHeaders(
            relativeUserInfo.Headers,
            relativeUserInfo.Content.Headers);
        Assert.DoesNotContain("alice", userInfoRetained["Location"], StringComparison.Ordinal);
        Assert.DoesNotContain("password", userInfoRetained["Location"], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", userInfoRetained["Location"], StringComparison.Ordinal);
        Assert.DoesNotContain("calendar", userInfoRetained["Location"], StringComparison.Ordinal);
        Assert.Contains(
            "token=[REDACTED-SENSITIVE]",
            Uri.UnescapeDataString(userInfoRetained["Location"]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonRoutedBrowserCapabilitiesAndFallbackDnsAreExplicitlyBlocked()
    {
        Assert.Contains("webrtc", ScreenshotNetworkPolicy.BlockedCapabilities);
        Assert.Contains("webtransport", ScreenshotNetworkPolicy.BlockedCapabilities);
        Assert.Contains("direct-sockets", ScreenshotNetworkPolicy.BlockedCapabilities);
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "Husaynia.BaselineCapture",
            "PlaywrightScreenshotCapture.cs"));
        var launcher = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "Husaynia.BaselineCapture",
            "BrowserExecutableVerifier.cs"));
        var comparer = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "Husaynia.BaselineCapture",
            "ScreenshotDeterminismComparer.cs"));

        Assert.Contains("install(window, 'RTCPeerConnection', blocked('webrtc'))", source, StringComparison.Ordinal);
        Assert.Contains("install(window, 'WebTransport', blocked('webtransport'))", source, StringComparison.Ordinal);
        Assert.Contains("install(window, 'TCPSocket', blocked('direct-sockets'))", source, StringComparison.Ordinal);
        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", launcher, StringComparison.Ordinal);
        Assert.Contains("--disable-features=WebTransport", launcher, StringComparison.Ordinal);
        Assert.Contains("DnsOverHttps", launcher, StringComparison.Ordinal);
        Assert.Contains("--disable-quic", launcher, StringComparison.Ordinal);
        Assert.Contains(
            "[\"MAP * ~NOTFOUND\"]",
            comparer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaywrightCaptureSourceDoesNotRewriteDomOrInjectLayoutCss()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "tools", "Husaynia.BaselineCapture", "PlaywrightScreenshotCapture.cs"));

        Assert.DoesNotContain("SetContentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddStyleTagAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("style.textContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("document.body.innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateScreenshotFixture", source, StringComparison.Ordinal);
        Assert.Contains("ViewportSize =", source, StringComparison.Ordinal);
        Assert.Contains("ScreenSize =", source, StringComparison.Ordinal);
        Assert.Contains("DeviceScaleFactor = 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaywrightDependencyIsToolingOnly()
    {
        var root = FindRepositoryRoot();
        var references = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Microsoft.Playwright", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj"], references);
    }

    [Fact]
    public void ScreenshotTimeoutsAreBoundedByAdr009()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), PlaywrightScreenshotCapture.MaximumCaptureDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), PlaywrightScreenshotCapture.MaximumReadinessDuration);
        Assert.Equal(3, PlaywrightScreenshotCapture.MaximumNavigationAttempts);
        Assert.Equal(10_000, PlaywrightScreenshotCapture.NavigationAttemptTimeoutMilliseconds);
        Assert.Equal(3_000, PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds);
    }

    [Theory]
    [InlineData("--delay-ms", "-1")]
    [InlineData("--timeout-seconds", "31")]
    [InlineData("--max-asset-bytes", "26214401")]
    [InlineData("--max-concurrency", "7")]
    [InlineData("--max-duration-minutes", "91")]
    public async Task CaptureOptionsCannotExceedSafetyBounds(string option, string value)
    {
        var output = Path.Combine(Path.GetTempPath(), $"husaynia-options-{Guid.NewGuid():N}");

        var exitCode = await Program.Main(
            ["capture", "--no-submit", "--output", output, option, value]);

        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    public void CaptureDurationAcceptsDocumentedValues(int minutes)
    {
        var options = Program.CreateCaptureOptions(
            [
                "capture",
                "--max-duration-minutes",
                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            CaptureProfile.Approved.PrimaryOrigin,
            "unused-output");

        Assert.Equal(minutes, options.MaxDurationMinutes);
    }

    [Fact]
    public void CaptureDurationDefaultsToSixtyMinutes()
    {
        var options = Program.CreateCaptureOptions(
            ["capture"],
            CaptureProfile.Approved.PrimaryOrigin,
            "unused-output");

        Assert.Equal(60, options.MaxDurationMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("91")]
    [InlineData("not-a-number")]
    public async Task CaptureDurationRejectsValuesOutsideDocumentedRange(
        string value)
    {
        var error = new StringWriter();

        var exitCode = await Program.ExecuteWithFailureHandlingAsync(
            () =>
            {
                _ = Program.CreateCaptureOptions(
                    ["capture", "--max-duration-minutes", value],
                    CaptureProfile.Approved.PrimaryOrigin,
                    "unused-output");
                return Task.FromResult(0);
            },
            error);

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "Safety refusal: "
            + "capture-option-out-of-range:--max-duration-minutes:"
            + $"accepted-range=1..90{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public async Task PromotionCliRejectsNonCanonicalDestinationBeforeServiceExecution()
    {
        var nonCanonical = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-noncanonical-{Guid.NewGuid():N}");

        var exitCode = await Program.Main(
            ["promote", "--approved", "--from", "missing-source", "--to", nonCanonical]);

        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(nonCanonical));
        Assert.True(Program.IsCanonicalBaselineDestination(Path.Combine(
            FindRepositoryRoot(),
            "evidence",
            "baseline")));
    }

    [Fact]
    public void CanonicalBaselineDestinationUsesRepositoryCasing()
    {
        var canonical = Program.CanonicalizeBaselineDestination(
            Path.Combine(
                FindRepositoryRoot(),
                "evidence",
                "baseline"));

        Assert.NotNull(canonical);
        Assert.Equal(
            "baseline",
            Path.GetFileName(canonical));

        var mixedCase = Path.Combine(
            Path.GetDirectoryName(canonical)!,
            "BaSeLiNe");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                canonical,
                Program.CanonicalizeBaselineDestination(mixedCase));
        }
        else
        {
            Assert.Null(
                Program.CanonicalizeBaselineDestination(mixedCase));
        }
    }

    [Fact]
    public async Task ComparisonCliRejectsNonCanonicalOutputBeforeReadingRuns()
    {
        var runA = Path.Combine(Path.GetTempPath(), $"husaynia-run-a-{Guid.NewGuid():N}");
        var runB = Path.Combine(Path.GetTempPath(), $"husaynia-run-b-{Guid.NewGuid():N}");
        var unsafeOutput = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-unsafe-compare-{Guid.NewGuid():N}",
            "result.json");
        Directory.CreateDirectory(runA);
        Directory.CreateDirectory(runB);
        try
        {
            var exitCode = await Program.Main(
                [
                    "compare-screenshot-metrics",
                    "--first-screenshots", Path.Combine(runA, "screenshots.json"),
                    "--first-provenance", Path.Combine(runA, "screenshot-capture-provenance.json"),
                    "--second-evidence", runB,
                    "--output", unsafeOutput
                ]);

            Assert.Equal(2, exitCode);
            Assert.False(Directory.Exists(Path.GetDirectoryName(unsafeOutput)));
        }
        finally
        {
            Directory.Delete(runA, recursive: true);
            Directory.Delete(runB, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://www.husaynia.org/path?authorization_code=secret")]
    [InlineData("/path?X-Goog-Signature=secret")]
    [InlineData("/path?ticket=secret")]
    public void ExtractedEvidenceUrlsRejectCredentialsAndSignedParameters(string value)
    {
        Assert.Null(BaselineCaptureService.SanitizeExtractedUrl(
            CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
            value));
    }

    [Fact]
    public void ExtractedEvidenceUrlsRejectUriUserInfo()
    {
        var uri = new UriBuilder(
            Uri.UriSchemeHttps,
            CaptureProfile.Approved.PrimaryOrigin.Host)
        {
            UserName = "fixture-user",
            Password = "fixture-password",
            Path = "/path"
        }.Uri;

        Assert.Null(BaselineCaptureService.SanitizeExtractedUrl(
            CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
            uri.AbsoluteUri));
    }

    [Fact]
    public void NavigationExtractionDoesNotPersistCredentialBearingDestination()
    {
        var navigation = BaselineCaptureService.ExtractNavigation(
            """<nav><a href="https://alice:password@www.husaynia.org/private?token=secret">Private</a><a href="/safe/">Safe</a></nav>""",
            CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri);

        var item = Assert.Single(navigation);
        Assert.Equal("Safe", item.Label);
        Assert.DoesNotContain("alice", item.Destination, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", item.Destination, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sitemap", BaselineCaptureService.MaximumSitemapDiscoveryCount)]
    [InlineData("route", BaselineCaptureService.MaximumRouteDiscoveryCount)]
    [InlineData("asset", BaselineCaptureService.MaximumAssetDiscoveryCount)]
    [InlineData(
        "asset-reference",
        BaselineCaptureService.MaximumAssetReferenceCount)]
    public void DiscoveryCountsAreRejectedBeforeTaskAllocation(
        string kind,
        int maximum)
    {
        BaselineCaptureService.EnsureDiscoveryCountWithinLimit(kind, maximum, maximum);

        var exception = Assert.Throws<CaptureSafetyException>(() =>
            BaselineCaptureService.EnsureDiscoveryCountWithinLimit(
                kind,
                maximum + 1,
                maximum));

        Assert.Equal($"{kind}-discovery-limit-exceeded", exception.ReasonCode);
    }

    [Fact]
    public void SharedAssetRemainsAssociatedOnlyWithItsFirstDiscoverySourcePage()
    {
        const string assetUrl = "https://www.husaynia.org/shared.png";
        const string firstPage = "https://www.husaynia.org/first/";
        const string secondPage = "https://www.husaynia.org/second/";
        var assets = new List<AssetRecord>
        {
            new(
                "asset-shared",
                assetUrl,
                firstPage,
                "image",
                "image",
                "same-origin-upload",
                200,
                "image/png",
                1,
                new string('0', 64),
                "assets/shared.png",
                null)
        };
        var references = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [assetUrl] = new(
                [firstPage, secondPage],
                StringComparer.OrdinalIgnoreCase)
        };

        var keys = BaselineCaptureService.SelectAssetKeysForSourcePage(
            assets,
            references,
            secondPage);

        Assert.Empty(keys);
    }

    [Fact]
    public void SitemapParserProhibitsDtdAndEntityExpansion()
    {
        var body = Encoding.UTF8.GetBytes("""
            <!DOCTYPE urlset [
              <!ENTITY expansion "https://www.husaynia.org/unsafe/">
            ]>
            <urlset>
              <url><loc>&expansion;</loc></url>
            </urlset>
            """);

        Assert.Throws<System.Xml.XmlException>(
            () => BaselineCaptureService.ParseSitemapDocument(body));
    }

    [Fact]
    public void SitemapParserAcceptsBoundedEntityFreeXml()
    {
        var body = Encoding.UTF8.GetBytes("""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.husaynia.org/safe/</loc></url>
            </urlset>
            """);

        var document = BaselineCaptureService.ParseSitemapDocument(body);

        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "loc"
                && element.Value == "https://www.husaynia.org/safe/");
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData(200, 200)]
    [InlineData(201, 201)]
    [InlineData(403, 403)]
    [InlineData(500, 500)]
    public void RouteManifestGenerationPreservesObservedStatusAndDefaultsMissingTo200(
        int? status,
        int expected)
    {
        var record = new HttpRecord(
            "https://www.husaynia.org/fixture/",
            "/fixture/",
            status,
            null,
            [],
            null,
            null,
            null,
            null,
            "fixture",
            new Dictionary<string, string>());

        Assert.Equal(
            expected,
            BaselineCaptureService.GetExpectedRouteStatus(record, null));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    public void RouteManifestGenerationPreservesObservedRedirectStatus(int status)
    {
        var record = new HttpRecord(
            "https://www.husaynia.org/fixture/",
            "/fixture/",
            200,
            "https://www.husaynia.org/final/",
            [],
            "text/html",
            1,
            new string('0', 64),
            "raw/html/fixture.html",
            null,
            new Dictionary<string, string>());
        var redirect = new RedirectHop(
            record.Url,
            status,
            record.FinalUrl);

        Assert.Equal(
            status,
            BaselineCaptureService.GetExpectedRouteStatus(record, redirect));
    }

    [Fact]
    public async Task RetainedPreReworkRouteStatusesMatchFrozenGeneration()
    {
        var fixture = RetainedPreReworkFixturePath();
        var manifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(Path.Combine(fixture, "route-manifest.json")),
            CaptureIO.JsonOptions)!;
        var records = JsonSerializer.Deserialize<HttpRecord[]>(
            await File.ReadAllTextAsync(Path.Combine(fixture, "http-inventory.json")),
            CaptureIO.JsonOptions)!;
        var recordsByPath = records
            .Where(record => !record.Path.Contains('?', StringComparison.Ordinal))
            .ToDictionary(record => record.Path, StringComparer.Ordinal);
        var matched = 0;
        foreach (var route in manifest.Routes)
        {
            if (!recordsByPath.TryGetValue(route.LegacyPath, out var record))
            {
                continue;
            }

            Assert.Equal(
                route.ExpectedStatus,
                BaselineCaptureService.GetExpectedRouteStatus(
                    record,
                    record.Redirects.Count == 0
                        ? null
                        : record.Redirects[0]));
            matched++;
        }

        Assert.True(matched > 0);
        Assert.Contains(
            records,
            record => record.Status is null
                && recordsByPath.ContainsKey(record.Path));
    }

    [Fact]
    public async Task RetainedPreReworkRouteAssetKeysMatchFirstDiscoveryAssociation()
    {
        var fixture = RetainedPreReworkFixturePath();
        var manifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(Path.Combine(fixture, "route-manifest.json")),
            CaptureIO.JsonOptions)!;
        var assets = JsonSerializer.Deserialize<List<AssetRecord>>(
            await File.ReadAllTextAsync(Path.Combine(fixture, "media-inventory.json")),
            CaptureIO.JsonOptions)!;
        var references = assets
            .GroupBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(asset => asset.SourcePage)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var compared = 0;
        foreach (var route in manifest.Routes.Where(route =>
                     route.TemplateKey != "asset-alias"))
        {
            var sourcePage = new Uri(
                new Uri(manifest.SourceBaseUrl),
                route.LegacyPath).AbsoluteUri;
            var expected = BaselineCaptureService.SelectAssetKeysForSourcePage(
                assets,
                references,
                sourcePage);

            Assert.Equal(route.AssetKeys, expected);
            compared++;
        }

        Assert.True(compared > 0);
    }

    [Fact]
    public async Task RetainedPreReworkImportMediaRefsMatchFirstDiscoveryAssociation()
    {
        var fixture = RetainedPreReworkFixturePath();
        var manifest = JsonSerializer.Deserialize<ImportManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(fixture, "migration-import-manifest.json")),
            CaptureIO.JsonOptions)!;
        var assets = JsonSerializer.Deserialize<List<AssetRecord>>(
            await File.ReadAllTextAsync(Path.Combine(fixture, "media-inventory.json")),
            CaptureIO.JsonOptions)!;
        var references = assets
            .GroupBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(asset => asset.SourcePage)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var compared = 0;
        foreach (var candidate in manifest.Candidates.Where(candidate =>
                     candidate.Kind is "content" or "religious-content"
                     && candidate.SourceUri is not null))
        {
            var sourceUri = candidate.SourceUri
                ?? throw new InvalidOperationException(
                    "Retained content candidate source URI is missing.");
            var expected = BaselineCaptureService.SelectMediaRefsForSourcePage(
                assets,
                references,
                sourceUri);

            Assert.Equal(candidate.MediaRefs, expected);
            compared++;
        }

        Assert.True(compared > 0);
    }

    [Fact]
    public void ReadinessRequiresThreeStableFullyIdleSamples()
    {
        var stable = Enumerable.Range(0, 3)
            .Select(index => ReadinessSample(index, inFlight: 0))
            .ToArray();
        var busyWindow = stable.ToArray();
        busyWindow[0] = ReadinessSample(0, inFlight: 1);

        Assert.True(PlaywrightScreenshotCapture.HasStableReadyWindow(stable));
        Assert.False(PlaywrightScreenshotCapture.HasStableReadyWindow(busyWindow));
        Assert.False(PlaywrightScreenshotCapture.HasStableReadyWindow(stable[..2]));
    }

    [Fact]
    public void CliRequiresExactlyThirtySixSuccessfulScreenshots()
    {
        Assert.True(Program.HasCompleteScreenshotSet(CreateSummary(36, 36, 36)));
        Assert.False(Program.HasCompleteScreenshotSet(CreateSummary(36, 35, 35)));
        Assert.False(Program.HasCompleteScreenshotSet(CreateSummary(36, 36, 35)));
        Assert.False(Program.HasCompleteScreenshotSet(CreateSummary(35, 35, 35)));
    }
    [Theory]
    [InlineData("/duas-dua-e-tawassul/")]
    [InlineData("/duas-dua-faraj/")]
    [InlineData("/dua-kumayl/")]
    [InlineData("/hadith-al-kisa/")]
    [InlineData("/monday-rites/")]
    [InlineData("/ziarat-e-ashura/")]
    public void ReligiousRoutesUseReligiousTemplate(string path)
    {
        Assert.Equal("religious-content", BaselineCaptureService.ClassifyTemplate(path));
    }

    [Fact]
    public void ReligiousFixtureRoundTripsOrderedTextAndAudio()
    {
        const string html = """
            <html><body><h1>Dua Fixture</h1>
            <audio><source src="/dua.mp3"></audio>
            <div class="Ara">بِسْمِ اللَّهِ</div>
            <div class="Trl">bismillah</div>
            <div class="Tra">In the name of Allah</div>
            </body></html>
            """;
        var sourceChecksum = CaptureIO.Sha256(Encoding.UTF8.GetBytes(html));

        var fixture = BaselineCaptureService.ExtractReligiousContent(
            "https://www.husaynia.org/duas-fixture/",
            html,
            sourceChecksum);

        Assert.Equal("religious-content", fixture.TemplateKey);
        Assert.Equal(["arabic", "transliteration", "english-translation"], fixture.Paragraphs.Select(item => item.Role));
        Assert.Equal([0, 1, 2], fixture.Paragraphs.Select(item => item.Order));
        Assert.Equal("rtl", fixture.Paragraphs[0].Direction);
        Assert.Equal("ar", fixture.Paragraphs[0].Language);
        Assert.Equal("ltr", fixture.Paragraphs[1].Direction);
        Assert.Single(fixture.AudioReferences);
        Assert.All(fixture.Paragraphs, item => Assert.Matches("^[0-9a-f]{64}$", item.Checksum));
        Assert.Matches("^[0-9a-f]{64}$", fixture.FixtureChecksum);
    }

    private static CaptureSummary CreateSummary(
        int screenshotCount,
        int successfulScreenshotCount,
        int qualityPassScreenshotCount) =>
        new(
            "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "https://www.husaynia.org/",
            true, 0, 0, 0, 0, 0, 0, 0, 0, 0, screenshotCount, successfulScreenshotCount,
            qualityPassScreenshotCount, 0, 1, 0, 0);

    private static ScreenshotReadinessSample ReadinessSample(int index, int inFlight) =>
        new(
            DateTimeOffset.UtcNow.AddMilliseconds(index * 250),
            320,
            568,
            1,
            320,
            1200,
            320,
            1200,
            true,
            0,
            inFlight);

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "tools", "Husaynia.BaselineCapture")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string RetainedPreReworkFixturePath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "evidence",
            "baseline-runs",
            "adr009-20260816T0645Z-run-a");
}
