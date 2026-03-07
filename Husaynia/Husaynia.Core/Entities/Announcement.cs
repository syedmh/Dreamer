namespace Husaynia.Core.Entities
{
    public class Announcement
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public DateTime PublishedDate { get; set; } = DateTime.UtcNow;
        public string Author { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool IsPinned { get; set; }
        public string? Tags { get; set; }
        public int ViewCount { get; set; }
        public bool IsPublished { get; set; } = true;
    }
}
