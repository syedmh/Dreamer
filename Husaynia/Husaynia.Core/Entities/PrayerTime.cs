namespace Husaynia.Core.Entities
{
    public class PrayerTime
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public TimeSpan Fajr { get; set; }
        public TimeSpan Sunrise { get; set; }
        public TimeSpan Dhuhr { get; set; }
        public TimeSpan Asr { get; set; }
        public TimeSpan Maghrib { get; set; }
        public TimeSpan Isha { get; set; }
    }
}
