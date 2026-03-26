namespace Khilat.Shared.DTOs.Products;

public class ProductImageDto
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string AltText { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
}
