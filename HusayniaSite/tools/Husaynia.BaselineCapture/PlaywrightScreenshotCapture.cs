using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace Husaynia.BaselineCapture;

public sealed record ScreenshotPolicyDecision(bool Allowed, string ReasonCode);

public static class ScreenshotNetworkPolicy
{
    public static string Version => CaptureProfile.Approved.PolicyVersion;
    public static readonly string[] AllowedMethods = ["GET", "HEAD"];
    public static readonly string[] AllowedStaticResourceTypes = ["stylesheet", "font", "image"];
    public static readonly string[] BlockedCapabilities =
    [
        "form-submission", "payment-hosts", "analytics", "trackers", "websockets",
        "eventsource", "sendbeacon", "service-workers", "webrtc", "webtransport",
        "direct-sockets", "payment-request", "downloads", "popups", "unexpected-navigation",
        "cookies"
    ];

    public static readonly IReadOnlySet<string> StaticHosts = CaptureProfile.Approved.StaticResources
        .Select(rule => rule.Host)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] BlockedHostFragments =
    [
        "google-analytics", "googletagmanager", "doubleclick", "facebook.com", "connect.facebook",
        "stripe.com", "paypal.com", "squareup.com", "sentry.io", "hotjar", "clarity.ms"
    ];

    public static ScreenshotPolicyDecision Decide(
        string method,
        string rawUrl,
        string resourceType,
        bool isNavigationRequest,
        string expectedMainDocumentUrl)
    {
        if (!AllowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "method-not-allowed");
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return new(false, "invalid-or-non-https-url");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new(false, "credentials-not-allowed");
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            return new(false, "ip-literal-not-allowed");
        }

        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443)
        {
            return new(false, "invalid-or-non-https-url");
        }

        if (EvidenceSanitizer.HasSensitiveQueryParameter(uri))
        {
            return new(false, "sensitive-query-parameter");
        }

        if (BlockedHostFragments.Any(fragment => uri.Host.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return new(false, "tracker-or-payment-host");
        }

        var source = new Uri(expectedMainDocumentUrl);
        var sameOrigin = uri.Host.Equals(CaptureProfile.Approved.PrimaryOrigin.Host, StringComparison.OrdinalIgnoreCase);
        if (isNavigationRequest && resourceType == "document"
            && !StripFragment(uri).Equals(StripFragment(source), StringComparison.Ordinal))
        {
            return new(false, "unexpected-document-navigation");
        }

        if (sameOrigin)
        {
            if (EndpointSafetyClassifier.IsSensitiveEndpoint(uri))
            {
                return new(false, "form-or-payment-endpoint");
            }

            return new(true, "same-origin-get-head");
        }

        if (!StaticHosts.Contains(uri.Host))
        {
            return new(false, "origin-not-allowlisted");
        }

        if (!AllowedStaticResourceTypes.Contains(resourceType, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "static-host-resource-type-not-allowed");
        }

        return new(true, "allowlisted-static-resource");
    }

    public static string RedactUrl(string rawUrl) => EvidenceSanitizer.RedactUrl(rawUrl);

    private static string StripFragment(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
}

public sealed record PlaywrightCaptureResult(
    ScreenshotRecord Screenshot,
    IReadOnlyList<ScreenshotNetworkDecision> NetworkDecisions);

public sealed class PlaywrightScreenshotCapture : IAsyncDisposable
{
    internal static string BrowserExecutableIdentity =>
        $"playwright-chromium-{CaptureProfile.ChromiumRevision}";

    internal const string BrowserWorkingDirectoryAttestation =
        "isolated-os-temp-directory";

    public static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MaximumReadinessDuration = TimeSpan.FromSeconds(30);
    public const int MaximumNavigationAttempts = 3;
    public const float NavigationAttemptTimeoutMilliseconds = 10_000;
    public const int InterCaptureDelayMilliseconds = 3_000;

    private readonly string _captureId;
    private readonly string _outputDirectory;
    private readonly CaptureProfile _profile;
    private readonly TrustedEndpointPolicy _endpointPolicy;
    private VerifiedBrowserSession _browserSession;
    private IReadOnlyList<string> _activeBrowserResolverRules;
    private readonly List<DnsContextEpochEvidence> _dnsContextEpochs = [];
    private readonly List<BrowserLaunchEvidence> _browserLaunches = [];

    private PlaywrightScreenshotCapture(
        string captureId,
        string outputDirectory,
        CaptureProfile profile,
        TrustedEndpointPolicy endpointPolicy,
        VerifiedBrowserSession browserSession,
        ScreenshotCaptureProvenance provenance)
    {
        _captureId = captureId;
        _outputDirectory = outputDirectory;
        _profile = profile;
        _endpointPolicy = endpointPolicy;
        _browserSession = browserSession;
        _activeBrowserResolverRules = endpointPolicy.ChromiumHostResolverRules;
        _browserLaunches.AddRange(provenance.BrowserLaunches);
        Provenance = provenance;
    }

    public ScreenshotCaptureProvenance Provenance { get; private set; }

    public static async Task<PlaywrightScreenshotCapture> CreateAsync(
        string captureId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var profile = CaptureProfile.Approved;
        var policy = await TrustedEndpointPolicy.CreateAsync(
            profile,
            new SystemTrustedDnsResolver(),
            cancellationToken);
        return await CreateAsync(captureId, outputDirectory, profile, policy, cancellationToken);
    }

    public static Task<PlaywrightScreenshotCapture> CreateAsync(
        string captureId,
        string outputDirectory,
        TrustedEndpointPolicy endpointPolicy,
        CancellationToken cancellationToken) =>
        CreateAsync(captureId, outputDirectory, CaptureProfile.Approved, endpointPolicy, cancellationToken);

