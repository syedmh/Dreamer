using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Entities;

public class Item
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public int CategoryId { get; set; }

    public ItemType ItemType { get; set; } = ItemType.ForSale;

    public decimal? Price { get; set; }

    [MaxLength(500)]
    public string? BarterPreference { get; set; }

    public ItemStatus Status { get; set; } = ItemStatus.Available;

    public int Quantity { get; set; } = 1;

    public DateTime? ExpirationDate { get; set; }

    [Required]
    public int LocationId { get; set; }

    public bool IsActive { get; set; } = true;

    public int Views { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ApplicationUser User { get; set; } = null!;
    public virtual Category Category { get; set; } = null!;
    public virtual Location Location { get; set; } = null!;
    public virtual ICollection<ItemImage> Images { get; set; } = new List<ItemImage>();
    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public virtual ICollection<Review> Reviews { get; set; } = new List<Review>();
}
