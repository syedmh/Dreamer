using FluentAssertions;
using Khilat.Api.Controllers;
using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Products;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Khilat.UnitTests.Api;

public class ProductsControllerTests
{
    private readonly Mock<IProductRepository> _mockRepo;
    private readonly ProductsController _controller;

    public ProductsControllerTests()
    {
        _mockRepo = new Mock<IProductRepository>();
        _controller = new ProductsController(_mockRepo.Object);
    }

    [Fact]
    public async Task GetAll_ReturnsProducts_WhenProductsExist()
    {
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var products = new List<Product>
        {
            new Product
            {
                Id = productId,
                Name = "Classic Kurta",
                Description = "A beautiful hand-stitched kurta",
                ShortDescription = "Classic kurta",
                Status = ProductStatus.Active,
                Variants = new List<ProductVariant>
                {
                    new ProductVariant
                    {
                        Id = variantId,
                        Color = ProductColor.Ivory,
                        Size = ProductSize.M,
                        Craftsmanship = CraftsmanshipType.HandStitched,
                        Price = 200m,
                        StockQuantity = 10,
                        Sku = "KH-IVR-M-HS"
                    }
                },
                Images = new List<ProductImage>
                {
                    new ProductImage { Url = "https://img.test/1.jpg", IsPrimary = true, SortOrder = 1 }
                }
            }
        };

        _mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(products);

        var actionResult = await _controller.GetAll();
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();

        var response = okResult!.Value as ApiResponse<List<ProductDto>>;
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Data.Should().ContainSingle();

        var dto = response.Data!.First();
        dto.Name.Should().Be("Classic Kurta");
        dto.Variants.Should().ContainSingle();
        dto.Variants.First().Color.Should().Be("Ivory");
        dto.Variants.First().Price.Should().Be(200m);
        dto.MinPrice.Should().Be(200m);
        dto.MaxPrice.Should().Be(200m);
    }

    [Fact]
    public async Task GetById_ReturnsProduct_WhenExists()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Silk Sherwani",
            Status = ProductStatus.Active,
            Variants = new List<ProductVariant>
            {
                new ProductVariant
                {
                    Id = Guid.NewGuid(),
                    Color = ProductColor.MidnightNavy,
                    Size = ProductSize.L,
                    Craftsmanship = CraftsmanshipType.HandStitched,
                    Price = 450m,
                    StockQuantity = 3,
                    Sku = "KH-MN-L-HS"
                }
            },
            Images = new List<ProductImage>()
        };

        _mockRepo.Setup(r => r.GetWithVariantsAsync(productId)).ReturnsAsync(product);
        _mockRepo.Setup(r => r.GetSizeChartAsync(productId)).ReturnsAsync(new List<SizeChart>());

        var actionResult = await _controller.GetById(productId);
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();

        var response = okResult!.Value as ApiResponse<ProductDto>;
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Data!.Name.Should().Be("Silk Sherwani");
        response.Data.Variants.Should().ContainSingle();
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenNotExists()
    {
        var productId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetWithVariantsAsync(productId)).ReturnsAsync((Product?)null);

        var actionResult = await _controller.GetById(productId);
        var notFoundResult = actionResult.Result as NotFoundObjectResult;

        notFoundResult.Should().NotBeNull();
        var response = notFoundResult!.Value as ApiResponse<ProductDto>;
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetSizeChart_ReturnsMeasurements()
    {
        var productId = Guid.NewGuid();
        var sizeCharts = new List<SizeChart>
        {
            new SizeChart
            {
                Size = ProductSize.M,
                FrontLengthInches = 28m,
                BackLengthInches = 31m,
                ArmLengthInches = 29m,
                CuffInches = 4m,
                ButtonLengthInches = 26m,
                ShoulderInches = 15.5m,
                ChestInches = 20.5m,
                WaistInches = 20m,
                HipInches = 20.5m
            }
        };

        _mockRepo.Setup(r => r.ExistsAsync(productId)).ReturnsAsync(true);
        _mockRepo.Setup(r => r.GetSizeChartAsync(productId)).ReturnsAsync(sizeCharts);

        var actionResult = await _controller.GetSizeChart(productId);
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();

        var response = okResult!.Value as ApiResponse<List<SizeChartDto>>;
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Data.Should().ContainSingle();

        var chart = response.Data!.First();
        chart.Size.Should().Be("M");
        chart.ChestInches.Should().Be(20.5m);
        chart.ShoulderInches.Should().Be(15.5m);
    }

    [Fact]
    public async Task GetVariants_AppliesFilters()
    {
        var productId = Guid.NewGuid();
        var filteredVariants = new List<ProductVariant>
        {
            new ProductVariant
            {
                Id = Guid.NewGuid(),
                Color = ProductColor.Ivory,
                Size = ProductSize.M,
                Craftsmanship = CraftsmanshipType.HandStitched,
                Price = 200m,
                StockQuantity = 5,
                Sku = "KH-IVR-M-HS"
            }
        };

        _mockRepo.Setup(r => r.ExistsAsync(productId)).ReturnsAsync(true);
        _mockRepo.Setup(r => r.GetVariantsAsync(
            productId,
            ProductColor.Ivory,
            ProductSize.M,
            It.IsAny<CraftsmanshipType?>()))
            .ReturnsAsync(filteredVariants);

        var actionResult = await _controller.GetVariants(productId, "Ivory", "M", null);
        var okResult = actionResult.Result as OkObjectResult;
        okResult.Should().NotBeNull();

        var response = okResult!.Value as ApiResponse<List<ProductVariantDto>>;
        response.Should().NotBeNull();
        response!.Success.Should().BeTrue();
        response.Data.Should().ContainSingle();
        response.Data!.First().Color.Should().Be("Ivory");
        response.Data.First().Size.Should().Be("M");
    }
}
