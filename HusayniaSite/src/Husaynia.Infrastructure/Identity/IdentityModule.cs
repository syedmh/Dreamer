using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.Infrastructure.Identity;

public sealed class IdentityInfrastructureModule : IHusayniaModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(PersistenceModule.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = IdentityModuleConfiguration.Read(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddDbContext<HusayniaIdentityDbContext>(dbOptions =>
            dbOptions.UseSqlServer(connectionString));

        services.AddIdentityCore<HusayniaIdentityUser>(ConfigureIdentityOptions)
            .AddRoles<HusayniaIdentityRole>();

        services.AddScoped<IUserStore<HusayniaIdentityUser>>(provider =>
            new UserStore<
                HusayniaIdentityUser,
                HusayniaIdentityRole,
                HusayniaIdentityDbContext,
                Guid,
                IdentityUserClaim<Guid>,
                IdentityUserRole<Guid>,
                IdentityUserLogin<Guid>,
                IdentityUserToken<Guid>,
                IdentityRoleClaim<Guid>>(
                provider.GetRequiredService<HusayniaIdentityDbContext>(),
                provider.GetRequiredService<IdentityErrorDescriber>()));
        services.AddScoped<IRoleStore<HusayniaIdentityRole>>(provider =>
            new RoleStore<
                HusayniaIdentityRole,
                HusayniaIdentityDbContext,
                Guid,
                IdentityUserRole<Guid>,
                IdentityRoleClaim<Guid>>(
                provider.GetRequiredService<HusayniaIdentityDbContext>(),
                provider.GetRequiredService<IdentityErrorDescriber>()));

        services.AddScoped<AdministrativeCapabilityAuthorizer>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IIdentityAnonymousRateLimiter, SqlIdentityAnonymousRateLimiter>();
        services.AddScoped<IIdentityAdministrationStore, IdentityAdministrationStore>();
        services.AddScoped<IUserAdministration, IdentityAdministrationService>();
    }

    private static void ConfigureIdentityOptions(IdentityOptions options)
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.SignIn.RequireConfirmedEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 14;
        options.Password.RequiredUniqueChars = 4;
        options.User.RequireUniqueEmail = true;
    }
}

public sealed class IdentityConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return IdentityModuleConfiguration.Validate(configuration);
    }
}

public sealed class IdentityModuleOptions
{
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromDays(7);

    public IdentityBootstrapOptions Bootstrap { get; init; } = new();

    public IdentityAnonymousRateLimitOptions AnonymousRateLimit { get; init; } = new();
}

public sealed class IdentityBootstrapOptions
{
    public bool Enabled { get; init; }

    public string EnvironmentName { get; init; } = string.Empty;

    public string AdminEmail { get; init; } = string.Empty;

    public string AdminPassword { get; init; } = string.Empty;
}

