using System.Security.Claims;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;
using Husaynia.Web.Composition;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Web.Areas.Admin.Identity;

public sealed class IdentityAdminEndpointModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddIdentityCore<HusayniaIdentityUser>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Husaynia.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.Zero);
        services.AddAuthorization(options =>
        {
            foreach (var capability in Enum.GetValues<AdministrativeCapability>())
            {
                var allowedRoles = AdministrativeCapabilityAuthorizer
                    .GetCapabilityRoles(capability);
                var policyName = AdministrativeCapabilityAuthorizer.GetPolicyName(capability);
                options.AddPolicy(policyName, policy => policy.RequireAssertion(context =>
                    context.User.Identity?.IsAuthenticated == true &&
                    allowedRoles.Any(context.User.IsInRole) &&
                    IdentityAdminEndpoints.HasSatisfiedMfa(context.User)));
            }
        });
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Husaynia.Antiforgery";
            options.Cookie.HttpOnly = false;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.HeaderName = "RequestVerificationToken";
        });
        services.AddScoped<IdentityAnonymousAdmission>();
        services.AddScoped<IIdentityAuditFinalizer, IdentityAuditFinalizer>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, IdentityAuthorizationMiddlewareResultHandler>();
        services.AddHostedService<IdentityBootstrapSeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        IdentityAdminEndpoints.Map(endpoints);
    }
}

public sealed class IdentityWebAssemblyMarker
{
}
