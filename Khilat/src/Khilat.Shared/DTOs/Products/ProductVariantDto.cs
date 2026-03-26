namespace Khilat.Shared.DTOs.Products;

public class ProductVariantDto
{
    public Guid Id { get; set; }
    public string Color { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Craftsmanship { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsAvailable { get; set; }
    public string Sku { get; set; } = string.Empty;
}
