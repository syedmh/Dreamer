namespace Khilat.Shared.DTOs.Cart;

public class CartItemDto
{
    public Guid Id { get; set; }
    public Guid ProductVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Craftsmanship { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}
