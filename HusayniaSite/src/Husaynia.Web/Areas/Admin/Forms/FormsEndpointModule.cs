using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Web.Composition;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Web.Areas.Admin.Forms;

public sealed class FormsEndpointModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Husaynia.Antiforgery";
            options.Cookie.HttpOnly = false;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.HeaderName = "RequestVerificationToken";
        });
        services.AddScoped<FormsAdmission>();
        services.AddHostedService<FormsDeliveryJobStartupService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        FormsEndpoints.Map(endpoints);
    }
}

internal sealed class FormsDeliveryJobStartupService(
    IServiceScopeFactory scopeFactory,
    FormsOptions options,
    IHostEnvironment hostEnvironment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.DeliveryMode == FormDeliveryMode.Pickup &&
            hostEnvironment.IsProduction())
        {
            throw new InvalidOperationException(
                "Forms pickup delivery is prohibited in the Production host environment.");
        }

        if (!options.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IFormsDeliveryJobCoordinator>()
            .RegisterAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Forms delivery job registration failed: {result.Error.Code}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FormsWebAssemblyMarker;
