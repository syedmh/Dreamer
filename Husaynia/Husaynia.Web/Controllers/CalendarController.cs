using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using System.Text;

namespace Husaynia.Web.Controllers
{
    public class CalendarController : Controller
    {
        private readonly IEventRepository _eventRepository;
        private readonly IPrayerTimeService _prayerTimeService;
        private readonly ILogger<CalendarController> _logger;

        public CalendarController(
            IEventRepository eventRepository,
            IPrayerTimeService prayerTimeService,
            ILogger<CalendarController> logger)
        {
            _eventRepository = eventRepository;
            _prayerTimeService = prayerTimeService;
            _logger = logger;
        }

        public async Task<IActionResult> PrayerTimings(int? year, int? month)
        {
            var targetDate = new DateTime(
                year ?? DateTime.Now.Year,
                month ?? DateTime.Now.Month,
                1);

            var prayerTimes = await _prayerTimeService.GetMonthlyPrayerTimesAsync(
                targetDate.Year,
                targetDate.Month);

            ViewBag.Year = targetDate.Year;
            ViewBag.Month = targetDate.Month;
            ViewBag.MonthName = targetDate.ToString("MMMM yyyy");

            return View(prayerTimes);
        }

        public IActionResult IslamicCalendar()
        {
            // For now, this is a placeholder page
            // You could integrate with an Islamic calendar API or library
            return View();
        }

        public async Task<IActionResult> UpcomingPrograms()
        {
            var upcomingEvents = await _eventRepository.GetUpcomingEventsAsync(50);
            return View(upcomingEvents);
        }

        public async Task<IActionResult> EventDetails(int id)
        {
            var eventItem = await _eventRepository.GetByIdAsync(id);

            if (eventItem == null || !eventItem.IsPublished)
            {
                return NotFound();
            }

            return View(eventItem);
        }

        [HttpGet]
        public async Task<JsonResult> GetEventsJson(DateTime start, DateTime end)
        {
            var events = await _eventRepository.GetEventsBetweenDatesAsync(start, end);

            var calendarEvents = events.Select(e => new
            {
                id = e.Id,
                title = e.Title,
                start = e.StartDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                end = e.EndDateTime?.ToString("yyyy-MM-ddTHH:mm:ss"),
                url = Url.Action("EventDetails", new { id = e.Id }),
                backgroundColor = GetCategoryColor(e.Category),
                borderColor = GetCategoryColor(e.Category),
                description = e.Description,
                location = e.Location
            });

            return Json(calendarEvents);
        }

        [HttpGet]
        public async Task<IActionResult> ExportICalendar(int? eventId = null)
        {
            var calendar = new Calendar();
            calendar.ProductId = "-//Husaynia Islamic Society//Events//EN";

            if (eventId.HasValue)
            {
                // Export single event
                var eventItem = await _eventRepository.GetByIdAsync(eventId.Value);
                if (eventItem == null || !eventItem.IsPublished)
                {
                    return NotFound();
                }

                calendar.Events.Add(CreateCalendarEvent(eventItem));
            }
            else
            {
                // Export all upcoming events
                var events = await _eventRepository.GetUpcomingEventsAsync(100);
                foreach (var eventItem in events)
                {
                    calendar.Events.Add(CreateCalendarEvent(eventItem));
                }
            }

            var serializer = new CalendarSerializer();
            var icalString = serializer.SerializeToString(calendar);

            if (string.IsNullOrEmpty(icalString))
            {
                return StatusCode(500, "Failed to generate calendar data");
            }

            var bytes = Encoding.UTF8.GetBytes(icalString);

            var fileName = eventId.HasValue
                ? $"husaynia_event_{eventId}.ics"
                : "husaynia_calendar.ics";

            return File(bytes, "text/calendar", fileName);
        }

        private CalendarEvent CreateCalendarEvent(Husaynia.Core.Entities.Event evt)
        {
            var calendarEvent = new CalendarEvent
            {
                Summary = evt.Title,
                Description = StripHtml(evt.Description),
                Location = evt.Location ?? "Husaynia Islamic Society, 15231 State St, Snohomish, WA",
                Start = new CalDateTime(evt.StartDateTime),
                End = new CalDateTime(evt.EndDateTime ?? evt.StartDateTime.AddHours(2)),
                Uid = $"event-{evt.Id}@husaynia.org",
                Created = new CalDateTime(evt.CreatedAt),
                LastModified = new CalDateTime(evt.UpdatedAt),
                Url = new Uri($"https://husaynia.org/Calendar/EventDetails/{evt.Id}")
            };

            if (!string.IsNullOrEmpty(evt.Category))
            {
                calendarEvent.Categories.Add(evt.Category);
            }

            return calendarEvent;
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            // Simple HTML tag removal
            return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
        }

        private string GetCategoryColor(string category)
        {
            return category?.ToLower() switch
            {
                "lecture" => "#b90000",
                "social" => "#0066cc",
                "religious" => "#006600",
                "education" => "#ff8c00",
                _ => "#666666"
            };
        }
    }
}
