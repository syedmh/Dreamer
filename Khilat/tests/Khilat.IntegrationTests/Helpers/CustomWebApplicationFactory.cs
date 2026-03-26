namespace Khilat.IntegrationTests.Helpers;

using Khilat.Core.Entities;
using Khilat.Core.Enums;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Remove all DbContext-related registrations added by AddInfrastructure
            RemoveService<DbContextOptions<KhilatDbContext>>(services);
            RemoveService<KhilatDbContext>(services);

            // Add InMemory database — database name must be constant so all scopes share the same store
            var dbName = "KhilatTestDb_" + Guid.NewGuid();
            services.AddDbContext<KhilatDbContext>(options =>
                options.UseInMemoryDatabase(dbName));

            // Remove original external service registrations and replace with mocks
            RemoveService<IPaymentService>(services);
            RemoveService<IBlobStorageService>(services);

            var mockPayment = new Mock<IPaymentService>();
            mockPayment
                .Setup(p => p.CreatePaymentIntentAsync(
                    It.IsAny<decimal>(),
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(new PaymentIntentResult
                {
                    PaymentIntentId = "pi_test",
                    ClientSecret = "cs_test",
                    Status = "requires_payment_method"
                });
            services.AddScoped(_ => mockPayment.Object);

            var mockBlob = new Mock<IBlobStorageService>();
            mockBlob
                .Setup(b => b.UploadAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()))
                .ReturnsAsync("https://test.blob.core/image.jpg");
            services.AddScoped(_ => mockBlob.Object);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // Seed test data after the host is fully built and Program.cs seeder has run
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KhilatDbContext>();
        SeedTestData(db);

        return host;
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    private static void SeedTestData(KhilatDbContext context)
    {
        // Program.cs SeedData may have already added products; only add if empty
        if (context.Products.Any()) return;

        var productId = Guid.NewGuid();
        context.Products.Add(new Product
        {
            Id = productId,
            Name = "Test Khilat",
            Description = "Test product",
            ShortDescription = "Test",
            Status = ProductStatus.Active
        });

        context.ProductVariants.AddRange(
            new ProductVariant
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Color = ProductColor.Ivory,
                Size = ProductSize.M,
                Craftsmanship = CraftsmanshipType.HandStitched,
                Price = 219m,
                StockQuantity = 10,
                Sku = "TEST-IVO-M-HS"
            },
            new ProductVariant
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Color = ProductColor.Ivory,
                Size = ProductSize.M,
                Craftsmanship = CraftsmanshipType.MachineProduced,
                Price = 109m,
                StockQuantity = 15,
                Sku = "TEST-IVO-M-MP"
            },
            new ProductVariant
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Color = ProductColor.Burgundy,
                Size = ProductSize.L,
                Craftsmanship = CraftsmanshipType.HandStitched,
                Price = 224m,
                StockQuantity = 5,
                Sku = "TEST-BUR-L-HS"
            });

        context.SizeCharts.Add(new SizeChart
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Size = ProductSize.M,
            FrontLengthInches = 28,
            BackLengthInches = 31,
            ArmLengthInches = 29,
            CuffInches = 4,
            ButtonLengthInches = 26,
            ShoulderInches = 15.5m,
            ChestInches = 20.5m,
            WaistInches = 20,
            HipInches = 20.5m
        });

        context.SaveChanges();
    }
}
