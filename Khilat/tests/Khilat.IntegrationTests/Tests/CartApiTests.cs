namespace Khilat.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Khilat.IntegrationTests.Helpers;
using Khilat.Shared.DTOs.Auth;
using Khilat.Shared.DTOs.Cart;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Products;

[Collection("Integration")]
public class CartApiTests
{
    private readonly CustomWebApplicationFactory _factory;

    public CartApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddToCart_ReturnsSuccess()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsync(client);
        var variantId = await GetFirstVariantIdAsync(client);

        var response = await client.PostAsJsonAsync("/api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Items.Should().HaveCountGreaterThan(0);
        result.Data.Items.Should().Contain(i => i.ProductVariantId == variantId);
    }

    [Fact]
    public async Task GetCart_ReturnsCartWithItems()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsync(client);
        var variantId = await GetFirstVariantIdAsync(client);

        // Add an item first
        await client.PostAsJsonAsync("/api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = 2 });

        // Get cart
        var response = await client.GetAsync("/api/cart");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Items.Should().HaveCountGreaterThan(0);
        result.Data.TotalItems.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UpdateCartItem_ChangesQuantity()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsync(client);
        var variantId = await GetFirstVariantIdAsync(client);

        // Add an item
        var addResponse = await client.PostAsJsonAsync("/api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = 1 });
        var addResult = await addResponse.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        var cartItemId = addResult!.Data!.Items.First(i => i.ProductVariantId == variantId).Id;

        // Update quantity
        var updateResponse = await client.PutAsJsonAsync($"/api/cart/items/{cartItemId}",
            new UpdateCartItemRequest { Quantity = 5 });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await updateResponse.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data!.Items.First(i => i.Id == cartItemId).Quantity.Should().Be(5);
    }

    [Fact]
    public async Task RemoveCartItem_RemovesFromCart()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsync(client);
        var variantId = await GetFirstVariantIdAsync(client);

        // Add an item
        var addResponse = await client.PostAsJsonAsync("/api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = 1 });
        var addResult = await addResponse.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        var cartItemId = addResult!.Data!.Items.First(i => i.ProductVariantId == variantId).Id;

        // Remove it
        var removeResponse = await client.DeleteAsync($"/api/cart/items/{cartItemId}");

        removeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await removeResponse.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data!.Items.Should().NotContain(i => i.Id == cartItemId);
    }

    [Fact]
    public async Task ClearCart_EmptiesCart()
    {
        var client = _factory.CreateClient();
        await AuthenticateAsync(client);
        var variantId = await GetFirstVariantIdAsync(client);

        // Add an item
        await client.PostAsJsonAsync("/api/cart/items",
            new AddToCartRequest { ProductVariantId = variantId, Quantity = 2 });

        // Clear cart
        var clearResponse = await client.DeleteAsync("/api/cart");

        clearResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await clearResponse.Content.ReadFromJsonAsync<ApiResponse<bool>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Verify cart is empty
        var getResponse = await client.GetAsync("/api/cart");
        var getResult = await getResponse.Content.ReadFromJsonAsync<ApiResponse<CartDto>>();
        getResult!.Data!.Items.Should().BeEmpty();
    }

    /// <summary>
    /// Registers a unique user, logs in, and sets the Authorization header on the client.
    /// </summary>
    private static async Task AuthenticateAsync(HttpClient client)
    {
        var email = $"cart-test-{Guid.NewGuid():N}@test.com";
        const string password = "TestPass1234";

        var registerRequest = new RegisterRequest
        {
            FirstName = "Cart",
            LastName = "Tester",
            Email = email,
            Password = password,
            ConfirmPassword = password
        };

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        var registerResult = await registerResponse.Content
            .ReadFromJsonAsync<ApiResponse<AuthResponse>>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registerResult!.Data!.Token);
    }

    /// <summary>
    /// Retrieves the first product variant id from the seeded test data.
    /// </summary>
    private static async Task<Guid> GetFirstVariantIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/products");
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProductDto>>>();
        return result!.Data![0].Variants[0].Id;
    }
}
