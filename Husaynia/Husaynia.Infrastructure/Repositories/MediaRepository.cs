using Husaynia.Core.Entities;
using Husaynia.Core.Enums;
using Husaynia.Core.Interfaces;
using Husaynia.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Repositories
{
    public class MediaRepository : IMediaRepository
    {
        private readonly ApplicationDbContext _context;

        public MediaRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<MediaItem?> GetByIdAsync(int id)
        {
            return await _context.MediaItems.FindAsync(id);
        }

        public async Task<List<MediaItem>> GetAllAsync()
        {
            return await _context.MediaItems
                .OrderByDescending(m => m.UploadDate)
                .ToListAsync();
        }

        public async Task<List<MediaItem>> GetByTypeAsync(MediaType type)
        {
            return await _context.MediaItems
                .Where(m => m.Type == type)
                .OrderByDescending(m => m.UploadDate)
                .ToListAsync();
        }

        public async Task<List<MediaItem>> GetByAlbumAsync(string album)
        {
            return await _context.MediaItems
                .Where(m => m.Album == album)
                .OrderBy(m => m.DisplayOrder)
                .ThenByDescending(m => m.UploadDate)
                .ToListAsync();
        }

        public async Task<List<string>> GetAlbumsAsync()
        {
            return await _context.MediaItems
                .Where(m => !string.IsNullOrEmpty(m.Album))
                .Select(m => m.Album!)
                .Distinct()
                .OrderBy(a => a)
                .ToListAsync();
        }

        public async Task<MediaItem> AddAsync(MediaItem mediaItem)
        {
            _context.MediaItems.Add(mediaItem);
            await _context.SaveChangesAsync();
            return mediaItem;
        }

        public async Task UpdateAsync(MediaItem mediaItem)
        {
            _context.Entry(mediaItem).State = EntityState.Modified;
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id)
        {
            var mediaItem = await _context.MediaItems.FindAsync(id);
            if (mediaItem != null)
            {
                _context.MediaItems.Remove(mediaItem);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<List<MediaItem>> GetPhotoGalleryAsync(int pageNumber = 1, int pageSize = 12)
        {
            return await _context.MediaItems
                .Where(m => m.Type == MediaType.Photo)
                .OrderByDescending(m => m.UploadDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<List<MediaItem>> GetVideoGalleryAsync(int pageNumber = 1, int pageSize = 12)
        {
            return await _context.MediaItems
                .Where(m => m.Type == MediaType.Video)
                .OrderByDescending(m => m.UploadDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }
    }
}
