using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Infrastructure.Identity.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HusayniaTabruk.Api.Auth;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddTabrukAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddTabrukIdentityServices(configuration);
        services.TryAddScoped<ICurrentActor, ClaimsCurrentActor>();
        services.TryAddScoped<AcceptInvitationService>();
        services.TryAddScoped<LoginService>();
        services.TryAddScoped<RefreshSessionService>();
        services.TryAddScoped<LogoutService>();
        services.TryAddScoped<IssueStepUpService>();

        services
            .AddAuthentication(ApiDefaults.BearerScheme)
            .AddScheme<AuthenticationSchemeOptions, TabrukBearerAuthenticationHandler>(
                ApiDefaults.BearerScheme,
                _ => { });

        return services;
    }
}
