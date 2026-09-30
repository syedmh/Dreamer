using Husaynia.Application.Operations.Health;
using Husaynia.Domain.Operations.Jobs;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Operations.Health;

public sealed class SqlDatabaseHealthProbe(HusayniaDbContext dbContext)
    : IOperationalHealthProbe
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HealthProbeResult(
            "sql_database",
            HealthComponentKind.Dependency,
            canConnect ? OperationalHealthStatus.Healthy : OperationalHealthStatus.Unhealthy,
            canConnect ? "database_available" : "database_unavailable");
    }
}

public sealed class DurableJobHealthProbe(
    HusayniaDbContext dbContext,
    TimeProvider timeProvider) : IOperationalHealthProbe
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var backlog = await dbContext.Set<JobInstance>()
            .AsNoTracking()
            .CountAsync(
                job => (job.State == JobInstanceState.Pending ||
                        job.State == JobInstanceState.RetryScheduled) &&
                    job.NextRunAtUtc <= now,
                cancellationToken)
            .ConfigureAwait(false);
        var deadLetters = await dbContext.Set<JobInstance>()
            .AsNoTracking()
            .CountAsync(
                job => job.State == JobInstanceState.DeadLettered,
                cancellationToken)
            .ConfigureAwait(false);
        var status = backlog >= 1_000
            ? OperationalHealthStatus.Unhealthy
            : backlog >= 100 || deadLetters > 0
                ? OperationalHealthStatus.Degraded
                : OperationalHealthStatus.Healthy;
        return new HealthProbeResult(
            "durable_jobs",
            HealthComponentKind.JobBacklog,
            status,
            status switch
            {
                OperationalHealthStatus.Unhealthy => "job_backlog_unhealthy",
                OperationalHealthStatus.Degraded when deadLetters > 0 => "job_dead_letters_present",
                OperationalHealthStatus.Degraded => "job_backlog_degraded",
                _ => "job_queue_healthy",
            });
    }
}
