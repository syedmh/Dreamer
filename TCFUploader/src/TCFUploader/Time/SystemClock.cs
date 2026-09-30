using System.Security.Cryptography;

namespace TCFUploader.Time;

internal sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
    public int NextJitterMilliseconds(int exclusiveUpperBound) =>
        RandomNumberGenerator.GetInt32(exclusiveUpperBound);
}
