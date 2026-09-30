namespace Husaynia.Web.Composition;

/// <summary>
/// Defines the optional web-only endpoint hook for modules that own HTTP endpoints.
/// </summary>
public interface IHusayniaEndpointModule
{
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
