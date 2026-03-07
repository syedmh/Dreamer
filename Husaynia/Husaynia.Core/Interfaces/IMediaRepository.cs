using Husaynia.Core.Entities;
using Husaynia.Core.Enums;

namespace Husaynia.Core.Interfaces
{
    public interface IMediaRepository
    {
        Task<MediaItem?> GetByIdAsync(int id);
        Task<List<MediaItem>> GetAllAsync();
        Task<List<MediaItem>> GetByTypeAsync(MediaType type);
        Task<List<MediaItem>> GetByAlbumAsync(string album);
        Task<List<string>> GetAlbumsAsync();
        Task<MediaItem> AddAsync(MediaItem mediaItem);
        Task UpdateAsync(MediaItem mediaItem);
        Task DeleteAsync(int id);
        Task<List<MediaItem>> GetPhotoGalleryAsync(int pageNumber = 1, int pageSize = 12);
        Task<List<MediaItem>> GetVideoGalleryAsync(int pageNumber = 1, int pageSize = 12);
    }
}
