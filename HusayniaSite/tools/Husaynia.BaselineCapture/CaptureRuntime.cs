namespace Husaynia.BaselineCapture;

internal enum CaptureCheckpoint
{
    BeforeBrowserSession,
    ScreenshotRowCompleted,
    RouteRecordCompleted,
    AssetRecordCompleted
}

internal interface IScreenshotCaptureSession : IAsyncDisposable
{
    ScreenshotCaptureProvenance Provenance { get; }

    Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        CancellationToken cancellationToken);

    Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int? controlledRunDonationFormCount,
        CancellationToken cancellationToken);
}

internal sealed class ScreenshotCaptureCanceledException(
    PlaywrightCaptureResult partialResult,
    CancellationToken cancellationToken,
    Exception? innerException = null) : OperationCanceledException(
        "screenshot-capture-cancelled",
        innerException,
        cancellationToken)
{
    public PlaywrightCaptureResult PartialResult { get; } = partialResult;
}

internal sealed class BaselineCaptureDependencies
{
    public static BaselineCaptureDependencies Default { get; } = new();

    public Func<CancellationToken, Task<TrustedEndpointPolicy>> CreateEndpointPolicyAsync { get; init; } =
        cancellationToken => TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            new SystemTrustedDnsResolver(),
            cancellationToken);

    public Func<CancellationToken, TimeSpan, CancellationTokenSource>
        CreateDurationLimit { get; init; } =
            static (cancellationToken, duration) =>
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                source.CancelAfter(duration);
                return source;
            };

    public Func<TrustedEndpointPolicy, HttpMessageHandler> CreateCrawlHandler { get; init; } =
        endpointPolicy => endpointPolicy.CreateCrawlHandler();

    public Func<string, string, TrustedEndpointPolicy, CancellationToken, Task<IScreenshotCaptureSession>>
        CreateScreenshotSessionAsync { get; init; } =
            async (captureId, outputDirectory, endpointPolicy, cancellationToken) =>
                new PlaywrightScreenshotCaptureSession(
                    await PlaywrightScreenshotCapture.CreateAsync(
                        captureId,
                        outputDirectory,
                        endpointPolicy,
                        cancellationToken));

    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        Task.Delay;

    public Func<DateTimeOffset> UtcNow { get; init; } =
        static () => DateTimeOffset.UtcNow;

    public Func<CaptureCheckpoint, CancellationToken, ValueTask> CheckpointAsync { get; init; } =
        static (_, _) => ValueTask.CompletedTask;
}

internal sealed class PlaywrightScreenshotCaptureSession(
    PlaywrightScreenshotCapture capture) : IScreenshotCaptureSession
{
    public ScreenshotCaptureProvenance Provenance => capture.Provenance;

    public Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        CancellationToken cancellationToken) =>
        capture.CaptureAsync(
            templateKey,
            url,
            viewportName,
            width,
            height,
            cancellationToken);

    public Task<PlaywrightCaptureResult> CaptureAsync(
        string templateKey,
        string url,
        string viewportName,
        int width,
        int height,
        int? controlledRunDonationFormCount,
        CancellationToken cancellationToken) =>
        capture.CaptureAsync(
            templateKey,
            url,
            viewportName,
            width,
            height,
            controlledRunDonationFormCount,
            cancellationToken);

    public ValueTask DisposeAsync() => capture.DisposeAsync();
}

internal sealed class ScreenshotMatrixRunnerDependencies
{
    public static ScreenshotMatrixRunnerDependencies Default { get; } = new();

    public Func<string, string, CancellationToken, Task<IScreenshotCaptureSession>>
        CreateScreenshotSessionAsync { get; init; } =
            async (captureId, outputDirectory, cancellationToken) =>
                new PlaywrightScreenshotCaptureSession(
                    await PlaywrightScreenshotCapture.CreateAsync(
                        captureId,
                        outputDirectory,
                        cancellationToken));

    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        Task.Delay;

    public Func<DateTimeOffset> UtcNow { get; init; } =
        static () => DateTimeOffset.UtcNow;
}
