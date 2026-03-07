using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MindScene.Core.Interfaces;

namespace MindScene.DialogueEngine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMindSceneDialogueEngine(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TtsOptions>(configuration.GetSection(TtsOptions.SectionName));
        services.AddHttpClient<TtsService>();
        services.AddScoped<IDialogueProcessor, DialogueProcessor>();
        return services;
    }
}
