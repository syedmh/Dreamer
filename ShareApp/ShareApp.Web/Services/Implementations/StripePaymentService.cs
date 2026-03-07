using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Services.Interfaces;
using Stripe;

namespace ShareApp.Web.Services.Implementations;

public class StripePaymentService : IPaymentService
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public StripePaymentService(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<string> CreatePaymentIntentAsync(int itemId, string buyerId)
    {
        var item = await _context.Items
            .Include(i => i.User)
            .FirstOrDefaultAsync(i => i.Id == itemId);

        if (item == null || !item.Price.HasValue)
            throw new InvalidOperationException("Item not found or no price set");

        if (item.Status != ItemStatus.Available)
            throw new InvalidOperationException("Item is no longer available");

        // Create Stripe PaymentIntent
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(item.Price.Value * 100), // Convert to cents
            Currency = _configuration["Stripe:Currency"] ?? "usd",
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
            },
            Metadata = new Dictionary<string, string>
            {
                { "item_id", itemId.ToString() },
                { "buyer_id", buyerId },
                { "seller_id", item.UserId }
            }
        };

        var service = new PaymentIntentService();
        var paymentIntent = await service.CreateAsync(options);

        // Create pending transaction
        var transaction = new Transaction
        {
            ItemId = itemId,
            BuyerId = buyerId,
            SellerId = item.UserId,
            TransactionType = TransactionType.Purchase,
            Amount = item.Price.Value,
            StripePaymentIntentId = paymentIntent.Id,
            Status = TransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _context.Transactions.Add(transaction);

        // Mark item as reserved
        item.Status = ItemStatus.Reserved;

        await _context.SaveChangesAsync();

        return paymentIntent.ClientSecret;
    }

    public async Task<string> CreatePaymentIntentAsync(int transactionId, decimal amount, string description, string email)
    {
        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
            throw new InvalidOperationException("Transaction not found");

        // Create Stripe PaymentIntent
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(amount * 100), // Convert to cents
            Currency = _configuration["Stripe:Currency"] ?? "usd",
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
            },
            Description = description,
            ReceiptEmail = email,
            Metadata = new Dictionary<string, string>
            {
                { "transaction_id", transactionId.ToString() },
                { "item_id", transaction.ItemId.ToString() },
                { "buyer_id", transaction.BuyerId },
                { "seller_id", transaction.SellerId }
            }
        };

        var service = new PaymentIntentService();
        var paymentIntent = await service.CreateAsync(options);

        return paymentIntent.Id;
    }

    public async Task<Transaction?> ProcessPaymentAsync(string paymentIntentId)
    {
        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntentId);

        if (transaction == null)
            return null;

        // Verify payment with Stripe
        var service = new PaymentIntentService();
        var paymentIntent = await service.GetAsync(paymentIntentId);

        if (paymentIntent.Status == "succeeded")
        {
            transaction.Status = TransactionStatus.Completed;
            transaction.CompletedAt = DateTime.UtcNow;
            transaction.StripeChargeId = paymentIntent.LatestChargeId;

            // Mark item as sold
            transaction.Item.Status = ItemStatus.Sold;

            await _context.SaveChangesAsync();
        }

        return transaction;
    }

    public async Task<bool> RefundPaymentAsync(int transactionId, string reason)
    {
        var transaction = await _context.Transactions
            .Include(t => t.Item)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null || string.IsNullOrEmpty(transaction.StripeChargeId))
            return false;

        try
        {
            var refundService = new RefundService();
            var refundOptions = new RefundCreateOptions
            {
                Charge = transaction.StripeChargeId,
                Reason = "requested_by_customer"
            };

            await refundService.CreateAsync(refundOptions);

            transaction.Status = TransactionStatus.Refunded;
            transaction.Item.Status = ItemStatus.Available;

            await _context.SaveChangesAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task HandleWebhookAsync(string json, string signature)
    {
        var webhookSecret = _configuration["Stripe:WebhookSecret"];
        if (string.IsNullOrEmpty(webhookSecret))
            throw new InvalidOperationException("Webhook secret not configured");

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                signature,
                webhookSecret
            );

            if (stripeEvent.Type == Events.PaymentIntentSucceeded)
            {
                var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
                if (paymentIntent != null)
                {
                    await ProcessPaymentAsync(paymentIntent.Id);
                }
            }
        }
        catch (StripeException)
        {
            throw;
        }
    }

    public async Task HandleWebhookAsync(Event stripeEvent)
    {
        if (stripeEvent.Type == Events.PaymentIntentSucceeded)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent != null)
            {
                await ProcessPaymentAsync(paymentIntent.Id);

                // Create notification for seller
                var transaction = await _context.Transactions
                    .Include(t => t.Item)
                    .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntent.Id);

                if (transaction != null)
                {
                    var notification = new Notification
                    {
                        UserId = transaction.SellerId,
                        Type = NotificationType.Purchase,
                        Title = "Payment Received",
                        Message = $"Payment received for {transaction.Item.Title}. Please arrange pickup with the buyer.",
                        RelatedEntityId = transaction.Id,
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Notifications.Add(notification);
                    await _context.SaveChangesAsync();
                }
            }
        }
        else if (stripeEvent.Type == Events.PaymentIntentPaymentFailed)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent != null)
            {
                var transaction = await _context.Transactions
                    .FirstOrDefaultAsync(t => t.StripePaymentIntentId == paymentIntent.Id);

                if (transaction != null)
                {
                    // Optionally handle payment failure (e.g., send notification)
                    var notification = new Notification
                    {
                        UserId = transaction.BuyerId,
                        Type = NotificationType.Message,
                        Title = "Payment Failed",
                        Message = $"Payment for transaction #{transaction.Id} failed. Please try again.",
                        RelatedEntityId = transaction.Id,
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Notifications.Add(notification);
                    await _context.SaveChangesAsync();
                }
            }
        }
    }
}
