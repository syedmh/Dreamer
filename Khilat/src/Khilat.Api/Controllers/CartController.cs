namespace Khilat.Api.Controllers;

using Khilat.Api.Helpers;
using Khilat.Core.Entities;
using Khilat.Core.Interfaces;
using Khilat.Shared.DTOs.Cart;
using Khilat.Shared.DTOs.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CartController : ControllerBase
{
    private readonly ICartRepository _cartRepository;
    private readonly IProductRepository _productRepository;

    public CartController(ICartRepository cartRepository, IProductRepository productRepository)
    {
        _cartRepository = cartRepository;
        _productRepository = productRepository;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<CartDto>>> GetCart()
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CartDto>.Fail("Customer not found."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null)
            return Ok(ApiResponse<CartDto>.Ok(new CartDto()));

        return Ok(ApiResponse<CartDto>.Ok(MapCart(cart)));
    }

    [HttpPost("items")]
    public async Task<ActionResult<ApiResponse<CartDto>>> AddItem([FromBody] AddToCartRequest request)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CartDto>.Fail("Customer not found."));

        var variant = await _productRepository.GetVariantByIdAsync(request.ProductVariantId);
        if (variant is null)
            return NotFound(ApiResponse<CartDto>.Fail("Product variant not found."));

        if (!variant.IsAvailable)
            return BadRequest(ApiResponse<CartDto>.Fail("Product variant is out of stock."));

        if (request.Quantity <= 0)
            return BadRequest(ApiResponse<CartDto>.Fail("Quantity must be greater than zero."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null)
        {
            cart = await _cartRepository.CreateAsync(new Cart
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId.Value
            });
        }

        var existingItem = cart.Items.FirstOrDefault(i => i.ProductVariantId == request.ProductVariantId);
        if (existingItem is not null)
        {
            existingItem.Quantity += request.Quantity;
            await _cartRepository.UpdateItemAsync(existingItem);
        }
        else
        {
            var cartItem = new CartItem
            {
                Id = Guid.NewGuid(),
                CartId = cart.Id,
                ProductVariantId = request.ProductVariantId,
                Quantity = request.Quantity,
                UnitPrice = variant.Price
            };
            await _cartRepository.AddItemAsync(cartItem);
        }

        var updatedCart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        return Ok(ApiResponse<CartDto>.Ok(MapCart(updatedCart!)));
    }

    [HttpPut("items/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CartDto>>> UpdateItem(Guid id, [FromBody] UpdateCartItemRequest request)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CartDto>.Fail("Customer not found."));

        if (request.Quantity <= 0)
            return BadRequest(ApiResponse<CartDto>.Fail("Quantity must be greater than zero."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null)
            return NotFound(ApiResponse<CartDto>.Fail("Cart not found."));

        var item = cart.Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return NotFound(ApiResponse<CartDto>.Fail("Cart item not found."));

        item.Quantity = request.Quantity;
        await _cartRepository.UpdateItemAsync(item);

        var updatedCart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        return Ok(ApiResponse<CartDto>.Ok(MapCart(updatedCart!)));
    }

    [HttpDelete("items/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CartDto>>> RemoveItem(Guid id)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CartDto>.Fail("Customer not found."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null)
            return NotFound(ApiResponse<CartDto>.Fail("Cart not found."));

        var item = cart.Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return NotFound(ApiResponse<CartDto>.Fail("Cart item not found."));

        await _cartRepository.RemoveItemAsync(id);

        var updatedCart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        return Ok(ApiResponse<CartDto>.Ok(MapCart(updatedCart!)));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<bool>>> ClearCart()
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<bool>.Fail("Customer not found."));

        var cart = await _cartRepository.GetByCustomerIdAsync(customerId.Value);
        if (cart is null)
            return Ok(ApiResponse<bool>.Ok(true));

        await _cartRepository.ClearAsync(cart.Id);
        return Ok(ApiResponse<bool>.Ok(true, "Cart cleared."));
    }

    private Guid? GetCustomerId()
    {
        var claim = User.FindFirst("CustomerId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static CartDto MapCart(Cart cart) => new()
    {
        Id = cart.Id,
        TotalAmount = cart.TotalAmount,
        TotalItems = cart.TotalItems,
        Items = cart.Items.Select(i => new CartItemDto
        {
            Id = i.Id,
            ProductVariantId = i.ProductVariantId,
            ProductName = i.ProductVariant?.Product?.Name ?? string.Empty,
            Color = i.ProductVariant?.Color.ToString() ?? string.Empty,
            ColorHex = i.ProductVariant is not null ? ColorHelper.GetHex(i.ProductVariant.Color) : string.Empty,
            Size = i.ProductVariant?.Size.ToString() ?? string.Empty,
            Craftsmanship = i.ProductVariant?.Craftsmanship.ToString() ?? string.Empty,
            ImageUrl = i.ProductVariant?.Product?.Images.FirstOrDefault(img => img.IsPrimary)?.Url
                ?? i.ProductVariant?.Product?.Images.FirstOrDefault()?.Url ?? string.Empty,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            Subtotal = i.Subtotal
        }).ToList()
    };
}
