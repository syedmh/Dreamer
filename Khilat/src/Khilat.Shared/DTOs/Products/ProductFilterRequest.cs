namespace Khilat.Shared.DTOs.Products;

public class ProductFilterRequest
{
    public string? Color { get; set; }
    public string? Size { get; set; }
    public string? Craftsmanship { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? SortBy { get; set; } = "price";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
