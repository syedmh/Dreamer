namespace Khilat.Infrastructure.Repositories;

using Khilat.Core.Entities;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CartRepository : ICartRepository
{
    private readonly KhilatDbContext _context;

    public CartRepository(KhilatDbContext context)
    {
        _context = context;
    }

    public async Task<Cart?> GetByCustomerIdAsync(Guid customerId)
        => await _context.Carts
            .Include(c => c.Items)
                .ThenInclude(i => i.ProductVariant)
                    .ThenInclude(v => v.Product)
                        .ThenInclude(p => p.Images.Where(img => img.IsPrimary))
            .FirstOrDefaultAsync(c => c.CustomerId == customerId);

    public async Task<Cart> CreateAsync(Cart cart)
    {
        await _context.Carts.AddAsync(cart);
        await _context.SaveChangesAsync();
        return cart;
    }

    public async Task AddItemAsync(CartItem item)
    {
        await _context.CartItems.AddAsync(item);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateItemAsync(CartItem item)
    {
        _context.CartItems.Update(item);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveItemAsync(Guid cartItemId)
    {
        var item = await _context.CartItems.FindAsync(cartItemId);
        if (item != null)
        {
            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync();
        }
    }

    public async Task ClearAsync(Guid cartId)
    {
        var items = await _context.CartItems.Where(i => i.CartId == cartId).ToListAsync();
        _context.CartItems.RemoveRange(items);
        await _context.SaveChangesAsync();
    }
}
