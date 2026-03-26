namespace Khilat.Core.Entities;

using Khilat.Core.Enums;

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductVariantId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal => Quantity * UnitPrice;

    // Snapshot at time of order
    public string ProductName { get; set; } = string.Empty;
    public ProductColor Color { get; set; }
    public ProductSize Size { get; set; }
    public CraftsmanshipType Craftsmanship { get; set; }

    // Navigation
    public Order Order { get; set; } = null!;
    public ProductVariant ProductVariant { get; set; } = null!;
}
