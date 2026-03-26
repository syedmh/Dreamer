namespace Khilat.Core.Interfaces;

using Khilat.Core.Entities;
using Khilat.Core.Enums;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id);
    Task<Product?> GetWithVariantsAsync(Guid id);
    Task<IReadOnlyList<Product>> GetAllAsync();
    Task<IReadOnlyList<ProductVariant>> GetVariantsAsync(Guid productId, ProductColor? color = null, ProductSize? size = null, CraftsmanshipType? craftsmanship = null);
    Task<ProductVariant?> GetVariantByIdAsync(Guid variantId);
    Task<IReadOnlyList<SizeChart>> GetSizeChartAsync(Guid productId);
    Task<Product> AddAsync(Product product);
    Task UpdateAsync(Product product);
    Task UpdateVariantAsync(ProductVariant variant);
    Task<bool> ExistsAsync(Guid id);
}
