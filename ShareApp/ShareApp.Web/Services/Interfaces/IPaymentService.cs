using ShareApp.Web.Entities;

namespace ShareApp.Web.Services.Interfaces;

public interface IPaymentService
{
    Task<string> CreatePaymentIntentAsync(int itemId, string buyerId);
    Task<string> CreatePaymentIntentAsync(int transactionId, decimal amount, string description, string email);
    Task<Transaction?> ProcessPaymentAsync(string paymentIntentId);
    Task<bool> RefundPaymentAsync(int transactionId, string reason);
    Task HandleWebhookAsync(string json, string signature);
    Task HandleWebhookAsync(Stripe.Event stripeEvent);
}
