using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Application.Contracts;

/// <summary>
/// Defines the deterministic service-composition hook implemented by each feature module.
/// Implementations must be public, concrete, and expose a public parameterless constructor.
/// </summary>
public interface IHusayniaModule
{
    void AddServices(IServiceCollection services, IConfiguration configuration);
}

/// <summary>
/// Validates a module's non-secret configuration before the application is built.
/// </summary>
public interface IHusayniaConfigurationValidator
{
    IReadOnlyCollection<string> Validate(IConfiguration configuration);
}
