using Microsoft.AspNetCore.Mvc;
using ShareApp.Web.Models.DTOs;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Controllers.Api
{
    [Route("api/[controller]")]
    [ApiController]
    public class ItemsApiController : ControllerBase
    {
        private readonly IItemService _itemService;
        private readonly ILocationService _locationService;
        private readonly ILogger<ItemsApiController> _logger;

        public ItemsApiController(
            IItemService itemService,
            ILocationService locationService,
            ILogger<ItemsApiController> logger)
        {
            _itemService = itemService;
            _locationService = locationService;
            _logger = logger;
        }

        [HttpGet("nearby")]
        public async Task<ActionResult<IEnumerable<ItemMarkerDto>>> GetNearbyItems(
            [FromQuery] decimal latitude,
            [FromQuery] decimal longitude,
            [FromQuery] int radiusMiles = 10,
            [FromQuery] int? categoryId = null,
            [FromQuery] int? itemType = null,
            [FromQuery] decimal? minPrice = null,
            [FromQuery] decimal? maxPrice = null)
        {
            try
            {
                var items = await _itemService.GetNearbyItemsAsync(latitude, longitude, radiusMiles);

                // Apply filters
                if (categoryId.HasValue)
                {
                    items = items.Where(i => i.CategoryId == categoryId.Value);
                }

                if (itemType.HasValue)
                {
                    items = items.Where(i => (int)i.ItemType == itemType.Value);
                }

                if (minPrice.HasValue)
                {
                    items = items.Where(i => i.Price.HasValue && i.Price.Value >= minPrice.Value);
                }

                if (maxPrice.HasValue)
                {
                    items = items.Where(i => i.Price.HasValue && i.Price.Value <= maxPrice.Value);
                }

                // Map to DTOs
                var markers = items.Select(item => new ItemMarkerDto
                {
                    Id = item.Id,
                    Title = item.Title,
                    ItemType = item.ItemType.ToString(),
                    Price = item.Price,
                    CategoryName = item.Category?.Name ?? "Other",
                    ImageUrl = item.Images != null && item.Images.Any()
                        ? item.Images.First().ImageUrl
                        : "/images/no-image.png",
                    Latitude = item.Location.Latitude,
                    Longitude = item.Location.Longitude,
                    Distance = _locationService.CalculateDistance(
                        latitude, longitude,
                        item.Location.Latitude, item.Location.Longitude)
                }).OrderBy(m => m.Distance).ToList();

                return Ok(markers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching nearby items");
                return StatusCode(500, "An error occurred while fetching nearby items");
            }
        }

        [HttpGet("categories")]
        public async Task<ActionResult<IEnumerable<object>>> GetCategories()
        {
            try
            {
                var categories = await _itemService.GetCategoriesAsync();
                var categoryList = categories.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    iconClass = c.IconClass
                }).ToList();

                return Ok(categoryList);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching categories");
                return StatusCode(500, "An error occurred while fetching categories");
            }
        }

        [HttpGet("{id}/summary")]
        public async Task<ActionResult<object>> GetItemSummary(int id)
        {
            try
            {
                var item = await _itemService.GetItemByIdAsync(id);
                if (item == null)
                {
                    return NotFound();
                }

                var summary = new
                {
                    id = item.Id,
                    title = item.Title,
                    description = item.Description?.Length > 100
                        ? item.Description.Substring(0, 100) + "..."
                        : item.Description,
                    itemType = item.ItemType.ToString(),
                    price = item.Price,
                    categoryName = item.Category?.Name,
                    imageUrl = item.Images.Any() ? item.Images.First().ImageUrl : "/images/no-image.png",
                    userName = item.User?.UserName,
                    status = item.Status.ToString()
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching item summary for ID {ItemId}", id);
                return StatusCode(500, "An error occurred while fetching item details");
            }
        }
    }
}
