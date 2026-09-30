using HusayniaTabruk.Api.Middleware;

namespace HusayniaTabruk.Api.Configuration;

public interface IApiEndpoint
{
    void Map(IEndpointRouteBuilder endpoints);
}

public static class ApiEndpointExtensions
{
    public static void MapDiscoveredEndpoints(this IEndpointRouteBuilder endpoints)
    {
        IEnumerable<IApiEndpoint> endpointDefinitions = typeof(Program).Assembly
            .DefinedTypes
            .Where(type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                typeof(IApiEndpoint).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (IApiEndpoint)Activator.CreateInstance(type.AsType())!);

        foreach (IApiEndpoint endpointDefinition in endpointDefinitions)
        {
            endpointDefinition.Map(endpoints);
        }
    }

    public static TBuilder WithRequestBodyLimit<TBuilder>(this TBuilder builder, int maximumBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        builder.WithMetadata(new RequestBodyLimitMetadata(maximumBytes));
        return builder;
    }

    public static TBuilder WithRateLimit<TBuilder>(this TBuilder builder, params RateLimitRule[] rules)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithRateLimitKeys(ApiRateLimitKeyStrategy.Claims, rules);

    public static TBuilder WithRateLimitKeys<TBuilder>(
        this TBuilder builder,
        ApiRateLimitKeyStrategy keyStrategy,
        params RateLimitRule[] rules)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Length == 0)
        {
            throw new ArgumentException("At least one rate limit rule is required.", nameof(rules));
        }

        builder.WithMetadata(new ApiRateLimitMetadata(rules, keyStrategy));
        return builder;
    }
}

public static class CursorPagination
{
    public static int NormalizePageSize(int? requestedPageSize)
    {
        if (requestedPageSize is null)
        {
            return Domain.Common.ApplicationLimits.DefaultPageSize;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedPageSize.Value);
        return Math.Min(requestedPageSize.Value, Domain.Common.ApplicationLimits.MaximumPageSize);
    }
}

public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
