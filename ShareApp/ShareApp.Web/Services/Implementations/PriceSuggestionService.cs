using Microsoft.EntityFrameworkCore;
using ShareApp.Web.Data;
using ShareApp.Web.Services.Interfaces;

namespace ShareApp.Web.Services.Implementations;

public class PriceSuggestionService : IPriceSuggestionService
{
    private readonly ApplicationDbContext _context;

    public PriceSuggestionService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(decimal MinPrice, decimal MaxPrice, decimal AveragePrice)?> SuggestPriceAsync(string itemTitle, int categoryId)
    {
        // Find similar items in the same category
        var similarItems = await _context.Items
            .Where(i => i.CategoryId == categoryId &&
                       i.ItemType == Entities.ItemType.ForSale &&
                       i.Price.HasValue &&
                       i.Price > 0 &&
                       i.IsActive)
            .Select(i => i.Price!.Value)
            .ToListAsync();

        if (!similarItems.Any())
            return null;

        var minPrice = similarItems.Min();
        var maxPrice = similarItems.Max();
        var averagePrice = similarItems.Average();

        return (minPrice, maxPrice, Math.Round(averagePrice, 2));
    }
}
