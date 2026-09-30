using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Prayer;

namespace Husaynia.Web.Areas.Admin.Prayer;

public sealed class PrayerRefreshJobStartupService(
    IServiceScopeFactory scopeFactory) : IHostedService
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly string correlationId = $"prayer-startup-{Guid.NewGuid():N}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var correlation = scope.ServiceProvider.GetRequiredService<ICorrelationContext>();
        using var _ = correlation.Begin(
            correlationId,
            "prayer-refresh-job-startup");
        var coordinator = scope.ServiceProvider
            .GetRequiredService<IPrayerRefreshJobCoordinator>();
        await coordinator.RegisterAndEnqueueCatchUpAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
