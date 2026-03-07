using Husaynia.Core.Entities;

namespace Husaynia.Core.Interfaces
{
    public interface IAnnouncementRepository
    {
        Task<Announcement?> GetByIdAsync(int id);
        Task<List<Announcement>> GetAllAsync();
        Task<IEnumerable<Announcement>> GetAllPublishedAsync(int pageNumber = 1, int pageSize = 10);
        Task<IEnumerable<Announcement>> GetRecentAsync(int count = 5);
        Task<int> GetPublishedCountAsync();
        Task AddAsync(Announcement announcement);
        Task UpdateAsync(Announcement announcement);
        Task DeleteAsync(int id);
        Task IncrementViewCountAsync(int id);
    }
}
