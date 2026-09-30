using System.Diagnostics;
using HusayniaTabruk.Api.Configuration;

namespace HusayniaTabruk.Api.Middleware;

public sealed class RequestTraceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = ActivityTraceId.CreateRandom().ToString();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiDefaults.TraceHeaderName] = context.TraceIdentifier;
            return Task.CompletedTask;
        });

        await next(context);
    }
}

public static class RequestTraceMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestTracing(this IApplicationBuilder application) =>
        application.UseMiddleware<RequestTraceMiddleware>();
}
