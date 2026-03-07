using ShareApp.Web.Entities;

namespace ShareApp.Web.Models.ViewModels;

public class ItemDetailsViewModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public ItemType ItemType { get; set; }
    public decimal? Price { get; set; }
    public string? BarterPreference { get; set; }
    public ItemStatus Status { get; set; }
    public int Quantity { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public int Views { get; set; }
    public DateTime CreatedAt { get; set; }

    // User info
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public decimal? UserRating { get; set; }

    // Location info
    public string LocationAddress { get; set; } = string.Empty;
    public string LocationCity { get; set; } = string.Empty;
    public string LocationState { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }

    // Images
    public List<string> ImageUrls { get; set; } = new();

    // For current user
    public bool IsOwner { get; set; }
}
