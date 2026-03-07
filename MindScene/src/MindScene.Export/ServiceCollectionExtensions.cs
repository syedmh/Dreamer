using Microsoft.Extensions.DependencyInjection;
using MindScene.Core.Interfaces;

namespace MindScene.Export;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneExport(this IServiceCollection services)
    {
        services.AddScoped<IExporter, FfmpegExporter>();
        return services;
    }
}
