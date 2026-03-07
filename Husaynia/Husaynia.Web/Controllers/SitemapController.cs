using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Xml;

namespace Husaynia.Web.Controllers
{
    public class SitemapController : Controller
    {
        private readonly IAnnouncementRepository _announcementRepository;
        private readonly IEventRepository _eventRepository;
        private readonly ILogger<SitemapController> _logger;

        public SitemapController(
            IAnnouncementRepository announcementRepository,
            IEventRepository eventRepository,
            ILogger<SitemapController> logger)
        {
            _announcementRepository = announcementRepository;
            _eventRepository = eventRepository;
            _logger = logger;
        }

        [HttpGet("sitemap.xml")]
        [ResponseCache(Duration = 3600)]
        public async Task<IActionResult> Index()
        {
            try
            {
                var baseUrl = $"{Request.Scheme}://{Request.Host}";

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

                // Add static pages
                AddUrl(sb, baseUrl, "/", "daily", "1.0");
                AddUrl(sb, baseUrl, "/Announcements", "daily", "0.9");
                AddUrl(sb, baseUrl, "/Calendar/PrayerTimings", "daily", "0.9");
                AddUrl(sb, baseUrl, "/Calendar/UpcomingPrograms", "daily", "0.9");
                AddUrl(sb, baseUrl, "/Calendar/IslamicCalendar", "monthly", "0.7");
                AddUrl(sb, baseUrl, "/Media/Photos", "weekly", "0.7");
                AddUrl(sb, baseUrl, "/Media/Videos", "weekly", "0.7");
                AddUrl(sb, baseUrl, "/Donations/BuildHusaynia", "weekly", "0.8");
                AddUrl(sb, baseUrl, "/About/Overview", "monthly", "0.6");
                AddUrl(sb, baseUrl, "/About/Contact", "monthly", "0.6");
                AddUrl(sb, baseUrl, "/About/Duas", "monthly", "0.7");
                AddUrl(sb, baseUrl, "/About/HadithAlKisa", "monthly", "0.7");
                AddUrl(sb, baseUrl, "/About/Privacy", "yearly", "0.3");

                // Add announcements
                var announcements = await _announcementRepository.GetAllAsync();
                foreach (var announcement in announcements.Where(a => a.IsPublished).Take(100))
                {
                    AddUrl(sb, baseUrl, $"/Announcements/Details/{announcement.Id}", "weekly", "0.7", announcement.PublishedDate);
                }

                // Add events
                var events = await _eventRepository.GetUpcomingEventsAsync(100);
                foreach (var evt in events.Where(e => e.IsPublished).Take(100))
                {
                    AddUrl(sb, baseUrl, $"/Calendar/EventDetails/{evt.Id}", "weekly", "0.7", evt.StartDateTime);
                }

                sb.AppendLine("</urlset>");

                return Content(sb.ToString(), "application/xml", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating sitemap");
                return StatusCode(500);
            }
        }

        private void AddUrl(StringBuilder sb, string baseUrl, string path, string changefreq, string priority, DateTime? lastmod = null)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{baseUrl}{path}</loc>");
            if (lastmod.HasValue)
            {
                sb.AppendLine($"    <lastmod>{lastmod.Value:yyyy-MM-dd}</lastmod>");
            }
            sb.AppendLine($"    <changefreq>{changefreq}</changefreq>");
            sb.AppendLine($"    <priority>{priority}</priority>");
            sb.AppendLine("  </url>");
        }
    }
}
