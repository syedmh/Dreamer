using FluentAssertions;
using Khilat.Core.Entities;
using Khilat.Core.Enums;

namespace Khilat.UnitTests.Core;

public class OrderTests
{
    [Fact]
    public void Order_DefaultStatus_IsPending()
    {
        var order = new Order();

        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Order_AllStatusValues_AreDefined()
    {
        var values = Enum.GetValues<OrderStatus>();

        values.Should().HaveCount(7);
        values.Should().Contain(OrderStatus.Pending);
        values.Should().Contain(OrderStatus.Confirmed);
        values.Should().Contain(OrderStatus.Processing);
        values.Should().Contain(OrderStatus.Shipped);
        values.Should().Contain(OrderStatus.Delivered);
        values.Should().Contain(OrderStatus.Cancelled);
        values.Should().Contain(OrderStatus.Refunded);
    }

    [Fact]
    public void OrderItem_Subtotal_CalculatesCorrectly()
    {
        var item = new OrderItem { Quantity = 2, UnitPrice = 150m };

        item.Subtotal.Should().Be(300m);
    }

    [Fact]
    public void OrderItem_SnapshotProperties_SetCorrectly()
    {
        var item = new OrderItem
        {
            ProductName = "Silk Kurta",
            Color = ProductColor.Emerald,
            Size = ProductSize.L,
            Craftsmanship = CraftsmanshipType.HandStitched,
            Quantity = 1,
            UnitPrice = 350m
        };

        item.ProductName.Should().Be("Silk Kurta");
        item.Color.Should().Be(ProductColor.Emerald);
        item.Size.Should().Be(ProductSize.L);
        item.Craftsmanship.Should().Be(CraftsmanshipType.HandStitched);
    }
}
