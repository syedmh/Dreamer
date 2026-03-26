namespace Khilat.Web.Services;

using Khilat.Shared.DTOs.Cart;
using Khilat.Shared.DTOs.Common;

public class CartStateService
{
    private readonly ApiClient _api;
    public event Action? OnCartChanged;
    public CartDto Cart { get; private set; } = new();

    public CartStateService(ApiClient api) => _api = api;

    public async Task LoadCartAsync()
    {
        try
        {
            var response = await _api.GetAsync<ApiResponse<CartDto>>("api/cart");
            if (response?.Success == true && response.Data != null)
                Cart = response.Data;
        }
        catch { Cart = new CartDto(); }
        OnCartChanged?.Invoke();
    }

    public async Task AddToCartAsync(Guid variantId, int qty = 1)
    {
        await _api.PostAsync<AddToCartRequest, ApiResponse<CartDto>>("api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = qty });
        await LoadCartAsync();
    }

    public async Task UpdateQuantityAsync(Guid itemId, int qty)
    {
        await _api.PutAsync<UpdateCartItemRequest, ApiResponse<CartDto>>(
            $"api/cart/items/{itemId}", new UpdateCartItemRequest { Quantity = qty });
        await LoadCartAsync();
    }

    public async Task RemoveItemAsync(Guid itemId)
    {
        await _api.DeleteAsync($"api/cart/items/{itemId}");
        await LoadCartAsync();
    }

    public async Task ClearCartAsync()
    {
        await _api.DeleteAsync("api/cart");
        await LoadCartAsync();
    }
}
