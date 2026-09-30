using System.Reflection;
using Husaynia.Application.Contracts;

namespace Husaynia.Web.Composition;

public static class HusayniaCompositionExtensions
{
    public static IServiceCollection AddHusayniaModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] compositionAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(compositionAssemblies);

        foreach (var module in Discover<IHusayniaModule>(compositionAssemblies))
        {
            module.AddServices(services, configuration);
            services.AddSingleton(module);
            if (module is IHusayniaEndpointModule endpointModule)
            {
                services.AddSingleton(endpointModule);
            }
        }

        return services;
    }

    public static IEndpointRouteBuilder MapHusayniaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var modules = endpoints.ServiceProvider
            .GetServices<IHusayniaEndpointModule>()
            .OrderBy(module => module.GetType().FullName, StringComparer.Ordinal);

        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }

    public static IConfiguration ValidateHusayniaConfiguration(
        this IConfiguration configuration,
        params Assembly[] compositionAssemblies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(compositionAssemblies);

        var failures = Discover<IHusayniaConfigurationValidator>(compositionAssemblies)
            .SelectMany(validator => validator.Validate(configuration).Select(
                failure => $"{validator.GetType().FullName}: {failure}"))
            .ToArray();

        if (failures.Length > 0)
        {
            throw new HusayniaConfigurationException(failures);
        }

        return configuration;
    }

    internal static IReadOnlyList<TContract> Discover<TContract>(IEnumerable<Assembly> assemblies)
        where TContract : class =>
        [.. assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type =>
                typeof(TContract).IsAssignableFrom(type) &&
                type is { IsAbstract: false, IsInterface: false })
            .OrderBy(type => type.Assembly.FullName, StringComparer.Ordinal)
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .Select(Create<TContract>)];

    private static TContract Create<TContract>(Type implementationType)
        where TContract : class
    {
        try
        {
            return (TContract)(Activator.CreateInstance(implementationType) ??
                throw new InvalidOperationException($"Could not create {implementationType.FullName}."));
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"{implementationType.FullName} must expose a public parameterless constructor.",
                exception);
        }
    }
}

public sealed class HusayniaConfigurationException(IReadOnlyCollection<string> failures) : Exception($"Husaynia configuration validation failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}")
{
    public IReadOnlyCollection<string> Failures { get; } = failures;
}
