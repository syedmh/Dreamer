namespace Khilat.Core.Interfaces;

public interface IPaymentService
{
    Task<PaymentIntentResult> CreatePaymentIntentAsync(decimal amount, string currency = "usd", Dictionary<string, string>? metadata = null);
    Task<PaymentIntentResult> ConfirmPaymentIntentAsync(string paymentIntentId);
    Task<RefundResult> RefundPaymentAsync(string chargeId, decimal? amount = null);
}

public class PaymentIntentResult
{
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Succeeded => Status == "succeeded";
}

public class RefundResult
{
    public string RefundId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Succeeded => Status == "succeeded";
}
