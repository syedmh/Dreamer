using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HusayniaTabruk.Api.Configuration;

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddTabrukApiConventions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(
                new StrictJsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        services.AddTabrukAuthentication(configuration);
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddSingleton<ApiRateLimitStore>();
        services.TryAddSingleton<IApiClientAddressProvider, RemoteIpApiClientAddressProvider>();
        services.TryAddSingleton<IApiRateLimitKeyProvider, EndpointApiRateLimitKeyProvider>();

        return services;
    }
}
