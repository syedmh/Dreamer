using TCFUploader.Time;

namespace TCFUploader.Tests.TestDoubles;

internal sealed class FakeClock(DateTime? initial = null) : IClock
{
    private readonly Queue<int> jitter = new();
    internal List<TimeSpan> Delays { get; } = [];
    public DateTime UtcNow { get; private set; } = initial ?? new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
    public int NextJitterMilliseconds(int exclusiveUpperBound) =>
        jitter.Count == 0 ? 0 : Math.Min(jitter.Dequeue(), exclusiveUpperBound - 1);
    internal void Advance(TimeSpan value) => UtcNow += value;
    internal void EnqueueJitter(params int[] values) { foreach (var value in values) jitter.Enqueue(value); }
}
