using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Husaynia.Web.Areas.Admin.Pages.Events
{
    [Authorize]
    public class EventsIndexModel : PageModel
    {
        private readonly IEventRepository _repository;

        public EventsIndexModel(IEventRepository repository)
        {
            _repository = repository;
        }

        public List<Event> Events { get; set; } = new();

        public async Task OnGetAsync()
        {
            Events = (await _repository.GetUpcomingEventsAsync(100)).ToList();
        }
    }
}
