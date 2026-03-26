namespace Khilat.Infrastructure.Repositories;

using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ProductRepository : IProductRepository
{
    private readonly KhilatDbContext _context;

    public ProductRepository(KhilatDbContext context)
    {
        _context = context;
    }

    public async Task<Product?> GetByIdAsync(Guid id)
        => await _context.Products.FindAsync(id);

    public async Task<Product?> GetWithVariantsAsync(Guid id)
        => await _context.Products
            .Include(p => p.Variants)
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.SizeCharts)
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IReadOnlyList<Product>> GetAllAsync()
        => await _context.Products
            .Include(p => p.Variants)
            .Include(p => p.Images.Where(i => i.IsPrimary))
            .AsNoTracking()
            .ToListAsync();

    public async Task<IReadOnlyList<ProductVariant>> GetVariantsAsync(
        Guid productId, ProductColor? color = null, ProductSize? size = null, CraftsmanshipType? craftsmanship = null)
    {
        var query = _context.ProductVariants.Where(v => v.ProductId == productId);
        if (color.HasValue) query = query.Where(v => v.Color == color.Value);
        if (size.HasValue) query = query.Where(v => v.Size == size.Value);
        if (craftsmanship.HasValue) query = query.Where(v => v.Craftsmanship == craftsmanship.Value);
        return await query.AsNoTracking().ToListAsync();
    }

    public async Task<ProductVariant?> GetVariantByIdAsync(Guid variantId)
        => await _context.ProductVariants
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.Id == variantId);

    public async Task<IReadOnlyList<SizeChart>> GetSizeChartAsync(Guid productId)
        => await _context.SizeCharts
            .Where(s => s.ProductId == productId)
            .OrderBy(s => s.Size)
            .AsNoTracking()
            .ToListAsync();

    public async Task<Product> AddAsync(Product product)
    {
        await _context.Products.AddAsync(product);
        await _context.SaveChangesAsync();
        return product;
    }

    public async Task UpdateAsync(Product product)
    {
        product.UpdatedAt = DateTime.UtcNow;
        _context.Products.Update(product);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateVariantAsync(ProductVariant variant)
    {
        variant.UpdatedAt = DateTime.UtcNow;
        _context.ProductVariants.Update(variant);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync(Guid id)
        => await _context.Products.AnyAsync(p => p.Id == id);
}
