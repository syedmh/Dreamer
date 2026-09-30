using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class CaptureFailureDiagnosticsTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task ThrowingSafetyWriterCannotAlterExitTwoOrCommandState()
    {
        var state = "not-started";

        var exitCode = await Program.ExecuteWithFailureHandlingAsync(
            () =>
            {
                state = "safety-refused";
                throw new CaptureSafetyException(
                    "injected-safety-refusal");
            },
            new ThrowingTextWriter());

        Assert.Equal(2, exitCode);
        Assert.Equal("safety-refused", state);
    }

    [Fact]
    public async Task CancellationBeforeBrowserRetainsClosedDiagnosticEvidence()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new CaptureOptions(
            CaptureProfile.Approved.PrimaryOrigin,
            output.Path,
            true,
            0,
            1,
            1024,
            1,
            1);

        using var service = new BaselineCaptureService(options);
        var error = new StringWriter();
        var exitCode = await Program.ExecuteWithFailureHandlingAsync(
            async () =>
            {
                await service.CaptureAsync(cancellation.Token);
                return 0;
            },
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"command-failed:{nameof(TaskCanceledException)}{Environment.NewLine}",
            error.ToString());

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
            "residual-risks.json",
            "README.md",
            "checksums.sha256"
        };
        Assert.All(required, relative => Assert.True(
            File.Exists(System.IO.Path.Combine(output.Path, relative)),
            relative));
        Assert.False(File.Exists(System.IO.Path.Combine(
            output.Path,
            "baseline-verification.json")));

        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(System.IO.Path.Combine(
                output.Path,
                "capture-summary.json")));
        Assert.Equal(
            "failed",
            summary.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "capture-cancelled",
            summary.RootElement.GetProperty("failureReason").GetString());
        Assert.Equal(
            0,
            summary.RootElement.GetProperty("routeCount").GetInt32());
        Assert.Equal(
            36,
            summary.RootElement.GetProperty("screenshotCount").GetInt32());

        var screenshots = JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(System.IO.Path.Combine(
                output.Path,
                "screenshots.json")),
            CaseInsensitiveJson);
        Assert.NotNull(screenshots);
        Assert.Equal(36, screenshots.Length);
        Assert.All(screenshots, screenshot =>
        {
            Assert.NotEqual("captured", screenshot.Status);
            Assert.Contains("capture-cancelled", screenshot.QualityReasonCodes);
        });

        var validation = await new BaselineEvidenceValidator().FinalizeAsync(
            output.Path,
            CancellationToken.None);
        Assert.False(validation.Passed);
        Assert.Contains(
            "required-artifact-missing:screenshot-determinism.json",
            validation.ReasonCodes);
        AssertChecksumsCoverRetainedFiles(output.Path);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output.Path, "*", SearchOption.AllDirectories),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationMidMatrixPreservesCompletedRowAndFailsRemainingRows()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, token) =>
            {
                if (checkpoint == CaptureCheckpoint.ScreenshotRowCompleted)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            });

        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(cancellation.Token));

        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(screenshots.Length, summary.ScreenshotCount);
        Assert.Equal(
            screenshots.Count(screenshot => screenshot.Status == "captured"),
            summary.SuccessfulScreenshotCount);
        Assert.Equal(1, summary.DurationSeconds);
        Assert.Equal(36, screenshots.Select(Key).Distinct(StringComparer.Ordinal).Count());
        var completed = Assert.Single(
            screenshots,
            screenshot => screenshot.Error == "completed-row-marker");
        Assert.Equal("fixture-complete", completed.Status);
        Assert.Equal(
            35,
            screenshots.Count(screenshot =>
                screenshot.QualityReasonCodes.Contains("capture-cancelled")));

        var decisions = await ReadJsonAsync<ScreenshotNetworkDecision[]>(
            output.Path,
            "screenshot-network-decisions.json");
        var decision = Assert.Single(decisions);
        Assert.Equal("fixture-request", decision.RequestId);
        var provenance = await ReadJsonAsync<ScreenshotCaptureProvenance>(
            output.Path,
            "screenshot-capture-provenance.json");
        Assert.Equal("fake-browser-session", provenance.OperatingSystem);
        Assert.Equal("complete", provenance.Status);
        Assert.Single(provenance.DnsContextEpochs);
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task BrowserStartupFailureUsesFailedFinalizerAndCompletesMatrixOnce()
    {
        using var output = new TemporaryDirectory();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: static (_, _) => ValueTask.CompletedTask,
            createScreenshotSessionAsync: static (_, _, _, _) =>
                throw new InvalidOperationException("browser-startup-sentinel"));
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(CancellationToken.None));

        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal("failed", summary.Status);
        Assert.Equal(
            "capture-error:invalidoperationexception",
            summary.FailureReason);
        Assert.Equal("browser-startup", summary.FailureStage);
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(
            36,
            screenshots.Select(Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            screenshots,
            screenshot => Assert.Contains(
                "capture-error:invalidoperationexception",
                screenshot.QualityReasonCodes));
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task CancellationAfterDnsRotationRetainsFinalEpochAndInitialRunIdentity()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var completedRows = 0;
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, token) =>
            {
                if (checkpoint == CaptureCheckpoint.ScreenshotRowCompleted
                    && Interlocked.Increment(ref completedRows) == 2)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            });
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(cancellation.Token));

        var provenance = await ReadJsonAsync<ScreenshotCaptureProvenance>(
            output.Path,
            "screenshot-capture-provenance.json");
        Assert.Equal(2, provenance.DnsContextEpochs.Count);
        var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
        Assert.Equal("93.184.216.34", provenance.PinnedDnsAnswers[staticHost][0]);
        Assert.Contains(
            $"MAP {staticHost} 93.184.216.34",
            provenance.ChromiumHostResolverRules);
        Assert.Equal(
            "93.184.216.35",
            provenance.DnsContextEpochs[1].Bindings
                .Single(binding => binding.Host == staticHost)
                .SelectedAddress);
        Assert.NotEqual(
            provenance.DnsContextEpochs[0].BrowserInstanceId,
            provenance.DnsContextEpochs[1].BrowserInstanceId);
    }

    [Fact]
    public async Task BrowserFailureMidMatrixPreservesCompletedRowsAndCompletesOnlyMissingKeys()
    {
        using var output = new TemporaryDirectory();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: static (_, _) => ValueTask.CompletedTask,
            createScreenshotSessionAsync:
                (captureId, _, _, _) =>
                    Task.FromResult<IScreenshotCaptureSession>(
                        new ThrowingScreenshotSession(captureId)));
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(CancellationToken.None));

        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal("failed", summary.Status);
        Assert.Equal("screenshot-matrix", summary.FailureStage);
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(
            36,
            screenshots.Select(Key).Distinct(StringComparer.Ordinal).Count());
        var completed = Assert.Single(
            screenshots,
            screenshot => screenshot.Error == "completed-row-marker");
        Assert.Equal("fixture-complete", completed.Status);
        Assert.Equal(
            35,
            screenshots.Count(screenshot =>
                screenshot.QualityReasonCodes.Contains(
                    "capture-error:invalidoperationexception")));
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task InAttemptCallerCancellationAttachesDecisionsBeforeDiagnosticFinalization()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: static (_, _) => ValueTask.CompletedTask,
            createScreenshotSessionAsync:
                (captureId, _, _, _) =>
                    Task.FromResult<IScreenshotCaptureSession>(
                        new CancellingAttemptScreenshotSession(
                            captureId,
                            cancellation)));
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(cancellation.Token));

        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        var decisions = await ReadJsonAsync<ScreenshotNetworkDecision[]>(
            output.Path,
            "screenshot-network-decisions.json");
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal("capture-cancelled", summary.FailureReason);
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(
            36,
            screenshots.Select(Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(
            screenshots,
            screenshot => screenshot.Error == "in-attempt-cancelled-row");
        var decision = Assert.Single(decisions);
        Assert.Equal("in-attempt-request", decision.RequestId);
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public void PlaywrightAttemptCancellationDistinguishesCallerFromInternalTimeout()
    {
        var cancellation = new OperationCanceledException();

        Assert.Equal(
            "capture-cancelled",
            PlaywrightScreenshotCapture.ClassifyAttemptFailure(
                cancellation,
                callerCancellationRequested: true));
        Assert.Equal(
            "capture-timeout",
            PlaywrightScreenshotCapture.ClassifyAttemptFailure(
                cancellation,
                callerCancellationRequested: false));
    }

    [Theory]
    [InlineData("RouteRecordCompleted", "route-capture")]
    [InlineData("AssetRecordCompleted", "asset-capture")]
    public async Task CrawlOrAssetErrorRetainsTruthfulSanitizedDiagnostics(
        string failurePointName,
        string expectedStage)
    {
        var failurePoint = Enum.Parse<CaptureCheckpoint>(failurePointName);
        using var output = new TemporaryDirectory();
        const string sensitiveMessage =
            "password=not-retained C:\\Users\\private\\workspace secret-token";
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, _) =>
            {
                if (checkpoint == failurePoint)
                {
                    throw new InvalidOperationException(sensitiveMessage);
                }

                return ValueTask.CompletedTask;
            });

        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(CancellationToken.None));
        Assert.Equal(sensitiveMessage, exception.Message);

        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal("failed", summary.Status);
        Assert.Equal(
            "capture-error:invalidoperationexception",
            summary.FailureReason);
        Assert.Equal(expectedStage, summary.FailureStage);
        var http = await ReadJsonAsync<HttpRecord[]>(
            output.Path,
            "http-inventory.json");
        Assert.Equal(
            http.Count(record => record.Status is not null),
            summary.SuccessfulRouteCount);
        Assert.True(summary.RouteCount >= http.Length);
        Assert.Equal(
            summary.AssetCount,
            (await ReadJsonAsync<AssetRecord[]>(
                output.Path,
                "media-inventory.json")).Length);
        Assert.Equal(1, summary.DurationSeconds);
        Assert.Equal(
            36,
            (await ReadJsonAsync<ScreenshotRecord[]>(
                output.Path,
                "screenshots.json")).Length);
        Assert.DoesNotContain(
            sensitiveMessage,
            await ReadAllTextEvidenceAsync(output.Path),
            StringComparison.Ordinal);
        Assert.Contains(
            await ReadJsonAsync<ResidualRisk[]>(
                output.Path,
                "residual-risks.json"),
            risk => risk.RiskId == "RISK-CAPTURE-INCOMPLETE");
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task RouteDiscoveryDiagnosticsExplainQueryOnlyFrozenSchemaDeltaWhenCaptureAbortsEarly()
    {
        using var output = new TemporaryDirectory();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, _) => checkpoint == CaptureCheckpoint.RouteRecordCompleted
                ? ValueTask.FromException(new InvalidOperationException("route-stop"))
                : ValueTask.CompletedTask);
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(CancellationToken.None));

        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        var manifest = await ReadJsonAsync<RouteManifest>(
            output.Path,
            "route-manifest.json");
        var risks = await ReadJsonAsync<ResidualRisk[]>(
            output.Path,
            "residual-risks.json");
        var readme = await File.ReadAllTextAsync(
            System.IO.Path.Combine(output.Path, "README.md"));

        Assert.Equal(summary.RouteCount, summary.DiscoveredUrlCount);
        Assert.Equal(manifest.Routes.Count, summary.ManifestPathCount);
        Assert.Equal(1, summary.QueryEndpointExcludedByFrozenSchemaCount);
        Assert.Equal(
            summary.QueryEndpointExcludedByFrozenSchemaCount,
            summary.DiscoveredUrlCount - summary.ManifestPathCount);
        Assert.Contains(
            risks,
            risk => risk.RiskId == "RISK-QUERY-ROUTE-SCHEMA");
        Assert.Contains("Discovered URLs", readme, StringComparison.Ordinal);
        Assert.Contains("manifest paths", readme, StringComparison.Ordinal);
        Assert.Contains(
            "query endpoints excluded by the frozen schema",
            readme,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SocketFailureReasonIsStableForDirectAndNestedExceptions(bool nested)
    {
        var socket = new SocketException((int)SocketError.ConnectionRefused);
        Exception exception = nested
            ? new HttpRequestException("sensitive transport message", socket)
            : socket;

        Assert.Equal(
            "socket-ConnectionRefused",
            BaselineCaptureService.GetSocketFailureReason(exception));
    }

    [Fact]
    public async Task RouteSocketFailureRetainsSuccessfulSiblingsAndRedactsSummaryUrl()
    {
        using var output = new TemporaryDirectory();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: static (_, _) => ValueTask.CompletedTask,
            createCrawlHandler: static _ =>
                new SocketFailureHttpMessageHandler(
                    nested: true,
                    wrapSafetyException: false));
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        var summary = await service.CaptureAsync(CancellationToken.None);

        var http = await ReadJsonAsync<HttpRecord[]>(
            output.Path,
            "http-inventory.json");
        var failed = Assert.Single(
            http,
            record => record.Path == "/events/?ical=1");
        Assert.Null(failed.Status);
        Assert.Equal("socket-ConnectionRefused", failed.Error);
        Assert.True(http.Count(record => record.Status is not null) > 1);
        Assert.Equal("socket-ConnectionRefused", summary.FailureDetailReason);
        Assert.NotNull(summary.FailureAffectedUrl);
        Assert.DoesNotContain("=1", summary.FailureAffectedUrl, StringComparison.Ordinal);
        Assert.Contains(
            "ical=[REDACTED]",
            Uri.UnescapeDataString(summary.FailureAffectedUrl),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sensitive transport message",
            await ReadAllTextEvidenceAsync(output.Path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDnsSetChangedSafetyExceptionStillEscapesFailClosed()
    {
        using var output = new TemporaryDirectory();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: static (_, _) => ValueTask.CompletedTask,
            createCrawlHandler: static _ =>
                new SocketFailureHttpMessageHandler(
                    nested: false,
                    wrapSafetyException: true));
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(
            () => service.CaptureAsync(CancellationToken.None));

        Assert.Equal("dns-set-changed", exception.ReasonCode);
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal(
            "capture-safety-refusal:dns-set-changed",
            summary.FailureReason);
        Assert.Equal(
            "capture-safety-refusal:dns-set-changed",
            summary.FailureDetailReason);
    }

    [Theory]
    [InlineData("RouteRecordCompleted", "route-capture")]
    [InlineData("AssetRecordCompleted", "asset-capture")]
    public async Task CrawlOrAssetCallerCancellationRetainsMaintainedDiagnostics(
        string cancellationPointName,
        string expectedStage)
    {
        var cancellationPoint = Enum.Parse<CaptureCheckpoint>(
            cancellationPointName);
        using var output = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, token) =>
            {
                if (checkpoint == cancellationPoint)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            });
        using var service = new BaselineCaptureService(
            Options(output.Path),
            dependencies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(cancellation.Token));

        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        Assert.Equal("failed", summary.Status);
        Assert.Equal("capture-cancelled", summary.FailureReason);
        Assert.Equal(expectedStage, summary.FailureStage);
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(
            36,
            screenshots.Select(Key).Distinct(StringComparer.Ordinal).Count());
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task NonEmptyOutputIsRefusedWithoutDeletingExistingEvidence()
    {
        using var output = new TemporaryDirectory(create: true);
        var retained = System.IO.Path.Combine(output.Path, "retain.txt");
        await File.WriteAllTextAsync(retained, "retain");
        using var service = new BaselineCaptureService(Options(output.Path));

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(
            () => service.CaptureAsync(CancellationToken.None));

        Assert.Equal("output-directory-not-empty", exception.ReasonCode);
        Assert.Equal("retain", await File.ReadAllTextAsync(retained));
        Assert.Single(Directory.EnumerateFiles(
            output.Path,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeadlineCancellationRetainsStableFailureEvidence()
    {
        using var output = new TemporaryDirectory();
        CancellationTokenSource? deadline = null;
        TimeSpan? requestedDuration = null;
        var dependencies = await CreateDependenciesAsync(
            checkpoint: (checkpoint, token) =>
            {
                if (checkpoint == CaptureCheckpoint.BeforeBrowserSession)
                {
                    Assert.NotNull(deadline);
                    deadline.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return ValueTask.CompletedTask;
            },
            durationLimitFactory: (callerToken, duration) =>
            {
                requestedDuration = duration;
                deadline = CancellationTokenSource.CreateLinkedTokenSource(
                    callerToken);
                return deadline;
            });
        using var service = new BaselineCaptureService(
            Program.CreateCaptureOptions(
                ["capture"],
                CaptureProfile.Approved.PrimaryOrigin,
                output.Path),
            dependencies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(CancellationToken.None));

        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal(
            "capture-deadline-exceeded",
            summary.FailureReason);
        Assert.Equal("browser-startup", summary.FailureStage);
        Assert.Equal(36, summary.ScreenshotCount);
        Assert.Equal(TimeSpan.FromMinutes(60), requestedDuration);
        var validation = await new BaselineEvidenceValidator().FinalizeAsync(
            output.Path,
            CancellationToken.None);
        Assert.False(validation.Passed);
        Assert.Contains(
            "required-artifact-missing:screenshot-determinism.json",
            validation.ReasonCodes);
        Assert.False(File.Exists(System.IO.Path.Combine(
            output.Path,
            "baseline-verification.json")));
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    [Fact]
    public async Task ScreenshotRecaptureCancellationRetainsRejectedDiagnosticMatrix()
    {
        using var source = new TemporaryDirectory(create: true);
        using var output = new TemporaryDirectory();
        var started = new DateTimeOffset(
            2026,
            8,
            16,
            23,
            27,
            0,
            TimeSpan.Zero);
        await CaptureIO.WriteJsonAsync(
            System.IO.Path.Combine(source.Path, "capture-summary.json"),
            new CaptureSummary(
                "source-capture",
                started,
                started.AddSeconds(1),
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
                36,
                36,
                36,
                0,
                1,
                0,
                0),
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            System.IO.Path.Combine(source.Path, "screenshots.json"),
            Array.Empty<ScreenshotRecord>(),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var clockCalls = 0;
        var dependencies = new ScreenshotMatrixRunnerDependencies
        {
            CreateScreenshotSessionAsync =
                (captureId, _, _) =>
                    Task.FromResult<IScreenshotCaptureSession>(
                        new FixtureScreenshotSession(
                            captureId,
                            cancellation.Cancel)),
            DelayAsync = static (_, _) => Task.CompletedTask,
            UtcNow = () => started.AddSeconds(Interlocked.Increment(ref clockCalls) - 1)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScreenshotMatrixRunner.RunAsync(
                source.Path,
                output.Path,
                dependencies,
                cancellation.Token));

        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            output.Path,
            "screenshots.json");
        var summary = await ReadJsonAsync<CaptureSummary>(
            output.Path,
            "capture-summary.json");
        Assert.Equal(36, screenshots.Length);
        Assert.Equal(1, summary.DurationSeconds);
        Assert.False(Program.HasCompleteScreenshotSet(summary with
        {
            ScreenshotCount = 36,
            SuccessfulScreenshotCount = 36,
            QualityPassScreenshotCount = 36
        }));
        Assert.Single(
            screenshots,
            screenshot => screenshot.Error == "completed-row-marker");
        var validation = await new BaselineEvidenceValidator()
            .ValidateScreenshotComparisonRunAsync(
                output.Path,
                CancellationToken.None);
        Assert.False(validation.Passed);
        Assert.Contains("capture-run-diagnostic", validation.ReasonCodes);
        var provenance = await ReadJsonAsync<ScreenshotCaptureProvenance>(
            output.Path,
            "screenshot-capture-provenance.json");
        Assert.Single(provenance.DnsContextEpochs);
        Assert.False(File.Exists(System.IO.Path.Combine(
            output.Path,
            "baseline-verification.json")));
        AssertChecksumsCoverRetainedFiles(output.Path);
    }

    private static CaptureOptions Options(string output) =>
        new(
            CaptureProfile.Approved.PrimaryOrigin,
            output,
            true,
            0,
            1,
            1024,
            1,
            1);

    private static async Task<BaselineCaptureDependencies> CreateDependenciesAsync(
        Func<CaptureCheckpoint, CancellationToken, ValueTask> checkpoint,
        Func<CancellationToken, TimeSpan, CancellationTokenSource>?
            durationLimitFactory = null,
        Func<string, string, TrustedEndpointPolicy, CancellationToken,
            Task<IScreenshotCaptureSession>>?
            createScreenshotSessionAsync = null,
        Func<TrustedEndpointPolicy, HttpMessageHandler>?
            createCrawlHandler = null)
    {
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            new PublicDnsResolver(),
            CancellationToken.None);
        var started = new DateTimeOffset(
            2026,
            8,
            16,
            23,
            27,
            0,
            TimeSpan.Zero);
        var clockCalls = 0;
        return new BaselineCaptureDependencies
        {
            CreateEndpointPolicyAsync = _ => Task.FromResult(policy),
            CreateDurationLimit = durationLimitFactory
                ?? BaselineCaptureDependencies.Default.CreateDurationLimit,
            CreateCrawlHandler = createCrawlHandler
                ?? (_ => new FixtureHttpMessageHandler()),
            CreateScreenshotSessionAsync = createScreenshotSessionAsync
                ?? ((captureId, _, _, _) =>
                    Task.FromResult<IScreenshotCaptureSession>(
                        new FixtureScreenshotSession(captureId))),
            DelayAsync = static (_, _) => Task.CompletedTask,
            UtcNow = () => started.AddSeconds(Interlocked.Increment(ref clockCalls) - 1),
            CheckpointAsync = checkpoint
        };
    }

    private static async Task<T> ReadJsonAsync<T>(
        string root,
        string relative)
    {
        var value = JsonSerializer.Deserialize<T>(
            await File.ReadAllTextAsync(System.IO.Path.Combine(root, relative)),
            CaseInsensitiveJson);
        return Assert.IsType<T>(value);
    }

    private static async Task<string> ReadAllTextEvidenceAsync(string root)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .Where(path => System.IO.Path.GetExtension(path) is ".json" or ".md" or ".sha256")
                 .Order(StringComparer.Ordinal))
        {
            builder.Append(await File.ReadAllTextAsync(path));
        }

        return builder.ToString();
    }

    private static void AssertChecksumsCoverRetainedFiles(string root)
    {
        var checksumPath = System.IO.Path.Combine(root, "checksums.sha256");
        var checksummed = File.ReadAllLines(checksumPath)
            .Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..])
            .ToHashSet(StringComparer.Ordinal);
        var retained = Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Where(path => path != checksumPath)
            .Select(path => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(retained.SetEquals(checksummed));
    }

    private static string Key(ScreenshotRecord screenshot) =>
        $"{screenshot.TemplateKey}|{screenshot.Viewport}";

    private sealed class PublicDnsResolver : ITrustedDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [IPAddress.Parse("93.184.216.34")]);
        }
    }

    private sealed class FixtureScreenshotSession(
        string captureId,
        Action? afterCapture = null) : IScreenshotCaptureSession
    {
        public ScreenshotCaptureProvenance Provenance { get; private set; } =
            CaptureFailureEvidence.ToolingFailureProvenance(
                captureId,
                new DateTimeOffset(2026, 8, 16, 23, 27, 0, TimeSpan.Zero),
                "fixture")
            with
            {
                OperatingSystem = "fake-browser-session",
                Status = CaptureFailureEvidence.CompleteStatus,
                FailureReason = null,
                PinnedDnsAnswers = FixturePinnedAnswers(),
                ChromiumHostResolverRules = FixturePinnedAnswers()
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"MAP {pair.Key} {pair.Value[0]}")
                    .Append("MAP * ~NOTFOUND")
                    .ToArray()
            };

        public Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = new ScreenshotRecord(
                templateKey,
                url,
                viewportName,
                width,
                height,
                null,
                null,
                "fixture-complete",
                "completed-row-marker",
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
                width,
                height,
                width,
                height,
                1,
                width,
                height,
                width,
                height,
                true,
                0,
                0,
                [],
                [],
                false,
                "screenshot-network-decisions.json",
                "screenshot-capture-provenance.json",
                ["fixture-row"],
                1,
                ["fixture-row"],
                null);
            var decision = new ScreenshotNetworkDecision(
                $"{templateKey}|{viewportName}",
                "fixture-request",
                new DateTimeOffset(2026, 8, 16, 23, 27, 0, TimeSpan.Zero),
                "request",
                "GET",
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                "document",
                true,
                "block",
                "fixture",
                null,
                "fixture",
                CaptureProfile.Approved.PolicyVersion,
                1,
                true,
                "93.184.216.34");
            var epoch = Provenance.DnsContextEpochs.Count + 1;
            var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
            var bindings = FixturePinnedAnswers()
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var rotated = epoch > 1 && pair.Key == staticHost;
                    var selected = rotated ? "93.184.216.35" : pair.Value[0];
                    return new DnsPinBindingEvidence(
                        pair.Key,
                        pair.Key == CaptureProfile.Approved.PrimaryOrigin.Host
                            ? nameof(TrustedHostClass.PrimaryOrigin)
                            : nameof(TrustedHostClass.StaticResource),
                        selected,
                        [selected],
                        CaptureIO.Sha256(Encoding.UTF8.GetBytes(selected)),
                        DateTimeOffset.UtcNow,
                        rotated);
                })
                .ToArray();
            Provenance = Provenance with
            {
                DnsContextEpochs = Provenance.DnsContextEpochs
                    .Append(new DnsContextEpochEvidence(
                        epoch,
                        $"{templateKey}|{viewportName}",
                        1,
                        $"fixture-browser-{epoch}",
                        $"fixture-context-{epoch}",
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        bindings))
                    .ToArray()
            };
            afterCapture?.Invoke();
            return Task.FromResult(
                new PlaywrightCaptureResult(screenshot, [decision]));
        }

        public Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            int? controlledRunDonationFormCount,
            CancellationToken cancellationToken) =>
            CaptureAsync(
                templateKey,
                url,
                viewportName,
                width,
                height,
                cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Dictionary<string, IReadOnlyList<string>> FixturePinnedAnswers() =>
        CaptureProfile.Approved.StaticResources
            .Select(rule => rule.Host)
            .Append(CaptureProfile.Approved.PrimaryOrigin.Host)
            .ToDictionary(
                host => host,
                _ => (IReadOnlyList<string>)["93.184.216.34"],
                StringComparer.OrdinalIgnoreCase);

    private sealed class ThrowingScreenshotSession(
        string captureId) : IScreenshotCaptureSession
    {
        private readonly FixtureScreenshotSession _inner = new(captureId);
        private int _captures;

        public ScreenshotCaptureProvenance Provenance => _inner.Provenance;

        public Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _captures) == 2)
            {
                throw new InvalidOperationException("browser-mid-matrix-sentinel");
            }

            return _inner.CaptureAsync(
                templateKey,
                url,
                viewportName,
                width,
                height,
                cancellationToken);
        }

        public Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            int? controlledRunDonationFormCount,
            CancellationToken cancellationToken) =>
            CaptureAsync(
                templateKey,
                url,
                viewportName,
                width,
                height,
                cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingAttemptScreenshotSession(
        string captureId,
        CancellationTokenSource cancellation) : IScreenshotCaptureSession
    {
        private readonly FixtureScreenshotSession _inner = new(captureId);

        public ScreenshotCaptureProvenance Provenance => _inner.Provenance;

        public async Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var screenshot = new ScreenshotRecord(
                templateKey,
                url,
                viewportName,
                width,
                height,
                null,
                null,
                "capture-cancelled",
                "in-attempt-cancelled-row",
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
                ["capture-cancelled"],
                1,
                ["capture-cancelled"],
                null);
            var decision = new ScreenshotNetworkDecision(
                $"{templateKey}|{viewportName}",
                "in-attempt-request",
                new DateTimeOffset(2026, 8, 16, 23, 27, 0, TimeSpan.Zero),
                "request",
                "GET",
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                "document",
                true,
                "allow",
                "primary-origin-get-head",
                null,
                null,
                CaptureProfile.Approved.PolicyVersion,
                1,
                true,
                "93.184.216.34");
            return await PlaywrightScreenshotCapture.RunCaptureAttemptsAsync(
                templateKey,
                url,
                viewportName,
                width,
                height,
                controlledRunDonationFormCount: null,
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(
                        new PlaywrightCaptureResult(
                            screenshot,
                            [decision]));
                },
                static _ => Task.CompletedTask,
                static (_, _) => Task.CompletedTask,
                cancellationToken);
        }

        public Task<PlaywrightCaptureResult> CaptureAsync(
            string templateKey,
            string url,
            string viewportName,
            int width,
            int height,
            int? controlledRunDonationFormCount,
            CancellationToken cancellationToken) =>
            CaptureAsync(
                templateKey,
                url,
                viewportName,
                width,
                height,
                cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFixtureResponse(request));
        }
    }

    private sealed class SocketFailureHttpMessageHandler(
        bool nested,
        bool wrapSafetyException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = Assert.IsType<Uri>(request.RequestUri);
            if (uri.AbsolutePath == "/events/" && uri.Query.Length > 0)
            {
                if (wrapSafetyException)
                {
                    throw new HttpRequestException(
                        "sensitive dns wrapper",
                        new CaptureSafetyException("dns-set-changed"));
                }

                var socket = new SocketException(
                    (int)SocketError.ConnectionRefused);
                throw nested
                    ? new HttpRequestException(
                        "sensitive transport message",
                        socket)
                    : socket;
            }

            return Task.FromResult(CreateFixtureResponse(request));
        }
    }

    private static HttpResponseMessage CreateFixtureResponse(
        HttpRequestMessage request)
    {
        var uri = Assert.IsType<Uri>(request.RequestUri);
        HttpContent content;
        if (uri.AbsolutePath == "/robots.txt")
        {
            content = new StringContent(
                "User-agent: *\nAllow: /\n",
                Encoding.UTF8,
                "text/plain");
        }
        else if (uri.AbsolutePath.EndsWith(
                     "sitemap.xml",
                     StringComparison.Ordinal))
        {
            content = new StringContent(
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://www.husaynia.org/fixture/</loc></url>
                </urlset>
                """,
                Encoding.UTF8,
                "application/xml");
        }
        else if (uri.AbsolutePath == "/wp-content/uploads/fixture.png")
        {
            content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        }
        else
        {
            content = new StringContent(
                """
                <!doctype html>
                <html lang="en"><head><title>Fixture</title></head>
                <body><h1>Fixture</h1><img src="/wp-content/uploads/fixture.png"></body></html>
                """,
                Encoding.UTF8,
                "text/html");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = content
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(bool create = false)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"husaynia-capture-failure-{Guid.NewGuid():N}");
            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("injected-writer-failure");
    }
}
