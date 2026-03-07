using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Services.Interfaces;
using Stripe;

namespace ShareApp.Web.Controllers.Api;

[Route("api/[controller]")]
[ApiController]
public class PaymentApiController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentApiController> _logger;

    public PaymentApiController(
        IPaymentService paymentService,
        ApplicationDbContext context,
        IConfiguration configuration,
        ILogger<PaymentApiController> logger)
    {
        _paymentService = paymentService;
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("create-intent")]
    [Authorize]
    public async Task<IActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentRequest request)
    {
        try
        {
            var transaction = await _context.Transactions
                .Include(t => t.Item)
                .Include(t => t.Buyer)
                .FirstOrDefaultAsync(t => t.Id == request.TransactionId);

            if (transaction == null)
            {
                return NotFound(new { error = "Transaction not found" });
            }

            // Verify user is the buyer
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (transaction.BuyerId != userId)
            {
                return Forbid();
            }

            // Verify transaction is pending and for purchase
            if (transaction.Status != TransactionStatus.Pending ||
                transaction.TransactionType != TransactionType.Purchase)
            {
                return BadRequest(new { error = "Invalid transaction status" });
            }

            // Verify amount matches
            if (!transaction.Amount.HasValue || transaction.Amount.Value != request.Amount)
            {
                return BadRequest(new { error = "Amount mismatch" });
            }

            // Create or retrieve payment intent
            string clientSecret;

            if (!string.IsNullOrEmpty(transaction.StripePaymentIntentId))
            {
                // Retrieve existing payment intent
                var paymentIntentService = new PaymentIntentService();
                var paymentIntent = await paymentIntentService.GetAsync(transaction.StripePaymentIntentId);
                clientSecret = paymentIntent.ClientSecret;
            }
            else
            {
                // Create new payment intent
                var paymentIntentId = await _paymentService.CreatePaymentIntentAsync(
                    transaction.Id,
                    transaction.Amount.Value,
                    transaction.Item.Title,
                    transaction.Buyer.Email ?? string.Empty
                );

                // Update transaction with payment intent ID
                transaction.StripePaymentIntentId = paymentIntentId;
                await _context.SaveChangesAsync();

                // Get client secret
                var paymentIntentService = new PaymentIntentService();
                var paymentIntent = await paymentIntentService.GetAsync(paymentIntentId);
                clientSecret = paymentIntent.ClientSecret;
            }

            return Ok(new { clientSecret });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating payment intent for transaction {TransactionId}", request.TransactionId);
            return StatusCode(500, new { error = "An error occurred while processing your payment" });
        }
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            var webhookSecret = _configuration["Stripe:WebhookSecret"];

            if (string.IsNullOrEmpty(webhookSecret))
            {
                _logger.LogWarning("Stripe webhook secret not configured");
                return Ok(); // Return 200 to avoid Stripe retries
            }

            var stripeEvent = EventUtility.ConstructEvent(
                json,
                stripeSignature,
                webhookSecret
            );

            _logger.LogInformation("Stripe webhook received: {EventType}", stripeEvent.Type);

            await _paymentService.HandleWebhookAsync(stripeEvent);

            return Ok();
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe webhook error");
            return BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook");
            return StatusCode(500);
        }
    }
}

public class CreatePaymentIntentRequest
{
    public int TransactionId { get; set; }
    public decimal Amount { get; set; }
}
