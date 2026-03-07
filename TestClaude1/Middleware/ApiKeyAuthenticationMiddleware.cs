namespace WeatherService.Middleware
{
    /// <summary>
    /// Middleware for API Key authentication
    /// Validates that requests include a valid API key in the header
    /// </summary>
    public class ApiKeyAuthenticationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
        private const string ApiKeyHeaderName = "X-API-Key";

        public ApiKeyAuthenticationMiddleware(
            RequestDelegate next,
            IConfiguration configuration,
            ILogger<ApiKeyAuthenticationMiddleware> logger)
        {
            _next = next;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Skip authentication for Swagger/health check endpoints
            var path = context.Request.Path.Value?.ToLower() ?? "";
            if (path.Contains("/swagger") || path.Contains("/health") || path == "/")
            {
                await _next(context);
                return;
            }

            // Check if API key is present in header
            if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey))
            {
                _logger.LogWarning("API Key missing in request from {IP}", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Unauthorized",
                    message = $"API Key is required. Include it in the '{ApiKeyHeaderName}' header."
                });
                return;
            }

            // Get valid API keys from configuration
            var validApiKeys = _configuration.GetSection("ApiKeyAuthentication:ValidApiKeys")
                .Get<List<string>>() ?? new List<string>();

            // If no valid keys configured, allow all requests (development mode)
            if (validApiKeys.Count == 0 || validApiKeys.All(string.IsNullOrWhiteSpace))
            {
                _logger.LogWarning("No API keys configured - authentication is disabled!");
                await _next(context);
                return;
            }

            // Validate the API key
            if (!validApiKeys.Contains(extractedApiKey.ToString()))
            {
                _logger.LogWarning("Invalid API Key attempted from {IP}", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Unauthorized",
                    message = "Invalid API Key."
                });
                return;
            }

            _logger.LogInformation("Request authenticated successfully with API Key");
            await _next(context);
        }
    }

    /// <summary>
    /// Extension method to add API Key authentication middleware
    /// </summary>
    public static class ApiKeyAuthenticationMiddlewareExtensions
    {
        public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<ApiKeyAuthenticationMiddleware>();
        }
    }
}
