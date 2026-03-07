using Microsoft.AspNetCore.Identity;

namespace ShareApp.Web.Entities;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? Bio { get; set; }
    public decimal? Rating { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? DefaultLocationId { get; set; }

    // Navigation properties
    public virtual Location? DefaultLocation { get; set; }
    public virtual ICollection<Location> Locations { get; set; } = new List<Location>();
    public virtual ICollection<Item> ListedItems { get; set; } = new List<Item>();
    public virtual ICollection<Transaction> PurchasedTransactions { get; set; } = new List<Transaction>();
    public virtual ICollection<Transaction> SoldTransactions { get; set; } = new List<Transaction>();
    public virtual ICollection<Review> GivenReviews { get; set; } = new List<Review>();
    public virtual ICollection<Review> ReceivedReviews { get; set; } = new List<Review>();
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
