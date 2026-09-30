using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class ScreenshotQualityEvaluatorTests
{
    private static readonly RequestLedgerSnapshot PassedLedger =
        new(true, 1, 1, 0, []);

    [Fact]
    public void PageOverflowRemainsCapturedButFailsQuality()
    {
        var screenshot = PassingScreenshot("event-detail", 768, 1024) with
        {
            HorizontalOverflow = true,
            DocumentScrollWidth = 850,
            OverflowSources = ["table.events[0,850,850]"]
        };

        var result = ScreenshotQualityEvaluator.Evaluate(
            new("event-detail", 768, 1024, screenshot, PassedLedger, null));

        Assert.Equal("captured", screenshot.Status);
        Assert.Equal("fail", result.QualityStatus);
        Assert.Contains("horizontal-overflow", result.ReasonCodes);
    }

    [Fact]
    public void InternalTableDoesNotWaiveOrCreatePageOverflow()
    {
        var screenshot = PassingScreenshot("event-detail", 768, 1024) with
        {
            HorizontalOverflow = false,
            DocumentScrollWidth = 768,
            OverflowSources = ["table.scroll-container[0,768,1200]"]
        };

        var result = ScreenshotQualityEvaluator.Evaluate(
            new("event-detail", 768, 1024, screenshot, PassedLedger, null));

        Assert.Equal("pass", result.QualityStatus);
        Assert.DoesNotContain("horizontal-overflow", result.ReasonCodes);
    }

    [Fact]
    public void GiantSvgLoadingViewportAndLedgerDefectsAllRemainDiagnostic()
    {
        var screenshot = PassingScreenshot("home", 320, 568) with
        {
            GiantSvgDetected = true,
            LoadingOnlyState = true,
            ActualViewportWidth = 319,
            PngWidth = 319
        };
        var ledger = new RequestLedgerSnapshot(
            false,
            2,
            1,
            1,
            ["request-terminal-missing"]);

        var result = ScreenshotQualityEvaluator.Evaluate(
            new("home", 320, 568, screenshot, ledger, null));

        Assert.Equal("fail", result.QualityStatus);
        Assert.Contains("giant-svg", result.ReasonCodes);
        Assert.Contains("loading-only-state", result.ReasonCodes);
        Assert.Contains("viewport-width-mismatch", result.ReasonCodes);
        Assert.Contains("png-dimension-mismatch", result.ReasonCodes);
        Assert.Contains("request-ledger-incomplete", result.ReasonCodes);
        Assert.Contains("ledger-request-terminal-missing", result.ReasonCodes);
    }

    [Fact]
    public void DonationShellCountMustMatchControlledRun()
    {
        var screenshot = PassingScreenshot("donation-form", 390, 844) with
        {
            FormCount = 2
        };

        var result = ScreenshotQualityEvaluator.Evaluate(
            new("donation-form", 390, 844, screenshot, PassedLedger, 1));

        Assert.Equal("fail", result.QualityStatus);
        Assert.Contains("donation-form-count-mismatch", result.ReasonCodes);
    }

    private static ScreenshotRecord PassingScreenshot(string template, int width, int height)
    {
        var landmarks = new List<string> { "content", "heading", "menu" };
        if (template is "contact-form" or "donation-form")
        {
            landmarks.Add("form-shell");
        }

        if (template == "donation-form")
        {
            landmarks.Add("embedded-form");
        }

        return new ScreenshotRecord(
            template,
            CaptureProfile.Approved.Representatives[template].AbsoluteUri,
            "viewport",
            width,
            height,
            "screenshots/test.png",
            new string('a', 64),
            "captured",
            null,
            true,
            500,
            false,
            template is "contact-form" or "donation-form" ? 1 : 0,
            100_000,
            width,
            height,
            template,
            "pending",
            7,
            false,
            false,
            landmarks,
            width,
            height,
            width,
            height,
            1,
            width,
            height * 2,
            width,
            height * 2,
            true,
            0,
            0,
            [],
            [],
            false,
            "screenshot-network-decisions.json",
            "screenshot-capture-provenance.json",
            [],
            1,
            [],
            null);
    }
}
