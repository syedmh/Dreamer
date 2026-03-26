namespace Khilat.Api.Controllers;

using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Khilat.Shared.DTOs.Admin;
using Khilat.Shared.DTOs.Auth;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Orders;
using Khilat.Shared.DTOs.Products;
using Khilat.Api.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly KhilatDbContext _dbContext;
    private readonly IOrderRepository _orderRepository;
    private readonly IProductRepository _productRepository;
    private readonly ICustomerRepository _customerRepository;

    public AdminController(
        KhilatDbContext dbContext,
        IOrderRepository orderRepository,
        IProductRepository productRepository,
        ICustomerRepository customerRepository)
    {
        _dbContext = dbContext;
        _orderRepository = orderRepository;
        _productRepository = productRepository;
        _customerRepository = customerRepository;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<AdminDashboardDto>>> GetDashboard()
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var allOrders = await _dbContext.Orders
            .Include(o => o.Items)
            .ToListAsync();

        var completedStatuses = new[] { OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Shipped, OrderStatus.Delivered };

        var totalRevenue = allOrders
            .Where(o => completedStatuses.Contains(o.Status))
            .Sum(o => o.TotalAmount);

        var revenueThisMonth = allOrders
            .Where(o => completedStatuses.Contains(o.Status) && o.CreatedAt >= startOfMonth)
            .Sum(o => o.TotalAmount);

        var ordersThisMonth = allOrders.Count(o => o.CreatedAt >= startOfMonth);
        var pendingOrders = allOrders.Count(o => o.Status == OrderStatus.Pending);
        var totalCustomers = await _dbContext.Customers.CountAsync();

        var topSellingVariants = allOrders
            .Where(o => completedStatuses.Contains(o.Status))
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.Color, i.Size, i.Craftsmanship })
            .Select(g => new TopSellingVariantDto
            {
                Color = g.Key.Color.ToString(),
                Size = g.Key.Size.ToString(),
                Craftsmanship = g.Key.Craftsmanship.ToString(),
                TotalSold = g.Sum(i => i.Quantity),
                TotalRevenue = g.Sum(i => i.Quantity * i.UnitPrice)
            })
            .OrderByDescending(v => v.TotalSold)
            .Take(10)
            .ToList();

        var revenueByMonth = allOrders
            .Where(o => completedStatuses.Contains(o.Status))
            .GroupBy(o => new { o.CreatedAt.Year, o.CreatedAt.Month })
            .Select(g => new RevenueByMonthDto
            {
                Month = $"{g.Key.Year}-{g.Key.Month:D2}",
                Revenue = g.Sum(o => o.TotalAmount),
                OrderCount = g.Count()
            })
            .OrderBy(r => r.Month)
            .ToList();

        var ordersByStatus = allOrders
            .GroupBy(o => o.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var dashboard = new AdminDashboardDto
        {
            TotalRevenue = totalRevenue,
            TotalOrders = allOrders.Count,
            TotalCustomers = totalCustomers,
            PendingOrders = pendingOrders,
            RevenueThisMonth = revenueThisMonth,
            OrdersThisMonth = ordersThisMonth,
            TopSellingVariants = topSellingVariants,
            RevenueByMonth = revenueByMonth,
            OrdersByStatus = ordersByStatus
        };

        return Ok(ApiResponse<AdminDashboardDto>.Ok(dashboard));
    }

    [HttpGet("orders")]
    public async Task<ActionResult<ApiResponse<PagedResult<OrderDto>>>> GetOrders(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        OrderStatus? statusFilter = status is not null && Enum.TryParse<OrderStatus>(status, true, out var s) ? s : null;

        var orders = await _orderRepository.GetAllAsync(statusFilter, page, pageSize);
        var totalCount = await _orderRepository.GetTotalCountAsync(statusFilter);

        var result = new PagedResult<OrderDto>
        {
            Items = orders.Select(MapOrder).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        return Ok(ApiResponse<PagedResult<OrderDto>>.Ok(result));
    }

    [HttpPut("orders/{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<OrderDto>>> UpdateOrderStatus(Guid id, [FromBody] UpdateOrderStatusRequest request)
    {
        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null)
            return NotFound(ApiResponse<OrderDto>.Fail("Order not found."));

        if (!Enum.TryParse<OrderStatus>(request.Status, true, out var newStatus))
            return BadRequest(ApiResponse<OrderDto>.Fail("Invalid order status."));

        order.Status = newStatus;
        order.UpdatedAt = DateTime.UtcNow;
        await _orderRepository.UpdateAsync(order);

        return Ok(ApiResponse<OrderDto>.Ok(MapOrder(order)));
    }

    [HttpGet("products")]
    public async Task<ActionResult<ApiResponse<List<ProductDto>>>> GetProducts()
    {
        var products = await _productRepository.GetAllAsync();

        var dtos = products.Select(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            ShortDescription = p.ShortDescription,
            Status = p.Status.ToString(),
            MinPrice = p.Variants.Count > 0 ? p.Variants.Min(v => v.Price) : 0,
            MaxPrice = p.Variants.Count > 0 ? p.Variants.Max(v => v.Price) : 0,
            PrimaryImageUrl = p.Images.FirstOrDefault(i => i.IsPrimary)?.Url
                ?? p.Images.FirstOrDefault()?.Url ?? string.Empty,
            Variants = p.Variants.Select(v => new ProductVariantDto
            {
                Id = v.Id,
                Color = v.Color.ToString(),
                ColorHex = ColorHelper.GetHex(v.Color),
                Size = v.Size.ToString(),
                Craftsmanship = v.Craftsmanship.ToString(),
                Price = v.Price,
                StockQuantity = v.StockQuantity,
                IsAvailable = v.IsAvailable,
                Sku = v.Sku
            }).ToList(),
            Images = p.Images.OrderBy(i => i.SortOrder).Select(i => new Shared.DTOs.Products.ProductImageDto
            {
                Id = i.Id,
                Url = i.Url,
                AltText = i.AltText,
                SortOrder = i.SortOrder,
                IsPrimary = i.IsPrimary
            }).ToList()
        }).ToList();

        return Ok(ApiResponse<List<ProductDto>>.Ok(dtos));
    }

    [HttpPut("products/{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProductStatus(Guid id, [FromBody] UpdateProductStatusRequest request)
    {
        var product = await _productRepository.GetByIdAsync(id);
        if (product is null)
            return NotFound(ApiResponse<bool>.Fail("Product not found."));

        if (!Enum.TryParse<ProductStatus>(request.Status, true, out var newStatus))
            return BadRequest(ApiResponse<bool>.Fail("Invalid product status."));

        product.Status = newStatus;
        product.UpdatedAt = DateTime.UtcNow;
        await _productRepository.UpdateAsync(product);

        return Ok(ApiResponse<bool>.Ok(true, "Product status updated."));
    }

    [HttpGet("customers")]
    public async Task<ActionResult<ApiResponse<PagedResult<CustomerProfileDto>>>> GetCustomers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var customers = await _customerRepository.GetAllAsync(page, pageSize);
        var totalCount = await _customerRepository.GetTotalCountAsync();

        var result = new PagedResult<CustomerProfileDto>
        {
            Items = customers.Select(c => new CustomerProfileDto
            {
                Id = c.Id,
                FirstName = c.FirstName,
                LastName = c.LastName,
                Email = c.Email,
                Phone = c.Phone,
                Addresses = c.Addresses.Select(a => new AddressDto
                {
                    Id = a.Id,
                    Street = a.Street,
                    City = a.City,
                    State = a.State,
                    ZipCode = a.ZipCode,
                    Country = a.Country,
                    IsDefault = a.IsDefault
                }).ToList()
            }).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        return Ok(ApiResponse<PagedResult<CustomerProfileDto>>.Ok(result));
    }

    [HttpPost("reports/sales")]
    public async Task<ActionResult<ApiResponse<SalesReportDto>>> GenerateSalesReport([FromBody] SalesReportRequest request)
    {
        var orders = await _dbContext.Orders
            .Where(o => o.CreatedAt >= request.FromDate && o.CreatedAt <= request.ToDate)
            .Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded)
            .ToListAsync();

        var items = orders
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new SalesReportItemDto
            {
                Date = g.Key,
                OrderCount = g.Count(),
                Revenue = g.Sum(o => o.TotalAmount)
            })
            .OrderBy(i => i.Date)
            .ToList();

        var report = new SalesReportDto
        {
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            TotalRevenue = orders.Sum(o => o.TotalAmount),
            TotalOrders = orders.Count,
            AverageOrderValue = orders.Count > 0 ? orders.Average(o => o.TotalAmount) : 0,
            Items = items
        };

        return Ok(ApiResponse<SalesReportDto>.Ok(report));
    }

    private static OrderDto MapOrder(Core.Entities.Order o) => new()
    {
        Id = o.Id,
        OrderNumber = o.OrderNumber,
        Status = o.Status.ToString(),
        SubTotal = o.SubTotal,
        ShippingCost = o.ShippingCost,
        Tax = o.Tax,
        TotalAmount = o.TotalAmount,
        ShippingAddress = new ShippingAddressDto
        {
            Street = o.ShippingStreet,
            City = o.ShippingCity,
            State = o.ShippingState,
            ZipCode = o.ShippingZipCode,
            Country = o.ShippingCountry
        },
        Items = o.Items.Select(i => new OrderItemDto
        {
            Id = i.Id,
            ProductName = i.ProductName,
            Color = i.Color.ToString(),
            Size = i.Size.ToString(),
            Craftsmanship = i.Craftsmanship.ToString(),
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            Subtotal = i.Subtotal
        }).ToList(),
        CreatedAt = o.CreatedAt
    };
}
