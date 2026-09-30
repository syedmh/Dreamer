using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Api.Middleware;

public sealed record RequestBodyLimitMetadata(int MaximumBytes);

public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        RequestBodyLimitMetadata? limit = context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyLimitMetadata>();
        if (limit is null || context.Request.ContentLength == 0)
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength > limit.MaximumBytes)
        {
            await WritePayloadTooLargeAsync(context, limit.MaximumBytes);
            return;
        }

        await using MemoryStream bufferedBody = new();
        byte[] buffer = new byte[4096];
        int totalBytes = 0;

        while (true)
        {
            int bytesRead = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > limit.MaximumBytes)
            {
                await WritePayloadTooLargeAsync(context, limit.MaximumBytes);
                return;
            }

            await bufferedBody.WriteAsync(buffer.AsMemory(0, bytesRead), context.RequestAborted);
        }

        bufferedBody.Position = 0;
        context.Request.Body = bufferedBody;
        context.Request.ContentLength = totalBytes;
        await next(context);
    }

    private static Task WritePayloadTooLargeAsync(HttpContext context, int maximumBytes) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status413PayloadTooLarge,
            ErrorCodes.PayloadTooLarge,
            "Payload too large",
            $"The request body exceeds the endpoint limit of {maximumBytes} bytes.");
}

public static class RequestBodyLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestBodyLimits(this IApplicationBuilder application) =>
        application.UseMiddleware<RequestBodyLimitMiddleware>();
}
