namespace Khilat.Shared.DTOs.Orders;

public class CheckoutRequest
{
    public ShippingAddressDto ShippingAddress { get; set; } = new();
    public string PaymentMethodId { get; set; } = string.Empty;
}
