namespace Khilat.Api.Controllers;

using Khilat.Core.Enums;
using Khilat.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
public partial class PaymentsController : ControllerBase
{
    private readonly KhilatDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        KhilatDbContext dbContext,
        IConfiguration configuration,
        ILogger<PaymentsController> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unhandled Stripe event type: {EventType}")]
    partial void LogUnhandledEvent(string eventType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderNumber} confirmed via payment intent {PaymentIntentId}.")]
    partial void LogOrderConfirmed(string orderNumber, string paymentIntentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderNumber} cancelled due to payment failure for intent {PaymentIntentId}.")]
    partial void LogOrderCancelled(string orderNumber, string paymentIntentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderNumber} refunded for charge {ChargeId}.")]
    partial void LogOrderRefunded(string orderNumber, string chargeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe webhook signature verification failed.")]
    partial void LogWebhookError(Exception ex);

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var webhookSecret = _configuration["Stripe:WebhookSecret"];

        try
        {
            var stripeSignature = Request.Headers["Stripe-Signature"].ToString();
            var stripeEvent = Stripe.EventUtility.ConstructEvent(json, stripeSignature, webhookSecret);

            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    await HandlePaymentSucceeded(stripeEvent);
                    break;
                case "payment_intent.payment_failed":
                    await HandlePaymentFailed(stripeEvent);
                    break;
                case "charge.refunded":
                    await HandleChargeRefunded(stripeEvent);
                    break;
                default:
                    LogUnhandledEvent(stripeEvent.Type);
                    break;
            }

            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            LogWebhookError(ex);
            return BadRequest("Invalid Stripe signature.");
        }
    }

    private async Task HandlePaymentSucceeded(Stripe.Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as Stripe.PaymentIntent;
        if (paymentIntent is null) return;

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(o => o.StripePaymentIntentId == paymentIntent.Id);

        if (order is not null)
        {
            order.Status = OrderStatus.Confirmed;
            order.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            LogOrderConfirmed(order.OrderNumber, paymentIntent.Id);
        }
    }

    private async Task HandlePaymentFailed(Stripe.Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as Stripe.PaymentIntent;
        if (paymentIntent is null) return;

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(o => o.StripePaymentIntentId == paymentIntent.Id);

        if (order is not null)
        {
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            LogOrderCancelled(order.OrderNumber, paymentIntent.Id);
        }
    }

    private async Task HandleChargeRefunded(Stripe.Event stripeEvent)
    {
        var charge = stripeEvent.Data.Object as Stripe.Charge;
        if (charge is null) return;

        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(o => o.StripeChargeId == charge.Id
                || o.StripePaymentIntentId == charge.PaymentIntentId);

        if (order is not null)
        {
            order.Status = OrderStatus.Refunded;
            order.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            LogOrderRefunded(order.OrderNumber, charge.Id);
        }
    }
}
