namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
