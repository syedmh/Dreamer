using FluentAssertions;
using Khilat.Core.Entities;

namespace Khilat.UnitTests.Core;

public class CartTests
{
    [Fact]
    public void Cart_TotalAmount_CalculatesCorrectly()
    {
        var cart = new Cart();
        cart.Items.Add(new CartItem { Quantity = 2, UnitPrice = 100m });
        cart.Items.Add(new CartItem { Quantity = 1, UnitPrice = 250m });
        cart.Items.Add(new CartItem { Quantity = 3, UnitPrice = 50m });

        cart.TotalAmount.Should().Be(2 * 100m + 1 * 250m + 3 * 50m); // 600
    }

    [Fact]
    public void Cart_TotalItems_CalculatesCorrectly()
    {
        var cart = new Cart();
        cart.Items.Add(new CartItem { Quantity = 2, UnitPrice = 100m });
        cart.Items.Add(new CartItem { Quantity = 1, UnitPrice = 250m });
        cart.Items.Add(new CartItem { Quantity = 3, UnitPrice = 50m });

        cart.TotalItems.Should().Be(6);
    }

    [Fact]
    public void Cart_EmptyCart_HasZeroTotals()
    {
        var cart = new Cart();

        cart.TotalAmount.Should().Be(0m);
        cart.TotalItems.Should().Be(0);
        cart.Items.Should().BeEmpty();
    }

    [Fact]
    public void CartItem_Subtotal_CalculatesCorrectly()
    {
        var item = new CartItem { Quantity = 3, UnitPrice = 75.50m };

        item.Subtotal.Should().Be(3 * 75.50m); // 226.50
    }
}
