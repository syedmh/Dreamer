using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Repositories
{
    public class EventRepository : IEventRepository
    {
        private readonly ApplicationDbContext _context;

        public EventRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Event?> GetByIdAsync(int id)
        {
            return await _context.Events
                .FirstOrDefaultAsync(e => e.Id == id);
        }

        public async Task<IEnumerable<Event>> GetAllPublishedAsync()
        {
            return await _context.Events
                .Where(e => e.IsPublished)
                .OrderBy(e => e.StartDateTime)
                .ToListAsync();
        }

        public async Task<IEnumerable<Event>> GetUpcomingEventsAsync(int count = 50)
        {
            var now = DateTime.Now;
            return await _context.Events
                .Where(e => e.IsPublished && e.StartDateTime >= now)
                .OrderBy(e => e.StartDateTime)
                .Take(count)
                .ToListAsync();
        }

        public async Task<IEnumerable<Event>> GetEventsBetweenDatesAsync(DateTime startDate, DateTime endDate)
        {
            return await _context.Events
                .Where(e => e.IsPublished &&
                           e.StartDateTime >= startDate &&
                           e.StartDateTime <= endDate)
                .OrderBy(e => e.StartDateTime)
                .ToListAsync();
        }

        public async Task<IEnumerable<Event>> GetEventsByMonthAsync(int year, int month)
        {
            var startDate = new DateTime(year, month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            return await GetEventsBetweenDatesAsync(startDate, endDate);
        }

        public async Task<int> GetPublishedCountAsync()
        {
            return await _context.Events
                .Where(e => e.IsPublished)
                .CountAsync();
        }

        public async Task AddAsync(Event eventItem)
        {
            _context.Events.Add(eventItem);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Event eventItem)
        {
            eventItem.UpdatedAt = DateTime.UtcNow;
            _context.Events.Update(eventItem);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id)
        {
            var eventItem = await GetByIdAsync(id);
            if (eventItem != null)
            {
                _context.Events.Remove(eventItem);
                await _context.SaveChangesAsync();
            }
        }
    }
}
