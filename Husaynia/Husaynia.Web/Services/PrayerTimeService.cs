using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Husaynia.Web.Services
{
    public class PrayerTimeService : IPrayerTimeService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;
        private readonly ILogger<PrayerTimeService> _logger;

        private const string CACHE_KEY_PREFIX = "PrayerTimes_";

        public PrayerTimeService(
            ApplicationDbContext context,
            IConfiguration configuration,
            IMemoryCache cache,
            ILogger<PrayerTimeService> logger)
        {
            _context = context;
            _configuration = configuration;
            _cache = cache;
            _logger = logger;
        }

        public async Task<PrayerTime?> GetTodaysPrayerTimesAsync()
        {
            return await GetPrayerTimesForDateAsync(DateTime.Today);
        }

        public async Task<PrayerTime?> GetPrayerTimesForDateAsync(DateTime date)
        {
            var dateOnly = date.Date;
            var cacheKey = $"{CACHE_KEY_PREFIX}{dateOnly:yyyyMMdd}";

            // Try to get from memory cache first
            if (_cache.TryGetValue(cacheKey, out PrayerTime? cachedPrayerTime))
            {
                _logger.LogDebug("Prayer times for {Date} found in memory cache", dateOnly);
                return cachedPrayerTime;
            }

            // Try to get from database
            var prayerTime = await _context.PrayerTimes
                .FirstOrDefaultAsync(pt => pt.Date.Date == dateOnly);

            if (prayerTime != null)
            {
                _logger.LogDebug("Prayer times for {Date} found in database", dateOnly);
                // Cache in memory for 1 hour
                _cache.Set(cacheKey, prayerTime, TimeSpan.FromHours(1));
                return prayerTime;
            }

            // Calculate and save
            _logger.LogInformation("Calculating prayer times for {Date}", dateOnly);
            prayerTime = CalculatePrayerTime(dateOnly);

            _context.PrayerTimes.Add(prayerTime);
            await _context.SaveChangesAsync();

            // Cache in memory
            _cache.Set(cacheKey, prayerTime, TimeSpan.FromHours(1));

            return prayerTime;
        }

        public async Task<List<PrayerTime>> GetMonthlyPrayerTimesAsync(int year, int month)
        {
            var firstDayOfMonth = new DateTime(year, month, 1);
            var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

            var prayerTimes = await _context.PrayerTimes
                .Where(pt => pt.Date >= firstDayOfMonth && pt.Date <= lastDayOfMonth)
                .OrderBy(pt => pt.Date)
                .ToListAsync();

            // If we don't have all days, calculate missing ones
            var daysInMonth = DateTime.DaysInMonth(year, month);
            if (prayerTimes.Count < daysInMonth)
            {
                var existingDates = prayerTimes.Select(pt => pt.Date.Date).ToHashSet();
                var missingPrayerTimes = new List<PrayerTime>();

                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(year, month, day);
                    if (!existingDates.Contains(date))
                    {
                        var prayerTime = CalculatePrayerTime(date);
                        missingPrayerTimes.Add(prayerTime);
                    }
                }

                if (missingPrayerTimes.Any())
                {
                    _context.PrayerTimes.AddRange(missingPrayerTimes);
                    await _context.SaveChangesAsync();
                    prayerTimes.AddRange(missingPrayerTimes);
                    prayerTimes = prayerTimes.OrderBy(pt => pt.Date).ToList();
                }
            }

            return prayerTimes;
        }

        public async Task CachePrayerTimesAsync(int daysAhead = 30)
        {
            _logger.LogInformation("Caching prayer times for the next {Days} days", daysAhead);

            var today = DateTime.Today;
            var calculatedCount = 0;

            for (int i = 0; i < daysAhead; i++)
            {
                var date = today.AddDays(i);
                var exists = await _context.PrayerTimes.AnyAsync(pt => pt.Date.Date == date);

                if (!exists)
                {
                    var prayerTime = CalculatePrayerTime(date);
                    _context.PrayerTimes.Add(prayerTime);
                    calculatedCount++;
                }
            }

            if (calculatedCount > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Calculated and saved {Count} prayer times", calculatedCount);
            }
            else
            {
                _logger.LogInformation("All prayer times already cached");
            }
        }

        private PrayerTime CalculatePrayerTime(DateTime date)
        {
            // Get configuration
            var latitude = _configuration.GetValue<double>("PrayerTimeSettings:Latitude");
            var longitude = _configuration.GetValue<double>("PrayerTimeSettings:Longitude");
            var fajrAngle = _configuration.GetValue<double>("PrayerTimeSettings:FajrAngle");
            var ishaAngle = _configuration.GetValue<double>("PrayerTimeSettings:IshaAngle");

            // Calculate Julian date
            var jd = GetJulianDate(date.Year, date.Month, date.Day);

            // Calculate prayer times using astronomical formulas
            var times = ComputePrayerTimes(jd, latitude, longitude, -8, fajrAngle, ishaAngle);

            return new PrayerTime
            {
                Date = date,
                Fajr = times.Fajr,
                Sunrise = times.Sunrise,
                Dhuhr = times.Dhuhr,
                Asr = times.Asr,
                Maghrib = times.Maghrib,
                Isha = times.Isha
            };
        }

        private double GetJulianDate(int year, int month, int day)
        {
            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }
            double A = Math.Floor(year / 100.0);
            double B = 2 - A + Math.Floor(A / 4.0);
            return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + B - 1524.5;
        }

        private (TimeSpan Fajr, TimeSpan Sunrise, TimeSpan Dhuhr, TimeSpan Asr, TimeSpan Maghrib, TimeSpan Isha)
            ComputePrayerTimes(double jd, double lat, double lng, double timezone, double fajrAngle, double ishaAngle)
        {
            // Calculate sun position
            var d = jd - 2451545.0;
            var g = 357.529 + 0.98560028 * d;
            var q = 280.459 + 0.98564736 * d;
            var L = q + 1.915 * Math.Sin(DegToRad(g)) + 0.020 * Math.Sin(DegToRad(2 * g));

            var e = 23.439 - 0.00000036 * d;
            var RA = RadToDeg(Math.Atan2(Math.Cos(DegToRad(e)) * Math.Sin(DegToRad(L)), Math.Cos(DegToRad(L)))) / 15.0;
            var D = RadToDeg(Math.Asin(Math.Sin(DegToRad(e)) * Math.Sin(DegToRad(L))));

            // Equation of time
            var EqT = q / 15.0 - RA;

            // Calculate times
            var dhuhrTime = 12 + timezone - lng / 15.0 - EqT;

            var sunriseTime = dhuhrTime - GetArcTime(90.833, lat, D) / 15.0;
            var fajrTime = dhuhrTime - GetArcTime(90 + fajrAngle, lat, D) / 15.0;
            var asrTime = dhuhrTime + GetAsrTime(1, lat, D) / 15.0; // Shafi method
            var maghribTime = dhuhrTime + GetArcTime(90.833, lat, D) / 15.0;
            var ishaTime = dhuhrTime + GetArcTime(90 + ishaAngle, lat, D) / 15.0;

            return (
                ConvertToTimeSpan(fajrTime),
                ConvertToTimeSpan(sunriseTime),
                ConvertToTimeSpan(dhuhrTime),
                ConvertToTimeSpan(asrTime),
                ConvertToTimeSpan(maghribTime),
                ConvertToTimeSpan(ishaTime)
            );
        }

        private double GetArcTime(double angle, double lat, double dec)
        {
            var arg = (-Math.Sin(DegToRad(angle)) - Math.Sin(DegToRad(lat)) * Math.Sin(DegToRad(dec))) /
                      (Math.Cos(DegToRad(lat)) * Math.Cos(DegToRad(dec)));

            if (arg < -1) arg = -1;
            if (arg > 1) arg = 1;

            return RadToDeg(Math.Acos(arg));
        }

        private double GetAsrTime(int factor, double lat, double dec)
        {
            var angle = -Math.Atan(1.0 / (factor + Math.Tan(DegToRad(Math.Abs(lat - dec)))));
            return GetArcTime(RadToDeg(angle), lat, dec);
        }

        private TimeSpan ConvertToTimeSpan(double time)
        {
            time = time - Math.Floor(time / 24.0) * 24.0;
            var hours = (int)Math.Floor(time);
            var minutes = (int)Math.Floor((time - hours) * 60);
            var seconds = (int)Math.Floor(((time - hours) * 60 - minutes) * 60);
            return new TimeSpan(hours, minutes, seconds);
        }

        private double DegToRad(double deg) => deg * Math.PI / 180.0;
        private double RadToDeg(double rad) => rad * 180.0 / Math.PI;
    }
}
