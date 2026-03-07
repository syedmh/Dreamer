using Microsoft.AspNetCore.Mvc;

namespace Husaynia.Web.Controllers
{
    public class AboutController : Controller
    {
        private readonly ILogger<AboutController> _logger;

        public AboutController(ILogger<AboutController> logger)
        {
            _logger = logger;
        }

        public IActionResult Overview()
        {
            return View();
        }

        public IActionResult Contact()
        {
            return View();
        }

        [HttpPost]
        public IActionResult SubmitContact(
            string name,
            string email,
            string subject,
            string message)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(email) ||
                string.IsNullOrWhiteSpace(message))
            {
                TempData["Error"] = "Please fill in all required fields.";
                return RedirectToAction(nameof(Contact));
            }

            try
            {
                // TODO: Implement email sending service
                // For now, just log the contact request
                _logger.LogInformation($"Contact form submitted: {name} ({email}) - {subject}");

                TempData["Success"] = "Thank you for contacting us! We will get back to you soon.";
                return RedirectToAction(nameof(Contact));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing contact form");
                TempData["Error"] = "An error occurred. Please try again later.";
                return RedirectToAction(nameof(Contact));
            }
        }

        public IActionResult Duas()
        {
            return View();
        }

        public IActionResult HadithAlKisa()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }
    }
}
