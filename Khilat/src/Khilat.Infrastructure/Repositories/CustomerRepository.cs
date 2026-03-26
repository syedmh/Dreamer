namespace Khilat.Infrastructure.Repositories;

using Khilat.Core.Entities;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CustomerRepository : ICustomerRepository
{
    private readonly KhilatDbContext _context;

    public CustomerRepository(KhilatDbContext context)
    {
        _context = context;
    }

    public async Task<Customer?> GetByIdAsync(Guid id)
        => await _context.Customers
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.Id == id);

    public async Task<Customer?> GetByUserIdAsync(string userId)
        => await _context.Customers
            .Include(c => c.Addresses)
            .FirstOrDefaultAsync(c => c.UserId == userId);

    public async Task<IReadOnlyList<Customer>> GetAllAsync(int page = 1, int pageSize = 20)
        => await _context.Customers
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

    public async Task<int> GetTotalCountAsync()
        => await _context.Customers.CountAsync();

    public async Task<Customer> AddAsync(Customer customer)
    {
        await _context.Customers.AddAsync(customer);
        await _context.SaveChangesAsync();
        return customer;
    }

    public async Task UpdateAsync(Customer customer)
    {
        customer.UpdatedAt = DateTime.UtcNow;
        _context.Customers.Update(customer);
        await _context.SaveChangesAsync();
    }
}
