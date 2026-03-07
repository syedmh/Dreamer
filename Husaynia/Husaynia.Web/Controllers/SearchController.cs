using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Husaynia.Web.Controllers
{
    public class SearchController : Controller
    {
        private readonly IAnnouncementRepository _announcementRepository;
        private readonly IEventRepository _eventRepository;
        private readonly ILogger<SearchController> _logger;

        public SearchController(
            IAnnouncementRepository announcementRepository,
            IEventRepository eventRepository,
            ILogger<SearchController> logger)
        {
            _announcementRepository = announcementRepository;
            _eventRepository = eventRepository;
            _logger = logger;
        }

        public async Task<IActionResult> Index(string q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                ViewBag.Query = string.Empty;
                ViewBag.Announcements = new List<Announcement>();
                ViewBag.Events = new List<Event>();
                return View();
            }

            var query = q.Trim().ToLower();
            ViewBag.Query = q;

            try
            {
                // Search announcements
                var allAnnouncements = await _announcementRepository.GetAllAsync();
                var matchingAnnouncements = allAnnouncements
                    .Where(a => a.IsPublished && (
                        a.Title.ToLower().Contains(query) ||
                        a.Content.ToLower().Contains(query) ||
                        (a.Tags != null && a.Tags.ToLower().Contains(query))
                    ))
                    .Take(20)
                    .ToList();

                // Search events
                var allEvents = await _eventRepository.GetUpcomingEventsAsync(100);
                var matchingEvents = allEvents
                    .Where(e => e.IsPublished && (
                        e.Title.ToLower().Contains(query) ||
                        e.Description.ToLower().Contains(query) ||
                        (e.Category != null && e.Category.ToLower().Contains(query)) ||
                        (e.Location != null && e.Location.ToLower().Contains(query))
                    ))
                    .Take(20)
                    .ToList();

                ViewBag.Announcements = matchingAnnouncements;
                ViewBag.Events = matchingEvents;
                ViewBag.TotalResults = matchingAnnouncements.Count + matchingEvents.Count;

                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing search for query: {Query}", query);
                ViewBag.Announcements = new List<Announcement>();
                ViewBag.Events = new List<Event>();
                ViewBag.Error = "An error occurred while searching. Please try again.";
                return View();
            }
        }
    }
}
