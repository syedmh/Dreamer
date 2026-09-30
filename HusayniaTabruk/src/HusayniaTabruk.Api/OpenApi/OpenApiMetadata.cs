using Microsoft.AspNetCore.Builder;

namespace HusayniaTabruk.Api.OpenApi;

internal static class OpenApiParameterComponents
{
    public const string Cursor = "cursor";
    public const string PageSize = "pageSize";
    public const string DateScope = "dateScope";
    public const string IfMatch = "ifMatch";
    public const string StepUpToken = "stepUpToken";
}

internal sealed record OpenApiParameterReferenceMetadata(string ComponentName);

internal sealed record OpenApiResponseHeaderMetadata(
    int StatusCode,
    string HeaderName);

internal static class OpenApiEndpointConventionBuilderExtensions
{
    public static TBuilder WithOpenApiParameterReference<TBuilder>(
        this TBuilder builder,
        string componentName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);

        builder.WithMetadata(new OpenApiParameterReferenceMetadata(componentName));
        return builder;
    }

    public static TBuilder WithOpenApiResponseHeader<TBuilder>(
        this TBuilder builder,
        int statusCode,
        string headerName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);

        builder.WithMetadata(new OpenApiResponseHeaderMetadata(statusCode, headerName));
        return builder;
    }
}
