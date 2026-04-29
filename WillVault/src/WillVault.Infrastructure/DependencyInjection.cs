using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Infrastructure.Data;
using WillVault.Infrastructure.Identity;
using WillVault.Infrastructure.Notifications;
using WillVault.Infrastructure.Repositories;
using WillVault.Infrastructure.Services;
using WillVault.Infrastructure.Storage;

namespace WillVault.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? "Data Source=willvault.db";

        services.AddDbContext<WillVaultDbContext>(options =>
            options.UseSqlite(connectionString));

        // Identity
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<WillVaultDbContext>();

        // Repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IVaultOwnerRepository, VaultOwnerRepository>();
        services.AddScoped<IVaultItemRepository, VaultItemRepository>();
        services.AddScoped<IRecipientRepository, RecipientRepository>();
        services.AddScoped<ITrustedContactRepository, TrustedContactRepository>();
        services.AddScoped<IDeathVerificationRepository, DeathVerificationRepository>();
        services.AddScoped<IDeliveryJobRepository, DeliveryJobRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // Storage
        services.AddScoped<IStorageProvider, LocalStorageProvider>();

        // Services
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INotificationService, EmailNotificationService>();
        services.AddScoped<ITokenService, TokenService>();

        // Background services
        services.AddHostedService<DeliveryBackgroundService>();

        return services;
    }
}
