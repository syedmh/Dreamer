using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Husaynia.Web.Models;
using Husaynia.Core.Interfaces;

namespace Husaynia.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IAnnouncementRepository _announcementRepository;
    private readonly IEventRepository _eventRepository;
    private readonly IDonationCampaignRepository _campaignRepository;
    private readonly IPrayerTimeService _prayerTimeService;

    public HomeController(
        ILogger<HomeController> logger,
        IAnnouncementRepository announcementRepository,
        IEventRepository eventRepository,
        IDonationCampaignRepository campaignRepository,
        IPrayerTimeService prayerTimeService)
    {
        _logger = logger;
        _announcementRepository = announcementRepository;
        _eventRepository = eventRepository;
        _campaignRepository = campaignRepository;
        _prayerTimeService = prayerTimeService;
    }

    public async Task<IActionResult> Index()
    {
        // Fetch featured announcements (pinned first, then recent)
        var featuredAnnouncements = await _announcementRepository.GetRecentAsync(3);
        ViewBag.FeaturedAnnouncements = featuredAnnouncements;

        // Fetch upcoming events
        var upcomingEvents = await _eventRepository.GetUpcomingEventsAsync(4);
        ViewBag.UpcomingEvents = upcomingEvents;

        // Fetch today's prayer times
        var todaysPrayerTimes = await _prayerTimeService.GetTodaysPrayerTimesAsync();
        ViewBag.TodaysPrayerTimes = todaysPrayerTimes;

        // Fetch Build Husaynia campaign progress
        var campaign = await _campaignRepository.GetByNameAsync("Build Husaynia");
        ViewBag.Campaign = campaign;

        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
