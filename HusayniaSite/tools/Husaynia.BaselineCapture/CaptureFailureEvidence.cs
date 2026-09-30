using System.Runtime.InteropServices;

namespace Husaynia.BaselineCapture;

internal static class CaptureFailureEvidence
{
    internal const string CompleteStatus = "complete";
    internal const string FailedStatus = "failed";
    internal const string ToolingFailureStatus = "tooling-failure";

    public static string Classify(
        Exception exception,
        bool callerCancellationRequested,
        bool deadlineCancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            if (callerCancellationRequested)
            {
                return "capture-cancelled";
            }

            return deadlineCancellationRequested
                ? "capture-deadline-exceeded"
                : "capture-operation-cancelled";
        }

        if (exception is CaptureSafetyException safety)
        {
            return $"capture-safety-refusal:{SanitizeReasonCode(safety.ReasonCode)}";
        }

        return $"capture-error:{SanitizeReasonCode(exception.GetType().Name)}";
    }

    internal static TException? FindInnerException<TException>(
        Exception exception)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    public static IReadOnlyList<ScreenshotRecord> CompleteScreenshotMatrix(
        IEnumerable<ScreenshotRecord> completed,
        string failureReason)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        var retained = completed
            .GroupBy(
                screenshot => $"{screenshot.TemplateKey}|{screenshot.Viewport}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var rows = new List<ScreenshotRecord>(36);
        foreach (var representative in CaptureProfile.Approved.Representatives)
        {
            foreach (var viewport in CaptureProfile.Approved.Viewports)
            {
                var key = $"{representative.Key}|{viewport.Name}";
                rows.Add(retained.TryGetValue(key, out var screenshot)
                    ? screenshot
                    : FailedScreenshot(
                        representative.Key,
                        representative.Value.AbsoluteUri,
                        viewport,
                        failureReason));
            }
        }

        return rows;
    }

    public static ScreenshotCaptureProvenance ToolingFailureProvenance(
        string captureId,
        DateTimeOffset capturedAtUtc,
        string failureReason) =>
        new(
            captureId,
            capturedAtUtc,
            "unavailable",
            RuntimeInformation.ProcessArchitecture.ToString(),
            "unavailable",
            CaptureProfile.PlaywrightVersion,
            "unavailable",
            string.Empty,
            "pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium",
            "unavailable",
            CaptureProfile.Approved.PolicyVersion,
            PlaywrightScreenshotCapture.MaximumNavigationAttempts,
            (int)(PlaywrightScreenshotCapture.NavigationAttemptTimeoutMilliseconds / 1000),
            (int)PlaywrightScreenshotCapture.MaximumReadinessDuration.TotalSeconds,
            (int)PlaywrightScreenshotCapture.MaximumCaptureDuration.TotalSeconds,
            PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds,
            true,
            CaptureProfile.ChromiumRevision,
            string.Empty,
            string.Empty,
            false,
            [],
            string.Empty,
            false,
            CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            [],
            false,
            false,
            false,
            false,
            false)
        {
            Status = ToolingFailureStatus,
            FailureReason = failureReason
        };

    public static ScreenshotNetworkPolicyEvidence NetworkPolicy(
        TrustedEndpointPolicy? endpointPolicy,
        ScreenshotCaptureProvenance? provenance,
        bool browserSessionExisted,
        string? failureReason,
        string? enforcement = null)
    {
        var diagnostic = !browserSessionExisted;
        return new(
            CaptureProfile.Approved.PolicyVersion,
            ScreenshotNetworkPolicy.AllowedMethods,
            [CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority)],
            ScreenshotNetworkPolicy.StaticHosts.Order(StringComparer.Ordinal).ToArray(),
            ScreenshotNetworkPolicy.AllowedStaticResourceTypes,
            ScreenshotNetworkPolicy.BlockedCapabilities,
            endpointPolicy?.ApprovedDnsAnswers
                ?? provenance?.PinnedDnsAnswers
                ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            endpointPolicy?.ChromiumHostResolverRules
                ?? provenance?.ChromiumHostResolverRules
                ?? [],
            PlaywrightScreenshotCapture.MaximumNavigationAttempts,
            (int)(PlaywrightScreenshotCapture.NavigationAttemptTimeoutMilliseconds / 1000),
            (int)PlaywrightScreenshotCapture.MaximumReadinessDuration.TotalSeconds,
            (int)PlaywrightScreenshotCapture.MaximumCaptureDuration.TotalSeconds,
            PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds,
            diagnostic
                ? "Browser session was not created; this diagnostic policy record cannot satisfy baseline validation."
                : enforcement
                    ?? "Microsoft.Playwright context-wide default-deny routing. Live HTTPS documents are not rewritten and no DOM, fixture, masking, or layout CSS is injected.")
        {
            Status = diagnostic ? ToolingFailureStatus : CompleteStatus,
            FailureReason = diagnostic
                ? failureReason ?? "browser-session-not-created"
                : null
        };
    }

    private static ScreenshotRecord FailedScreenshot(
        string templateKey,
        string url,
        CaptureViewport viewport,
        string failureReason) =>
        new(
            templateKey,
            url,
            viewport.Name,
            viewport.Width,
            viewport.Height,
            null,
            null,
            failureReason,
            failureReason,
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
            [failureReason],
            0,
            [failureReason],
            null);

    private static string SanitizeReasonCode(string value)
    {
        var sanitized = new string(
            value
                .Select(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                        ? char.ToLowerInvariant(character)
                        : '-')
                .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "unknown"
            : sanitized;
    }
}