    private static async Task<PlaywrightScreenshotCapture> CreateAsync(
        string captureId,
        string outputDirectory,
        CaptureProfile profile,
        TrustedEndpointPolicy endpointPolicy,
        CancellationToken cancellationToken)
    {
        var session = await VerifiedBrowserSession.CreateAsync(
            profile,
            endpointPolicy.ChromiumHostResolverRules,
            cancellationToken);
        try
        {
            var lockPath = FindRepositoryFile(
                Path.Combine("tools", "Husaynia.BaselineCapture", "packages.lock.json"));
            var lockHash = lockPath is not null
                ? await CaptureIO.Sha256FileAsync(lockPath, cancellationToken)
                : "lock-unavailable";
            var verification = session.Verification;
            var provenance = CreateProvenance(
                captureId,
                DateTimeOffset.UtcNow,
                session.Browser.Version,
                verification,
                lockHash,
                endpointPolicy.ApprovedDnsAnswers,
                endpointPolicy.ChromiumHostResolverRules)
                with
                {
                    BrowserLaunches =
                    [
                        CreateBrowserLaunchEvidence(
                            session,
                            previousBrowserInstanceId: null,
                            "bootstrap")
                    ]
                };
            return new(
                captureId,
                Path.GetFullPath(outputDirectory),
                profile,
                endpointPolicy,
                session,
                provenance);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static ScreenshotCaptureProvenance CreateProvenance(
        string captureId,
        DateTimeOffset capturedAtUtc,
        string chromiumVersion,
        BrowserLaunchVerification verification,
        string lockHash,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pinnedDnsAnswers,
        IReadOnlyList<string> chromiumHostResolverRules) =>
        new(
            captureId,
            capturedAtUtc,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadDotNetSdkVersion(),
            CaptureProfile.PlaywrightVersion,
            chromiumVersion,
            BrowserExecutableIdentity,
            "pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium",
            $"{RuntimeInformation.OSDescription}|{RuntimeInformation.ProcessArchitecture}|{lockHash}|{CaptureProfile.PlaywrightVersion}",
            CaptureProfile.Approved.PolicyVersion,
            MaximumNavigationAttempts,
            (int)(NavigationAttemptTimeoutMilliseconds / 1000),
            (int)MaximumReadinessDuration.TotalSeconds,
            (int)MaximumCaptureDuration.TotalSeconds,
            InterCaptureDelayMilliseconds,
            true,
            CaptureProfile.ChromiumRevision,
            verification.ExpectedSha256,
            verification.ActualSha256,
            verification.ChromiumSandbox,
            verification.ChildEnvironment.Keys.Order(StringComparer.Ordinal).ToArray(),
            BrowserWorkingDirectoryAttestation,
            false,
            CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority),
            pinnedDnsAnswers,
            chromiumHostResolverRules,
            false,
            false,
            false,
            false,
            false)
        {
            BrowserExecutableOutsideWorkspace = true,
            BrowserWorkingDirectoryOutsideWorkspace = true,
            BrowserWorkingDirectoryEmpty = true,
            ChildEnvironmentSanitized = true
        };

    public Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        CancellationToken cancellationToken) =>
        CaptureAsync(
            templateKey,
            url,
            viewportName,
            width,
            height,
            controlledRunDonationFormCount: null,
            cancellationToken);

