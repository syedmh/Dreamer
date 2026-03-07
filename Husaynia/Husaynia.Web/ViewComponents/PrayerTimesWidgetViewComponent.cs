using Husaynia.Core.Interfaces;
using Husaynia.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Husaynia.Web.ViewComponents
{
    public class PrayerTimesWidgetViewComponent : ViewComponent
    {
        private readonly IPrayerTimeService _prayerTimeService;

        public PrayerTimesWidgetViewComponent(IPrayerTimeService prayerTimeService)
        {
            _prayerTimeService = prayerTimeService;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var prayerTime = await _prayerTimeService.GetTodaysPrayerTimesAsync();

            if (prayerTime == null)
            {
                return View("Error");
            }

            var viewModel = new PrayerTimesViewModel
            {
                Date = prayerTime.Date,
                Fajr = prayerTime.Fajr,
                Sunrise = prayerTime.Sunrise,
                Dhuhr = prayerTime.Dhuhr,
                Asr = prayerTime.Asr,
                Maghrib = prayerTime.Maghrib,
                Isha = prayerTime.Isha
            };

            return View(viewModel);
        }
    }
}
