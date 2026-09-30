using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Husaynia.BaselineCapture;

public sealed class BaselineCaptureService : IDisposable
{
    private static readonly TimeSpan DiagnosticFinalizationTimeout = TimeSpan.FromMinutes(2);

    internal const int MaximumSitemapDiscoveryCount = 256;
    internal const int MaximumRouteDiscoveryCount = 10_000;
    internal const int MaximumAssetDiscoveryCount = 20_000;
    internal const int MaximumAssetReferenceCount = 100_000;
    internal const long MaximumSitemapXmlCharacters = 4L * 1024 * 1024;

    private static readonly string[] SitemapSeeds =
    [
        "/sitemap.xml",
        "/page-sitemap.xml",
        "/post-sitemap.xml",
        "/tribe_events-sitemap.xml",
        "/post-archive-sitemap.xml"
    ];

    private static readonly string[] PublicEndpointSeeds =
    [
        "/",
        "/robots.txt",
        "/feed/",
        "/events/",
        "/events/?ical=1"
    ];

    private readonly CaptureOptions _options;
    private readonly BaselineCaptureDependencies _dependencies;
    private readonly CaptureProfile _profile = CaptureProfile.Approved;
    private HttpClient? _client;
    private TrustedEndpointPolicy? _endpointPolicy;
    private readonly HashSet<string> _sitemapUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retainedSitemapRouteUrls = new(StringComparer.Ordinal);
    private readonly List<HttpRecord> _httpRecords = [];
    private readonly List<MetadataRecord> _metadata = [];
    private readonly List<AssetRecord> _assets = [];
    private readonly List<NavigationItem> _navigation = [];
    private readonly List<FormObservation> _forms = [];
    private readonly List<DynamicRegionObservation> _dynamicRegions = [];
    private readonly List<ScreenshotRecord> _screenshots = [];
    private readonly List<ScreenshotNetworkDecision> _screenshotNetworkDecisions = [];
    private ScreenshotCaptureProvenance? _screenshotProvenance;
    private readonly List<ReligiousContentRecord> _religiousContent = [];
    private readonly List<ResidualRisk> _risks = [];
    private readonly Dictionary<string, string> _htmlByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssetDiscovery> _assetDiscoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _assetSourcePages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _discoveredRoutes = new(StringComparer.OrdinalIgnoreCase);
    private int _assetReferenceCount;
    private readonly ConcurrentDictionary<string, Lazy<Task<HttpResult>>> _responseCache = new(StringComparer.OrdinalIgnoreCase);
    private int _networkRequestCount;
    private int _cacheHitCount;
    private bool _crossCuttingRisksAdded;
    private string _captureStage = "output-initialization";

    public BaselineCaptureService(CaptureOptions options)
        : this(options, BaselineCaptureDependencies.Default)
    {
    }

    internal BaselineCaptureService(
        CaptureOptions options,
        BaselineCaptureDependencies dependencies)
    {
        _options = options;
        _dependencies = dependencies;
    }

