namespace HusayniaTabruk.Api.Configuration;

public static class ApiDefaults
{
    public const string BasePath = "/api/v1";
    public const string BearerScheme = "Bearer";
    public const string TraceHeaderName = "X-Trace-Id";
    public const string IdempotencyHeaderName = "Idempotency-Key";
    public const string IfMatchHeaderName = "If-Match";
    public const string RetryAfterHeaderName = "Retry-After";
}
