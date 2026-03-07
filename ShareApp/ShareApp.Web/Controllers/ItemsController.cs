using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Models.ViewModels;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Controllers;

[Authorize]
public class ItemsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IItemService _itemService;
    private readonly ILocationService _locationService;
    private readonly IImageService _imageService;
    private readonly IPriceSuggestionService _priceSuggestionService;
    private readonly ILogger<ItemsController> _logger;

    public ItemsController(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IItemService itemService,
        ILocationService locationService,
        IImageService imageService,
        IPriceSuggestionService priceSuggestionService,
        ILogger<ItemsController> logger)
    {
        _context = context;
        _userManager = userManager;
        _itemService = itemService;
        _locationService = locationService;
        _imageService = imageService;
        _priceSuggestionService = priceSuggestionService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var locations = await _locationService.GetUserLocationsAsync(user.Id);
        if (!locations.Any())
        {
            TempData["ErrorMessage"] = "Please add a location first before listing an item.";
            return RedirectToAction("ManageLocations", "Profile");
        }

        ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
        ViewBag.Locations = locations;

        return View(new ItemCreateViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ItemCreateViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        if (!ModelState.IsValid)
        {
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);
            return View(model);
        }

        // Validate price based on item type
        if (model.ItemType == ItemType.ForSale && (!model.Price.HasValue || model.Price.Value <= 0))
        {
            ModelState.AddModelError("Price", "Price is required for items for sale");
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);
            return View(model);
        }

        try
        {
            var item = new Item
            {
                UserId = user.Id,
                Title = model.Title,
                Description = model.Description,
                CategoryId = model.CategoryId,
                ItemType = model.ItemType,
                Price = model.ItemType == ItemType.ForSale ? model.Price : null,
                BarterPreference = model.ItemType == ItemType.Barter ? model.BarterPreference : null,
                Quantity = model.Quantity,
                ExpirationDate = model.ExpirationDate,
                LocationId = model.LocationId,
                Status = ItemStatus.Available,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var itemId = await _itemService.CreateItemAsync(item);

            // Handle image uploads
            if (model.Images != null && model.Images.Any())
            {
                int displayOrder = 0;
                foreach (var imageFile in model.Images.Take(5)) // Max 5 images
                {
                    if (_imageService.ValidateImage(imageFile))
                    {
                        var imageUrl = await _imageService.SaveImageAsync(imageFile, "items");
                        var itemImage = new ItemImage
                        {
                            ItemId = itemId,
                            ImageUrl = imageUrl,
                            IsPrimary = displayOrder == 0,
                            DisplayOrder = displayOrder++,
                            UploadedAt = DateTime.UtcNow
                        };
                        _context.ItemImages.Add(itemImage);
                    }
                }
                await _context.SaveChangesAsync();
            }

            TempData["SuccessMessage"] = "Item listed successfully!";
            return RedirectToAction("Details", new { id = itemId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating item");
            ModelState.AddModelError("", "An error occurred while creating the item. Please try again.");
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);
            return View(model);
        }
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Details(int id)
    {
        var item = await _itemService.GetItemByIdAsync(id);
        if (item == null)
            return NotFound();

        // Increment view count
        await _itemService.IncrementViewCountAsync(id);

        var currentUserId = _userManager.GetUserId(User);
        var viewModel = new ItemDetailsViewModel
        {
            Id = item.Id,
            Title = item.Title,
            Description = item.Description,
            CategoryName = item.Category.Name,
            ItemType = item.ItemType,
            Price = item.Price,
            BarterPreference = item.BarterPreference,
            Status = item.Status,
            Quantity = item.Quantity,
            ExpirationDate = item.ExpirationDate,
            Views = item.Views,
            CreatedAt = item.CreatedAt,
            UserId = item.UserId,
            UserName = $"{item.User.FirstName} {item.User.LastName}",
            UserRating = item.User.Rating,
            LocationAddress = item.Location.AddressLine1,
            LocationCity = item.Location.City,
            LocationState = item.Location.State,
            Latitude = item.Location.Latitude,
            Longitude = item.Location.Longitude,
            ImageUrls = item.Images.OrderBy(i => i.DisplayOrder).Select(i => i.ImageUrl).ToList(),
            IsOwner = currentUserId == item.UserId
        };

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var item = await _itemService.GetItemByIdAsync(id);
        if (item == null)
            return NotFound();

        if (item.UserId != user.Id)
            return Forbid();

        var viewModel = new ItemEditViewModel
        {
            Id = item.Id,
            Title = item.Title,
            Description = item.Description,
            CategoryId = item.CategoryId,
            ItemType = item.ItemType,
            Price = item.Price,
            BarterPreference = item.BarterPreference,
            Quantity = item.Quantity,
            ExpirationDate = item.ExpirationDate,
            LocationId = item.LocationId,
            ExistingImageUrls = item.Images.OrderBy(i => i.DisplayOrder).Select(i => i.ImageUrl).ToList()
        };

        ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
        ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ItemEditViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        if (id != model.Id)
            return NotFound();

        if (!ModelState.IsValid)
        {
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);
            return View(model);
        }

        try
        {
            var item = new Item
            {
                Id = model.Id,
                UserId = user.Id,
                Title = model.Title,
                Description = model.Description,
                CategoryId = model.CategoryId,
                ItemType = model.ItemType,
                Price = model.ItemType == ItemType.ForSale ? model.Price : null,
                BarterPreference = model.ItemType == ItemType.Barter ? model.BarterPreference : null,
                Quantity = model.Quantity,
                ExpirationDate = model.ExpirationDate,
                LocationId = model.LocationId
            };

            var success = await _itemService.UpdateItemAsync(item);
            if (!success)
            {
                TempData["ErrorMessage"] = "Unable to update item. Please try again.";
                return RedirectToAction("MyListings");
            }

            // Handle new image uploads
            if (model.NewImages != null && model.NewImages.Any())
            {
                var existingImages = await _context.ItemImages.Where(i => i.ItemId == id).ToListAsync();
                int displayOrder = existingImages.Count;

                foreach (var imageFile in model.NewImages.Take(5 - existingImages.Count))
                {
                    if (_imageService.ValidateImage(imageFile))
                    {
                        var imageUrl = await _imageService.SaveImageAsync(imageFile, "items");
                        var itemImage = new ItemImage
                        {
                            ItemId = id,
                            ImageUrl = imageUrl,
                            IsPrimary = displayOrder == 0,
                            DisplayOrder = displayOrder++,
                            UploadedAt = DateTime.UtcNow
                        };
                        _context.ItemImages.Add(itemImage);
                    }
                }
                await _context.SaveChangesAsync();
            }

            TempData["SuccessMessage"] = "Item updated successfully!";
            return RedirectToAction("Details", new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating item");
            ModelState.AddModelError("", "An error occurred while updating the item.");
            ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
            ViewBag.Locations = await _locationService.GetUserLocationsAsync(user.Id);
            return View(model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var item = await _itemService.GetItemByIdAsync(id);
        if (item == null)
            return NotFound();

        if (item.UserId != user.Id)
            return Forbid();

        return View(item);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var success = await _itemService.DeleteItemAsync(id, user.Id);
        if (success)
        {
            TempData["SuccessMessage"] = "Item deleted successfully!";
        }
        else
        {
            TempData["ErrorMessage"] = "Unable to delete item. Please try again.";
        }

        return RedirectToAction("MyListings");
    }

    [HttpGet]
    public async Task<IActionResult> MyListings()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return RedirectToAction("Login", "Account");

        var items = await _itemService.GetUserItemsAsync(user.Id);
        return View(items);
    }

    [HttpGet]
    public async Task<IActionResult> SuggestPrice(string title, int categoryId)
    {
        var suggestion = await _priceSuggestionService.SuggestPriceAsync(title, categoryId);
        if (suggestion.HasValue)
        {
            return Json(new
            {
                success = true,
                minPrice = suggestion.Value.MinPrice,
                maxPrice = suggestion.Value.MaxPrice,
                averagePrice = suggestion.Value.AveragePrice
            });
        }

        return Json(new { success = false, message = "No similar items found" });
    }
}