internal static class IdentityModuleConfiguration
{
    internal static IdentityModuleOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var failures = Validate(configuration);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Identity configuration is invalid: {string.Join(" ", failures)}");
        }

        return new IdentityModuleOptions
        {
            InvitationLifetime = ReadInvitationLifetime(configuration),
            Bootstrap = new IdentityBootstrapOptions
            {
                Enabled = bool.TryParse(configuration["Identity:Bootstrap:Enabled"], out var enabled) &&
                    enabled,
                EnvironmentName = configuration["Identity:Bootstrap:EnvironmentName"] ?? string.Empty,
                AdminEmail = configuration["Identity:Bootstrap:AdminEmail"] ?? string.Empty,
                AdminPassword = configuration["Identity:Bootstrap:AdminPassword"] ?? string.Empty,
            },
            AnonymousRateLimit = new IdentityAnonymousRateLimitOptions
            {
                PermitLimit = int.Parse(
                    configuration["Identity:AnonymousRateLimit:PermitLimit"]!,
                    System.Globalization.CultureInfo.InvariantCulture),
                Window = TimeSpan.Parse(
                    configuration["Identity:AnonymousRateLimit:Window"]!,
                    System.Globalization.CultureInfo.InvariantCulture),
                Retention = TimeSpan.Parse(
                    configuration["Identity:AnonymousRateLimit:Retention"]!,
                    System.Globalization.CultureInfo.InvariantCulture),
                FingerprintKey = Convert.FromBase64String(
                    configuration["Identity:AnonymousRateLimit:FingerprintKey"]!),
            },
        };
    }

    internal static IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var failures = new List<string>();
        var invitationLifetimeValue = configuration["Identity:InvitationLifetime"];
        if (invitationLifetimeValue is not null &&
            !TimeSpan.TryParse(invitationLifetimeValue, out _))
        {
            failures.Add("Identity:InvitationLifetime must be a valid TimeSpan.");
        }

        var invitationLifetime = ReadInvitationLifetime(configuration);
        if (invitationLifetime <= TimeSpan.Zero ||
            invitationLifetime > TimeSpan.FromDays(30))
        {
            failures.Add("Identity:InvitationLifetime must be greater than zero and at most 30 days.");
        }

        var enabledValue = configuration["Identity:Bootstrap:Enabled"];
        if (enabledValue is not null && !bool.TryParse(enabledValue, out _))
        {
            failures.Add("Identity:Bootstrap:Enabled must be true or false.");
        }

        var bootstrapEnabled = bool.TryParse(enabledValue, out var enabled) && enabled;
        if (bootstrapEnabled)
        {
            var environmentName = configuration["Identity:Bootstrap:EnvironmentName"];
            var adminEmail = configuration["Identity:Bootstrap:AdminEmail"];
            var adminPassword = configuration["Identity:Bootstrap:AdminPassword"];
            if (string.IsNullOrWhiteSpace(environmentName))
            {
                failures.Add("Identity:Bootstrap:EnvironmentName is required when bootstrap seeding is enabled.");
            }

            if (string.IsNullOrWhiteSpace(adminEmail) ||
                adminEmail.Trim().Length > 256)
            {
                failures.Add("Identity:Bootstrap:AdminEmail is required and must be 256 characters or fewer.");
            }

            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                failures.Add("Identity:Bootstrap:AdminPassword is required when bootstrap seeding is enabled.");
            }
            else if (adminPassword.Trim().Length < 14)
            {
                failures.Add("Identity:Bootstrap:AdminPassword must be at least 14 characters.");
            }
        }

        ValidateAnonymousRateLimit(configuration, failures);
        return failures;
    }

    private static TimeSpan ReadInvitationLifetime(IConfiguration configuration) =>
        TimeSpan.TryParse(
            configuration["Identity:InvitationLifetime"],
            out var invitationLifetime)
                ? invitationLifetime
                : TimeSpan.FromDays(7);

    private static void ValidateAnonymousRateLimit(
        IConfiguration configuration,
        List<string> failures)
    {
        var permitLimitValue = configuration["Identity:AnonymousRateLimit:PermitLimit"];
        if (!int.TryParse(
                permitLimitValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var permitLimit))
        {
            failures.Add("Identity:AnonymousRateLimit:PermitLimit is required and must be an integer.");
        }
        else if (permitLimit is < 1 or > 1_000)
        {
            failures.Add("Identity:AnonymousRateLimit:PermitLimit must be between 1 and 1000.");
        }

        var windowValue = configuration["Identity:AnonymousRateLimit:Window"];
        if (!TimeSpan.TryParse(
                windowValue,
                System.Globalization.CultureInfo.InvariantCulture,
                out var window))
        {
            failures.Add("Identity:AnonymousRateLimit:Window is required and must be a TimeSpan.");
        }
        else if (window <= TimeSpan.Zero || window > TimeSpan.FromHours(1))
        {
            failures.Add("Identity:AnonymousRateLimit:Window must be greater than zero and at most one hour.");
        }

        var retentionValue = configuration["Identity:AnonymousRateLimit:Retention"];
        if (!TimeSpan.TryParse(
                retentionValue,
                System.Globalization.CultureInfo.InvariantCulture,
                out var retention))
        {
            failures.Add("Identity:AnonymousRateLimit:Retention is required and must be a TimeSpan.");
        }
        else if (retention <= TimeSpan.Zero ||
                 retention > TimeSpan.FromDays(30) ||
                 (window > TimeSpan.Zero && retention < window))
        {
            failures.Add(
                "Identity:AnonymousRateLimit:Retention must be at least the window and at most 30 days.");
        }

        var fingerprintKeyValue = configuration["Identity:AnonymousRateLimit:FingerprintKey"];
        if (string.IsNullOrWhiteSpace(fingerprintKeyValue))
        {
            failures.Add("Identity:AnonymousRateLimit:FingerprintKey is required.");
            return;
        }

        try
        {
            if (Convert.FromBase64String(fingerprintKeyValue).Length < 32)
            {
                failures.Add(
                    "Identity:AnonymousRateLimit:FingerprintKey must contain at least 32 bytes.");
            }
        }
        catch (FormatException)
        {
            failures.Add("Identity:AnonymousRateLimit:FingerprintKey must be valid base64.");
        }
    }
}
