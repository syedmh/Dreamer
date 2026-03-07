using Microsoft.Extensions.DependencyInjection;
using MindScene.Core.Interfaces;

namespace MindScene.NLP;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneNlp(this IServiceCollection services)
    {
        services.AddScoped<ISceneParser, LlmSceneParser>();
        return services;
    }
}
