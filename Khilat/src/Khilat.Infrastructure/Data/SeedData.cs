namespace Khilat.Infrastructure.Data;

using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var context = new KhilatDbContext(
            serviceProvider.GetRequiredService<DbContextOptions<KhilatDbContext>>());

        if (await context.Products.AnyAsync())
            return;

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "The Khilat",
            Description = "The Khilat is a premium women's kurta that seamlessly blends traditional craftsmanship with modern elegance. Featuring a sophisticated collar, button-front closure, long sleeves with tailored cuffs, and a signature curved hemline — shorter in front, longer in back — this piece is designed for the modern woman who values both style and modesty. Available in hand-stitched artisan and machine-produced variants, crafted from the highest quality fabrics.",
            ShortDescription = "Premium women's kurta — where tradition meets modern elegance.",
            Status = ProductStatus.Active,
        };

        // Generate all 144 variants (12 colors × 6 sizes × 2 craftsmanship types)
        var variants = new List<ProductVariant>();
        foreach (ProductColor color in Enum.GetValues<ProductColor>())
        {
            foreach (ProductSize size in Enum.GetValues<ProductSize>())
            {
                foreach (CraftsmanshipType craft in Enum.GetValues<CraftsmanshipType>())
                {
                    var sizeOffset = (int)size - (int)ProductSize.M;
                    decimal basePrice = craft == CraftsmanshipType.HandStitched ? 219m : 109m;
                    decimal price = basePrice + (sizeOffset * 5m);

                    variants.Add(new ProductVariant
                    {
                        Id = Guid.NewGuid(),
                        ProductId = productId,
                        Color = color,
                        Size = size,
                        Craftsmanship = craft,
                        Price = price,
                        StockQuantity = 25,
                        Sku = $"KHL-{color.ToString()[..4].ToUpperInvariant()}-{size}-{(craft == CraftsmanshipType.HandStitched ? "HS" : "MP")}"
                    });
                }
            }
        }

        // Size chart based on sketch measurements (base = M)
        var sizeCharts = new List<SizeChart>();
        var sizeScales = new Dictionary<ProductSize, decimal>
        {
            { ProductSize.XS, -2m }, { ProductSize.S, -1m }, { ProductSize.M, 0m },
            { ProductSize.L, 1m }, { ProductSize.XL, 2m }, { ProductSize.XXL, 3m }
        };

        foreach (var (size, offset) in sizeScales)
        {
            sizeCharts.Add(new SizeChart
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Size = size,
                FrontLengthInches = 28m + offset,
                BackLengthInches = 31m + offset,
                ArmLengthInches = 29m + (offset * 0.5m),
                CuffInches = 4m + (offset * 0.25m),
                ButtonLengthInches = 26m + offset,
                ShoulderInches = 15.5m + (offset * 0.75m),
                ChestInches = 20.5m + (offset * 1m),
                WaistInches = 20m + (offset * 1m),
                HipInches = 20.5m + (offset * 1m)
            });
        }

        // Placeholder images
        var images = new List<ProductImage>
        {
            new() { Id = Guid.NewGuid(), ProductId = productId, Url = "/images/khilat-front.jpg", AltText = "The Khilat - Front View", SortOrder = 1, IsPrimary = true },
            new() { Id = Guid.NewGuid(), ProductId = productId, Url = "/images/khilat-back.jpg", AltText = "The Khilat - Back View", SortOrder = 2, IsPrimary = false },
            new() { Id = Guid.NewGuid(), ProductId = productId, Url = "/images/khilat-detail.jpg", AltText = "The Khilat - Detail View", SortOrder = 3, IsPrimary = false },
        };

        await context.Products.AddAsync(product);
        await context.ProductVariants.AddRangeAsync(variants);
        await context.SizeCharts.AddRangeAsync(sizeCharts);
        await context.ProductImages.AddRangeAsync(images);
        await context.SaveChangesAsync();
    }
}
