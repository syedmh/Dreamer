using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Entities;

public class ItemImage
{
    public int Id { get; set; }

    [Required]
    public int ItemId { get; set; }

    [Required]
    [MaxLength(500)]
    public string ImageUrl { get; set; } = string.Empty;

    public bool IsPrimary { get; set; } = false;

    public int DisplayOrder { get; set; } = 0;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual Item Item { get; set; } = null!;
}
