namespace Khilat.Core.Entities;

public class CartItem
{
    public Guid Id { get; set; }
    public Guid CartId { get; set; }
    public Guid ProductVariantId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal => Quantity * UnitPrice;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Cart Cart { get; set; } = null!;
    public ProductVariant ProductVariant { get; set; } = null!;
}