    public async Task<CaptureSummary> CaptureAsync(CancellationToken cancellationToken)
    {
        var startedAt = _dependencies.UtcNow();
        var captureId = $"husaynia-{startedAt:yyyyMMddTHHmmssZ}";
        PrepareOutputDirectory();
        using var durationLimit = _dependencies.CreateDurationLimit(
            cancellationToken,
            TimeSpan.FromMinutes(_options.MaxDurationMinutes));
        var captureToken = durationLimit.Token;
        try
        {
            _captureStage = "endpoint-policy";
            _endpointPolicy = await _dependencies.CreateEndpointPolicyAsync(captureToken);
            _client = new HttpClient(_dependencies.CreateCrawlHandler(_endpointPolicy))
            {
                Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds)
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "HusayniaBaselineCapture/1.0 (+public parity evidence; no-submit)");
            _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");

            // Capture browser evidence immediately after the approved DNS sets are resolved so the
            // short-lived static-host answers are checked and pinned within the bounded matrix window.
            _captureStage = "browser-startup";
            await _dependencies.CheckpointAsync(
                CaptureCheckpoint.BeforeBrowserSession,
                captureToken);
            await CaptureScreenshotsAsync(captureId, captureToken);
            _captureStage = "robots";
            var robotsText = await CaptureRobotsAsync(captureToken);
            var robots = RobotsPolicy.Parse(robotsText);
            _captureStage = "route-discovery";
            var routes = await DiscoverRoutesAsync(robots, captureToken);
            Console.WriteLine(
                $"Discovered {routes.Count} public URLs and {_sitemapUrls.Count} sitemaps.");
            _captureStage = "route-capture";
            await CaptureRoutesAsync(routes, robots, captureToken);
            _captureStage = "asset-capture";
            await CaptureAssetsAsync(robots, captureToken);
            _captureStage = "artifact-finalization";
            return await WriteEvidenceAsync(
                captureId,
                startedAt,
                CaptureFailureEvidence.CompleteStatus,
                failureReason: null,
                failureStage: null,
                failureDetailReason: null,
                captureToken);
        }
        catch (Exception exception)
        {
            var failureReason = CaptureFailureEvidence.Classify(
                exception,
                cancellationToken.IsCancellationRequested,
                durationLimit.IsCancellationRequested);
            using var finalizationLimit = new CancellationTokenSource(
                DiagnosticFinalizationTimeout);
            await WriteEvidenceAsync(
                captureId,
                startedAt,
                CaptureFailureEvidence.FailedStatus,
                failureReason,
                _captureStage,
                GetSocketFailureReason(exception) ?? failureReason,
                finalizationLimit.Token);
            throw;
        }
    }

    private async Task<CaptureSummary> WriteEvidenceAsync(
        string captureId,
        DateTimeOffset startedAt,
        string status,
        string? failureReason,
        string? failureStage,
        string? failureDetailReason,
        CancellationToken cancellationToken)
    {
        var checksumPath = PathFor("checksums.sha256");
        if (File.Exists(checksumPath))
        {
            File.Delete(checksumPath);
        }

        EnsureCrossCuttingRisks();
        if (failureReason is not null
            && _risks.All(risk => risk.RiskId != "RISK-CAPTURE-INCOMPLETE"))
        {
            _risks.Add(new ResidualRisk(
                "RISK-CAPTURE-INCOMPLETE",
                "diagnostics",
                $"Capture ended before completion during {failureStage ?? "unknown"} ({failureReason}).",
                "capture-summary.json; screenshots.json",
                "Retain this run for diagnosis only. Start a fresh empty-output run; do not seal, verify, or promote this evidence."));
        }

        if (failureReason is not null)
        {
            var completedRows = CaptureFailureEvidence.CompleteScreenshotMatrix(
                _screenshots,
                failureReason);
            _screenshots.Clear();
            _screenshots.AddRange(completedRows);
        }

        var completedAt = _dependencies.UtcNow();
        var provenance = _screenshotProvenance
            ?? CaptureFailureEvidence.ToolingFailureProvenance(
                captureId,
                startedAt,
                failureReason ?? "browser-session-not-created");
        var networkPolicy = CaptureFailureEvidence.NetworkPolicy(
            _endpointPolicy,
            _screenshotProvenance,
            _screenshotProvenance is not null,
            failureReason);
        var manifest = BuildRouteManifest(captureId, startedAt, _discoveredRoutes);
        var importManifest = BuildImportManifest(captureId, startedAt);
        var queryEndpointExcludedCount = _discoveredRoutes.Count(url =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Query));
        var routeSocketFailure = _httpRecords.FirstOrDefault(record =>
            record.Status is null
            && record.Error?.StartsWith(
                "socket-",
                StringComparison.Ordinal) == true);
        var summary = new CaptureSummary(
            captureId,
            startedAt,
            completedAt,
            _options.BaseUri.ToString(),
            _options.NoSubmit,
            _sitemapUrls.Count,
            _discoveredRoutes.Count,
            _httpRecords.Count(x => x.Status is not null),
            _httpRecords.Count(x => x.Redirects.Count > 0),
            _metadata.Count,
            _assets.Count,
            _assets.Count(x => x.Sha256 is not null),
            _forms.Count,
            _dynamicRegions.Count,
            _screenshots.Count,
            _screenshots.Count(x => x.Status == "captured"),
            _screenshots.Count(x => x.QualityStatus == "pass"),
            _risks.Count,
            (completedAt - startedAt).TotalSeconds,
            _networkRequestCount,
            _cacheHitCount)
        {
            Status = status,
            FailureReason = failureReason,
            FailureStage = failureStage,
            DiscoveredUrlCount = _discoveredRoutes.Count,
            ManifestPathCount = manifest.Routes.Count,
            QueryEndpointExcludedByFrozenSchemaCount =
                queryEndpointExcludedCount,
            FailureAffectedUrl = routeSocketFailure is null
                ? null
                : EvidenceSanitizer.RedactUrl(
                    routeSocketFailure.FinalUrl
                        ?? routeSocketFailure.Url),
            FailureDetailReason =
                routeSocketFailure?.Error ?? failureDetailReason
        };

        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("route-manifest.json"),
            manifest,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("migration-import-manifest.json"),
            importManifest,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("http-inventory.json"),
            _httpRecords.OrderBy(x => x.Url),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("metadata-inventory.json"),
            _metadata.OrderBy(x => x.Url),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("media-inventory.json"),
            _assets.OrderBy(x => x.Url),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("navigation-inventory.json"),
            _navigation.OrderBy(x => x.SourcePage).ThenBy(x => x.Order),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("forms-widgets.json"),
            new { noSubmit = true, forms = _forms, dynamicRegions = _dynamicRegions },
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("religious-content.json"),
            _religiousContent.OrderBy(x => x.Route),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("screenshots.json"),
            _screenshots,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("screenshot-network-decisions.json"),
            _screenshotNetworkDecisions.OrderBy(x => x.TimestampUtc),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("screenshot-capture-provenance.json"),
            provenance,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("screenshot-network-policy.json"),
            networkPolicy,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("residual-risks.json"),
            _risks,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            PathFor("capture-summary.json"),
            summary,
            cancellationToken);
        await WriteReadmeAsync(summary, cancellationToken);
        await CaptureIO.WriteChecksumsAsync(
            _options.OutputDirectory,
            cancellationToken);
        return summary;
    }

    private void PrepareOutputDirectory()
    {
        CaptureIO.EnsureEmptyOutputDirectory(_options.OutputDirectory);

        foreach (var relative in new[] { "raw/sitemaps", "raw/html", "raw/endpoints", "assets", "screenshots" })
        {
            Directory.CreateDirectory(PathFor(relative));
        }
    }

    private async Task<string> CaptureRobotsAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(_options.BaseUri, "/robots.txt");
        var result = await GetCachedAsync(uri, cancellationToken);
        var text = result.Body is null ? string.Empty : Encoding.UTF8.GetString(result.Body);
        await File.WriteAllTextAsync(PathFor("raw/endpoints/robots.txt"), text, cancellationToken);
        if (result.Error is not null)
        {
            _risks.Add(new ResidualRisk("RISK-ROBOTS", "crawl", $"robots.txt could not be captured: {result.Error}", "raw/endpoints/robots.txt", "Re-run capture before approving the baseline."));
        }

        return text;
    }

    private async Task<HashSet<string>> DiscoverRoutesAsync(RobotsPolicy robots, CancellationToken cancellationToken)
    {
        var pending = new Queue<Uri>(SitemapSeeds.Select(path => new Uri(_options.BaseUri, path)));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            EnsureDiscoveryCountWithinLimit(
                "sitemap",
                visited.Count + pending.Count,
                MaximumSitemapDiscoveryCount);
            var sitemapUri = pending.Dequeue();
            if (!visited.Add(sitemapUri.AbsoluteUri) || !robots.IsAllowed(sitemapUri))
            {
                continue;
            }

            _discoveredRoutes.Add(NormalizePublicUrl(sitemapUri));
            EnsureDiscoveryCountWithinLimit(
                "route",
                _discoveredRoutes.Count,
                MaximumRouteDiscoveryCount);
            var result = await GetCachedAsync(sitemapUri, cancellationToken, robots);
            if (result.Body is null
                || result.Status is not (>= 200 and < 300))
            {
                _risks.Add(new ResidualRisk(
                    CaptureIO.StableKey("risk-sitemap", sitemapUri.AbsoluteUri),
                    "sitemap",
                    $"Sitemap unavailable: {result.Error ?? $"HTTP {result.Status}"}",
                    sitemapUri.AbsoluteUri,
                    "Re-run and reconcile against an authorized export."));
                continue;
            }

            var relative = $"raw/sitemaps/{CaptureIO.SafeFileName(sitemapUri.AbsolutePath)}.xml";
            await File.WriteAllBytesAsync(PathFor(relative), result.Body, cancellationToken);
            _sitemapUrls.Add(sitemapUri.AbsoluteUri);

            try
            {
                var document = ParseSitemapDocument(result.Body);
                var sitemapLocations = document.Descendants()
                    .Where(x => x.Name.LocalName == "sitemap")
                    .SelectMany(x => x.Elements().Where(child => child.Name.LocalName == "loc"))
                    .Select(x => x.Value.Trim());
                foreach (var location in sitemapLocations)
                {
                    if (!Uri.TryCreate(location, UriKind.Absolute, out var discovered) || !IsSameHost(discovered))
                    {
                        continue;
                    }

                    pending.Enqueue(discovered);
                    EnsureDiscoveryCountWithinLimit(
                        "sitemap",
                        visited.Count + pending.Count,
                        MaximumSitemapDiscoveryCount);
                }

                var pageLocations = document.Root?.Name.LocalName == "urlset"
                    ? document.Root.Elements()
                    .Where(x => x.Name.LocalName == "url")
                    .SelectMany(x => x.Elements().Where(child => child.Name.LocalName == "loc"))
                    .Select(x => x.Value.Trim())
                    : [];
                foreach (var location in pageLocations)
                {
                    if (Uri.TryCreate(location, UriKind.Absolute, out var discovered) && IsSameHost(discovered) && robots.IsAllowed(discovered))
                    {
                        var normalized = NormalizePublicUrl(discovered);
                        _retainedSitemapRouteUrls.Add(normalized);
                        _discoveredRoutes.Add(normalized);
                        EnsureDiscoveryCountWithinLimit(
                            "route",
                            _discoveredRoutes.Count,
                            MaximumRouteDiscoveryCount);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
            {
                _risks.Add(new ResidualRisk(
                    CaptureIO.StableKey("risk-sitemap-parse", sitemapUri.AbsoluteUri),
                    "sitemap",
                    $"Sitemap XML could not be parsed ({ex.GetType().Name}).",
                    relative,
                    "Inspect the retained response and reconcile manually."));
            }
        }

        foreach (var seed in PublicEndpointSeeds)
        {
            var uri = new Uri(_options.BaseUri, seed);
            if (robots.IsAllowed(uri))
            {
                _discoveredRoutes.Add(NormalizePublicUrl(uri));
                EnsureDiscoveryCountWithinLimit(
                    "route",
                    _discoveredRoutes.Count,
                    MaximumRouteDiscoveryCount);
            }
        }

        return _discoveredRoutes;
    }

    internal static XDocument ParseSitemapDocument(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var stream = new MemoryStream(body, writable: false);
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumSitemapXmlCharacters
            });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private async Task CaptureRoutesAsync(IEnumerable<string> routes, RobotsPolicy robots, CancellationToken cancellationToken)
    {
        var routeCollection = routes as ICollection<string> ?? routes.ToArray();
        EnsureDiscoveryCountWithinLimit(
            "route",
            routeCollection.Count,
            MaximumRouteDiscoveryCount);
        var ordered = routeCollection.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency);
        var fetches = ordered.Select(async url =>
        {
            var uri = new Uri(url);
            if (!robots.IsAllowed(uri))
            {
                return (Url: url, Result: new HttpResult(null, url, Array.Empty<RedirectHop>(), null, null, new Dictionary<string, string>(), "Request blocked by robots.txt."));
            }

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return (
                    Url: url,
                    Result: await GetRouteResultAsync(
                        uri,
                        robots,
                        cancellationToken));
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var fetched = (await Task.WhenAll(fetches)).ToList();
        var failedUrls = fetched.Where(item => item.Result.Status is null).Select(item => item.Url).ToArray();
        if (failedUrls.Length > 0)
        {
            Console.WriteLine($"Retrying {failedUrls.Length} routes with reduced concurrency.");
            using var retrySemaphore = new SemaphoreSlim(2);
            var retries = failedUrls.Select(async url =>
            {
                _responseCache.TryRemove(url, out _);
                await retrySemaphore.WaitAsync(cancellationToken);
                try
                {
                    return (
                        Url: url,
                        Result: await GetRouteResultAsync(
                            new Uri(url),
                            robots,
                            cancellationToken));
                }
                finally
                {
                    retrySemaphore.Release();
                }
            });
            var retryResults = await Task.WhenAll(retries);
            var retryMap = retryResults.ToDictionary(item => item.Url, item => item.Result, StringComparer.OrdinalIgnoreCase);
            fetched = fetched.Select(item => retryMap.TryGetValue(item.Url, out var result) ? (item.Url, result) : item).ToList();
        }

        var completed = 0;
        foreach (var item in fetched.OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase))
        {
            var url = item.Url;
            var uri = new Uri(url);
            var result = item.Result;
            string? relative = null;
            string? sha = null;
            if (result.Body is not null)
            {
                sha = CaptureIO.Sha256(result.Body);
                var contentType = result.ContentType ?? string.Empty;
                var folder = _sitemapUrls.Contains(url)
                    ? "raw/sitemaps"
                    : contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                        ? "raw/html"
                        : "raw/endpoints";
                var extension = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ? ".html" :
                    contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? ".xml" :
                    contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? ".json" : ".bin";
                relative = $"{folder}/{CaptureIO.SafeFileName(uri.PathAndQuery)}{extension}";
                await File.WriteAllBytesAsync(PathFor(relative), result.Body, cancellationToken);

                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = DecodeBody(result.Body, result.ContentType);
                    _htmlByUrl[url] = html;
                    await InspectHtmlAsync(url, html, sha, cancellationToken);
                }
            }

            _httpRecords.Add(new HttpRecord(
                url,
                NormalizePath(uri),
                result.Status,
                result.FinalUrl,
                result.Redirects,
                result.ContentType,
                result.Body?.LongLength,
                sha,
                relative,
                result.Error,
                result.Headers));
            await _dependencies.CheckpointAsync(
                CaptureCheckpoint.RouteRecordCompleted,
                cancellationToken);

            if (result.Error is not null)
            {
                _risks.Add(new ResidualRisk(
                    CaptureIO.StableKey("risk-http", url),
                    "http",
                    $"Public GET failed: {result.Error}",
                    url,
                    "Re-run capture and retain a successful response before parity approval."));
            }

            completed++;
            if (completed % 25 == 0 || completed == ordered.Length)
            {
                Console.WriteLine($"Captured routes: {completed}/{ordered.Length}");
            }
        }
    }

    private Task InspectHtmlAsync(string url, string html, string contentSha, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var jsonLdTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match script in Regex.Matches(html, @"<script\b[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            try
            {
                using var json = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups["json"].Value));
                CollectJsonLdTypes(json.RootElement, jsonLdTypes);
            }
            catch (JsonException)
            {
                // The retained HTML remains authoritative if a third-party emits non-standard JSON-LD.
            }
        }

        _metadata.Add(new MetadataRecord(
            url,
            ExtractElementText(html, "title"),
            ExtractMeta(html, "name", "description"),
            SanitizeExtractedUrl(url, ExtractLink(html, "canonical")),
            ExtractMeta(html, "name", "robots"),
            ExtractMeta(html, "property", "og:title"),
            ExtractMeta(html, "property", "og:description"),
            SanitizeExtractedUrl(url, ExtractMeta(html, "property", "og:image")),
            ExtractMeta(html, "name", "twitter:card"),
            jsonLdTypes.Order().ToArray(),
            ExtractTagAttribute(html, "html", "lang"),
            ExtractTagAttribute(html, "html", "dir"),
            ExtractElementText(html, "h1"),
            contentSha));

        InspectNavigation(html, url);
        InspectForms(html, url);
        InspectDynamicRegions(html, url);
        DiscoverAssets(html, url);
        if (ClassifyTemplate(new Uri(url).AbsolutePath) == "religious-content")
        {
            var religious = ExtractReligiousContent(url, html, contentSha);
            if (religious.Paragraphs.Count > 0)
            {
                _religiousContent.Add(religious);
            }
        }

        return Task.CompletedTask;
    }

    private void InspectNavigation(string html, string sourceUrl)
    {
        _navigation.AddRange(ExtractNavigation(html, sourceUrl));
    }

    public static IReadOnlyList<NavigationItem> ExtractNavigation(string html, string sourceUrl)
    {
        var output = new List<NavigationItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        var regions = Regex.Matches(html, @"<(?<region>nav|header|footer)\b[^>]*>(?<body>.*?)</\k<region>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Select(match => (Region: match.Groups["region"].Value.ToLowerInvariant(), Body: match.Groups["body"].Value))
            .ToList();
        if (regions.Count == 0)
        {
            regions.Add(("document", html));
        }

        foreach (var region in regions)
        {
            var stack = new List<NavigationContext>();
            foreach (Match token in Regex.Matches(region.Body, @"(?<close></li\s*>)|<li\b(?<liattrs>[^>]*)>|<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                if (token.Groups["close"].Success)
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                }

                if (token.Groups["liattrs"].Success)
                {
                    stack.Add(new NavigationContext(token.Groups["liattrs"].Value));
                    continue;
                }

                var label = CleanHtmlText(token.Groups["text"].Value);
                var attrs = token.Groups["attrs"].Value;
                var href = ExtractAttribute(attrs, "href");
                if (label.Length == 0 || string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var isParentControl = href == "#" || href.StartsWith('#');
                var isDestination = !isParentControl;
                string destination;
                if (isParentControl)
                {
                    destination = $"{sourceUrl.Split('#', 2)[0]}{href}";
                }
                else if (!TryResolvePublicUri(sourceUrl, href, out destination))
                {
                    continue;
                }

                var current = stack.Count > 0 ? stack[^1] : null;
                var parent = stack.Count > 1 ? stack.Take(stack.Count - 1).LastOrDefault(item => item.Key is not null) : null;
                var ancestors = stack.Take(Math.Max(0, stack.Count - 1)).Where(item => item.Key is not null).ToArray();
                var depth = Math.Max(0, stack.Count - 1);
                var navigationKey = CaptureIO.StableKey("nav", $"{sourceUrl}:{region.Region}:{order}:{label}:{destination}");
                if (current is not null)
                {
                    current.Key ??= navigationKey;
                    current.Label ??= label;
                }

                var dedupeKey = $"{region.Region}\n{label}\n{destination}\n{parent?.Key}";
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                var combinedClasses = $"{ExtractAttribute(attrs, "class")} {current?.Attributes}";
                var isActive = combinedClasses.Contains("current", StringComparison.OrdinalIgnoreCase)
                    || combinedClasses.Contains("active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ExtractAttribute(attrs, "aria-current"), "page", StringComparison.OrdinalIgnoreCase);
                var isMobile = combinedClasses.Contains("mobile", StringComparison.OrdinalIgnoreCase)
                    || combinedClasses.Contains("toggle", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("menu", StringComparison.OrdinalIgnoreCase);
                output.Add(new NavigationItem(
                    navigationKey,
                    sourceUrl,
                    label,
                    destination,
                    parent?.Label,
                    parent?.Key,
                    ancestors.Select(item => item.Label!).ToArray(),
                    ancestors.Select(item => item.Key!).ToArray(),
                    depth,
                    region.Region,
                    order++,
                    !string.Equals(new Uri(destination).Host, new Uri(sourceUrl).Host, StringComparison.OrdinalIgnoreCase),
                    isDestination,
                    isParentControl,
                    isActive,
                    true,
                    isMobile,
                    !isMobile,
                    true,
                    $"class={combinedClasses.Trim()}; aria-current={ExtractAttribute(attrs, "aria-current") ?? "none"}"));
            }

            foreach (Match button in Regex.Matches(region.Body, @"<button\b(?<attrs>[^>]*)>(?<text>.*?)</button>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var attrs = button.Groups["attrs"].Value;
                var classes = ExtractAttribute(attrs, "class") ?? string.Empty;
                if (!classes.Contains("menu", StringComparison.OrdinalIgnoreCase)
                    && !classes.Contains("toggle", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var label = ExtractAttribute(attrs, "aria-label") ?? CleanHtmlText(button.Groups["text"].Value);
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = "Mobile menu";
                }

                var destination = $"{sourceUrl.TrimEnd('/')}#mobile-menu";
                var navigationKey = CaptureIO.StableKey("nav", $"{sourceUrl}:{region.Region}:mobile:{order}:{label}");
                output.Add(new NavigationItem(
                    navigationKey,
                    sourceUrl,
                    label,
                    destination,
                    null,
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    0,
                    region.Region,
                    order++,
                    false,
                    false,
                    true,
                    false,
                    true,
                    true,
                    false,
                    true,
                    $"class={classes}; aria-expanded={ExtractAttribute(attrs, "aria-expanded") ?? "unknown"}"));
            }
        }

        return output;
    }

    private void InspectForms(string html, string sourceUrl)
    {
        var index = 0;
        foreach (Match form in Regex.Matches(html, @"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var fields = new List<FormFieldObservation>();
            foreach (Match field in Regex.Matches(form.Groups["body"].Value, @"<(?<tag>input|select|textarea|button)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var attrs = field.Groups["attrs"].Value;
                var id = ExtractAttribute(attrs, "id");
                var label = id is null ? null : ExtractLabel(html, id);
                fields.Add(new FormFieldObservation(
                    ExtractAttribute(attrs, "name"),
                    ExtractAttribute(attrs, "type") ?? field.Groups["tag"].Value.ToLowerInvariant(),
                    label,
                    Regex.IsMatch(attrs, @"\brequired(?:\s|=|$)", RegexOptions.IgnoreCase) || string.Equals(ExtractAttribute(attrs, "aria-required"), "true", StringComparison.OrdinalIgnoreCase),
                    ExtractAttribute(attrs, "autocomplete")));
            }

            var rawAction = ExtractAttribute(form.Groups["attrs"].Value, "action") ?? sourceUrl;
            var action = SanitizeExtractedUrl(sourceUrl, rawAction)
                ?? "[blocked-sensitive-url]";
            var method = (ExtractAttribute(form.Groups["attrs"].Value, "method") ?? "get").ToUpperInvariant();
            _forms.Add(new FormObservation(
                sourceUrl,
                CaptureIO.StableKey("form", $"{sourceUrl}:{index++}"),
                method,
                action,
                fields,
                false,
                "Observed markup only; validation, destination workflow, anti-abuse behavior, and success states require authorized non-production testing/export evidence."));
        }
    }

    private void InspectDynamicRegions(string html, string sourceUrl)
    {
        var index = 0;
        foreach (Match element in Regex.Matches(html, @"<(?<tag>iframe|div|section|form)\b(?<attrs>[^>]*(?:facebook|instagram|tribe-events|wpforms|data-provider)[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = element.Groups["attrs"].Value;
            var src = ExtractAttribute(attrs, "src");
            var provider = src is not null && Uri.TryCreate(src, UriKind.Absolute, out var providerUri)
                ? providerUri.Host
                : ExtractAttribute(attrs, "class") ?? element.Groups["tag"].Value;
            var id = ExtractAttribute(attrs, "id");
            var selector = id is { Length: > 0 } ? $"#{id}" : $"{element.Groups["tag"].Value}.{(ExtractAttribute(attrs, "class") ?? "dynamic").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()}";
            var regionKey = CaptureIO.StableKey("dynamic", $"{sourceUrl}:{selector}:{index++}");
            if (_dynamicRegions.Any(x => x.SourcePage == sourceUrl && x.Selector == selector))
            {
                continue;
            }

            _dynamicRegions.Add(new DynamicRegionObservation(
                sourceUrl,
                regionKey,
                provider,
                selector,
                "Animations/transitions disabled; embedded frames and volatile social/calendar content masked during screenshots.",
                "Snapshot captures container/layout only. Live provider data, consent state, loading, empty, and upstream-error states require later controlled fixtures."));
        }
    }

    private void DiscoverAssets(string html, string sourceUrl)
    {
        foreach (Match element in Regex.Matches(html, @"<(?<tag>img|source|video|audio|script|link|a)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var tag = element.Groups["tag"].Value.ToLowerInvariant();
            var attrs = element.Groups["attrs"].Value;
            var value = ExtractAttribute(attrs, tag is "link" or "a" ? "href" : "src");
            if (string.IsNullOrWhiteSpace(value) || !TryResolvePublicUri(sourceUrl, value, out var resolved))
            {
                continue;
            }

            var uri = new Uri(resolved);
            var path = uri.AbsolutePath;
            var relation = ClassifyAssetRelation(tag, attrs, path);
            var isAsset = relation is not null;

            if (isAsset && IsSameHost(uri))
            {
                AddAssetDiscovery(uri.GetLeftPart(UriPartial.Path), sourceUrl, relation!);
            }

            var srcset = ExtractAttribute(attrs, "srcset");
            if (!string.IsNullOrWhiteSpace(srcset))
            {
                foreach (var item in srcset.Split(','))
                {
                    var candidate = item.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    if (TryResolvePublicUri(sourceUrl, candidate, out var srcsetUrl) && IsSameHost(new Uri(srcsetUrl)))
                    {
                        AddAssetDiscovery(new Uri(srcsetUrl).GetLeftPart(UriPartial.Path), sourceUrl, "responsive-image");
                    }
                }
            }
        }
    }

    private async Task CaptureAssetsAsync(RobotsPolicy robots, CancellationToken cancellationToken)
    {
        EnsureDiscoveryCountWithinLimit(
            "asset",
            _assetDiscoveries.Count,
            MaximumAssetDiscoveryCount);
        var discoveries = _assetDiscoveries.Values.OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase).ToArray();
        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency);
        var completedAssets = new ConcurrentBag<AssetRecord>();
        var tasks = discoveries.Select(async discovery =>
        {
            var uri = new Uri(discovery.Url);
            if (!robots.IsAllowed(uri))
            {
                var blocked = new AssetRecord(CaptureIO.StableKey("asset", discovery.Url), discovery.Url, discovery.SourcePage, "rejected", discovery.Relation, ClassifyOwnership(uri), null, null, null, null, null, "Request blocked by robots.txt.");
                completedAssets.Add(blocked);
                await _dependencies.CheckpointAsync(
                    CaptureCheckpoint.AssetRecordCompleted,
                    cancellationToken);
                return blocked;
            }

            await semaphore.WaitAsync(cancellationToken);
            HttpResult result;
            try
            {
                result = await GetCachedAsync(uri, cancellationToken, robots);
            }
            finally
            {
                semaphore.Release();
            }

            string? relative = null;
            string? sha = null;
            string? error = result.Error;
            var kind = ClassifyAsset(uri.AbsolutePath, result.ContentType);
            var successful = result.Status is >= 200 and < 300;
            var contentIsMedia = IsApprovedAssetContentType(result.ContentType, uri.AbsolutePath);
            if (!successful || !contentIsMedia)
            {
                error = !successful
                    ? $"Rejected asset response HTTP {result.Status?.ToString(CultureInfo.InvariantCulture) ?? "none"}."
                    : $"Rejected non-media content type {result.ContentType ?? "unknown"}.";
                kind = "rejected";
            }
            else if (result.Body is { } body)
            {
                if (body.Length > _options.MaxAssetBytes)
                {
                    error = $"Asset exceeds configured {_options.MaxAssetBytes} byte retention limit.";
                }
                else
                {
                    sha = CaptureIO.Sha256(body);
                    var extension = Path.GetExtension(uri.AbsolutePath);
                    if (extension.Length is < 2 or > 8)
                    {
                        extension = ".bin";
                    }

                    relative = $"assets/{CaptureIO.SafeFileName(uri.AbsolutePath)}-{CaptureIO.StableKey("a", discovery.Url)[2..]}{extension.ToLowerInvariant()}";
                    await File.WriteAllBytesAsync(PathFor(relative), body, cancellationToken);
                }
            }

            var asset = new AssetRecord(
                CaptureIO.StableKey("asset", discovery.Url),
                discovery.Url,
                discovery.SourcePage,
                kind,
                discovery.Relation,
                ClassifyOwnership(uri),
                result.Status,
                result.ContentType,
                result.Body?.LongLength,
                sha,
                relative,
                error);
            completedAssets.Add(asset);
            await _dependencies.CheckpointAsync(
                CaptureCheckpoint.AssetRecordCompleted,
                cancellationToken);
            return asset;
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            _assets.AddRange(
                completedAssets
                    .OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase));
        }

        if (_assets.Count > 0)
        {
            Console.WriteLine($"Captured assets: {_assets.Count}/{discoveries.Length}");
        }
    }

    private async Task CaptureScreenshotsAsync(string captureId, CancellationToken cancellationToken)
    {
        var representatives = SelectTemplateRepresentatives();
        if (representatives.Count == 0)
        {
            _risks.Add(new ResidualRisk("RISK-SCREENSHOT-NO-HTML", "visual", "No HTML route was captured, so screenshots could not be attempted.", "http-inventory.json", "Re-run after the public site is reachable."));
            return;
        }

        await using var capture = await _dependencies.CreateScreenshotSessionAsync(
            captureId,
            _options.OutputDirectory,
            _endpointPolicy ?? throw new InvalidOperationException("endpoint-policy-not-created"),
            cancellationToken);
        try
        {
            _captureStage = "screenshot-matrix";
            foreach (var representative in representatives)
            {
                foreach (var viewport in _profile.Viewports)
                {
                    PlaywrightCaptureResult result;
                    try
                    {
                        result = await capture.CaptureAsync(
                            representative.Key,
                            representative.Value,
                            viewport.Name,
                            viewport.Width,
                            viewport.Height,
                            cancellationToken);
                    }
                    catch (ScreenshotCaptureCanceledException exception)
                    {
                        _screenshots.Add(exception.PartialResult.Screenshot);
                        _screenshotNetworkDecisions.AddRange(
                            exception.PartialResult.NetworkDecisions);
                        throw;
                    }

                    _screenshots.Add(result.Screenshot);
                    _screenshotNetworkDecisions.AddRange(result.NetworkDecisions);
                    Console.WriteLine($"Captured screenshot {result.Screenshot.Status}: {representative.Key}/{viewport.Name}");
                    await _dependencies.CheckpointAsync(
                        CaptureCheckpoint.ScreenshotRowCompleted,
                        cancellationToken);
                    await _dependencies.DelayAsync(
                        TimeSpan.FromMilliseconds(
                            PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds),
                        cancellationToken);
                }
            }
        }
        finally
        {
            _screenshotProvenance = capture.Provenance;
        }
    }

    private Dictionary<string, string> SelectTemplateRepresentatives() =>
        _profile.Representatives.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.AbsoluteUri,
            StringComparer.Ordinal);

    private RouteManifest BuildRouteManifest(string captureId, DateTimeOffset capturedAt, IEnumerable<string> routes)
    {
        var entries = new List<RouteEntry>();
        foreach (var url in routes.Order(StringComparer.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            if (!string.IsNullOrEmpty(uri.Query))
            {
                continue;
            }

            var record = _httpRecords.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            var metadata = _metadata.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
            var finalPath = record?.FinalUrl is { } final && Uri.TryCreate(final, UriKind.Absolute, out var finalUri)
                ? NormalizePath(finalUri)
                : NormalizePath(uri);
            var redirect = record?.Redirects.Count > 0 ? record.Redirects[0] : null;
            var expectedStatus = GetExpectedRouteStatus(record, redirect);

            entries.Add(new RouteEntry(
                CaptureIO.StableKey("route", NormalizePath(uri)),
                NormalizePath(uri),
                NormalizePath(uri),
                expectedStatus,
                expectedStatus is 301 or 308 ? finalPath : null,
                ClassifyTemplate(uri.AbsolutePath),
                record?.Sha256 is null ? null : CaptureIO.StableKey("content", NormalizePath(uri)),
                !string.Equals(metadata?.Robots, "noindex", StringComparison.OrdinalIgnoreCase),
                IsSitemapDiscovered(url),
                metadata is null ? null : CaptureIO.StableKey("metadata", NormalizePath(uri)),
                SelectAssetKeysForSourcePage(
                    _assets,
                    _assetSourcePages,
                    url),
                _dynamicRegions.Where(x => x.SourcePage == url).Select(x => x.RegionKey).Distinct().Order().ToArray(),
                new[] { record?.SavedAs ?? "http-inventory.json", "http-inventory.json", metadata is null ? "capture-summary.json" : "metadata-inventory.json" }.Distinct().ToArray(),
                record?.Sha256));
        }

        foreach (var asset in _assets
            .Where(x => x.Kind != "rejected"
                && x.Status is >= 200 and < 300
                && new Uri(x.Url).AbsolutePath.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase))
        {
            var uri = new Uri(asset.Url);
            var path = NormalizePath(uri);
            if (entries.Any(x => string.Equals(x.LegacyPath, path, StringComparison.Ordinal)))
            {
                continue;
            }

            entries.Add(new RouteEntry(
                CaptureIO.StableKey("route", path),
                path,
                path,
                asset.Status == 404 ? 404 : 200,
                null,
                "asset-alias",
                asset.Sha256 is null ? null : asset.Key,
                false,
                false,
                null,
                new[] { asset.Key },
                Array.Empty<string>(),
                new[] { "media-inventory.json", asset.SavedAs ?? "media-inventory.json" }.Distinct().ToArray(),
                asset.Sha256));
        }

        return new RouteManifest("1.0.0", captureId, capturedAt, _options.BaseUri.ToString(), entries.OrderBy(x => x.LegacyPath, StringComparer.Ordinal).ToArray());
    }

    internal static int GetExpectedRouteStatus(
        HttpRecord? record,
        RedirectHop? redirect) =>
        redirect?.Status ?? record?.Status ?? 200;

    private ImportManifest BuildImportManifest(string captureId, DateTimeOffset capturedAt)
    {
        var candidates = new List<ImportCandidate>();
        foreach (var record in _httpRecords.OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase))
        {
            if (new Uri(record.Url).Query.Length > 0)
            {
                candidates.Add(CreateCandidate(
                    captureId,
                    "endpoint",
                    record.Url,
                    record.Sha256 ?? HashRecord(record),
                    record.Url,
                    null,
                    record.SavedAs ?? "http-inventory.json",
                    record.Status is null ? "reject" : "update",
                    record.Status is null ? record.Error ?? "Query endpoint capture failed." : "Contractually significant query endpoint is represented by sourceUri; canonicalPath is omitted to satisfy C5.",
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                continue;
            }

            if (record.Redirects.Count > 0)
            {
                candidates.Add(CreateCandidate(
                    captureId,
                    "redirect",
                    record.Url,
                    HashRecord(record),
                    record.Url,
                    record.Path.Contains('?', StringComparison.Ordinal) ? null : record.Path,
                    "http-inventory.json",
                    "update",
                    "Legacy redirect must be reconciled to a single permanent target.",
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                continue;
            }

            if (record.Status is not (>= 200 and < 300) || record.Sha256 is null || record.SavedAs is null)
            {
                candidates.Add(CreateCandidate(
                    captureId,
                    "endpoint",
                    record.Url,
                    HashRecord(record),
                    record.Url,
                    record.Path.Contains('?', StringComparison.Ordinal) ? null : record.Path,
                    "http-inventory.json",
                    "reject",
                    $"Captured endpoint was not importable: HTTP {record.Status?.ToString(CultureInfo.InvariantCulture) ?? "none"} {record.Error}".Trim(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
                continue;
            }

            var html = record.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
            var template = ClassifyTemplate(new Uri(record.Url).AbsolutePath);
            candidates.Add(CreateCandidate(
                captureId,
                html && template == "religious-content" ? "religious-content" : html ? "content" : "endpoint",
                record.Url,
                record.Sha256,
                record.Url,
                record.Path.Contains('?', StringComparison.Ordinal) ? null : record.Path,
                record.SavedAs,
                html ? "create" : "update",
                html ? null : "Generated public endpoint must be recreated rather than copied as editable content.",
                SelectMediaRefsForSourcePage(
                    _assets,
                    _assetSourcePages,
                    record.Url),
                Array.Empty<string>()));
        }

        var directContentUrls = _httpRecords
            .Where(record => record.Redirects.Count == 0 && new Uri(record.Url).Query.Length == 0)
            .Select(record => record.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var metadata in _metadata.Where(item => directContentUrls.Contains(item.Url)).OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase))
        {
            var path = NormalizePath(new Uri(metadata.Url));
            candidates.Add(CreateCandidate(
                captureId,
                "metadata",
                metadata.Url,
                HashRecord(metadata),
                metadata.Url,
                path.Contains('?', StringComparison.Ordinal) ? null : path,
                "metadata-inventory.json",
                "update",
                "SEO metadata is reconciled independently from body content.",
                Array.Empty<string>(),
                new[]
                {
                    CandidateSourceKey(
                        ClassifyTemplate(new Uri(metadata.Url).AbsolutePath) == "religious-content" ? "religious-content" : "content",
                        metadata.Url)
                }));
        }

        foreach (var group in _navigation.Where(item => directContentUrls.Contains(item.SourcePage)).GroupBy(x => x.SourcePage, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(CreateCandidate(
                captureId,
                "navigation",
                group.Key,
                HashRecord(group.OrderBy(x => x.Order).ToArray()),
                group.Key,
                NormalizePath(new Uri(group.Key)),
                "navigation-inventory.json",
                "update",
                "Hierarchy, ordering, active, focusable, desktop and mobile states require explicit editorial reconciliation.",
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        foreach (var form in _forms.Where(item => directContentUrls.Contains(item.SourcePage)).OrderBy(x => x.FormKey, StringComparer.Ordinal))
        {
            candidates.Add(CreateCandidate(
                captureId,
                "form",
                form.FormKey,
                HashRecord(form),
                form.SourcePage,
                NormalizePath(new Uri(form.SourcePage)),
                "forms-widgets.json",
                "conflict",
                "Public markup is observable, but destination, anti-abuse, consent and success workflow require authorized export/non-production evidence.",
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        candidates.Add(CreateCandidate(
            captureId,
            "editable-setting",
            "site-settings",
            HashRecord(new { _options.BaseUri, Navigation = _navigation.Count, DynamicRegions = _dynamicRegions.Count }),
            _options.BaseUri.ToString(),
            "/",
            "forms-widgets.json",
            "conflict",
            "Provider credentials, payment configuration, form destinations and widget settings are not observable from public GET evidence.",
            Array.Empty<string>(),
            Array.Empty<string>()));

        var contentUrls = _httpRecords.Where(x => x.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => x.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _assets.OrderBy(x => x.Url, StringComparer.OrdinalIgnoreCase))
        {
            if (contentUrls.Contains(asset.Url))
            {
                continue;
            }

            var decision = asset.Kind == "rejected" ? "reject"
                : asset.Ownership is "same-origin-plugin" or "same-origin-theme" or "same-origin-core" ? "skip"
                : string.IsNullOrWhiteSpace(asset.SourcePage) ? "orphan"
                : "create";
            var reason = decision switch
            {
                "reject" => asset.Error ?? "Response was not an approved successful media type.",
                "skip" => "Technical WordPress/plugin/theme implementation asset is evidence only and must not be migrated as editable media.",
                "orphan" => "Successful media has no captured content reference and requires manual reconciliation.",
                _ => null
            };
            candidates.Add(CreateCandidate(
                captureId,
                "media",
                asset.Url,
                asset.Sha256 ?? HashRecord(new { asset.Url, asset.Status, asset.Error }),
                asset.Url,
                null,
                asset.SavedAs ?? "media-inventory.json",
                decision,
                reason,
                Array.Empty<string>(),
                Array.Empty<string>()));
        }

        candidates = candidates
            .GroupBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .ToList();

        return new ImportManifest(
            "1.0.0",
            _options.BaseUri.ToString(),
            captureId,
            capturedAt,
            "public-fidelity-evidence-only; publication/reuse requires ownership or license confirmation; excludes third-party embeds, analytics/payment payloads, submissions, and private/export-only data",
            "dry-run",
            candidates);
    }

    private void EnsureCrossCuttingRisks()
    {
        if (_crossCuttingRisksAdded)
        {
            return;
        }

        _crossCuttingRisksAdded = true;
        if (_forms.Count > 0)
        {
            _risks.Add(new ResidualRisk(
                "RISK-FORMS-NO-SUBMIT",
                "forms",
                $"{_forms.Count} forms were observed but never submitted, by design.",
                "forms-widgets.json",
                "Confirm field rules, anti-abuse, destinations, consent, and success/error states in an authorized non-production environment or export."));
        }

        if (_dynamicRegions.Count > 0)
        {
            _risks.Add(new ResidualRisk(
                "RISK-DYNAMIC-PROVIDERS",
                "dynamic-content",
                $"{_dynamicRegions.Count} volatile/third-party regions were normalized for reproducibility.",
                "forms-widgets.json",
                "Create deterministic fixtures for loading, populated, empty, consent-denied, and upstream-error states."));
        }

        if (_httpRecords.Any(x => x.Redirects.Count > 1))
        {
            _risks.Add(new ResidualRisk(
                "RISK-REDIRECT-CHAINS",
                "urls",
                "One or more live routes currently redirect through multiple hops.",
                "http-inventory.json",
                "The replacement must collapse each approved legacy route to a single permanent redirect."));
        }

        if (_discoveredRoutes.Any(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Query)))
        {
            _risks.Add(new ResidualRisk(
                "RISK-QUERY-ROUTE-SCHEMA",
                "urls",
                "Contractually significant query endpoints were discovered but cannot be represented by the frozen route schema path pattern.",
                "capture-summary.json; route-manifest.json; http-inventory.json",
                "Later URL implementation must preserve these endpoints; resolve the frozen schema contradiction through the architecture change process before altering C1."));
        }
    }

    private async Task WriteReadmeAsync(CaptureSummary summary, CancellationToken cancellationToken)
    {
        if (summary.Status == CaptureFailureEvidence.FailedStatus)
        {
            var diagnostic = $"""
                # Husaynia.org incomplete capture diagnostics

                Capture ID: `{summary.CaptureId}`  
                Started UTC: `{summary.StartedAtUtc:O}`  
                Failed UTC: `{summary.CompletedAtUtc:O}`  
                Source: `{summary.SourceBaseUrl}`  
                Safety mode: `--no-submit`  
                Status: `failed`  
                Failure stage: `{summary.FailureStage ?? "unknown"}`  
                Failure reason: `{summary.FailureReason ?? "capture-error:unknown"}`

                This directory is retained diagnostic evidence only. The capture did not complete,
                has no determinism comparison or baseline verification seal, cannot satisfy the
                validator, and must never be promoted. Start any retry in a new empty output
                directory so this evidence remains intact.

                ## Truthful partial counts

                Discovered URLs `{summary.DiscoveredUrlCount ?? summary.RouteCount}`; manifest paths `{summary.ManifestPathCount ?? 0}`; query endpoints excluded by the frozen schema `{summary.QueryEndpointExcludedByFrozenSchemaCount ?? 0}`; retained route observations `{_httpRecords.Count}`; successful/redirect responses
                `{summary.SuccessfulRouteCount}`; metadata `{summary.MetadataCount}`; assets
                `{summary.AssetCount}` (`{summary.DownloadedAssetCount}` retained); forms
                `{summary.FormCount}`; religious fixtures `{_religiousContent.Count}`; dynamic
                regions `{summary.DynamicRegionCount}`; screenshots captured
                `{summary.SuccessfulScreenshotCount}/{summary.ScreenshotCount}`; screenshots
                quality-pass `{summary.QualityPassScreenshotCount}/{summary.ScreenshotCount}`;
                residual risks `{summary.ResidualRiskCount}`. Duration
                `{summary.DurationSeconds:F3}` seconds; network requests
                `{summary.NetworkRequestCount}`; cache hits `{summary.CacheHitCount}`.

                `screenshots.json` always contains the complete 36-key matrix. Rows completed before
                failure are retained unchanged; remaining rows are explicit failures with the stable
                failure reason. `screenshot-network-decisions.json` contains every decision returned
                by a completed screenshot attempt. Browser provenance and policy are retained when a
                browser session existed; otherwise the tooling-failure provenance/policy records are
                intentionally invalid for baseline approval. Exception messages, ambient environment
                values, paths, credentials, and secrets are not written. `checksums.sha256` was
                generated last and covers every retained file except itself.
                """;
            await CaptureIO.WriteTextAtomicAsync(
                PathFor("README.md"),
                diagnostic,
                cancellationToken);
            return;
        }

        var text = $"""
            # Husaynia.org live baseline

            Capture ID: `{summary.CaptureId}`  
            Started UTC: `{summary.StartedAtUtc:O}`  
            Completed UTC: `{summary.CompletedAtUtc:O}`  
            Source: `{summary.SourceBaseUrl}`  
            Safety mode: `--no-submit` (no form, login, donation, or payment action is executed)

            ## Reproduce

            From `HusayniaSite`:

            ```powershell
            dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
            dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore
            pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
            dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --output evidence/staging/<run-id>
            $env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/staging/<run-id>)
            dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
            dotnet run --project tools/Husaynia.BaselineCapture -c Release -- promote --approved --from evidence/staging/<run-id> --to evidence/baseline
            ```

            Capture output is staged and is not promoted over the approved baseline without independent approval and the explicit `promote --approved` command. Capture refuses a non-empty output directory. The crawler uses at most {_options.MaxConcurrency} concurrent safe public GET requests, honors `robots.txt`, performs at most two attempts per request, uses a {_options.RequestTimeoutSeconds}-second request timeout and {_options.MaxDurationMinutes}-minute whole-capture deadline, caps retained assets at {_options.MaxAssetBytes} bytes each, and applies a {_options.DelayMilliseconds} ms per-request delay. An in-run deterministic URL cache prevents redundant downloads.

            ## Inventories

            - `route-manifest.json`: frozen C1-compatible, query-free URL contract instance.
            - `http-inventory.json`: status, redirect hops, headers, retained payload, and checksum.
            - `metadata-inventory.json`: title, canonical, robots, Open Graph, Twitter, JSON-LD, language/direction, and H1.
            - `media-inventory.json`: discovered same-origin public assets and checksums.
            - `navigation-inventory.json`: observed header/nav/footer labels, order, hierarchy, and destinations.
            - `forms-widgets.json`: markup-only form fields and normalized dynamic-region observations.
            - `religious-content.json`: ordered Arabic/transliteration/translation/audio fixtures and checksums.
            - `screenshots.json`: validated representative template captures at all six AC-03 viewports.
            - `screenshot-network-policy.json`: executable allow/block rules for GET-only visual rendering.
            - `screenshot-network-decisions.json`: redacted per-request, terminal response/failure, and blocked-capability decisions.
            - `screenshot-capture-provenance.json`: pinned Playwright/Chromium/runtime provenance and browser install command.
            - `migration-import-manifest.json`: C5-shaped dry-run candidate evidence with an explicit rights profile.
            - `residual-risks.json`: export/widget/form/payment unknowns and required follow-up.
            - `checksums.sha256`: integrity hashes for every retained file except the checksum file itself.

            ## Counts

            Discovered URLs `{summary.DiscoveredUrlCount ?? summary.RouteCount}`; manifest paths `{summary.ManifestPathCount ?? 0}`; query endpoints excluded by the frozen schema `{summary.QueryEndpointExcludedByFrozenSchemaCount ?? 0}`; successful/redirect responses `{summary.SuccessfulRouteCount}`; metadata `{summary.MetadataCount}`; assets `{summary.AssetCount}` (`{summary.DownloadedAssetCount}` retained); forms `{summary.FormCount}`; religious fixtures `{_religiousContent.Count}`; dynamic regions `{summary.DynamicRegionCount}`; screenshots captured `{summary.SuccessfulScreenshotCount}/{summary.ScreenshotCount}`; screenshots quality-pass `{summary.QualityPassScreenshotCount}/{summary.ScreenshotCount}`; residual risks `{summary.ResidualRiskCount}`. Duration `{summary.DurationSeconds:F1}` seconds; network requests `{summary.NetworkRequestCount}`; cache hits `{summary.CacheHitCount}`.

            ## Stability and rights

            Screenshots use Microsoft.Playwright 1.62.0 with package-matched Chromium 151.0.7922.34 (revision 1234). The executable is SHA-256 verified before launch, ChromiumSandbox is true, and the browser receives only an allowlisted OS/runtime/temp environment while running as a non-administrative account from an empty OS temporary directory. The browser cache is read/execute, the repository is not exposed as a browser working directory, and only the run staging directory is written by .NET. Each bounded attempt opens a fresh context and page with exact viewport and screen dimensions, DPR 1, service workers blocked, downloads disabled, no cookies/storage state/permissions, and context-wide default-deny routing. DNS answers are public-only, fixed to https://www.husaynia.org:443 plus the approved static hosts, checked for changes before each attempt, and pinned for crawl and Chromium traffic. Same-origin GET/HEAD documents/resources and explicitly allowlisted static font/CDN resources are retained; non-GET/HEAD requests, form/payment endpoints, refresh navigation, WebSockets, popups, downloads, analytics, payment hosts, trackers, and non-allowlisted third-party widgets are blocked and terminally logged. No local HTML, fixture markup, masking, DOM rewrite, layout CSS, or viewport rescaling is used. Layout readiness waits for fonts, visible images, zero allowed in-flight requests, stable geometry, and a terminal request barrier; genuine live-page overflow is recorded as captured evidence but fails quality and promotion. Retained public assets are fidelity/migration evidence only; later publication requires ownership/license confirmation. Private/export-only records, form payloads, credentials, and payment data are not captured.
            """;
        await CaptureIO.WriteTextAtomicAsync(
            PathFor("README.md"),
            text,
            cancellationToken);
    }

    private Task<HttpResult> GetCachedAsync(Uri uri, CancellationToken cancellationToken, RobotsPolicy? robots = null)
    {
        var key = uri.AbsoluteUri;
        if (_responseCache.TryGetValue(key, out var existing))
        {
            Interlocked.Increment(ref _cacheHitCount);
            return existing.Value;
        }

        var created = new Lazy<Task<HttpResult>>(
            () => GetWithRedirectsAsync(uri, cancellationToken, robots),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _responseCache.GetOrAdd(key, created);
        if (!ReferenceEquals(created, selected))
        {
            Interlocked.Increment(ref _cacheHitCount);
        }

        return selected.Value;
    }

    private async Task<HttpResult> GetRouteResultAsync(
        Uri uri,
        RobotsPolicy robots,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetCachedAsync(uri, cancellationToken, robots);
        }
        catch (Exception exception)
            when (CaptureFailureEvidence.FindInnerException<CaptureSafetyException>(
                    exception) is null
                && GetSocketFailureReason(exception) is not null)
        {
            return new HttpResult(
                null,
                EvidenceSanitizer.RedactUrl(uri.AbsoluteUri),
                [],
                null,
                null,
                new Dictionary<string, string>(),
                GetSocketFailureReason(exception));
        }
    }

    private async Task<HttpResult> GetWithRedirectsAsync(Uri initialUri, CancellationToken cancellationToken, RobotsPolicy? robots = null)
    {
        var current = initialUri;
        var redirects = new List<RedirectHop>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await (_endpointPolicy ?? throw new InvalidOperationException("endpoint-policy-not-created"))
                    .AssertHostDnsSetUnchangedAsync(current.Host, cancellationToken);
                for (var hop = 0; hop <= 5; hop++)
                {
                    var endpointDecision = await _endpointPolicy.AuthorizeAsync(
                        new EndpointRequest(current, "GET", "document", true, true),
                        cancellationToken);
                    if (!endpointDecision.Allowed)
                    {
                        return new HttpResult(
                            null,
                            current.AbsoluteUri,
                            redirects,
                            null,
                            null,
                            new Dictionary<string, string>(),
                            endpointDecision.ReasonCode);
                    }

                    if (robots is not null && !robots.IsAllowed(current))
                    {
                        return new HttpResult(null, current.AbsoluteUri, redirects, null, null, new Dictionary<string, string>(), "Request blocked by robots.txt.");
                    }

                    await Task.Delay(_options.DelayMilliseconds, cancellationToken);
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    Interlocked.Increment(ref _networkRequestCount);
                    using var response = await (_client ?? throw new InvalidOperationException("crawl-client-not-created"))
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var headers = EvidenceSanitizer.SelectSafeResponseHeaders(
                        response.Headers,
                        response.Content.Headers);
                    var status = (int)response.StatusCode;
                    if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                    {
                        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                        redirects.Add(new RedirectHop(
                            EvidenceSanitizer.RedactUrl(current.AbsoluteUri),
                            status,
                            EvidenceSanitizer.RedactUrl(next.AbsoluteUri)));
                        var redirectDecision = await _endpointPolicy.AuthorizeAsync(
                            new EndpointRequest(next, "GET", "document", true, true),
                            cancellationToken);
                        if (!redirectDecision.Allowed || !IsSameHost(next))
                        {
                            return new HttpResult(
                                status,
                                EvidenceSanitizer.RedactUrl(next.AbsoluteUri),
                                redirects,
                                response.Content.Headers.ContentType?.ToString(),
                                null,
                                headers,
                                redirectDecision.Allowed
                                    ? "outside-origin-redirect"
                                    : redirectDecision.ReasonCode);
                        }

                        current = next;
                        continue;
                    }

                    var body = await ReadCappedAsync(response.Content, _options.MaxAssetBytes, cancellationToken);
                    return new HttpResult(
                        status,
                        EvidenceSanitizer.RedactUrl(current.AbsoluteUri),
                        redirects,
                        response.Content.Headers.ContentType?.ToString(),
                        body,
                        headers,
                        null);
                }

                return new HttpResult(
                    null,
                    EvidenceSanitizer.RedactUrl(current.AbsoluteUri),
                    redirects,
                    null,
                    null,
                    new Dictionary<string, string>(),
                    "redirect-limit-exceeded");
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (CaptureFailureEvidence.FindInnerException<CaptureSafetyException>(
                        ex) is { } safety)
                {
                    throw safety;
                }

                var socketReason = GetSocketFailureReason(ex);
                if (socketReason is null
                    && ex is not (HttpRequestException
                        or TaskCanceledException
                        or IOException))
                {
                    throw;
                }

                if (attempt == 1)
                {
                    return new HttpResult(
                        null,
                        EvidenceSanitizer.RedactUrl(current.AbsoluteUri),
                        redirects,
                        null,
                        null,
                        new Dictionary<string, string>(),
                        socketReason
                            ?? $"crawl-request-failed:{ex.GetType().Name}");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("Unreachable retry state.");
    }

    internal static string? GetSocketFailureReason(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return CaptureFailureEvidence.FindInnerException<SocketException>(
            exception) is { } socket
                ? $"socket-{socket.SocketErrorCode}"
                : null;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new IOException($"Response exceeds configured {maxBytes} byte capture limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private bool IsSitemapDiscovered(string url) =>
        _retainedSitemapRouteUrls.Contains(
            NormalizePublicUrl(new Uri(url)));

    private bool IsSameHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && !EvidenceSanitizer.HasSensitiveQueryParameter(uri)
        && string.Equals(uri.Host, _profile.PrimaryOrigin.Host, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePublicUrl(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.Normalize(NormalizationForm.FormC);
    }

    private static string NormalizePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Normalize(NormalizationForm.FormC);
        if (!path.StartsWith('/'))
        {
            path = $"/{path}";
        }

        return string.IsNullOrEmpty(uri.Query) ? path : $"{path}{uri.Query}";
    }

    private static bool TryResolvePublicUri(string sourceUrl, string value, out string resolved)
    {
        resolved = string.Empty;
        if (value.StartsWith('#') || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(new Uri(sourceUrl), value, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || EvidenceSanitizer.HasSensitiveQueryParameter(uri))
        {
            return false;
        }

        resolved = NormalizePublicUrl(uri);
        return true;
    }

    internal static string? SanitizeExtractedUrl(string sourceUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !TryResolvePublicUri(sourceUrl, value, out var resolved))
        {
            return null;
        }

        return EvidenceSanitizer.RedactUrl(resolved);
    }

    private static string DecodeBody(byte[] body, string? contentType)
    {
        var charset = Regex.Match(contentType ?? string.Empty, @"charset\s*=\s*([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim('"', '\'');
        try
        {
            return charset.Length > 0 ? Encoding.GetEncoding(charset).GetString(body) : Encoding.UTF8.GetString(body);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    private static void CollectJsonLdTypes(JsonElement element, ISet<string> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("@type"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                    {
                        output.Add(value);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String))
                        {
                            if (item.GetString() is { } itemValue)
                            {
                                output.Add(itemValue);
                            }
                        }
                    }
                }

                CollectJsonLdTypes(property.Value, output);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectJsonLdTypes(item, output);
            }
        }
    }

    public static string ClassifyTemplate(string path)
    {
        if (path == "/")
        {
            return "home";
        }

        if (path.StartsWith("/event/", StringComparison.OrdinalIgnoreCase))
        {
            return "event-detail";
        }

        if (Regex.IsMatch(path, @"/(?:duas?(?:-|/)|hadith(?:-|/)|ziarat(?:-|/)|monday-rites(?:/|$)|[^/]*-rites(?:/|$))", RegexOptions.IgnoreCase))
        {
            return "religious-content";
        }

        if (Regex.IsMatch(path, @"/(donate|pledge|fundraiser)", RegexOptions.IgnoreCase))
        {
            return "donation-form";
        }

        if (path.Contains("contact", StringComparison.OrdinalIgnoreCase))
        {
            return "contact-form";
        }

        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || path.Contains("ical", StringComparison.OrdinalIgnoreCase))
        {
            return "machine-feed";
        }

        return "content-page";
    }

    private static string ClassifyAsset(string path, string? contentType)
    {
        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return "image";
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return "audio";
        if (contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return "video";
        if (contentType?.Contains("font", StringComparison.OrdinalIgnoreCase) == true) return "font";
        if (contentType?.Contains("css", StringComparison.OrdinalIgnoreCase) == true) return "stylesheet";
        if (contentType?.Contains("javascript", StringComparison.OrdinalIgnoreCase) == true) return "script";
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "document";
        return "asset";
    }

    public static string? ClassifyAssetRelation(string tag, string attributes, string path)
    {
        if (tag is "img") return "image";
        if (tag is "source") return "media-source";
        if (tag is "video") return "video";
        if (tag is "audio") return "audio";
        if (tag is "script") return "script";
        if (tag is "link")
        {
            var rel = ExtractAttribute(attributes, "rel") ?? string.Empty;
            var asValue = ExtractAttribute(attributes, "as") ?? string.Empty;
            if (rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(value => value is "stylesheet" or "icon" or "manifest"))
            {
                return rel.Contains("icon", StringComparison.OrdinalIgnoreCase) ? "icon" : rel.ToLowerInvariant();
            }

            if (rel.Contains("preload", StringComparison.OrdinalIgnoreCase) && asValue is "font" or "image" or "style" or "script")
            {
                return $"preload-{asValue}";
            }

            return null;
        }

        if (tag is "a" && (path.Contains("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(path, @"\.(?:pdf|mp3|m4a|wav|mp4|webm|jpe?g|png|gif|webp|svg)$", RegexOptions.IgnoreCase)))
        {
            return "download";
        }

        return null;
    }

    public static bool IsApprovedAssetContentType(string? contentType, string path)
    {
        if (contentType is not null && (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("font", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("css", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("manifest", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return contentType is null && Regex.IsMatch(path, @"\.(?:pdf|mp3|m4a|wav|mp4|webm|jpe?g|png|gif|webp|svg|css|js|woff2?|ttf|otf)$", RegexOptions.IgnoreCase);
    }

    private static string ClassifyOwnership(Uri uri)
    {
        if (uri.AbsolutePath.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase)) return "same-origin-upload";
        if (uri.AbsolutePath.StartsWith("/wp-content/plugins/", StringComparison.OrdinalIgnoreCase)) return "same-origin-plugin";
        if (uri.AbsolutePath.StartsWith("/wp-content/themes/", StringComparison.OrdinalIgnoreCase)) return "same-origin-theme";
        return "same-origin-core";
    }

    private void AddAssetDiscovery(string url, string sourcePage, string relation)
    {
        if (!_assetSourcePages.TryGetValue(url, out var sourcePages))
        {
            sourcePages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _assetSourcePages[url] = sourcePages;
        }

        if (sourcePages.Add(sourcePage))
        {
            _assetReferenceCount++;
            EnsureDiscoveryCountWithinLimit(
                "asset-reference",
                _assetReferenceCount,
                MaximumAssetReferenceCount);
        }

        if (!_assetDiscoveries.ContainsKey(url))
        {
            _assetDiscoveries[url] = new AssetDiscovery(url, sourcePage, relation);
            EnsureDiscoveryCountWithinLimit(
                "asset",
                _assetDiscoveries.Count,
                MaximumAssetDiscoveryCount);
        }
    }

    internal static string[] SelectAssetKeysForSourcePage(
        List<AssetRecord> assets,
        Dictionary<string, HashSet<string>> assetSourcePages,
        string sourcePage) =>
        SelectAssetsForSourcePage(assets, assetSourcePages, sourcePage)
            .Select(asset => asset.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string[] SelectMediaRefsForSourcePage(
        List<AssetRecord> assets,
        Dictionary<string, HashSet<string>> assetSourcePages,
        string sourcePage) =>
        SelectAssetsForSourcePage(assets, assetSourcePages, sourcePage)
            .Where(asset => asset.Kind != "rejected")
            .Select(asset => MediaCandidateKey(asset.Url))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<AssetRecord> SelectAssetsForSourcePage(
        List<AssetRecord> assets,
        Dictionary<string, HashSet<string>> assetSourcePages,
        string sourcePage) =>
        assets.Where(asset =>
            string.Equals(
                asset.SourcePage,
                sourcePage,
                StringComparison.OrdinalIgnoreCase));

    internal static void EnsureDiscoveryCountWithinLimit(
        string kind,
        int count,
        int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (count < 0 || maximum <= 0)
        {
            throw new ArgumentOutOfRangeException(
                count < 0 ? nameof(count) : nameof(maximum));
        }

        if (count > maximum)
        {
            throw new CaptureSafetyException($"{kind}-discovery-limit-exceeded");
        }
    }

    private static ImportCandidate CreateCandidate(
        string captureId,
        string kind,
        string identity,
        string checksum,
        string? sourceUri,
        string? canonicalPath,
        string payloadRef,
        string decision,
        string? reason,
        IReadOnlyList<string> mediaRefs,
        IReadOnlyList<string> dependencyKeys) =>
        new(
            kind == "media" ? MediaCandidateKey(identity) : CandidateSourceKey(kind, identity),
            captureId,
            checksum,
            kind,
            sourceUri,
            CaptureIO.StableKey($"target-{kind}", identity),
            canonicalPath,
            payloadRef,
            mediaRefs,
            dependencyKeys,
            null,
            decision,
            reason);

    private static string CandidateSourceKey(string kind, string identity) => CaptureIO.StableKey("source", $"{kind}:{identity}");

    public static string MediaCandidateKey(string canonicalUrl) =>
        CandidateSourceKey("media", new Uri(canonicalUrl, UriKind.Absolute).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));

    private static string HashRecord<T>(T value) => CaptureIO.Sha256(JsonSerializer.SerializeToUtf8Bytes(value, CaptureIO.JsonOptions));

    public static ReligiousContentRecord ExtractReligiousContent(string url, string html, string sourceChecksum)
    {
        var paragraphs = new List<ReligiousParagraph>();
        foreach (Match match in Regex.Matches(html, @"<div\b(?<attrs>[^>]*class\s*=\s*[""'][^""']*\b(?<role>Ara|Trl|Tra)\b[^""']*[""'][^>]*)>(?<text>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var roleCode = match.Groups["role"].Value;
            var text = CleanHtmlText(match.Groups["text"].Value).Normalize(NormalizationForm.FormC);
            if (text.Length == 0)
            {
                continue;
            }

            var role = roleCode.Equals("Ara", StringComparison.OrdinalIgnoreCase) ? "arabic"
                : roleCode.Equals("Trl", StringComparison.OrdinalIgnoreCase) ? "transliteration"
                : "english-translation";
            paragraphs.Add(new ReligiousParagraph(
                paragraphs.Count,
                role,
                text,
                role == "arabic" ? "rtl" : "ltr",
                role == "arabic" ? "ar" : role == "transliteration" ? "ar-Latn" : "en",
                CaptureIO.Sha256(Encoding.UTF8.GetBytes(text))));
        }

        var audioReferences = Regex.Matches(html, @"<(?:source|audio|a)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => ExtractAttribute(match.Groups["attrs"].Value, match.Value.StartsWith("<a", StringComparison.OrdinalIgnoreCase) ? "href" : "src"))
            .Where(value => value is not null && Regex.IsMatch(value, @"\.(?:mp3|m4a|wav)(?:\?|$)", RegexOptions.IgnoreCase))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var title = ExtractElementText(html, "h1") ?? ExtractElementText(html, "title") ?? new Uri(url).AbsolutePath;
        var fixtureChecksum = HashRecord(new { Route = NormalizePath(new Uri(url)), Paragraphs = paragraphs, Audio = audioReferences });
        return new ReligiousContentRecord(
            NormalizePath(new Uri(url)),
            title,
            "religious-content",
            $"raw/html/{CaptureIO.SafeFileName(new Uri(url).PathAndQuery)}.html",
            sourceChecksum,
            paragraphs,
            audioReferences,
            fixtureChecksum);
    }

    private static string? ExtractMeta(string html, string keyAttribute, string keyValue)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = tag.Groups["attrs"].Value;
            if (string.Equals(ExtractAttribute(attrs, keyAttribute), keyValue, StringComparison.OrdinalIgnoreCase))
            {
                return ExtractAttribute(attrs, "content");
            }
        }

        return null;
    }

    private static string? ExtractLink(string html, string rel)
    {
        foreach (Match tag in Regex.Matches(html, @"<link\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = tag.Groups["attrs"].Value;
            if ((ExtractAttribute(attrs, "rel") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                return ExtractAttribute(attrs, "href");
            }
        }

        return null;
    }

    private static string? ExtractTagAttribute(string html, string tagName, string attribute)
    {
        var match = Regex.Match(html, $@"<{Regex.Escape(tagName)}\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? ExtractAttribute(match.Groups["attrs"].Value, attribute) : null;
    }

    private static string? ExtractElementText(string html, string tagName)
    {
        var match = Regex.Match(html, $@"<{Regex.Escape(tagName)}\b[^>]*>(?<text>.*?)</{Regex.Escape(tagName)}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanHtmlText(match.Groups["text"].Value) : null;
    }

    private static string? ExtractAttribute(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $@"(?:^|\s){Regex.Escape(name)}\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["dq"].Success ? match.Groups["dq"].Value :
            match.Groups["sq"].Success ? match.Groups["sq"].Value : match.Groups["bare"].Value;
        return WebUtility.HtmlDecode(value).Trim();
    }

    private static string? ExtractLabel(string html, string id)
    {
        foreach (Match label in Regex.Matches(html, @"<label\b(?<attrs>[^>]*)>(?<text>.*?)</label>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (string.Equals(ExtractAttribute(label.Groups["attrs"].Value, "for"), id, StringComparison.Ordinal))
            {
                return CleanHtmlText(label.Groups["text"].Value);
            }
        }

        return null;
    }

    private static string CleanHtmlText(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
    }

    private static ScreenshotRecord FailedScreenshot(
        string templateKey,
        string url,
        CaptureViewport viewport,
        string status,
        string error) =>
        new(
            templateKey, url, viewport.Name, viewport.Width, viewport.Height, null, null, status, error,
            false, 0, false, 0, 0, 0, 0, templateKey, "fail", 0, false, false,
            Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0,
            Array.Empty<string>(), Array.Empty<ScreenshotReadinessSample>(), false,
            "screenshot-network-decisions.json", "screenshot-capture-provenance.json",
            ["capture-incomplete"], 0, [status], null);
    private string PathFor(string relative) => Path.Combine(_options.OutputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        _client?.Dispose();
    }

    private sealed record HttpResult(
        int? Status,
        string? FinalUrl,
        IReadOnlyList<RedirectHop> Redirects,
        string? ContentType,
        byte[]? Body,
        IReadOnlyDictionary<string, string> Headers,
        string? Error);

    private sealed record AssetDiscovery(string Url, string SourcePage, string Relation);

    private sealed class NavigationContext(string attributes)
    {
        public string Attributes { get; } = attributes;
        public string? Key { get; set; }
        public string? Label { get; set; }
    }
}
