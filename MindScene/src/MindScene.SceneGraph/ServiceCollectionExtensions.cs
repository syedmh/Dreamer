using Microsoft.Extensions.DependencyInjection;

namespace MindScene.SceneGraph;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneSceneGraph(this IServiceCollection services)
    {
        services.AddTransient<SceneTimeline>();
        return services;
    }
}
