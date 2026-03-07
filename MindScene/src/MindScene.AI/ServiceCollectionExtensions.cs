using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MindScene.Core.Interfaces;

namespace MindScene.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.AddSingleton<IAiClient, AnthropicAiClient>();
        return services;
    }
}
