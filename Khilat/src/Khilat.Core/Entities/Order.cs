namespace Khilat.Core.Entities;

using Khilat.Core.Enums;

public class Order
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public decimal SubTotal { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal Tax { get; set; }
    public decimal TotalAmount { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? StripeChargeId { get; set; }

    // Shipping address (denormalized for order history)
    public string ShippingStreet { get; set; } = string.Empty;
    public string ShippingCity { get; set; } = string.Empty;
    public string ShippingState { get; set; } = string.Empty;
    public string ShippingZipCode { get; set; } = string.Empty;
    public string ShippingCountry { get; set; } = "US";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
