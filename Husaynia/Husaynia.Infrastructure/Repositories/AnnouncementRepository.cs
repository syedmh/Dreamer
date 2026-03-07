using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Repositories
{
    public class AnnouncementRepository : IAnnouncementRepository
    {
        private readonly ApplicationDbContext _context;

        public AnnouncementRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Announcement?> GetByIdAsync(int id)
        {
            return await _context.Announcements
                .FirstOrDefaultAsync(a => a.Id == id);
        }

        public async Task<List<Announcement>> GetAllAsync()
        {
            return await _context.Announcements
                .OrderByDescending(a => a.IsPinned)
                .ThenByDescending(a => a.PublishedDate)
                .ToListAsync();
        }

        public async Task<IEnumerable<Announcement>> GetAllPublishedAsync(int pageNumber = 1, int pageSize = 10)
        {
            return await _context.Announcements
                .Where(a => a.IsPublished)
                .OrderByDescending(a => a.IsPinned)
                .ThenByDescending(a => a.PublishedDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<IEnumerable<Announcement>> GetRecentAsync(int count = 5)
        {
            return await _context.Announcements
                .Where(a => a.IsPublished)
                .OrderByDescending(a => a.PublishedDate)
                .Take(count)
                .ToListAsync();
        }

        public async Task<int> GetPublishedCountAsync()
        {
            return await _context.Announcements
                .Where(a => a.IsPublished)
                .CountAsync();
        }

        public async Task AddAsync(Announcement announcement)
        {
            _context.Announcements.Add(announcement);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Announcement announcement)
        {
            _context.Announcements.Update(announcement);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id)
        {
            var announcement = await GetByIdAsync(id);
            if (announcement != null)
            {
                _context.Announcements.Remove(announcement);
                await _context.SaveChangesAsync();
            }
        }

        public async Task IncrementViewCountAsync(int id)
        {
            var announcement = await GetByIdAsync(id);
            if (announcement != null)
            {
                announcement.ViewCount++;
                await _context.SaveChangesAsync();
            }
        }
    }
}
