namespace ShareApp.Web.Services.Interfaces;

public interface IPriceSuggestionService
{
    Task<(decimal MinPrice, decimal MaxPrice, decimal AveragePrice)?> SuggestPriceAsync(string itemTitle, int categoryId);
}
