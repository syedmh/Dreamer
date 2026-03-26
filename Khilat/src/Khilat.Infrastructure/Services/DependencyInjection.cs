namespace Khilat.Infrastructure.Services;

using Azure.Storage.Blobs;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Data;
using Khilat.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<KhilatDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                b => b.MigrationsAssembly(typeof(KhilatDbContext).Assembly.FullName)));

        // Repositories
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICartRepository, CartRepository>();

        // Stripe
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
        services.AddScoped<IPaymentService, StripePaymentService>();

        // Azure Blob Storage
        var blobConnectionString = configuration["AzureBlobStorage:ConnectionString"] ?? "UseDevelopmentStorage=true";
        services.AddSingleton(new BlobServiceClient(blobConnectionString));
        services.AddScoped<IBlobStorageService, AzureBlobStorageService>();

        return services;
    }
}
