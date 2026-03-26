namespace Khilat.Core.Interfaces;

using Khilat.Core.Entities;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id);
    Task<Customer?> GetByUserIdAsync(string userId);
    Task<IReadOnlyList<Customer>> GetAllAsync(int page = 1, int pageSize = 20);
    Task<int> GetTotalCountAsync();
    Task<Customer> AddAsync(Customer customer);
    Task UpdateAsync(Customer customer);
}
