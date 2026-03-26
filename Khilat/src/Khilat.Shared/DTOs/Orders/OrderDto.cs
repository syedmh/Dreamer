namespace Khilat.Shared.DTOs.Orders;

public class OrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal Tax { get; set; }
    public decimal TotalAmount { get; set; }
    public ShippingAddressDto ShippingAddress { get; set; } = new();
    public List<OrderItemDto> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
