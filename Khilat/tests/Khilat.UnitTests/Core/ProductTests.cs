using FluentAssertions;
using Khilat.Core.Entities;
using Khilat.Core.Enums;

namespace Khilat.UnitTests.Core;

public class ProductTests
{
    [Fact]
    public void Product_DefaultValues_AreCorrect()
    {
        var product = new Product();

        product.Status.Should().Be(ProductStatus.Active);
        product.Variants.Should().BeEmpty();
        product.Images.Should().BeEmpty();
        product.SizeCharts.Should().BeEmpty();
        product.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Product_Variants_Collection_CanBePopulated()
    {
        var product = new Product();
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            Color = ProductColor.Ivory,
            Size = ProductSize.M,
            Price = 120m
        };

        product.Variants.Add(variant);

        product.Variants.Should().ContainSingle();
        product.Variants.First().Price.Should().Be(120m);
    }

    [Fact]
    public void Product_Images_Collection_CanBePopulated()
    {
        var product = new Product();
        var image = new ProductImage
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com/image.jpg",
            IsPrimary = true,
            SortOrder = 1
        };

        product.Images.Add(image);

        product.Images.Should().ContainSingle();
        product.Images.First().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void Product_SizeCharts_Collection_CanBePopulated()
    {
        var product = new Product();
        var sizeChart = new SizeChart
        {
            Id = Guid.NewGuid(),
            Size = ProductSize.M,
            ChestInches = 20.5m
        };

        product.SizeCharts.Add(sizeChart);

        product.SizeCharts.Should().ContainSingle();
        product.SizeCharts.First().Size.Should().Be(ProductSize.M);
    }
}
