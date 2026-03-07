using System.Threading.RateLimiting;
using WeatherService.Services;
using WeatherService.Middleware;

// Create the web application builder with default configuration
var builder = WebApplication.CreateBuilder(args);

// ===== SERVICE CONFIGURATION =====
// Add services to the dependency injection container

// Register MVC controllers for API endpoints
builder.Services.AddControllers();

// Add API explorer for endpoint discovery (required for Swagger)
builder.Services.AddEndpointsApiExplorer();

// Add Swagger/OpenAPI documentation generator
builder.Services.AddSwaggerGen();

// Register HttpClient for making external API calls with timeout and retry policies
builder.Services.AddHttpClient<IWeatherService, OpenWeatherMapService>()
    .ConfigureHttpClient(client =>
    {
        // Set timeout for external API calls (10 seconds)
        client.Timeout = TimeSpan.FromSeconds(10);
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5)); // Rotate handlers every 5 minutes

// Register the weather service with dependency injection
builder.Services.AddScoped<IWeatherService, OpenWeatherMapService>();

// Configure Rate Limiting
builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.RejectionStatusCode = 429; // Too Many Requests

    // Fixed window rate limiter: 100 requests per minute per IP
    rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0 // No queuing
            }));
});

// Build the application
var app = builder.Build();

// ===== MIDDLEWARE PIPELINE CONFIGURATION =====
// Configure the HTTP request pipeline (middleware order matters!)

// Add security headers
app.Use(async (context, next) =>
{
    // Prevent MIME-sniffing attacks
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");

    // Prevent clickjacking attacks
    context.Response.Headers.Append("X-Frame-Options", "DENY");

    // Enable XSS protection
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");

    // Control referrer information
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

    // Enforce HTTPS (HSTS)
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }

    await next();
});

// Enable Rate Limiting
app.UseRateLimiter();

// Enable Swagger UI only in Development environment
if (app.Environment.IsDevelopment())
{
    // Expose Swagger JSON endpoint
    app.UseSwagger();

    // Enable interactive Swagger UI at /swagger
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Weather Service API v1");
        c.DocumentTitle = "Weather Service API";
    });
}

// Redirect HTTP requests to HTTPS
app.UseHttpsRedirection();

// Enable API Key Authentication middleware
app.UseApiKeyAuthentication();

// Map controller endpoints to routes (rate limiting applied globally)
app.MapControllers();

// Add a simple health check endpoint (no auth required)
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    timestamp = DateTime.UtcNow
})).WithTags("Health");

// Start the application and listen for requests
app.Run();
