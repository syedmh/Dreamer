using Microsoft.Extensions.DependencyInjection;
using MindScene.ActorSystem;
using MindScene.Core.Interfaces;

namespace MindScene.RenderEngine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneRenderEngine(this IServiceCollection services)
    {
        services.AddTransient<CharacterRenderer>();
        services.AddScoped<IRenderer, SceneRenderer>();
        return services;
    }
}
