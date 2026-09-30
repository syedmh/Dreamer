namespace TCFUploader.Time;

internal interface IClock
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    int NextJitterMilliseconds(int exclusiveUpperBound);
}
