namespace Husaynia.Web.ViewModels
{
    public class PrayerTimesViewModel
    {
        public DateTime Date { get; set; }
        public TimeSpan Fajr { get; set; }
        public TimeSpan Sunrise { get; set; }
        public TimeSpan Dhuhr { get; set; }
        public TimeSpan Asr { get; set; }
        public TimeSpan Maghrib { get; set; }
        public TimeSpan Isha { get; set; }

        public string FajrFormatted => FormatTime(Fajr);
        public string SunriseFormatted => FormatTime(Sunrise);
        public string DhuhrFormatted => FormatTime(Dhuhr);
        public string AsrFormatted => FormatTime(Asr);
        public string MaghribFormatted => FormatTime(Maghrib);
        public string IshaFormatted => FormatTime(Isha);

        private string FormatTime(TimeSpan time)
        {
            var dateTime = DateTime.Today.Add(time);
            return dateTime.ToString("h:mm tt");
        }
    }
}
