using System.Text.Json;

namespace Husaynia.BaselineCapture;

public static class ScreenshotMatrixRunner
{
    private static readonly TimeSpan DiagnosticFinalizationTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Task<int> RunAsync(
        string evidenceDirectory,
        string outputDirectory,
        CancellationToken cancellationToken) =>
        RunAsync(
            evidenceDirectory,
            outputDirectory,
            ScreenshotMatrixRunnerDependencies.Default,
            cancellationToken);

    internal static async Task<int> RunAsync(
        string evidenceDirectory,
        string outputDirectory,
        ScreenshotMatrixRunnerDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var evidence = Path.GetFullPath(evidenceDirectory);
        var output = Path.GetFullPath(outputDirectory);
        var summaryPath = Path.Combine(evidence, "capture-summary.json");
        var screenshotsPath = Path.Combine(evidence, "screenshots.json");
        if (!File.Exists(summaryPath) || !File.Exists(screenshotsPath))
        {
            Console.Error.WriteLine("Screenshot recapture requires capture-summary.json and screenshots.json.");
            return 2;
        }

        var sourceSummary = JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(summaryPath, cancellationToken),
            CaseInsensitiveJson);
        var sourceScreenshots = JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(screenshotsPath, cancellationToken),
            CaseInsensitiveJson) ?? [];
        if (sourceSummary is null
            || !sourceSummary.NoSubmit
            || sourceSummary.SourceBaseUrl != CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri)
        {
            Console.Error.WriteLine("Screenshot recapture requires an approved-origin no-submit capture.");
            return 2;
        }

        var donationCounts = sourceScreenshots
            .Where(item => item.TemplateKey == "donation-form")
            .ToDictionary(item => item.Viewport, item => item.FormCount, StringComparer.Ordinal);
        CaptureIO.EnsureEmptyOutputDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "screenshots"));
        var startedAt = dependencies.UtcNow();
        var runId = $"husaynia-recapture-{startedAt:yyyyMMddTHHmmssZ}";
        var screenshots = new List<ScreenshotRecord>(36);
        var decisions = new List<ScreenshotNetworkDecision>();
        ScreenshotCaptureProvenance? provenance = null;
        try
        {
            await using (var capture = await dependencies.CreateScreenshotSessionAsync(
                             runId,
                             output,
                             cancellationToken))
            {
                try
                {
                    foreach (var representative in CaptureProfile.Approved.Representatives)
                    {
                        foreach (var viewport in CaptureProfile.Approved.Viewports)
                        {
                            var expectedDonationCount = representative.Key == "donation-form"
                                && donationCounts.TryGetValue(viewport.Name, out var count)
                                    ? count
                                    : (int?)null;
                            PlaywrightCaptureResult result;
                            try
                            {
                                result = await capture.CaptureAsync(
                                    representative.Key,
                                    representative.Value.AbsoluteUri,
                                    viewport.Name,
                                    viewport.Width,
                                    viewport.Height,
                                    expectedDonationCount,
                                    cancellationToken);
                            }
                            catch (ScreenshotCaptureCanceledException exception)
                            {
                                screenshots.Add(exception.PartialResult.Screenshot);
                                decisions.AddRange(
                                    exception.PartialResult.NetworkDecisions);
                                throw;
                            }

                            screenshots.Add(result.Screenshot);
                            decisions.AddRange(result.NetworkDecisions);
                            Console.WriteLine(
                                $"Recaptured screenshot {result.Screenshot.Status}/{result.Screenshot.QualityStatus}: "
                                + $"{representative.Key}/{viewport.Name}");
                            await dependencies.DelayAsync(
                                TimeSpan.FromMilliseconds(
                                    PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds),
                                cancellationToken);
                        }
                    }
                }
                finally
                {
                    provenance = capture.Provenance;
                }
            }

            return await WriteEvidenceAsync(
                output,
                sourceSummary.CaptureId,
                runId,
                startedAt,
                screenshots,
                decisions,
                provenance,
                CaptureFailureEvidence.CompleteStatus,
                failureReason: null,
                dependencies.UtcNow,
                cancellationToken);
        }
        catch (Exception exception)
        {
            var failureReason = CaptureFailureEvidence.Classify(
                exception,
                cancellationToken.IsCancellationRequested,
                deadlineCancellationRequested: false);
            var completedRows = CaptureFailureEvidence.CompleteScreenshotMatrix(
                screenshots,
                failureReason);
            using var finalizationLimit = new CancellationTokenSource(
                DiagnosticFinalizationTimeout);
            await WriteEvidenceAsync(
                output,
                sourceSummary.CaptureId,
                runId,
                startedAt,
                completedRows,
                decisions,
                provenance,
                CaptureFailureEvidence.FailedStatus,
                failureReason,
                dependencies.UtcNow,
                finalizationLimit.Token);
            throw;
        }
    }

    private static async Task<int> WriteEvidenceAsync(
        string output,
        string sourceCaptureId,
        string runId,
        DateTimeOffset startedAt,
        IReadOnlyList<ScreenshotRecord> screenshots,
        IReadOnlyList<ScreenshotNetworkDecision> decisions,
        ScreenshotCaptureProvenance? provenance,
        string status,
        string? failureReason,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken)
    {
        var completedAt = utcNow();
        var captured = screenshots.Count(item => item.Status == "captured");
        var qualityPass = screenshots.Count(item => item.QualityStatus == "pass");
        var summary = new CaptureSummary(
            runId,
            startedAt,
            completedAt,
            CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
            true,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            screenshots.Count,
            captured,
            qualityPass,
            0,
            (completedAt - startedAt).TotalSeconds,
            decisions.Count(item => item.EventType == "request"),
            0)
        {
            Status = status,
            FailureReason = failureReason,
            FailureStage = failureReason is null ? null : "screenshot-matrix"
        };
        var retainedProvenance = provenance
            ?? CaptureFailureEvidence.ToolingFailureProvenance(
                runId,
                startedAt,
                failureReason ?? "browser-session-not-created");
        var networkPolicy = CaptureFailureEvidence.NetworkPolicy(
            endpointPolicy: null,
            retainedProvenance,
            provenance is not null,
            failureReason,
            "Fresh context/page/ledger per attempt; public GET/HEAD only; terminal request barrier; no DOM/layout mutation.");
        var checksumPath = Path.Combine(output, "checksums.sha256");
        if (File.Exists(checksumPath))
        {
            File.Delete(checksumPath);
        }

        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(output, "screenshots.json"),
            screenshots,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(output, "screenshot-network-decisions.json"),
            decisions.OrderBy(item => item.TimestampUtc),
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(output, "screenshot-capture-provenance.json"),
            retainedProvenance,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(output, "screenshot-network-policy.json"),
            networkPolicy,
            cancellationToken);
        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(output, "capture-summary.json"),
            summary,
            cancellationToken);
        var readme = failureReason is null
            ? $"""
              # Husaynia.org screenshot recapture

              Capture ID: `{runId}`  
              Source capture ID: `{sourceCaptureId}`  
              Started UTC: `{startedAt:O}`  
              Completed UTC: `{completedAt:O}`  
              Source: `{CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri}`  
              Safety mode: `--no-submit`

              Captured `{captured}/{screenshots.Count}`; quality-pass `{qualityPass}/{screenshots.Count}`.
              Duration `{summary.DurationSeconds:F1}` seconds. This run is a comparison input only and is
              never promoted directly.
              """
            : $"""
              # Husaynia.org incomplete screenshot recapture diagnostics

              Capture ID: `{runId}`  
              Source capture ID: `{sourceCaptureId}`  
              Started UTC: `{startedAt:O}`  
              Failed UTC: `{completedAt:O}`  
              Status: `failed`  
              Failure reason: `{failureReason}`

              This run is diagnostic only, contains exactly 36 explicit matrix rows, is not a
              comparison candidate, has no verification seal, and must never be promoted. Completed
              rows and collected network decisions are retained; unattempted rows are explicit
              failures. Exception messages, ambient environment values, paths, credentials, and
              secrets are not retained. `checksums.sha256` was generated last.
              """;
        await CaptureIO.WriteTextAtomicAsync(
            Path.Combine(output, "README.md"),
            readme,
            cancellationToken);
        await CaptureIO.WriteChecksumsAsync(output, cancellationToken);
        return screenshots.Count == 36 && captured == 36 && qualityPass == 36 ? 0 : 1;
    }
}
