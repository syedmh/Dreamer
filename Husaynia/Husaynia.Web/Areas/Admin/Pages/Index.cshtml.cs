using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Husaynia.Web.Areas.Admin.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly IAnnouncementRepository _announcementRepository;
        private readonly IEventRepository _eventRepository;
        private readonly IMediaRepository _mediaRepository;
        private readonly IDonationRepository _donationRepository;

        public IndexModel(
            IAnnouncementRepository announcementRepository,
            IEventRepository eventRepository,
            IMediaRepository mediaRepository,
            IDonationRepository donationRepository)
        {
            _announcementRepository = announcementRepository;
            _eventRepository = eventRepository;
            _mediaRepository = mediaRepository;
            _donationRepository = donationRepository;
        }

        public int AnnouncementCount { get; set; }
        public int EventCount { get; set; }
        public int MediaCount { get; set; }
        public decimal TotalDonations { get; set; }
        public List<Announcement> RecentAnnouncements { get; set; } = new();
        public List<Event> UpcomingEvents { get; set; } = new();

        public async Task OnGetAsync()
        {
            var allAnnouncements = await _announcementRepository.GetAllAsync();
            AnnouncementCount = allAnnouncements.Count();

            var allEvents = await _eventRepository.GetUpcomingEventsAsync(100);
            EventCount = allEvents.Count();

            var allMedia = await _mediaRepository.GetAllAsync();
            MediaCount = allMedia.Count();

            TotalDonations = await _donationRepository.GetTotalDonationsAsync();

            RecentAnnouncements = (await _announcementRepository.GetRecentAsync(5)).ToList();
            UpcomingEvents = (await _eventRepository.GetUpcomingEventsAsync(5)).ToList();
        }
    }
}
