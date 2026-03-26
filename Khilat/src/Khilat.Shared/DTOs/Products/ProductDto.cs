namespace Khilat.Shared.DTOs.Products;

public class ProductDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public string PrimaryImageUrl { get; set; } = string.Empty;
    public List<ProductVariantDto> Variants { get; set; } = new();
    public List<ProductImageDto> Images { get; set; } = new();
    public List<SizeChartDto> SizeChart { get; set; } = new();
}
