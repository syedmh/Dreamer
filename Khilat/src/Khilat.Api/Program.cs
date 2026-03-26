using System.Text;
using Khilat.Api.Services;
using Khilat.Infrastructure.Data;
using Khilat.Infrastructure.Identity;
using Khilat.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));

    // Infrastructure (EF Core, Repositories, External Services)
    builder.Services.AddInfrastructure(builder.Configuration);

    // ASP.NET Core Identity
    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<KhilatDbContext>()
    .AddDefaultTokenProviders();

    // JWT Authentication
    var jwtSettings = builder.Configuration.GetSection("JwtSettings");
    var key = Encoding.UTF8.GetBytes(jwtSettings["Key"]!);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero
        };
    });

    builder.Services.AddAuthorization();

    // Auth service
    builder.Services.AddScoped<IAuthService, AuthService>();

    // Controllers
    builder.Services.AddControllers();

    // CORS (allow Blazor WASM)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(
                    "https://localhost:7230",
                    "http://localhost:5287")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    // Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "Khilat API",
            Version = "v1",
            Description = "API for the Khilat boutique e-commerce platform"
        });

        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Description = "Enter your JWT token"
        });

        options.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer"),
                new List<string>()
            }
        });
    });

    var app = builder.Build();

    // Seed data
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            // Ensure database is created
            var context = services.GetRequiredService<KhilatDbContext>();
            await context.Database.EnsureCreatedAsync();

            // Ensure roles exist
            var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
            string[] roles = ["Admin", "Customer"];
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));
            }

            await SeedData.InitializeAsync(services);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while seeding the database.");
        }
    }

    // Middleware pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Khilat API v1");
        });
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
