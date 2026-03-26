using FluentAssertions;
using Khilat.Core.Entities;
using Khilat.Core.Enums;

namespace Khilat.UnitTests.Core;

public class ProductVariantTests
{
    [Fact]
    public void Variant_IsAvailable_ReturnsTrue_WhenStockPositive()
    {
        var variant = new ProductVariant { StockQuantity = 10 };

        variant.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Variant_IsAvailable_ReturnsFalse_WhenStockZero()
    {
        var variant = new ProductVariant { StockQuantity = 0 };

        variant.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Variant_Properties_SetCorrectly()
    {
        var id = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var variant = new ProductVariant
        {
            Id = id,
            ProductId = productId,
            Color = ProductColor.Burgundy,
            Size = ProductSize.L,
            Craftsmanship = CraftsmanshipType.HandStitched,
            Price = 250m,
            StockQuantity = 5,
            Sku = "KH-BURG-L-HS"
        };

        variant.Id.Should().Be(id);
        variant.ProductId.Should().Be(productId);
        variant.Color.Should().Be(ProductColor.Burgundy);
        variant.Size.Should().Be(ProductSize.L);
        variant.Craftsmanship.Should().Be(CraftsmanshipType.HandStitched);
        variant.Price.Should().Be(250m);
        variant.StockQuantity.Should().Be(5);
        variant.Sku.Should().Be("KH-BURG-L-HS");
    }

    [Fact]
    public void Variant_AllCraftsmanshipTypes_AreValid()
    {
        var values = Enum.GetValues<CraftsmanshipType>();

        values.Should().HaveCount(2);
        values.Should().Contain(CraftsmanshipType.MachineProduced);
        values.Should().Contain(CraftsmanshipType.HandStitched);
    }

    [Fact]
    public void Variant_AllColors_AreValid()
    {
        var values = Enum.GetValues<ProductColor>();

        values.Should().HaveCount(12);
    }

    [Fact]
    public void Variant_AllSizes_AreValid()
    {
        var values = Enum.GetValues<ProductSize>();

        values.Should().HaveCount(6);
    }
}
