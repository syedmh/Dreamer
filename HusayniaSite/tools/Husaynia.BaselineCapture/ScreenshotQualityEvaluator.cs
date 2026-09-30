namespace Husaynia.BaselineCapture;

public sealed record ScreenshotQualityInput(
    string TemplateKey,
    int RequestedWidth,
    int RequestedHeight,
    ScreenshotRecord Screenshot,
    RequestLedgerSnapshot Ledger,
    int? ControlledRunDonationFormCount);

public sealed record ScreenshotQualityResult(
    string QualityStatus,
    IReadOnlyList<string> ReasonCodes);

public static class ScreenshotQualityEvaluator
{
    public static ScreenshotQualityResult Evaluate(ScreenshotQualityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var screenshot = input.Screenshot;
        var reasons = new HashSet<string>(StringComparer.Ordinal);

        if (screenshot.Status != "captured")
        {
            reasons.Add("capture-incomplete");
        }

        if (screenshot.ActualViewportWidth != input.RequestedWidth)
        {
            reasons.Add("viewport-width-mismatch");
        }

        if (screenshot.ActualViewportHeight != input.RequestedHeight)
        {
            reasons.Add("viewport-height-mismatch");
        }

        if (screenshot.ActualScreenWidth != input.RequestedWidth)
        {
            reasons.Add("screen-width-mismatch");
        }

        if (screenshot.ActualScreenHeight != input.RequestedHeight)
        {
            reasons.Add("screen-height-mismatch");
        }

        if (Math.Abs(screenshot.ActualDevicePixelRatio - 1d) > 0.001)
        {
            reasons.Add("device-pixel-ratio-mismatch");
        }

        if (screenshot.PngWidth != input.RequestedWidth || screenshot.PngHeight != input.RequestedHeight)
        {
            reasons.Add("png-dimension-mismatch");
        }

        if (!screenshot.Ready)
        {
            reasons.Add("readiness-failed");
        }

        if (!screenshot.FontsReady)
        {
            reasons.Add("fonts-not-ready");
        }

        if (screenshot.IncompleteImageCount != 0)
        {
            reasons.Add("incomplete-images");
        }

        if (screenshot.InFlightAllowedRequests != 0)
        {
            reasons.Add("allowed-requests-in-flight");
        }

        if (!input.Ledger.Passed)
        {
            reasons.Add("request-ledger-incomplete");
            foreach (var reason in input.Ledger.ReasonCodes)
            {
                reasons.Add($"ledger-{reason}");
            }
        }

        if (screenshot.TextLength < 100)
        {
            reasons.Add("text-too-short");
        }

        if (!screenshot.VisibleLandmarks.Contains("menu", StringComparer.Ordinal))
        {
            reasons.Add("missing-menu");
        }

        if (!screenshot.VisibleLandmarks.Contains("heading", StringComparer.Ordinal))
        {
            reasons.Add("missing-heading");
        }

        if (!screenshot.VisibleLandmarks.Contains("content", StringComparer.Ordinal))
        {
            reasons.Add("missing-content");
        }

        if (input.TemplateKey == "contact-form"
            && !screenshot.VisibleLandmarks.Contains("form-shell", StringComparer.Ordinal))
        {
            reasons.Add("missing-contact-form-shell");
        }

        if (input.TemplateKey == "donation-form")
        {
            if (!screenshot.VisibleLandmarks.Contains("embedded-form", StringComparer.Ordinal)
                || screenshot.FormCount <= 0)
            {
                reasons.Add("missing-donation-form-shell");
            }

            if (input.ControlledRunDonationFormCount is { } expected
                && screenshot.FormCount != expected)
            {
                reasons.Add("donation-form-count-mismatch");
            }
        }

        if (screenshot.PngBytes < Math.Max(2_500, input.RequestedWidth * input.RequestedHeight / 80))
        {
            reasons.Add("png-too-small");
        }

        if (screenshot.ByteEntropy < 5)
        {
            reasons.Add("png-low-entropy");
        }

        if (screenshot.GiantSvgDetected)
        {
            reasons.Add("giant-svg");
        }

        if (screenshot.LoadingOnlyState)
        {
            reasons.Add("loading-only-state");
        }

        if (screenshot.HorizontalOverflow
            || screenshot.DocumentScrollWidth > screenshot.ActualViewportWidth)
        {
            reasons.Add("horizontal-overflow");
        }

        var ordered = reasons.Order(StringComparer.Ordinal).ToArray();
        return new ScreenshotQualityResult(ordered.Length == 0 ? "pass" : "fail", ordered);
    }
}
