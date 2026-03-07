using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Entities;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Services.Implementations;

public class ItemService : IItemService
{
    private readonly ApplicationDbContext _context;
    private readonly ILocationService _locationService;

    public ItemService(ApplicationDbContext context, ILocationService locationService)
    {
        _context = context;
        _locationService = locationService;
    }

    public async Task<Item?> GetItemByIdAsync(int id)
    {
        return await _context.Items
            .Include(i => i.User)
            .Include(i => i.Category)
            .Include(i => i.Location)
            .Include(i => i.Images.OrderBy(img => img.DisplayOrder))
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<IEnumerable<Item>> GetNearbyItemsAsync(decimal latitude, decimal longitude, int radiusMiles)
    {
        // Get all active items with their locations
        var items = await _context.Items
            .Include(i => i.User)
            .Include(i => i.Category)
            .Include(i => i.Location)
            .Include(i => i.Images.Where(img => img.IsPrimary).Take(1))
            .Where(i => i.IsActive && i.Status == ItemStatus.Available)
            .ToListAsync();

        // Filter by distance
        var nearbyItems = items.Where(i =>
        {
            var distance = _locationService.CalculateDistance(
                latitude, longitude,
                i.Location.Latitude, i.Location.Longitude);
            return distance <= radiusMiles;
        }).ToList();

        return nearbyItems;
    }

    public async Task<IEnumerable<Item>> GetUserItemsAsync(string userId)
    {
        return await _context.Items
            .Include(i => i.Category)
            .Include(i => i.Location)
            .Include(i => i.Images.Where(img => img.IsPrimary).Take(1))
            .Where(i => i.UserId == userId && i.IsActive)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> CreateItemAsync(Item item)
    {
        item.CreatedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        item.Status = ItemStatus.Available;
        item.IsActive = true;

        _context.Items.Add(item);
        await _context.SaveChangesAsync();
        return item.Id;
    }

    public async Task<bool> UpdateItemAsync(Item item)
    {
        var existing = await _context.Items.FindAsync(item.Id);
        if (existing == null || existing.UserId != item.UserId)
            return false;

        existing.Title = item.Title;
        existing.Description = item.Description;
        existing.CategoryId = item.CategoryId;
        existing.ItemType = item.ItemType;
        existing.Price = item.Price;
        existing.BarterPreference = item.BarterPreference;
        existing.Quantity = item.Quantity;
        existing.ExpirationDate = item.ExpirationDate;
        existing.LocationId = item.LocationId;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteItemAsync(int id, string userId)
    {
        var item = await _context.Items.FindAsync(id);
        if (item == null || item.UserId != userId)
            return false;

        // Soft delete - mark as inactive
        item.IsActive = false;
        item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IncrementViewCountAsync(int itemId)
    {
        var item = await _context.Items.FindAsync(itemId);
        if (item == null)
            return false;

        item.Views++;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<Item>> SearchItemsAsync(
        string? searchTerm,
        int? categoryId,
        ItemType? itemType,
        decimal? minPrice,
        decimal? maxPrice)
    {
        var query = _context.Items
            .Include(i => i.User)
            .Include(i => i.Category)
            .Include(i => i.Location)
            .Include(i => i.Images.Where(img => img.IsPrimary).Take(1))
            .Where(i => i.IsActive && i.Status == ItemStatus.Available)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(i =>
                i.Title.Contains(searchTerm) ||
                i.Description.Contains(searchTerm));
        }

        if (categoryId.HasValue)
        {
            query = query.Where(i => i.CategoryId == categoryId.Value);
        }

        if (itemType.HasValue)
        {
            query = query.Where(i => i.ItemType == itemType.Value);
        }

        if (minPrice.HasValue)
        {
            query = query.Where(i => i.Price >= minPrice.Value);
        }

        if (maxPrice.HasValue)
        {
            query = query.Where(i => i.Price <= maxPrice.Value);
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Category>> GetCategoriesAsync()
    {
        return await _context.Categories
            .OrderBy(c => c.Name)
            .ToListAsync();
    }
}
