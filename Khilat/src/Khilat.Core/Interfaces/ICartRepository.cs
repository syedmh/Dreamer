namespace Khilat.Core.Interfaces;

using Khilat.Core.Entities;

public interface ICartRepository
{
    Task<Cart?> GetByCustomerIdAsync(Guid customerId);
    Task<Cart> CreateAsync(Cart cart);
    Task AddItemAsync(CartItem item);
    Task UpdateItemAsync(CartItem item);
    Task RemoveItemAsync(Guid cartItemId);
    Task ClearAsync(Guid cartId);
}
