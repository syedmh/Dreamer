using Husaynia.Core.Entities;

namespace Husaynia.Core.Interfaces
{
    public interface IPrayerTimeService
    {
        Task<PrayerTime?> GetPrayerTimesForDateAsync(DateTime date);
        Task<PrayerTime?> GetTodaysPrayerTimesAsync();
        Task<List<PrayerTime>> GetMonthlyPrayerTimesAsync(int year, int month);
        Task CachePrayerTimesAsync(int daysAhead = 30);
    }
}
