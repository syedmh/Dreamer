using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Participants;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddTabrukIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TabrukAuthOptions>(configuration.GetSection("TabrukAuth"));

        string connectionString = configuration.GetConnectionString("Tabruk")
            ?? "Host=127.0.0.1;Port=5432;Database=tabruk;Username=tabruk;Password=tabruk;Pooling=false";
        services.AddDbContext<TabrukDbContext>(
            options => options
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.CommandTimeout(10))
                .EnableDetailedErrors());

        services.TryAddSingleton<IPasswordHasher<TabrukIdentityUser>, PasswordHasher<TabrukIdentityUser>>();
        services.TryAddSingleton<IAccessTokenCodec, HmacJwtAccessTokenCodec>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IUnitOfWork, PostgresUnitOfWork>();
        services.TryAddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.TryAddScoped<IServiceDateRepository, PostgresServiceDateRepository>();
        services.TryAddScoped<ISignupRepository, PostgresSignupRepository>();
        services.TryAddScoped<IThreadRepository, PostgresThreadRepository>();
        services.TryAddScoped<INotificationRepository, PostgresNotificationRepository>();
        services.TryAddScoped<INotificationWriter, PostgresNotificationRepository>();
        services.TryAddScoped<IAuditWriter, PostgresAuditWriter>();
        services.TryAddScoped<IPrivilegedAccessWriter, PostgresPrivilegedAccessWriter>();
        services.TryAddScoped<IOutboxWriter, PostgresOutboxWriter>();
        services.TryAddScoped<IEligibleSignupParticipantRepository, PostgresAuthenticationMembershipRepository>();
        services.TryAddScoped<IIdentityService, AspNetIdentityService>();
        services.TryAddScoped<IMembershipRepository, PostgresAuthenticationMembershipRepository>();
        services.TryAddScoped<ITokenService, IdentityTokenService>();
        services.TryAddScoped<IStepUpVerifier, IdentityStepUpVerifier>();
        services.TryAddSingleton<IRefreshTokenFamilyFingerprintProvider, RefreshTokenFamilyFingerprintProvider>();

        return services;
    }
}
