namespace Khilat.Shared.DTOs.Orders;

public class PaymentResponse
{
    public bool Success { get; set; }
    public string? ClientSecret { get; set; }
    public string? PaymentIntentId { get; set; }
    public string? ErrorMessage { get; set; }
    public OrderDto? Order { get; set; }
}
