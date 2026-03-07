using Husaynia.Core.Entities;
using Husaynia.Core.Enums;
using Husaynia.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Stripe.Checkout;

namespace Husaynia.Web.Controllers
{
    public class DonationsController : Controller
    {
        private readonly IDonationRepository _donationRepository;
        private readonly IDonationCampaignRepository _campaignRepository;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DonationsController> _logger;

        public DonationsController(
            IDonationRepository donationRepository,
            IDonationCampaignRepository campaignRepository,
            IConfiguration configuration,
            ILogger<DonationsController> logger)
        {
            _donationRepository = donationRepository;
            _campaignRepository = campaignRepository;
            _configuration = configuration;
            _logger = logger;

            // Configure Stripe API key
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"] ?? "sk_test_placeholder";
        }

        public async Task<IActionResult> Donate(string? campaign = null)
        {
            var campaigns = await _campaignRepository.GetAllActiveAsync();
            ViewBag.Campaigns = campaigns;
            ViewBag.SelectedCampaign = campaign ?? "Build Husaynia";

            // Get current campaign progress
            var buildHusayniaCampaign = campaigns.FirstOrDefault(c => c.Name == "Build Husaynia");
            if (buildHusayniaCampaign != null)
            {
                ViewBag.CurrentAmount = buildHusayniaCampaign.CurrentAmount;
                ViewBag.GoalAmount = buildHusayniaCampaign.GoalAmount;
            }
            else
            {
                // Fallback values from the WordPress site
                ViewBag.CurrentAmount = 2961788m;
                ViewBag.GoalAmount = 6950000m;
            }

            ViewBag.StripePublishableKey = _configuration["Stripe:PublishableKey"] ?? "pk_test_placeholder";

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateCheckoutSession(
            decimal amount,
            string donorName,
            string donorEmail,
            bool isAnonymous,
            string campaign = "Build Husaynia")
        {
            try
            {
                var domain = $"{Request.Scheme}://{Request.Host}";

                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                Currency = "usd",
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = $"Donation to {campaign}",
                                    Description = "Thank you for your generous donation to Husaynia Islamic Society"
                                },
                                UnitAmount = (long)(amount * 100), // Convert to cents
                            },
                            Quantity = 1,
                        },
                    },
                    Mode = "payment",
                    SuccessUrl = $"{domain}/Donations/Success?session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = $"{domain}/Donations/Cancel",
                    CustomerEmail = donorEmail,
                    Metadata = new Dictionary<string, string>
                    {
                        { "donor_name", donorName },
                        { "is_anonymous", isAnonymous.ToString() },
                        { "campaign", campaign }
                    }
                };

                var service = new SessionService();
                var session = await service.CreateAsync(options);

                // Create pending donation record
                var donation = new Core.Entities.Donation
                {
                    Amount = amount,
                    DonorName = donorName,
                    DonorEmail = donorEmail,
                    IsAnonymous = isAnonymous,
                    Campaign = campaign,
                    PaymentMethod = "Stripe",
                    TransactionId = session.Id,
                    Status = DonationStatus.Pending,
                    DonationDate = DateTime.UtcNow
                };

                await _donationRepository.AddAsync(donation);

                return Json(new { sessionId = session.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Stripe checkout session");
                return BadRequest(new { error = "Unable to process donation. Please try again." });
            }
        }

        public async Task<IActionResult> Success(string session_id)
        {
            try
            {
                var service = new SessionService();
                var session = await service.GetAsync(session_id);

                if (session.PaymentStatus == "paid")
                {
                    var donation = await _donationRepository.GetByTransactionIdAsync(session_id);
                    if (donation != null && donation.Status == DonationStatus.Pending)
                    {
                        donation.Status = DonationStatus.Completed;
                        await _donationRepository.UpdateAsync(donation);

                        // Update campaign amount
                        var campaignObj = await _campaignRepository.GetByNameAsync(donation.Campaign);
                        if (campaignObj != null)
                        {
                            await _campaignRepository.UpdateCampaignAmountAsync(campaignObj.Id, donation.Amount);
                        }
                    }

                    ViewBag.Amount = session.AmountTotal / 100m; // Convert from cents
                    ViewBag.DonorEmail = session.CustomerEmail;
                    return View();
                }

                return RedirectToAction(nameof(Cancel));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing donation success");
                return RedirectToAction(nameof(Cancel));
            }
        }

        public IActionResult Cancel()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Webhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            try
            {
                var stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _configuration["Stripe:WebhookSecret"] ?? "whsec_placeholder"
                );

                if (stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    var session = stripeEvent.Data.Object as Session;
                    if (session != null && session.PaymentStatus == "paid")
                    {
                        var donation = await _donationRepository.GetByTransactionIdAsync(session.Id);
                        if (donation != null && donation.Status == DonationStatus.Pending)
                        {
                            donation.Status = DonationStatus.Completed;
                            await _donationRepository.UpdateAsync(donation);

                            // Update campaign amount
                            var campaign = await _campaignRepository.GetByNameAsync(donation.Campaign);
                            if (campaign != null)
                            {
                                await _campaignRepository.UpdateCampaignAmountAsync(campaign.Id, donation.Amount);
                            }

                            _logger.LogInformation($"Donation {donation.Id} completed via webhook");
                        }
                    }
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Stripe webhook");
                return BadRequest();
            }
        }

        public async Task<IActionResult> BuildHusaynia()
        {
            var campaign = await _campaignRepository.GetByNameAsync("Build Husaynia");
            var recentDonations = await _donationRepository.GetRecentDonationsAsync(10);

            if (campaign == null)
            {
                // Create default campaign if it doesn't exist
                campaign = new DonationCampaign
                {
                    Name = "Build Husaynia",
                    GoalAmount = 6950000m,
                    CurrentAmount = 2961788m,
                    StartDate = new DateTime(2020, 1, 1),
                    IsActive = true,
                    Description = "Help us build a permanent home for Husaynia Islamic Society"
                };
            }

            ViewBag.Campaign = campaign;
            ViewBag.RecentDonations = recentDonations;

            return View();
        }
    }
}
