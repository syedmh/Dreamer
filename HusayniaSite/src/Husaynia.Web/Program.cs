using System.Reflection;
using Husaynia.Application.Contracts;
using Husaynia.Web.Composition;
using Husaynia.Web.Features.LocalSite;

var builder = WebApplication.CreateBuilder(args);

var localSiteEnabled =
    builder.Configuration.GetValue<bool>(LocalSiteOptions.EnabledKey);
LocalSiteOptions.EnsureEnvironment(localSiteEnabled, builder.Environment);

if (localSiteEnabled)
{
    builder.Services.AddLocalSite();
}
else
{
    var compositionAssemblies = new[]
    {
        typeof(IHusayniaModule).Assembly,
        Assembly.Load("Husaynia.Infrastructure"),
        Assembly.GetExecutingAssembly(),
    };

    builder.Configuration.ValidateHusayniaConfiguration(compositionAssemblies);
    builder.Services.AddHusayniaModules(builder.Configuration, compositionAssemblies);
}

var app = builder.Build();
if (localSiteEnabled)
{
    app.UseLocalSiteSecurityHeaders();
    app.UseStaticFiles();
    app.MapLocalSite();
}
else
{
    app.MapHusayniaEndpoints();
}

app.Run();

public partial class Program;
