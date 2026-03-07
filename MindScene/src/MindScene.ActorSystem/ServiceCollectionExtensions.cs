using Microsoft.Extensions.DependencyInjection;

namespace MindScene.ActorSystem;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneActorSystem(this IServiceCollection services)
    {
        services.AddTransient<CharacterRenderer>();
        return services;
    }
}
