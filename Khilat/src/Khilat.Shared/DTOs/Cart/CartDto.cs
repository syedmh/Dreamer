namespace Khilat.Shared.DTOs.Cart;

public class CartDto
{
    public Guid Id { get; set; }
    public List<CartItemDto> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public int TotalItems { get; set; }
}
