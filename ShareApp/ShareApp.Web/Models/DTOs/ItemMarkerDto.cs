using ShareApp.Web.Entities;

namespace ShareApp.Web.Models.DTOs;

public class ItemMarkerDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty; // String for easier JSON serialization
    public decimal? Price { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ImageUrl { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public double Distance { get; set; } // Distance in miles from user location
}
