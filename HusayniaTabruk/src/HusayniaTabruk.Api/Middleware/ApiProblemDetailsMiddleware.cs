using System.Text.Json.Serialization;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Domain.Common.Errors;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Api.Middleware;

public sealed record ApiProblemDetails(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("fieldErrors"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? FieldErrors = null);

public sealed partial class ApiProblemDetailsMiddleware(
    RequestDelegate next,
    ILogger<ApiProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception)
        {
            if (context.Response.HasStarted)
            {
                LogResponseStarted(
                    logger,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Request.Path,
                    GetEndpointName(context),
                    exception);
                throw;
            }

            LogMalformedRequest(
                logger,
                context.TraceIdentifier,
                context.Request.Method,
                context.Request.Path,
                GetEndpointName(context),
                exception);
            context.Response.Clear();
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "bad_request",
                "Bad request",
                "The request body is malformed or invalid.");
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (DependencyUnavailableException exception)
        {
            if (context.Response.HasStarted)
            {
                LogResponseStarted(
                    logger,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Request.Path,
                    GetEndpointName(context),
                    exception);
                throw;
            }

            LogDependencyUnavailable(
                logger,
                context.TraceIdentifier,
                context.Request.Method,
                context.Request.Path,
                GetEndpointName(context),
                exception);
            context.Response.Clear();
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.DependencyUnavailable,
                "Service unavailable",
                "A required dependency is temporarily unavailable.");
            return;
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                LogResponseStarted(
                    logger,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Request.Path,
                    GetEndpointName(context),
                    exception);
                throw;
            }

            LogUnexpectedException(
                logger,
                context.TraceIdentifier,
                context.Request.Method,
                context.Request.Path,
                GetEndpointName(context),
                exception);
            context.Response.Clear();
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "internal_error",
                "Internal server error",
                "An unexpected error occurred.");
            return;
        }

        if (IsProblemDetailsPath(context.Request.Path) &&
            context.Response.StatusCode >= StatusCodes.Status400BadRequest &&
            !context.Response.HasStarted)
        {
            (string code, string title) = ProblemForStatus(context.Response.StatusCode);
            await ApiProblemWriter.WriteAsync(
                context,
                context.Response.StatusCode,
                code,
                title,
                title + ".");
        }
    }

    private static (string Code, string Title) ProblemForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => ("bad_request", "Bad request"),
        StatusCodes.Status401Unauthorized => ("unauthorized", "Authentication required"),
        StatusCodes.Status403Forbidden => ("forbidden", "Forbidden"),
        StatusCodes.Status404NotFound => ("not_found", "Not found"),
        StatusCodes.Status409Conflict => ("conflict", "Conflict"),
        StatusCodes.Status412PreconditionFailed => (ErrorCodes.StaleVersion, "Precondition failed"),
        StatusCodes.Status413PayloadTooLarge => (ErrorCodes.PayloadTooLarge, "Payload too large"),
        StatusCodes.Status429TooManyRequests => (ErrorCodes.RateLimited, "Rate limit exceeded"),
        StatusCodes.Status500InternalServerError => ("internal_error", "Internal server error"),
        StatusCodes.Status503ServiceUnavailable => (ErrorCodes.DependencyUnavailable, "Service unavailable"),
        _ => ("request_failed", "Request failed"),
    };

    private static bool IsProblemDetailsPath(PathString path) =>
        path.StartsWithSegments(ApiDefaults.BasePath, StringComparison.Ordinal) ||
        path.Equals("/openapi/v1.json");

    private static string GetEndpointName(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ??
        context.GetEndpoint()?.DisplayName ??
        "unmatched";

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message =
            "Malformed API request. TraceId: {TraceId}; Method: {Method}; Path: {Path}; Endpoint: {Endpoint}")]
    private static partial void LogMalformedRequest(
        ILogger logger,
        string traceId,
        string method,
        PathString path,
        string endpoint,
        Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message =
            "Unexpected API exception. TraceId: {TraceId}; Method: {Method}; Path: {Path}; Endpoint: {Endpoint}")]
    private static partial void LogUnexpectedException(
        ILogger logger,
        string traceId,
        string method,
        PathString path,
        string endpoint,
        Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message =
            "Required API dependency unavailable. TraceId: {TraceId}; Method: {Method}; Path: {Path}; Endpoint: {Endpoint}")]
    private static partial void LogDependencyUnavailable(
        ILogger logger,
        string traceId,
        string method,
        PathString path,
        string endpoint,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message =
            "API exception after response started. TraceId: {TraceId}; Method: {Method}; Path: {Path}; Endpoint: {Endpoint}")]
    private static partial void LogResponseStarted(
        ILogger logger,
        string traceId,
        string method,
        PathString path,
        string endpoint,
        Exception exception);
}

public static class ApiProblemWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        ApiProblemDetails problem = new(
            Type: $"https://httpstatuses.com/{status}",
            Title: title,
            Status: status,
            Code: code,
            Detail: detail,
            TraceId: context.TraceIdentifier,
            FieldErrors: fieldErrors);

        JsonOptions options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value;
        await context.Response.WriteAsJsonAsync(
            problem,
            options.SerializerOptions,
            contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }
}

public static class ApiProblemDetailsMiddlewareExtensions
{
    public static IApplicationBuilder UseApiProblemDetails(this IApplicationBuilder application) =>
        application.UseMiddleware<ApiProblemDetailsMiddleware>();
}
