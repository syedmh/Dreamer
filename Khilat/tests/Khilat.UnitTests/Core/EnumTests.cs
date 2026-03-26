using FluentAssertions;
using Khilat.Core.Enums;

namespace Khilat.UnitTests.Core;

public class EnumTests
{
    [Fact]
    public void ProductColor_Has12Values()
    {
        Enum.GetValues<ProductColor>().Should().HaveCount(12);
    }

    [Fact]
    public void ProductSize_Has6Values()
    {
        Enum.GetValues<ProductSize>().Should().HaveCount(6);
    }

    [Fact]
    public void CraftsmanshipType_Has2Values()
    {
        Enum.GetValues<CraftsmanshipType>().Should().HaveCount(2);
    }

    [Fact]
    public void OrderStatus_Has7Values()
    {
        Enum.GetValues<OrderStatus>().Should().HaveCount(7);
    }

    [Fact]
    public void ProductStatus_Has4Values()
    {
        Enum.GetValues<ProductStatus>().Should().HaveCount(4);
    }

    [Fact]
    public void ProductColor_ValuesAreCorrect()
    {
        ((int)ProductColor.Ivory).Should().Be(0);
        ((int)ProductColor.Champagne).Should().Be(1);
        ((int)ProductColor.DustyRose).Should().Be(2);
        ((int)ProductColor.SageGreen).Should().Be(3);
        ((int)ProductColor.MidnightNavy).Should().Be(4);
        ((int)ProductColor.Burgundy).Should().Be(5);
        ((int)ProductColor.Charcoal).Should().Be(6);
        ((int)ProductColor.PearlWhite).Should().Be(7);
        ((int)ProductColor.Emerald).Should().Be(8);
        ((int)ProductColor.Mauve).Should().Be(9);
        ((int)ProductColor.SlateBlue).Should().Be(10);
        ((int)ProductColor.Terracotta).Should().Be(11);
    }

    [Fact]
    public void ProductSize_ValuesAreCorrect()
    {
        ((int)ProductSize.XS).Should().Be(0);
        ((int)ProductSize.S).Should().Be(1);
        ((int)ProductSize.M).Should().Be(2);
        ((int)ProductSize.L).Should().Be(3);
        ((int)ProductSize.XL).Should().Be(4);
        ((int)ProductSize.XXL).Should().Be(5);
    }
}
