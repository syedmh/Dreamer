using Xunit;
using System.Text.Json;

namespace Husaynia.BaselineCapture.Tests;

public sealed class PlaywrightLiveCaptureTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveHomeCaptureUsesExactViewportAndCompleteRequestEvidence()
    {
        var output = Path.Combine(Path.GetTempPath(), $"husaynia-playwright-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        try
        {
            await using var capture = await PlaywrightScreenshotCapture.CreateAsync(
                "integration-test",
                output,
                CancellationToken.None);
            var result = await capture.CaptureAsync(
                "home",
                "https://www.husaynia.org/",
                "mobile-320x568",
                320,
                568,
                CancellationToken.None);

            Assert.True(
                result.Screenshot.Status == "captured",
                JsonSerializer.Serialize(result.Screenshot, IndentedJson));
            Assert.Equal("pass", result.Screenshot.QualityStatus);
            Assert.Equal(320, result.Screenshot.ActualViewportWidth);
            Assert.Equal(568, result.Screenshot.ActualViewportHeight);
            Assert.Equal(320, result.Screenshot.ActualScreenWidth);
            Assert.Equal(568, result.Screenshot.ActualScreenHeight);
            Assert.Equal(1d, result.Screenshot.ActualDevicePixelRatio);
            Assert.False(result.Screenshot.LayoutInjectionUsed);
            Assert.Empty(result.Screenshot.OverflowSources);

            var requests = result.NetworkDecisions.Where(item => item.EventType == "request").ToArray();
            Assert.NotEmpty(requests);
            foreach (var request in requests)
            {
                var terminals = result.NetworkDecisions.Count(item =>
                    item.RequestId == request.RequestId
                    && item.EventType is "response" or "failure");
                Assert.Equal(1, terminals);
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