    public async Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int? controlledRunDonationFormCount,
        CancellationToken cancellationToken)
    {
        if (!_profile.Representatives.TryGetValue(templateKey, out var representative)
            || !SameDocumentUrl(representative, new Uri(url)))
        {
            return new(
                FailedScreenshot(
                    templateKey,
                    url,
                    viewportName,
                    width,
                    height,
                    "representative-not-approved",
                    0,
                    ["representative-not-approved"],
                    controlledRunDonationFormCount),
                []);
        }

        var lifecycleFactory = new ScreenshotAttemptLifecycleFactory();
        return await RunCaptureAttemptsAsync(
            templateKey,
            representative.AbsoluteUri,
            viewportName,
            width,
            height,
            controlledRunDonationFormCount,
            (attempt, token) => CaptureAttemptAsync(
                templateKey,
                representative.AbsoluteUri,
                viewportName,
                width,
                height,
                attempt,
                lifecycleFactory,
                controlledRunDonationFormCount,
                token),
            static _ => Task.CompletedTask,
            Task.Delay,
            cancellationToken);
    }

    internal static async Task<PlaywrightCaptureResult> RunCaptureAttemptsAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int? controlledRunDonationFormCount,
        Func<int, CancellationToken, Task<PlaywrightCaptureResult>>
            captureAttemptAsync,
        Func<CancellationToken, Task> assertDnsSetsUnchangedAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureAttemptAsync);
        ArgumentNullException.ThrowIfNull(assertDnsSetsUnchangedAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);
        var allDecisions = new List<ScreenshotNetworkDecision>();
        var attemptReasons = new List<string>();
        ScreenshotRecord? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumNavigationAttempts; attempt++)
        {
            try
            {
                await assertDnsSetsUnchangedAsync(cancellationToken);
            }
            catch (CaptureSafetyException ex)
            {
                attemptReasons.Add(ex.ReasonCode);
                return new(
                    FailedScreenshot(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        ex.ReasonCode,
                        attempt,
                        attemptReasons,
                        controlledRunDonationFormCount),
                    allDecisions);
            }

            PlaywrightCaptureResult outcome;
            try
            {
                outcome = await captureAttemptAsync(
                    attempt,
                    cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw CreateCancellationException(
                    templateKey,
                    url,
                    viewportName,
                    width,
                    height,
                    attempt,
                    controlledRunDonationFormCount,
                    lastFailure,
                    attemptReasons,
                    allDecisions,
                    cancellationToken,
                    exception);
            }

            allDecisions.AddRange(outcome.NetworkDecisions);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new ScreenshotCaptureCanceledException(
                    new PlaywrightCaptureResult(
                        outcome.Screenshot with
                        {
                            AttemptCount = attempt,
                            AttemptReasonCodes = attemptReasons
                                .Append("capture-cancelled")
                                .Distinct(StringComparer.Ordinal)
                                .ToArray()
                        },
                        allDecisions
                            .OrderBy(item => item.TimestampUtc)
                            .ToArray()),
                    cancellationToken);
            }

            if (outcome.Screenshot.Status == "captured")
            {
                return new(
                    outcome.Screenshot with
                    {
                        AttemptCount = attempt,
                        AttemptReasonCodes = attemptReasons.ToArray()
                    },
                    allDecisions.OrderBy(item => item.TimestampUtc).ToArray());
            }

            lastFailure = outcome.Screenshot;
            attemptReasons.AddRange(outcome.Screenshot.QualityReasonCodes);
            if (attempt < MaximumNavigationAttempts)
            {
                try
                {
                    await delayAsync(
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw CreateCancellationException(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        attempt,
                        controlledRunDonationFormCount,
                        lastFailure,
                        attemptReasons,
                        allDecisions,
                        cancellationToken,
                        exception);
                }
            }
        }

        return new(
            (lastFailure ?? FailedScreenshot(
                templateKey,
                url,
                viewportName,
                width,
                height,
                "capture-attempts-exhausted",
                MaximumNavigationAttempts,
                attemptReasons,
                controlledRunDonationFormCount)) with
            {
                AttemptCount = MaximumNavigationAttempts,
                AttemptReasonCodes = attemptReasons.Distinct(StringComparer.Ordinal).ToArray()
            },
            allDecisions.OrderBy(item => item.TimestampUtc).ToArray());
    }

    private static ScreenshotCaptureCanceledException CreateCancellationException(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int attempt,
        int? controlledRunDonationFormCount,
        ScreenshotRecord? lastFailure,
        IReadOnlyList<string> attemptReasons,
        IReadOnlyList<ScreenshotNetworkDecision> allDecisions,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        var reasons = attemptReasons
            .Append("capture-cancelled")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var screenshot = (lastFailure ?? FailedScreenshot(
            templateKey,
            url,
            viewportName,
            width,
            height,
            "capture-cancelled",
            attempt,
            reasons,
            controlledRunDonationFormCount)) with
        {
            Status = "capture-cancelled",
            Error = "capture-cancelled",
            QualityStatus = "fail",
            QualityReasonCodes = reasons,
            AttemptCount = attempt,
            AttemptReasonCodes = reasons
        };
        return new ScreenshotCaptureCanceledException(
            new PlaywrightCaptureResult(
                screenshot,
                allDecisions
                    .OrderBy(item => item.TimestampUtc)
                    .ToArray()),
            cancellationToken,
            innerException);
    }

    public async ValueTask DisposeAsync() => await _browserSession.DisposeAsync();

    private async Task EnsureBrowserForContextPolicyAsync(
        TrustedContextNetworkPolicy contextPolicy,
        CancellationToken cancellationToken)
    {
        var previousSession = _browserSession;
        var result = await EnsureBrowserForContextPolicyAsync(
            _browserSession,
            _activeBrowserResolverRules,
            contextPolicy,
            static session => session.DisposeAsync(),
            (rules, token) => VerifiedBrowserSession.CreateAsync(
                _profile,
                rules,
                token),
            cancellationToken);
        _browserSession = result.Session;
        _activeBrowserResolverRules = result.ResolverRules;
        if (!ReferenceEquals(previousSession, result.Session))
        {
            RecordBrowserLaunch(CreateBrowserLaunchEvidence(
                result.Session,
                previousSession.InstanceId,
                "resolver-map-changed"));
        }
    }

    internal static async Task<(TSession Session, IReadOnlyList<string> ResolverRules)>
        EnsureBrowserForContextPolicyAsync<TSession>(
        TSession activeSession,
        IReadOnlyList<string> activeResolverRules,
        TrustedContextNetworkPolicy contextPolicy,
        Func<TSession, ValueTask> disposeAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task<TSession>> createAsync,
        CancellationToken cancellationToken)
    {
        if (activeResolverRules.SequenceEqual(
                contextPolicy.ChromiumHostResolverRules,
                StringComparer.Ordinal))
        {
            return (activeSession, activeResolverRules);
        }

        await disposeAsync(activeSession);
        var replacement = await createAsync(
            contextPolicy.ChromiumHostResolverRules,
            cancellationToken);
        return (replacement, contextPolicy.ChromiumHostResolverRules);
    }

    private static DnsContextEpochEvidence CreateDnsEpochEvidence(
        TrustedContextNetworkPolicy contextPolicy,
        string captureKey,
        int attempt,
        string browserInstanceId,
        string browserContextId) =>
        new(
            contextPolicy.Epoch,
            captureKey,
            attempt,
            browserInstanceId,
            browserContextId,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            contextPolicy.Bindings.Values
                .OrderBy(binding => binding.Host, StringComparer.Ordinal)
                .Select(binding => new DnsPinBindingEvidence(
                    binding.Host,
                    binding.HostClass.ToString(),
                    binding.SelectedAddress.ToString(),
                    binding.CompleteObservedPublicSet
                        .Select(address => address.ToString())
                        .ToArray(),
                    binding.AnswerSetSha256,
                    binding.ObservedAtUtc,
                    binding.Rotated))
                .ToArray());

    private static BrowserLaunchEvidence CreateBrowserLaunchEvidence(
        VerifiedBrowserSession session,
        string? previousBrowserInstanceId,
        string reason) =>
        new(
            session.InstanceId,
            session.LaunchedAtUtc,
            session.ResolverMapSha256,
            previousBrowserInstanceId,
            reason);

    private void RecordBrowserLaunch(BrowserLaunchEvidence evidence)
    {
        _browserLaunches.Add(evidence);
        Provenance = Provenance with
        {
            BrowserLaunches = _browserLaunches.ToArray()
        };
    }

    private void RecordDnsEpoch(DnsContextEpochEvidence evidence)
    {
        var index = _dnsContextEpochs.FindIndex(item => item.Epoch == evidence.Epoch);
        if (index < 0)
        {
            _dnsContextEpochs.Add(evidence);
        }
        else
        {
            _dnsContextEpochs[index] = evidence;
        }

        Provenance = Provenance with
        {
            DnsContextEpochs = _dnsContextEpochs
                .OrderBy(item => item.Epoch)
                .ToArray()
        };
    }

    public static bool HasStableReadyWindow(IReadOnlyList<ScreenshotReadinessSample> samples)
    {
        if (samples.Count < 3)
        {
            return false;
        }

        var window = samples.TakeLast(3).ToArray();
        return window.DistinctBy(GeometryKey).Count() == 1
            && window.All(sample => sample.FontsReady
                && sample.IncompleteImageCount == 0
                && sample.InFlightAllowedRequests == 0);
    }

    private async Task<PlaywrightCaptureResult> CaptureAttemptAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int attempt,
        ScreenshotAttemptLifecycleFactory lifecycleFactory,
        int? controlledRunDonationFormCount,
        CancellationToken cancellationToken)
    {
        var captureKey = $"{templateKey}|{viewportName}";
        var contextPolicy = await _endpointPolicy.CreateContextPolicyAsync(cancellationToken);
        await EnsureBrowserForContextPolicyAsync(contextPolicy, cancellationToken);
        var browserSession = _browserSession;
        var browserContextId = Guid.NewGuid().ToString("N");
        var epochEvidence = CreateDnsEpochEvidence(
            contextPolicy,
            captureKey,
            attempt,
            browserSession.InstanceId,
            browserContextId);
        RecordDnsEpoch(epochEvidence);
        var decisions = new ConcurrentQueue<ScreenshotNetworkDecision>();
        var ledger = new RequestLedger();
        var trackedRequests = new ConcurrentDictionary<IRequest, TrackedRequest>();
        var decisionSequence = 0;
        var initialNavigationComplete = 0;
        var firstMainDocumentSeen = 0;

        var context = await browserSession.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ScreenSize = new ScreenSize { Width = width, Height = height },
            DeviceScaleFactor = 1,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
            IgnoreHTTPSErrors = false,
            Locale = "en-US",
            TimezoneId = "America/Los_Angeles",
            ColorScheme = ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce
        }).WaitAsync(cancellationToken);
        await using var contextLifetime = new AttemptResourceLifetime(context.DisposeAsync);
        context.SetDefaultTimeout(30_000);
        context.SetDefaultNavigationTimeout(30_000);

        await context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var requestId = $"{captureKey}:attempt:{attempt}:request:{Interlocked.Increment(ref decisionSequence)}";
            var isMainFrame = request.IsNavigationRequest
                && request.ResourceType == "document"
                && request.Frame == request.Frame.Page.MainFrame;
            EndpointDecision policy;
            try
            {
                policy = await _endpointPolicy.AuthorizeAsync(
                    contextPolicy,
                    new EndpointRequest(
                        new Uri(request.Url),
                        request.Method,
                        request.ResourceType,
                        request.IsNavigationRequest,
                        isMainFrame),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is UriFormatException or CaptureSafetyException)
            {
                policy = new(false, "unclassified-request", null);
            }

            if (policy.Allowed && isMainFrame)
            {
                if (Interlocked.CompareExchange(ref firstMainDocumentSeen, 1, 0) == 0
                    && !SameDocumentUrl(new Uri(url), new Uri(request.Url)))
                {
                    policy = new(false, "unexpected-initial-navigation", policy.PinnedAddress);
                }
                else if (Volatile.Read(ref initialNavigationComplete) != 0)
                {
                    policy = new(false, "post-load-document-navigation", policy.PinnedAddress);
                }
            }

            if (policy.Allowed)
            {
                var cookieReason = BrowserCookiePolicy.ValidateRequestHeaders(
                    await request.AllHeadersAsync());
                if (cookieReason is not null)
                {
                    policy = new(false, cookieReason, policy.PinnedAddress);
                }
            }

            var decision = Decision(
                captureKey,
                requestId,
                "request",
                request.Method,
                request.Url,
                request.ResourceType,
                request.IsNavigationRequest,
                policy.Allowed ? "allow" : "block",
                policy.ReasonCode,
                attempt: attempt,
                isMainFrame: isMainFrame,
                pinnedAddress: policy.PinnedAddress,
                dnsEpoch: contextPolicy.Epoch,
                browserInstanceId: browserSession.InstanceId,
                browserContextId: browserContextId);
            ledger.RegisterDecision(requestId, decision);
            decisions.Enqueue(decision);
            trackedRequests.TryAdd(
                request,
                new TrackedRequest(
                    requestId,
                    policy.Allowed,
                    isMainFrame,
                    policy.PinnedAddress,
                    request.Method,
                    request.Url,
                    request.ResourceType,
                    request.IsNavigationRequest));
            if (!policy.Allowed)
            {
                decisions.Enqueue(Decision(
                    captureKey,
                    requestId,
                    "failure",
                    request.Method,
                    request.Url,
                    request.ResourceType,
                    request.IsNavigationRequest,
                    "block",
                    policy.ReasonCode,
                    failure: "blocked-by-policy",
                    attempt: attempt,
                    isMainFrame: isMainFrame,
                    pinnedAddress: policy.PinnedAddress,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
                await route.AbortAsync("blockedbyclient");
                return;
            }

            try
            {
                await route.ContinueAsync();
            }
            catch (PlaywrightException)
            {
                if (ledger.RecordFailure(
                        requestId,
                        "allowed-request-continue-failed",
                        "playwright-route-continue-failed"))
                {
                    decisions.Enqueue(Decision(
                        captureKey,
                        requestId,
                        "failure",
                        request.Method,
                        request.Url,
                        request.ResourceType,
                        request.IsNavigationRequest,
                        "allow",
                        "allowed-request-continue-failed",
                        failure: "playwright-route-continue-failed",
                        attempt: attempt,
                        isMainFrame: isMainFrame,
                        pinnedAddress: policy.PinnedAddress,
                        dnsEpoch: contextPolicy.Epoch,
                        browserInstanceId: browserSession.InstanceId,
                        browserContextId: browserContextId));
                }

                throw;
            }
        });

        context.Response += (_, response) =>
        {
            if (!trackedRequests.TryGetValue(response.Request, out var tracked))
            {
                var untrackedId = $"{captureKey}:attempt:{attempt}:untracked-response:{Interlocked.Increment(ref decisionSequence)}";
                ledger.RecordResponse(untrackedId, response.Status);
                decisions.Enqueue(Decision(
                    captureKey,
                    untrackedId,
                    "response",
                    response.Request.Method,
                    response.Url,
                    response.Request.ResourceType,
                    response.Request.IsNavigationRequest,
                    "allow",
                    "untracked-response",
                    response.Status,
                    attempt: attempt,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
                return;
            }

            if (ledger.RecordResponse(tracked.Id, response.Status))
            {
                tracked.ResponseStatus = response.Status;
            }
        };
        context.RequestFinished += (_, request) =>
        {
            if (!trackedRequests.TryRemove(request, out var tracked))
            {
                ledger.RecordFinished($"{captureKey}:attempt:{attempt}:untracked-finished:{Interlocked.Increment(ref decisionSequence)}");
                return;
            }

            if (!tracked.Allowed)
            {
                return;
            }

            if (ledger.RecordFinished(tracked.Id))
            {
                decisions.Enqueue(Decision(
                    captureKey,
                    tracked.Id,
                    tracked.ResponseStatus is null ? "failure" : "response",
                    request.Method,
                    request.Url,
                    request.ResourceType,
                    request.IsNavigationRequest,
                    "allow",
                    tracked.ResponseStatus is null ? "response-status-missing" : "response-complete",
                    tracked.ResponseStatus,
                    tracked.ResponseStatus is null ? "response-status-missing" : null,
                    attempt,
                    tracked.IsMainFrame,
                    tracked.PinnedAddress,
                    contextPolicy.Epoch,
                    browserSession.InstanceId,
                    browserContextId));
            }
        };
        context.RequestFailed += (_, request) =>
        {
            if (!trackedRequests.TryRemove(request, out var tracked))
            {
                var untrackedId = $"{captureKey}:attempt:{attempt}:untracked-failure:{Interlocked.Increment(ref decisionSequence)}";
                ledger.RecordFailure(
                    untrackedId,
                    "untracked-request-failure",
                    "playwright-request-failed");
                decisions.Enqueue(Decision(
                    captureKey,
                    untrackedId,
                    "failure",
                    request.Method,
                    request.Url,
                    request.ResourceType,
                    request.IsNavigationRequest,
                    "allow",
                    "untracked-request-failure",
                    failure: "playwright-request-failed",
                    attempt: attempt,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
                return;
            }

            if (!tracked.Allowed)
            {
                return;
            }

            if (ledger.RecordFailure(
                    tracked.Id,
                    "allowed-request-failed",
                    "playwright-request-failed"))
            {
                decisions.Enqueue(Decision(
                    captureKey,
                    tracked.Id,
                    "failure",
                    request.Method,
                    request.Url,
                    request.ResourceType,
                    request.IsNavigationRequest,
                    "allow",
                    "allowed-request-failed",
                    failure: "playwright-request-failed",
                    attempt: attempt,
                    isMainFrame: tracked.IsMainFrame,
                    pinnedAddress: tracked.PinnedAddress,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
            }
        };

        void RecordCapability(
            string capability,
            string capabilityUrl,
            ScreenshotPolicyDecision capabilityPolicy)
        {
            var requestId = $"{captureKey}:attempt:{attempt}:capability:{Interlocked.Increment(ref decisionSequence)}";
            var decision = Decision(
                captureKey,
                requestId,
                "capability",
                capability.ToUpperInvariant(),
                capabilityUrl,
                "capability",
                false,
                capabilityPolicy.Allowed ? "allow" : "block",
                capabilityPolicy.ReasonCode,
                attempt: attempt,
                dnsEpoch: contextPolicy.Epoch,
                browserInstanceId: browserSession.InstanceId,
                browserContextId: browserContextId);
            ledger.RegisterDecision(requestId, decision);
            decisions.Enqueue(decision);
            if (capabilityPolicy.Allowed)
            {
                ledger.RecordCapabilityTerminal(
                    requestId,
                    capabilityPolicy.ReasonCode);
                decisions.Enqueue(Decision(
                    captureKey,
                    requestId,
                    "capability-terminal",
                    capability.ToUpperInvariant(),
                    capabilityUrl,
                    "capability",
                    false,
                    "allow",
                    capabilityPolicy.ReasonCode,
                    attempt: attempt,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
                return;
            }

            decisions.Enqueue(Decision(
                captureKey,
                requestId,
                "failure",
                capability.ToUpperInvariant(),
                capabilityUrl,
                "capability",
                false,
                "block",
                capabilityPolicy.ReasonCode,
                failure: "blocked-by-policy",
                attempt: attempt,
                dnsEpoch: contextPolicy.Epoch,
                browserInstanceId: browserSession.InstanceId,
                browserContextId: browserContextId));
        }

        await context.RouteWebSocketAsync("**/*", async webSocket =>
        {
            RecordCapability(
                "websocket",
                webSocket.Url,
                BrowserCookiePolicy.ClassifyCapability("websocket"));
            await webSocket.CloseAsync();
        });

        var page = await context.NewPageAsync().WaitAsync(cancellationToken);
        await using var pageLifetime = new AttemptResourceLifetime(page.DisposeAsync);
        var lifecycle = lifecycleFactory.Create(attempt, context, page, ledger);
        var capabilityConsoleMessages = 0;
        var capabilityNonce = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var capabilityLogFailures = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        context.Page += (_, popup) =>
        {
            RecordCapability(
                "popup",
                popup.Url,
                BrowserCookiePolicy.ClassifyCapability("popup"));
            _ = popup.CloseAsync();
        };
        page.Download += (_, download) =>
            RecordCapability(
                "download",
                download.Url,
                BrowserCookiePolicy.ClassifyCapability("download"));
        page.Console += (_, message) =>
        {
            if (!message.Text.StartsWith(CapabilityConsolePolicy.Prefix, StringComparison.Ordinal))
            {
                return;
            }

            var messageNumber = Interlocked.Increment(ref capabilityConsoleMessages);
            var parsed = CapabilityConsolePolicy.Parse(
                message.Text,
                messageNumber,
                url,
                capabilityNonce);
            if (!parsed.Accepted)
            {
                capabilityLogFailures.TryAdd(parsed.FailureReason!, 0);
                if (messageNumber <= CapabilityConsolePolicy.MaximumMessageCount + 1)
                {
                    RecordCapability(
                        "unknown",
                        url,
                        new(false, parsed.FailureReason!));
                }

                return;
            }

            RecordCapability(
                parsed.Capability,
                parsed.Url,
                BrowserCookiePolicy.ClassifyCapability(parsed.Capability));
        };
        await page.AddInitScriptAsync(CreateCapabilityBlockScript(capabilityNonce));

        using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        captureTimeout.CancelAfter(MaximumCaptureDuration);

        async Task ValidateContextCheckpointAsync(string checkpoint)
        {
            await _endpointPolicy.AssertContextDnsSafeAsync(
                contextPolicy,
                captureTimeout.Token);
            var cookieReason = BrowserCookiePolicy.ValidateContextCookieCount(
                (await context.CookiesAsync()).Count);
            if (cookieReason is not null)
            {
                throw new CaptureSafetyException(cookieReason);
            }

            var validatedAtUtc = DateTimeOffset.UtcNow;
            epochEvidence = checkpoint switch
            {
                "pre-navigation" => epochEvidence with
                {
                    PreNavigationValidatedAtUtc = validatedAtUtc
                },
                "pre-screenshot" => epochEvidence with
                {
                    PreScreenshotValidatedAtUtc = validatedAtUtc
                },
                "post-screenshot" => epochEvidence with
                {
                    PostScreenshotValidatedAtUtc = validatedAtUtc
                },
                _ => throw new ArgumentOutOfRangeException(
                    nameof(checkpoint),
                    checkpoint,
                    "Unknown context checkpoint.")
            };
            RecordDnsEpoch(epochEvidence);
        }

        async Task<PlaywrightCaptureResult> AbortAttemptAsync(string reason)
        {
            ledger.MarkAttemptAborting();
            var abortReasons = new List<string> { reason };
            try
            {
                await pageLifetime.DisposeAsync();
            }
            catch (Exception)
            {
                abortReasons.Add("attempt-page-disposal-failed");
            }

            try
            {
                await contextLifetime.DisposeAsync();
            }
            catch (Exception)
            {
                abortReasons.Add("attempt-context-disposal-failed");
            }

            var trackedById = trackedRequests.Values
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var requestId in ledger.FinalizeAbortedAllowedRequests())
            {
                if (!trackedById.TryGetValue(requestId, out var tracked))
                {
                    abortReasons.Add(
                        "attempt-abort-request-metadata-missing");
                    continue;
                }

                decisions.Enqueue(Decision(
                    captureKey,
                    tracked.Id,
                    "lifecycle",
                    tracked.Method,
                    tracked.Url,
                    tracked.ResourceType,
                    tracked.IsNavigationRequest,
                    "allow",
                    "attempt-aborted-before-browser-terminal",
                    failure: "capture-attempt-aborted",
                    attempt: attempt,
                    isMainFrame: tracked.IsMainFrame,
                    pinnedAddress: tracked.PinnedAddress,
                    dnsEpoch: contextPolicy.Epoch,
                    browserInstanceId: browserSession.InstanceId,
                    browserContextId: browserContextId));
            }

            var ledgerSnapshot = ledger.Complete();
            if (!ledgerSnapshot.Passed)
            {
                abortReasons.Add("request-ledger-incomplete");
                abortReasons.AddRange(ledgerSnapshot.ReasonCodes);
            }

            var retainedReasons = abortReasons
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(
                FailedScreenshot(
                    templateKey,
                    url,
                    viewportName,
                    width,
                    height,
                    reason,
                    attempt,
                    retainedReasons,
                    controlledRunDonationFormCount),
                decisions.OrderBy(item => item.TimestampUtc).ToArray());
        }

        try
        {
            await ValidateContextCheckpointAsync("pre-navigation");
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = NavigationAttemptTimeoutMilliseconds
            }).WaitAsync(captureTimeout.Token);
            Interlocked.Exchange(ref initialNavigationComplete, 1);
            var capabilityPolicyFailures = await page.EvaluateAsync<string[]>(
                "() => window.__HUSAYNIA_BASELINE_CAPABILITY_POLICY_FAILURES__ ?? ['policy-not-installed']")
                .WaitAsync(captureTimeout.Token);
            if (capabilityPolicyFailures.Length != 0)
            {
                return new(
                    FailedScreenshot(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        "capability-policy-install-failed",
                        attempt,
                        capabilityPolicyFailures,
                        controlledRunDonationFormCount),
                    decisions.OrderBy(item => item.TimestampUtc).ToArray());
            }

            var samples = await WaitForReadinessAsync(
                page,
                () => trackedRequests.Values.Count(item => item.Allowed && item.ResponseStatus is null),
                captureTimeout.Token);
            await ValidateContextCheckpointAsync("pre-screenshot");

            if (!capabilityLogFailures.IsEmpty)
            {
                return new(
                    FailedScreenshot(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        "capability-log-invalid",
                        attempt,
                        capabilityLogFailures.Keys.Order(StringComparer.Ordinal).ToArray(),
                        controlledRunDonationFormCount),
                    decisions.OrderBy(item => item.TimestampUtc).ToArray());
            }

            await ledger.AwaitSnapshotBarrierAsync(TimeSpan.FromMilliseconds(250), captureTimeout.Token);
            var final = samples[^1];
            var metrics = await ReadMetricsAsync(page, captureTimeout.Token);
            var png = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = false,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Timeout = 10_000
            }).WaitAsync(captureTimeout.Token);
            await ValidateContextCheckpointAsync("post-screenshot");

            if (!capabilityLogFailures.IsEmpty)
            {
                return new(
                    FailedScreenshot(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        "capability-log-invalid",
                        attempt,
                        capabilityLogFailures.Keys.Order(StringComparer.Ordinal).ToArray(),
                        controlledRunDonationFormCount),
                    decisions.OrderBy(item => item.TimestampUtc).ToArray());
            }

            var completion = lifecycle.CompleteSnapshot(png);
            var ledgerSnapshot = completion.Ledger;
            if (!ledgerSnapshot.Passed)
            {
                return new(
                    FailedScreenshot(
                        templateKey,
                        url,
                        viewportName,
                        width,
                        height,
                        "request-ledger-incomplete",
                        attempt,
                        ledgerSnapshot.ReasonCodes,
                        controlledRunDonationFormCount),
                    decisions.OrderBy(item => item.TimestampUtc).ToArray());
            }

            png = completion.RetainedPng
                ?? throw new InvalidOperationException("passed-ledger-must-retain-png");
            var (pngWidth, pngHeight) = CaptureIO.ReadPngDimensions(png);
            var pngBytes = png.LongLength;
            var entropy = CaptureIO.ComputeByteEntropy(png);
            var landmarks = metrics.Landmarks
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var relative = $"screenshots/{templateKey}-{viewportName}.png";
            var sha = CaptureIO.Sha256(png);
            var preliminary = new ScreenshotRecord(
                templateKey,
                url,
                viewportName,
                width,
                height,
                relative,
                sha,
                "captured",
                null,
                HasStableReadyWindow(samples),
                metrics.TextLength,
                metrics.DocumentScrollWidth > final.InnerWidth,
                metrics.FormCount,
                pngBytes,
                pngWidth,
                pngHeight,
                $"{templateKey}:{metrics.Heading ?? url}",
                "pending",
                entropy,
                metrics.GiantSvgDetected,
                metrics.LoadingOnlyState,
                landmarks,
                final.InnerWidth,
                final.InnerHeight,
                metrics.ScreenWidth,
                metrics.ScreenHeight,
                final.DevicePixelRatio,
                metrics.DocumentScrollWidth,
                metrics.DocumentScrollHeight,
                metrics.BodyWidth,
                metrics.BodyHeight,
                final.FontsReady,
                final.IncompleteImageCount,
                ledgerSnapshot.AllowedInFlightCount,
                metrics.OverflowSources,
                samples,
                false,
                "screenshot-network-decisions.json",
                "screenshot-capture-provenance.json",
                [],
                attempt,
                [],
                controlledRunDonationFormCount);
            var quality = ScreenshotQualityEvaluator.Evaluate(new(
                templateKey,
                width,
                height,
                preliminary,
                ledgerSnapshot,
                controlledRunDonationFormCount));
            var record = preliminary with
            {
                QualityStatus = quality.QualityStatus,
                QualityReasonCodes = quality.ReasonCodes,
                Error = quality.QualityStatus == "pass"
                    ? null
                    : string.Join(", ", quality.ReasonCodes)
            };
            var outputPath = Path.Combine(
                _outputDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, png, captureTimeout.Token);
            return new(record, decisions.OrderBy(item => item.TimestampUtc).ToArray());
        }
        catch (Exception ex) when (ex is TimeoutException
            or OperationCanceledException
            or CaptureSafetyException
            or PlaywrightException)
        {
            var reason = ClassifyAttemptFailure(
                ex,
                cancellationToken.IsCancellationRequested);
            return await AbortAttemptAsync(reason);
        }
    }

    internal static string ClassifyAttemptFailure(
        Exception exception,
        bool callerCancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            if (callerCancellationRequested)
            {
                return "capture-cancelled";
            }

            return "capture-timeout";
        }

        if (exception is CaptureSafetyException safetyException)
        {
            return safetyException.ReasonCode;
        }

        return exception is TimeoutException
            ? "navigation-timeout"
            : "playwright-attempt-failed";
    }

    private static async Task<IReadOnlyList<ScreenshotReadinessSample>> WaitForReadinessAsync(
        IPage page,
        Func<int> inFlight,
        CancellationToken cancellationToken)
    {
        using var readinessTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessTimeout.CancelAfter(MaximumReadinessDuration);
        await page.EvaluateAsync(
            "() => document.fonts ? document.fonts.ready : Promise.resolve()")
            .WaitAsync(readinessTimeout.Token);
        var samples = new List<ScreenshotReadinessSample>();
        while (true)
        {
            var sample = await ReadSampleAsync(page, inFlight(), readinessTimeout.Token);
            samples.Add(sample);
            if (HasStableReadyWindow(samples))
            {
                return samples;
            }

            await Task.Delay(250, readinessTimeout.Token);
        }
    }

    private static async Task<ScreenshotReadinessSample> ReadSampleAsync(
        IPage page,
        int inFlight,
        CancellationToken cancellationToken)
    {
        var value = await page.EvaluateAsync<JsonElement>(
            """
            () => {
              const body = document.body?.getBoundingClientRect() ?? { width: 0, height: 0 };
              const visible = element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
              };
              return {
                innerWidth: window.innerWidth,
                innerHeight: window.innerHeight,
                dpr: window.devicePixelRatio,
                scrollWidth: document.documentElement.scrollWidth,
                scrollHeight: document.documentElement.scrollHeight,
                bodyWidth: body.width,
                bodyHeight: body.height,
                fontsReady: !document.fonts || document.fonts.status === 'loaded',
                incompleteImages: Array.from(document.images).filter(image => visible(image) && !image.complete).length
              };
            }
            """).WaitAsync(cancellationToken);
        var fontsReady = value.GetProperty("fontsReady").GetBoolean();
        var incompleteImages = value.GetProperty("incompleteImages").GetInt32();
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
        {
            try
            {
                var frameReadiness = await frame.EvaluateAsync<JsonElement>(
                    """
                    () => {
                      const visible = element => {
                        const style = getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                      };
                      return {
                        fontsReady: !document.fonts || document.fonts.status === 'loaded',
                        incompleteImages: Array.from(document.images).filter(image => visible(image) && !image.complete).length
                      };
                    }
                    """).WaitAsync(cancellationToken);
                fontsReady &= frameReadiness.GetProperty("fontsReady").GetBoolean();
                incompleteImages += frameReadiness.GetProperty("incompleteImages").GetInt32();
            }
            catch (PlaywrightException)
            {
                fontsReady = false;
            }
        }

        return new(
            DateTimeOffset.UtcNow,
            value.GetProperty("innerWidth").GetInt32(),
            value.GetProperty("innerHeight").GetInt32(),
            value.GetProperty("dpr").GetDouble(),
            value.GetProperty("scrollWidth").GetInt32(),
            value.GetProperty("scrollHeight").GetInt32(),
            value.GetProperty("bodyWidth").GetDouble(),
            value.GetProperty("bodyHeight").GetDouble(),
            fontsReady,
            incompleteImages,
            inFlight);
    }

    private static async Task<BrowserMetrics> ReadMetricsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var value = await page.EvaluateAsync<JsonElement>(
            """
            () => {
              const visible = element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
              };
              const body = document.body?.getBoundingClientRect() ?? { width: 0, height: 0 };
              const pageOverflows = document.documentElement.scrollWidth > innerWidth;
              const overflow = pageOverflows
                ? Array.from(document.body?.querySelectorAll('*') ?? [])
                  .map(element => ({ element, rect: element.getBoundingClientRect(), style: getComputedStyle(element) }))
                  .filter(item => visible(item.element) && item.style.position !== 'fixed'
                    && (item.rect.left < -1 || item.rect.right > innerWidth + 1 || item.rect.width > innerWidth + 1))
                  .slice(0, 20)
                  .map(item => `${item.element.tagName.toLowerCase()}${item.element.id ? '#' + item.element.id : ''}${item.element.classList.length ? '.' + Array.from(item.element.classList).slice(0,3).join('.') : ''}[${Math.round(item.rect.left)},${Math.round(item.rect.right)},${Math.round(item.rect.width)}]`)
                : [];
              const count = selector => Array.from(document.querySelectorAll(selector)).filter(visible).length;
              const text = document.body?.innerText?.replace(/\s+/g, ' ').trim() ?? '';
              const visibleLoading = count('[class*="loader"],[class*="spinner"],[aria-busy="true"]');
              const landmarkCount = count('header,nav,h1,h2,h3,main,article,form,.give-embed-form-wrapper,.entry-content');
              const giantSvg = Array.from(document.querySelectorAll('svg')).some(svg => {
                const rect = svg.getBoundingClientRect();
                return visible(svg) && rect.width * rect.height > innerWidth * innerHeight * 0.8;
              });
              return {
                screenWidth: screen.width,
                screenHeight: screen.height,
                scrollWidth: document.documentElement.scrollWidth,
                scrollHeight: document.documentElement.scrollHeight,
                bodyWidth: body.width,
                bodyHeight: body.height,
                textLength: text.length,
                formCount: count('form,.give-embed-form-wrapper'),
                heading: document.querySelector('h1,h2,h3')?.textContent?.replace(/\s+/g,' ').trim() ?? null,
                giantSvg,
                loadingOnly: visibleLoading > 0 && landmarkCount === 0,
                overflow,
                landmarks: {
                  logo: count('[class*="logo"],img[alt*="logo" i],.custom-logo'),
                  menu: count('nav,[role="navigation"]'),
                  heading: count('h1,h2,h3'),
                  content: count('main,article,.entry-content,.tribe-events-content'),
                  "form-shell": count('form,.give-embed-form-wrapper')
                }
              };
            }
            """).WaitAsync(cancellationToken);
        var childFrameForms = 0;
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
        {
            try
            {
                childFrameForms += await frame.EvaluateAsync<int>(
                    """
                    () => Array.from(document.querySelectorAll('form')).filter(form => {
                      const style = getComputedStyle(form);
                      const rect = form.getBoundingClientRect();
                      return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
                    }).length
                    """).WaitAsync(cancellationToken);
            }
            catch (PlaywrightException)
            {
                // A detached child frame is diagnosed by the readiness and ledger predicates.
            }
        }

        var landmarks = value.GetProperty("landmarks").EnumerateObject()
            .ToDictionary(item => item.Name, item => ReadInteger(item.Value), StringComparer.Ordinal);
        landmarks["form-shell"] += childFrameForms;
        landmarks["embedded-form"] = childFrameForms;
        return new(
            value.GetProperty("screenWidth").GetInt32(),
            value.GetProperty("screenHeight").GetInt32(),
            value.GetProperty("scrollWidth").GetInt32(),
            value.GetProperty("scrollHeight").GetInt32(),
            value.GetProperty("bodyWidth").GetDouble(),
            value.GetProperty("bodyHeight").GetDouble(),
            value.GetProperty("textLength").GetInt32(),
            value.GetProperty("formCount").GetInt32() + childFrameForms,
            value.GetProperty("heading").ValueKind == JsonValueKind.String
                ? value.GetProperty("heading").GetString()
                : null,
            value.GetProperty("giantSvg").GetBoolean(),
            value.GetProperty("loadingOnly").GetBoolean(),
            value.GetProperty("overflow").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            landmarks);
    }

    private static ScreenshotRecord FailedScreenshot(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        string reason,
        int attemptCount,
        IReadOnlyList<string> attemptReasons,
        int? controlledRunDonationFormCount,
        string? error = null) =>
        new(
            templateKey,
            url,
            viewportName,
            width,
            height,
            null,
            null,
            "failed",
            error ?? reason,
            false,
            0,
            false,
            0,
            0,
            0,
            0,
            templateKey,
            "fail",
            0,
            false,
            false,
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            0,
            [],
            [],
            false,
            "screenshot-network-decisions.json",
            "screenshot-capture-provenance.json",
            [reason],
            attemptCount,
            attemptReasons,
            controlledRunDonationFormCount);

    private static ScreenshotNetworkDecision Decision(
        string captureKey,
        string requestId,
        string eventType,
        string method,
        string url,
        string resourceType,
        bool isNavigation,
        string decision,
        string reason,
        int? status = null,
        string? failure = null,
        int attempt = 0,
        bool isMainFrame = false,
        IPAddress? pinnedAddress = null,
        long? dnsEpoch = null,
        string? browserInstanceId = null,
        string? browserContextId = null) =>
        new(
            captureKey,
            requestId,
            DateTimeOffset.UtcNow,
            eventType,
            method,
            EvidenceSanitizer.RedactUrl(url),
            resourceType,
            isNavigation,
            decision,
            reason,
            status,
            failure,
            ScreenshotNetworkPolicy.Version,
            attempt,
            isMainFrame,
            pinnedAddress?.ToString())
        {
            DnsEpoch = dnsEpoch,
            BrowserInstanceId = browserInstanceId,
            BrowserContextId = browserContextId
        };

    private static string GeometryKey(ScreenshotReadinessSample sample) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{sample.InnerWidth}:{sample.InnerHeight}:{sample.DevicePixelRatio:0.###}:{sample.DocumentScrollWidth}:{sample.DocumentScrollHeight}:{sample.BodyWidth:0.##}:{sample.BodyHeight:0.##}");

    private static int ReadInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : int.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);

    private static bool SameDocumentUrl(Uri left, Uri right) =>
        left.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.UriEscaped)
            .Equals(
                right.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped),
                StringComparison.Ordinal);

    private static string ReadDotNetSdkVersion()
    {
        var globalJson = FindRepositoryFile("global.json");
        if (globalJson is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(globalJson));
                var version = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
                // Runtime provenance below is the in-process fallback.
            }
        }

        return RuntimeInformation.FrameworkDescription;
    }

    private static string? FindRepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class TrackedRequest(
        string id,
        bool allowed,
        bool isMainFrame,
        IPAddress? pinnedAddress,
        string method,
        string url,
        string resourceType,
        bool isNavigationRequest)
    {
        public string Id { get; } = id;
        public bool Allowed { get; } = allowed;
        public bool IsMainFrame { get; } = isMainFrame;
        public IPAddress? PinnedAddress { get; } = pinnedAddress;
        public string Method { get; } = method;
        public string Url { get; } = url;
        public string ResourceType { get; } = resourceType;
        public bool IsNavigationRequest { get; } = isNavigationRequest;
        public int? ResponseStatus { get; set; }
    }

    private sealed record BrowserMetrics(
        int ScreenWidth,
        int ScreenHeight,
        int DocumentScrollWidth,
        int DocumentScrollHeight,
        double BodyWidth,
        double BodyHeight,
        int TextLength,
        int FormCount,
        string? Heading,
        bool GiantSvgDetected,
        bool LoadingOnlyState,
        IReadOnlyList<string> OverflowSources,
        IReadOnlyDictionary<string, int> Landmarks);

    internal static string CreateCapabilityBlockScript(string capabilityNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityNonce);
        return CapabilityBlockScript.Replace(
            "__CAPABILITY_NONCE__",
            capabilityNonce,
            StringComparison.Ordinal);
    }

    private const string CapabilityBlockScript =
        """
        (() => {
          const nonce = '__CAPABILITY_NONCE__';
          const trustedDebug = console.debug.bind(console);
          const emit = (capability, url) => trustedDebug('__HUSAYNIA_BASELINE_CAPABILITY__' + JSON.stringify({
            capability,
            url: String(url || location.href),
            nonce
          }));
          const blocked = capability => function(url) {
            emit(capability, url);
            throw new DOMException(`${capability} blocked by baseline capture policy`, 'SecurityError');
          };
          const blockedAtLocation = capability => function() {
            emit(capability, location.href);
            throw new DOMException(`${capability} blocked by baseline capture policy`, 'SecurityError');
          };
          const policyFailures = [];
          const install = (target, name, value) => {
            try {
              Object.defineProperty(target, name, {
                configurable: false,
                enumerable: false,
                writable: false,
                value
              });
            } catch {
              try {
                target[name] = value;
              } catch {
                policyFailures.push(name);
                return;
              }
              if (target[name] !== value) policyFailures.push(name);
            }
          };
          install(window, 'WebSocket', blocked('websocket'));
          install(window, 'EventSource', blocked('eventsource'));
          install(window, 'RTCPeerConnection', blocked('webrtc'));
          install(window, 'webkitRTCPeerConnection', blocked('webrtc'));
          install(window, 'WebTransport', blocked('webtransport'));
          install(window, 'TCPSocket', blocked('direct-sockets'));
          install(window, 'UDPSocket', blocked('direct-sockets'));
          install(window, 'PaymentRequest', blocked('payment-request'));
          install(navigator, 'sendBeacon', function(url) { emit('sendbeacon', url); return false; });
          if (navigator.serviceWorker) {
            install(navigator.serviceWorker, 'register', function(url) {
              emit('serviceworker', url);
              return Promise.reject(new DOMException('service worker blocked', 'SecurityError'));
            });
          }
          install(HTMLFormElement.prototype, 'submit', function() {
            emit('form-submit', this.action || location.href);
            throw new DOMException('form submission blocked', 'SecurityError');
          });
          install(HTMLFormElement.prototype, 'requestSubmit', function() {
            emit('form-request-submit', this.action || location.href);
            throw new DOMException('form submission blocked', 'SecurityError');
          });
          try {
            Object.defineProperty(Document.prototype, 'cookie', {
              configurable: false,
              enumerable: false,
              get() {
                emit('cookie-read', location.href);
                return '';
              },
              set() {
                emit('cookie-write', location.href);
                throw new DOMException('cookies blocked by baseline capture policy', 'SecurityError');
              }
            });
          } catch {
            policyFailures.push('document.cookie');
          }
          if (window.cookieStore) {
            install(window.cookieStore, 'get', function() {
              emit('cookie-read', location.href);
              return Promise.resolve(null);
            });
            install(window.cookieStore, 'getAll', function() {
              emit('cookie-read', location.href);
              return Promise.resolve([]);
            });
            install(window.cookieStore, 'set', blockedAtLocation('cookie-write'));
            install(window.cookieStore, 'delete', blockedAtLocation('cookie-write'));
          }
          Object.defineProperty(window, '__HUSAYNIA_BASELINE_CAPABILITY_POLICY_FAILURES__', {
            configurable: false,
            enumerable: false,
            writable: false,
            value: Object.freeze(policyFailures.slice())
          });
        })();
        """;
}
