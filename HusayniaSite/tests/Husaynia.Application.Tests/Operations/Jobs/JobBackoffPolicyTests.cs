using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;

namespace Husaynia.Application.Tests.Operations.Jobs;

public sealed class JobBackoffPolicyTests
{
    [Fact]
    public void ExponentialBackoffIsBoundedAndJitteredWithinConfiguredRange()
    {
        var options = new DurableJobOptions { JitterRatio = 0.2 };
        var policy = new ExponentialJitterBackoffPolicy(options, new Random(42));
        var job = new AcquiredJob(
            Guid.NewGuid(),
            "definition",
            "handler",
            "{}",
            "idempotency",
            "correlation",
            10,
            20,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1),
            new WorkerIdentity("worker"),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            false);

        var delay = policy.GetDelay(job);

        Assert.InRange(delay, TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(72));
    }
}
