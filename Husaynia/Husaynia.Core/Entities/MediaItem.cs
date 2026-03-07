using Husaynia.Core.Enums;

namespace Husaynia.Core.Entities
{
    public class MediaItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public MediaType Type { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public string? Album { get; set; }
        public DateTime UploadDate { get; set; } = DateTime.UtcNow;
        public int DisplayOrder { get; set; }
        public string? Tags { get; set; }
    }
}
