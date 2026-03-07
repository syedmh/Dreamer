using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Husaynia.Web.Areas.Admin.Pages.Announcements
{
    [Authorize]
    public class AnnouncementsIndexModel : PageModel
    {
        private readonly IAnnouncementRepository _repository;

        public AnnouncementsIndexModel(IAnnouncementRepository repository)
        {
            _repository = repository;
        }

        public List<Announcement> Announcements { get; set; } = new();

        public async Task OnGetAsync()
        {
            Announcements = await _repository.GetAllAsync();
        }
    }
}
