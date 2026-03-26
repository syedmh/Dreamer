namespace Khilat.Core.Interfaces;

using Khilat.Core.Entities;
using Khilat.Core.Enums;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id);
    Task<Order?> GetByOrderNumberAsync(string orderNumber);
    Task<IReadOnlyList<Order>> GetByCustomerIdAsync(Guid customerId);
    Task<IReadOnlyList<Order>> GetAllAsync(OrderStatus? status = null, int page = 1, int pageSize = 20);
    Task<int> GetTotalCountAsync(OrderStatus? status = null);
    Task<Order> AddAsync(Order order);
    Task UpdateAsync(Order order);
    Task<string> GenerateOrderNumberAsync();
}
