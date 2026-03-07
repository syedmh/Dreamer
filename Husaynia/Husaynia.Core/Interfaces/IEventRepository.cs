using Husaynia.Core.Entities;

namespace Husaynia.Core.Interfaces
{
    public interface IEventRepository
    {
        Task<Event?> GetByIdAsync(int id);
        Task<IEnumerable<Event>> GetAllPublishedAsync();
        Task<IEnumerable<Event>> GetUpcomingEventsAsync(int count = 50);
        Task<IEnumerable<Event>> GetEventsBetweenDatesAsync(DateTime startDate, DateTime endDate);
        Task<IEnumerable<Event>> GetEventsByMonthAsync(int year, int month);
        Task<int> GetPublishedCountAsync();
        Task AddAsync(Event eventItem);
        Task UpdateAsync(Event eventItem);
        Task DeleteAsync(int id);
    }
}
