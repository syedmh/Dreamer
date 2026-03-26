namespace Khilat.Api.Controllers;

using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _orderRepository;
    private readonly ICartRepository _cartRepository;
    private readonly IPaymentService _paymentService;

    public OrdersController(
        IOrderRepository orderRepository,
        ICartRepository cartRepository,
        IPaymentService paymentService)
    {
        _orderRepository = orderRepository;
        _cartRepository = cartRepository;
        _paymentService = paymentService;
    }

    [HttpPost("checkout")]
    public async Task<ActionResult<ApiResponse<PaymentResponse>>> Checkout([FromBody] CheckoutRequest request)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<PaymentResponse>.Fail("Customer not found."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null || cart.Items.Count == 0)
            return BadRequest(ApiResponse<PaymentResponse>.Fail("Cart is empty."));

        var subTotal = cart.TotalAmount;
        var shippingCost = 0m;
        var tax = Math.Round(subTotal * 0.08m, 2);
        var totalAmount = subTotal + shippingCost + tax;

        // Create Stripe PaymentIntent
        var metadata = new Dictionary<string, string> { { "customerId", customerId.Value.ToString() } };
        var paymentResult = await _paymentService.CreatePaymentIntentAsync(totalAmount, "usd", metadata);

        var orderNumber = await _orderRepository.GenerateOrderNumberAsync();

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = orderNumber,
            CustomerId = customerId.Value,
            Status = OrderStatus.Pending,
            SubTotal = subTotal,
            ShippingCost = shippingCost,
            Tax = tax,
            TotalAmount = totalAmount,
            StripePaymentIntentId = paymentResult.PaymentIntentId,
            ShippingStreet = request.ShippingAddress.Street,
            ShippingCity = request.ShippingAddress.City,
            ShippingState = request.ShippingAddress.State,
            ShippingZipCode = request.ShippingAddress.ZipCode,
            ShippingCountry = request.ShippingAddress.Country,
            Items = cart.Items.Select(ci => new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductVariantId = ci.ProductVariantId,
                Quantity = ci.Quantity,
                UnitPrice = ci.UnitPrice,
                ProductName = ci.ProductVariant?.Product?.Name ?? string.Empty,
                Color = ci.ProductVariant?.Color ?? ProductColor.Ivory,
                Size = ci.ProductVariant?.Size ?? ProductSize.M,
                Craftsmanship = ci.ProductVariant?.Craftsmanship ?? CraftsmanshipType.MachineProduced
            }).ToList()
        };

        await _orderRepository.AddAsync(order);
        await _cartRepository.ClearAsync(cart.Id);

        var response = new PaymentResponse
        {
            Success = true,
            ClientSecret = paymentResult.ClientSecret,
            PaymentIntentId = paymentResult.PaymentIntentId,
            Order = MapOrder(order)
        };

        return Ok(ApiResponse<PaymentResponse>.Ok(response));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<OrderDto>>>> GetOrders()
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<List<OrderDto>>.Fail("Customer not found."));

        var orders = await _orderRepository.GetByCustomerIdAsync(customerId.Value);
        var dtos = orders.Select(MapOrder).ToList();

        return Ok(ApiResponse<List<OrderDto>>.Ok(dtos));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<OrderDto>>> GetOrder(Guid id)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<OrderDto>.Fail("Customer not found."));

        var order = await _orderRepository.GetByIdAsync(id);
        if (order is null || order.CustomerId != customerId.Value)
            return NotFound(ApiResponse<OrderDto>.Fail("Order not found."));

        return Ok(ApiResponse<OrderDto>.Ok(MapOrder(order)));
    }

    private Guid? GetCustomerId()
    {
        var claim = User.FindFirst("CustomerId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static OrderDto MapOrder(Order o) => new()
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
