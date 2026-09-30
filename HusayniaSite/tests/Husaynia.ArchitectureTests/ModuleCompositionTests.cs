using Husaynia.Application.Contracts;
using Husaynia.Web.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.ArchitectureTests;

public sealed class ModuleCompositionTests
{
    [Fact]
    public void ModulesAreDiscoveredAndRegisteredInDeterministicTypeOrder()
    {
        ModuleRegistrationLog.Names.Clear();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddHusayniaModules(configuration, typeof(ModuleCompositionTests).Assembly);

        Assert.Equal(["Alpha", "Zeta"], ModuleRegistrationLog.Names);
        using var provider = services.BuildServiceProvider();
        Assert.Collection(
            provider.GetServices<IHusayniaModule>(),
            module => Assert.IsType<AlphaFoundationModule>(module),
            module => Assert.IsType<ZetaFoundationModule>(module));
    }

    [Fact]
    public void ConfigurationValidationAggregatesModuleFailures()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<HusayniaConfigurationException>(
            () => configuration.ValidateHusayniaConfiguration(typeof(ModuleCompositionTests).Assembly));

        Assert.Contains("Foundation:RequiredValue is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointModulesAreMappedInDeterministicTypeOrder()
    {
        EndpointMappingLog.Names.Clear();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddHusayniaModules(configuration, typeof(ModuleCompositionTests).Assembly);
        using var provider = services.BuildServiceProvider();
        var endpoints = new TestEndpointRouteBuilder(provider);

        endpoints.MapHusayniaEndpoints();

        Assert.Equal(["Alpha", "Zeta"], EndpointMappingLog.Names);
    }
}

public static class ModuleRegistrationLog
{
    public static List<string> Names { get; } = [];
}

public static class EndpointMappingLog
{
    public static List<string> Names { get; } = [];
}

public sealed class AlphaFoundationModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        ModuleRegistrationLog.Names.Add("Alpha");

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        EndpointMappingLog.Names.Add("Alpha");
}

public sealed class ZetaFoundationModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        ModuleRegistrationLog.Names.Add("Zeta");

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        EndpointMappingLog.Names.Add("Zeta");
}

public sealed class FoundationConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["Foundation:RequiredValue"])
            ? ["Foundation:RequiredValue is required."]
            : [];
}

internal sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    public ICollection<EndpointDataSource> DataSources { get; } = [];

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
