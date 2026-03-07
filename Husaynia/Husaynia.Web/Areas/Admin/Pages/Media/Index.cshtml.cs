using Husaynia.Core.Entities;
using Husaynia.Core.Enums;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Husaynia.Web.Areas.Admin.Pages.Media
{
    [Authorize]
    public class MediaIndexModel : PageModel
    {
        private readonly IMediaRepository _repository;

        public MediaIndexModel(IMediaRepository repository)
        {
            _repository = repository;
        }

        public int PhotoCount { get; set; }
        public int VideoCount { get; set; }
        public List<MediaItem> RecentMedia { get; set; } = new();

        public async Task OnGetAsync()
        {
            var allMedia = await _repository.GetAllAsync();
            PhotoCount = allMedia.Count(m => m.Type == MediaType.Photo);
            VideoCount = allMedia.Count(m => m.Type == MediaType.Video);
            RecentMedia = allMedia.Take(12).ToList();
        }
    }
}
