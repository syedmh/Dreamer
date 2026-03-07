using Fundraiser.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fundraiser.Controllers;

public class DonationController : Controller
{
    private readonly DonationService _donationService;
    private readonly IConfiguration _configuration;
    private readonly decimal _fundraisingGoal;
    private readonly decimal _scaleIncrement;

    public DonationController(DonationService donationService, IConfiguration configuration)
    {
        _donationService = donationService;
        _configuration = configuration;
        _fundraisingGoal = _configuration.GetValue<decimal>("FundraisingSettings:GoalAmount", 4000000m);
        _scaleIncrement = _configuration.GetValue<decimal>("FundraisingSettings:ScaleIncrement", 50000m);
    }

    public IActionResult Index()
    {
        ViewBag.TotalRaised = _donationService.GetTotalRaised();
        ViewBag.Goal = _fundraisingGoal;
        ViewBag.ScaleIncrement = _scaleIncrement;
        ViewBag.RecentDonations = _donationService.GetAllDonations()
            .OrderByDescending(d => d.Timestamp)
            .Take(10)
            .ToList();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AddDonation([FromBody] decimal amount)
    {
        if (amount <= 0 || amount > 1000000)
        {
            return BadRequest(new { success = false, message = "Amount must be between $0.01 and $1,000,000" });
        }

        _donationService.SaveDonation(amount);
        var totalRaised = _donationService.GetTotalRaised();
        var showFireworks = amount > 500;
        var percentage = (totalRaised / _fundraisingGoal) * 100;
        var isOverflow = totalRaised > _fundraisingGoal;
        var overflowAmount = isOverflow ? totalRaised - _fundraisingGoal : 0;

        return Json(new
        {
            success = true,
            totalRaised = totalRaised,
            showFireworks = showFireworks,
            percentage = percentage,
            isOverflow = isOverflow,
            overflowAmount = overflowAmount
        });
    }

    [Authorize]
    public IActionResult DownloadCsv()
    {
        var filePath = _donationService.GetCsvFilePath();

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound("No donations found.");
        }

        var fileName = $"donations_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var fileBytes = System.IO.File.ReadAllBytes(filePath);
        return File(fileBytes, "text/csv", fileName);
    }
}
