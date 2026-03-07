using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ShareApp.Web.Entities;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILocationService _locationService;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        UserManager<ApplicationUser> userManager,
        ILocationService locationService,
        ILogger<ProfileController> logger)
    {
        _userManager = userManager;
        _locationService = locationService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        return View(user);
    }

    [HttpGet]
    public async Task<IActionResult> ManageLocations()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var locations = await _locationService.GetUserLocationsAsync(user.Id);
        return View(locations);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultLocation(int locationId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var success = await _locationService.SetDefaultLocationAsync(locationId, user.Id);
        if (success)
        {
            TempData["SuccessMessage"] = "Default location updated successfully!";
        }
        else
        {
            TempData["ErrorMessage"] = "Unable to update default location.";
        }

        return RedirectToAction("ManageLocations");
    }
}
