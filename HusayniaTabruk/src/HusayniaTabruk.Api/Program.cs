using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;

namespace HusayniaTabruk.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplication application = BuildApplication(new WebApplicationOptions
        {
            Args = args,
        });

        await application.RunAsync();
    }

    public static WebApplication BuildApplication(
        WebApplicationOptions? options = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<RouteGroupBuilder>? configureApi = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(options ?? new WebApplicationOptions());
        configureBuilder?.Invoke(builder);
        builder.Services.AddTabrukApiConventions(builder.Configuration);

        WebApplication application = builder.Build();

        application.UseRequestTracing();
        application.UseApiProblemDetails();
        application.UseRouting();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseRequestBodyLimits();
        application.UseApiRateLimits();

        application.MapGet(
                "/openapi/v1.json",
                () => Results.Text(
                    ApiOpenApiDocument.CreateJson(((IEndpointRouteBuilder)application).DataSources),
                    "application/json"))
            .ExcludeFromDescription();

        RouteGroupBuilder api = application
            .MapGroup(ApiDefaults.BasePath)
            .RequireAuthorization();
        api.MapDiscoveredEndpoints();
        configureApi?.Invoke(api);

        if (application.Environment.IsEnvironment("Testing"))
        {
            MapConventionProbeEndpoints(api);
        }

        return application;
    }

    private static void MapConventionProbeEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/_conventions/body/message", ReadBodyAsync)
            .WithRequestBodyLimit(ApplicationLimits.MaximumMessageRequestBytes)
            .ExcludeFromDescription();
        api.MapPost("/_conventions/body/report", ReadBodyAsync)
            .WithRequestBodyLimit(ApplicationLimits.MaximumReportRequestBytes)
            .ExcludeFromDescription();
        api.MapPost("/_conventions/body/signup", ReadBodyAsync)
            .WithRequestBodyLimit(ApplicationLimits.MaximumSignupRequestBytes)
            .ExcludeFromDescription();
        api.MapPost("/_conventions/body/administrative", ReadBodyAsync)
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .ExcludeFromDescription();
        api.MapGet("/_conventions/rate", () => Results.NoContent())
            .WithRateLimit(new RateLimitRule(
                ApiRateLimitPartitions.Account,
                permitLimit: 1,
                window: TimeSpan.FromMinutes(1)))
            .ExcludeFromDescription();
        api.MapGet("/_conventions/rate/multi", () => Results.NoContent())
            .WithRateLimit(
                new RateLimitRule(
                    ApiRateLimitPartitions.Account,
                    permitLimit: 1,
                    window: TimeSpan.FromMinutes(1)),
                new RateLimitRule(
                    ApiRateLimitPartitions.Organization,
                    permitLimit: 2,
                    window: TimeSpan.FromMinutes(1)))
            .ExcludeFromDescription();
        api.MapGet("/_conventions/problem", () => Results.BadRequest())
            .ExcludeFromDescription();
        api.MapPost(
                "/_conventions/json/enum",
                (EnumConventionContract contract) => Results.Ok(contract))
            .ExcludeFromDescription();
        api.MapGet(
                "/_conventions/problem/unexpected",
                static IResult () => throw new InvalidOperationException("contract exception sentinel"))
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        await request.Body.CopyToAsync(Stream.Null, cancellationToken);
        return Results.NoContent();
    }
}

public sealed record EnumConventionContract(MembershipStatus Status);
