using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Husaynia.Web.Controllers
{
    public class AnnouncementsController : Controller
    {
        private readonly IAnnouncementRepository _announcementRepository;
        private readonly ILogger<AnnouncementsController> _logger;

        public AnnouncementsController(
            IAnnouncementRepository announcementRepository,
            ILogger<AnnouncementsController> logger)
        {
            _announcementRepository = announcementRepository;
            _logger = logger;
        }

        public async Task<IActionResult> Index(int page = 1)
        {
            const int pageSize = 10;

            var announcements = await _announcementRepository.GetAllPublishedAsync(page, pageSize);
            var totalCount = await _announcementRepository.GetPublishedCountAsync();
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.HasPreviousPage = page > 1;
            ViewBag.HasNextPage = page < totalPages;

            return View(announcements);
        }

        public async Task<IActionResult> Details(int id)
        {
            var announcement = await _announcementRepository.GetByIdAsync(id);

            if (announcement == null || !announcement.IsPublished)
            {
                return NotFound();
            }

            // Increment view count
            await _announcementRepository.IncrementViewCountAsync(id);

            return View(announcement);
        }
    }
}
