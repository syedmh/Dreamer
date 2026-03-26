namespace Khilat.Core.Entities;

using Khilat.Core.Enums;

public class ProductVariant
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public ProductColor Color { get; set; }
    public ProductSize Size { get; set; }
    public CraftsmanshipType Craftsmanship { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public string Sku { get; set; } = string.Empty;
    public bool IsAvailable => StockQuantity > 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Product Product { get; set; } = null!;
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
