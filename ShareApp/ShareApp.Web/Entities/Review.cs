using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Entities;

public class Review
{
    public int Id { get; set; }

    public int? ItemId { get; set; }

    [Required]
    public string ReviewerId { get; set; } = string.Empty;

    [Required]
    public string ReviewedUserId { get; set; } = string.Empty;

    [Required]
    [Range(1, 5)]
    public int Rating { get; set; }

    [MaxLength(1000)]
    public string? Comment { get; set; }

    public int? TransactionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual Item? Item { get; set; }
    public virtual ApplicationUser Reviewer { get; set; } = null!;
    public virtual ApplicationUser ReviewedUser { get; set; } = null!;
}
