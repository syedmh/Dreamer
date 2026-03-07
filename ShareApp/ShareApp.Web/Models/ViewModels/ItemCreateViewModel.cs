using System.ComponentModel.DataAnnotations;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Models.ViewModels;

public class ItemCreateViewModel
{
    [Required(ErrorMessage = "Title is required")]
    [StringLength(100, ErrorMessage = "Title cannot exceed 100 characters")]
    [Display(Name = "Item Title")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Description is required")]
    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
    [Display(Name = "Description")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required")]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }

    [Required(ErrorMessage = "Item type is required")]
    [Display(Name = "Item Type")]
    public ItemType ItemType { get; set; } = ItemType.ForSale;

    [Display(Name = "Price")]
    [Range(0.01, 10000, ErrorMessage = "Price must be between $0.01 and $10,000")]
    public decimal? Price { get; set; }

    [StringLength(500, ErrorMessage = "Barter preference cannot exceed 500 characters")]
    [Display(Name = "What would you like in exchange?")]
    public string? BarterPreference { get; set; }

    [Required(ErrorMessage = "Quantity is required")]
    [Range(1, 1000, ErrorMessage = "Quantity must be between 1 and 1000")]
    [Display(Name = "Quantity")]
    public int Quantity { get; set; } = 1;

    [Display(Name = "Expiration Date (optional)")]
    [DataType(DataType.Date)]
    public DateTime? ExpirationDate { get; set; }

    [Required(ErrorMessage = "Location is required")]
    [Display(Name = "Pickup Location")]
    public int LocationId { get; set; }

    [Display(Name = "Upload Images (up to 5)")]
    public List<IFormFile>? Images { get; set; }
}
