namespace Khilat.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Khilat.IntegrationTests.Helpers;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Products;

[Collection("Integration")]
public class ProductsApiTests
{
    private readonly HttpClient _client;

    public ProductsApiTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetProducts_ReturnsSuccessAndProducts()
    {
        var response = await _client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<ProductDto>>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Count.Should().BeGreaterThan(0);
        result.Data[0].Name.Should().Be("The Khilat");
    }

    [Fact]
    public async Task GetProductById_ReturnsProduct_WhenExists()
    {
        // First get the list to find a valid product id
        var listResponse = await _client.GetAsync("/api/products");
        var listResult = await listResponse.Content.ReadFromJsonAsync<ApiResponse<List<ProductDto>>>();
        var productId = listResult!.Data![0].Id;

        var response = await _client.GetAsync($"/api/products/{productId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Id.Should().Be(productId);
        result.Data.Name.Should().Be("The Khilat");
        result.Data.Variants.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task GetProductById_ReturnsNotFound_WhenNotExists()
    {
        var randomId = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/products/{randomId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<ProductDto>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetSizeChart_ReturnsMeasurements()
    {
        // Get a valid product id
        var listResponse = await _client.GetAsync("/api/products");
        var listResult = await listResponse.Content.ReadFromJsonAsync<ApiResponse<List<ProductDto>>>();
        var productId = listResult!.Data![0].Id;

        var response = await _client.GetAsync($"/api/products/{productId}/sizechart");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<SizeChartDto>>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Count.Should().BeGreaterThan(0);

        var sizeM = result.Data.First(s => s.Size == "M");
        sizeM.FrontLengthInches.Should().Be(28);
        sizeM.BackLengthInches.Should().Be(31);
        sizeM.ChestInches.Should().Be(20.5m);
    }
}
