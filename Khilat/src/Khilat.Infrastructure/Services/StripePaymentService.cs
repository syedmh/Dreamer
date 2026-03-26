namespace Khilat.Infrastructure.Services;

using Khilat.Core.Interfaces;
using Stripe;

public class StripePaymentService : IPaymentService
{
    public async Task<PaymentIntentResult> CreatePaymentIntentAsync(
        decimal amount, string currency = "usd", Dictionary<string, string>? metadata = null)
    {
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(amount * 100), // Stripe uses cents
            Currency = currency,
            PaymentMethodTypes = new List<string> { "card" },
            Metadata = metadata
        };

        var service = new PaymentIntentService();
        var intent = await service.CreateAsync(options);

        return new PaymentIntentResult
        {
            PaymentIntentId = intent.Id,
            ClientSecret = intent.ClientSecret,
            Status = intent.Status
        };
    }

    public async Task<PaymentIntentResult> ConfirmPaymentIntentAsync(string paymentIntentId)
    {
        var service = new PaymentIntentService();
        var intent = await service.GetAsync(paymentIntentId);

        return new PaymentIntentResult
        {
            PaymentIntentId = intent.Id,
            ClientSecret = intent.ClientSecret,
            Status = intent.Status
        };
    }

    public async Task<RefundResult> RefundPaymentAsync(string chargeId, decimal? amount = null)
    {
        var options = new RefundCreateOptions
        {
            Charge = chargeId,
            Amount = amount.HasValue ? (long)(amount.Value * 100) : null
        };

        var service = new RefundService();
        var refund = await service.CreateAsync(options);

        return new RefundResult
        {
            RefundId = refund.Id,
            Status = refund.Status
        };
    }
}
