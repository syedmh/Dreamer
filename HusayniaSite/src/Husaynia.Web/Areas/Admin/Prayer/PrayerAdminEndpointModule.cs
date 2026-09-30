using Husaynia.Application.Contracts;
using Husaynia.Web.Composition;

namespace Husaynia.Web.Areas.Admin.Prayer;

public sealed class PrayerAdminEndpointModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddHostedService<PrayerRefreshJobStartupService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        PrayerAdminEndpoints.Map(endpoints);
    }
}
