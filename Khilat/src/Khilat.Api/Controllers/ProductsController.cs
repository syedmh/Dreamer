namespace Khilat.Api.Controllers;

using Khilat.Api.Helpers;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Shared.DTOs.Common;
using Khilat.Shared.DTOs.Products;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductRepository _productRepository;

    public ProductsController(IProductRepository productRepository)
    {
        _productRepository = productRepository;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ProductDto>>>> GetAll()
    {
        var products = await _productRepository.GetAllAsync();

        var dtos = products
            .Where(p => p.Status == ProductStatus.Active)
            .Select(p => new ProductDto
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
                Variants = p.Variants.Select(MapVariant).ToList(),
                Images = p.Images.OrderBy(i => i.SortOrder).Select(MapImage).ToList(),
                SizeChart = new List<SizeChartDto>()
            })
            .ToList();

        return Ok(ApiResponse<List<ProductDto>>.Ok(dtos));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ProductDto>>> GetById(Guid id)
    {
        var product = await _productRepository.GetWithVariantsAsync(id);
        if (product is null)
            return NotFound(ApiResponse<ProductDto>.Fail("Product not found."));

        var sizeCharts = await _productRepository.GetSizeChartAsync(id);

        var dto = new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            ShortDescription = product.ShortDescription,
            Status = product.Status.ToString(),
            MinPrice = product.Variants.Count > 0 ? product.Variants.Min(v => v.Price) : 0,
            MaxPrice = product.Variants.Count > 0 ? product.Variants.Max(v => v.Price) : 0,
            PrimaryImageUrl = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url
                ?? product.Images.FirstOrDefault()?.Url ?? string.Empty,
            Variants = product.Variants.Select(MapVariant).ToList(),
            Images = product.Images.OrderBy(i => i.SortOrder).Select(MapImage).ToList(),
            SizeChart = sizeCharts.Select(MapSizeChart).ToList()
        };

        return Ok(ApiResponse<ProductDto>.Ok(dto));
    }

    [HttpGet("{id:guid}/variants")]
    public async Task<ActionResult<ApiResponse<List<ProductVariantDto>>>> GetVariants(
        Guid id,
        [FromQuery] string? color = null,
        [FromQuery] string? size = null,
        [FromQuery] string? craftsmanship = null)
    {
        if (!await _productRepository.ExistsAsync(id))
            return NotFound(ApiResponse<List<ProductVariantDto>>.Fail("Product not found."));

        ProductColor? colorFilter = color is not null && Enum.TryParse<ProductColor>(color, true, out var c) ? c : null;
        ProductSize? sizeFilter = size is not null && Enum.TryParse<ProductSize>(size, true, out var s) ? s : null;
        CraftsmanshipType? craftFilter = craftsmanship is not null && Enum.TryParse<CraftsmanshipType>(craftsmanship, true, out var cr) ? cr : null;

        var variants = await _productRepository.GetVariantsAsync(id, colorFilter, sizeFilter, craftFilter);
        var dtos = variants.Select(MapVariant).ToList();

        return Ok(ApiResponse<List<ProductVariantDto>>.Ok(dtos));
    }

    [HttpGet("{id:guid}/sizechart")]
    public async Task<ActionResult<ApiResponse<List<SizeChartDto>>>> GetSizeChart(Guid id)
    {
        if (!await _productRepository.ExistsAsync(id))
            return NotFound(ApiResponse<List<SizeChartDto>>.Fail("Product not found."));

        var sizeCharts = await _productRepository.GetSizeChartAsync(id);
        var dtos = sizeCharts.Select(MapSizeChart).ToList();

        return Ok(ApiResponse<List<SizeChartDto>>.Ok(dtos));
    }

    private static ProductVariantDto MapVariant(Core.Entities.ProductVariant v) => new()
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
    };

    private static ProductImageDto MapImage(Core.Entities.ProductImage i) => new()
    {
        Id = i.Id,
        Url = i.Url,
        AltText = i.AltText,
        SortOrder = i.SortOrder,
        IsPrimary = i.IsPrimary
    };

    private static SizeChartDto MapSizeChart(Core.Entities.SizeChart s) => new()
    {
        Size = s.Size.ToString(),
        FrontLengthInches = s.FrontLengthInches,
        BackLengthInches = s.BackLengthInches,
        ArmLengthInches = s.ArmLengthInches,
        CuffInches = s.CuffInches,
        ButtonLengthInches = s.ButtonLengthInches,
        ShoulderInches = s.ShoulderInches,
        ChestInches = s.ChestInches,
        WaistInches = s.WaistInches,
        HipInches = s.HipInches
    };
}
