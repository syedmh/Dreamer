namespace Khilat.Infrastructure.Repositories;

using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class OrderRepository : IOrderRepository
{
    private readonly KhilatDbContext _context;

    public OrderRepository(KhilatDbContext context)
    {
        _context = context;
    }

    public async Task<Order?> GetByIdAsync(Guid id)
        => await _context.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == id);

    public async Task<Order?> GetByOrderNumberAsync(string orderNumber)
        => await _context.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

    public async Task<IReadOnlyList<Order>> GetByCustomerIdAsync(Guid customerId)
        => await _context.Orders
            .Include(o => o.Items)
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

    public async Task<IReadOnlyList<Order>> GetAllAsync(OrderStatus? status = null, int page = 1, int pageSize = 20)
    {
        var query = _context.Orders
            .Include(o => o.Items)
            .Include(o => o.Customer)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        return await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<int> GetTotalCountAsync(OrderStatus? status = null)
    {
        var query = _context.Orders.AsQueryable();
        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);
        return await query.CountAsync();
    }

    public async Task<Order> AddAsync(Order order)
    {
        await _context.Orders.AddAsync(order);
        await _context.SaveChangesAsync();
        return order;
    }

    public async Task UpdateAsync(Order order)
    {
        order.UpdatedAt = DateTime.UtcNow;
        _context.Orders.Update(order);
        await _context.SaveChangesAsync();
    }

    public async Task<string> GenerateOrderNumberAsync()
    {
        var count = await _context.Orders.CountAsync();
        return $"KHL-{DateTime.UtcNow:yyyyMMdd}-{(count + 1):D4}";
    }
}
