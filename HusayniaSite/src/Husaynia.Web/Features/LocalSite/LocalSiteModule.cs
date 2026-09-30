using Microsoft.AspNetCore.Antiforgery;

namespace Husaynia.Web.Features.LocalSite;

public static class LocalSiteOptions
{
    public const string EnabledKey = "Husaynia:LocalDemo:Enabled";

    public static void EnsureEnvironment(bool enabled, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (enabled && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The synthetic local site can run only in the Development environment.");
        }
    }
}

public static class LocalSiteModule
{
    public static IServiceCollection AddLocalSite(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Husaynia.Local.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.FormFieldName = "__RequestVerificationToken";
            options.HeaderName = "RequestVerificationToken";
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LocalSiteStore>();
        return services;
    }

    public static IApplicationBuilder UseLocalSiteSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; base-uri 'self'; form-action 'self'; " +
                "frame-ancestors 'none'; img-src 'self' data:; object-src 'none'; " +
                "script-src 'self'; style-src 'self'; connect-src 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Permissions-Policy"] =
                "camera=(), geolocation=(), microphone=(), payment=(), usb=()";
            context.Response.Headers.CacheControl = "no-store";
            await next().ConfigureAwait(false);
        });
    }
}
