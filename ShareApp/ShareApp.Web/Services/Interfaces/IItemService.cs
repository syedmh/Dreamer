using ShareApp.Web.Entities;

namespace ShareApp.Web.Services.Interfaces;

public interface IItemService
{
    Task<Item?> GetItemByIdAsync(int id);
    Task<IEnumerable<Item>> GetNearbyItemsAsync(decimal latitude, decimal longitude, int radiusMiles);
    Task<IEnumerable<Item>> GetUserItemsAsync(string userId);
    Task<int> CreateItemAsync(Item item);
    Task<bool> UpdateItemAsync(Item item);
    Task<bool> DeleteItemAsync(int id, string userId);
    Task<bool> IncrementViewCountAsync(int itemId);
    Task<IEnumerable<Item>> SearchItemsAsync(string? searchTerm, int? categoryId, ItemType? itemType, decimal? minPrice, decimal? maxPrice);
    Task<IEnumerable<Category>> GetCategoriesAsync();
}
