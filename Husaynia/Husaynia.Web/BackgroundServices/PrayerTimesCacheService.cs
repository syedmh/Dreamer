using Husaynia.Core.Interfaces;

namespace Husaynia.Web.BackgroundServices
{
    public class PrayerTimesCacheService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PrayerTimesCacheService> _logger;

        public PrayerTimesCacheService(
            IServiceProvider serviceProvider,
            ILogger<PrayerTimesCacheService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Prayer Times Cache Service is starting");

            // Initial cache on startup
            await CachePrayerTimesAsync(stoppingToken);

            // Schedule daily updates at midnight
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var nextMidnight = DateTime.Today.AddDays(1);
                var delay = nextMidnight - now;

                _logger.LogInformation("Next prayer times cache update scheduled for {Time}", nextMidnight);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                    await CachePrayerTimesAsync(stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogInformation("Prayer Times Cache Service is stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Prayer Times Cache Service");
                    // Wait 1 hour before retrying on error
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        private async Task CachePrayerTimesAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var prayerTimeService = scope.ServiceProvider.GetRequiredService<IPrayerTimeService>();

                _logger.LogInformation("Caching prayer times for the next 30 days");
                await prayerTimeService.CachePrayerTimesAsync(30);
                _logger.LogInformation("Prayer times cached successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cache prayer times");
            }
        }
    }
}
