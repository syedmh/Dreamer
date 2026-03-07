using Husaynia.Infrastructure.Data;
using Husaynia.Core.Entities;
using Husaynia.Core.Interfaces;
using Husaynia.Web.Services;
using Husaynia.Web.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using SixLabors.ImageSharp.Web.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity
builder.Services.AddDefaultIdentity<AppUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = false;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<ApplicationDbContext>();

// Caching
builder.Services.AddMemoryCache();
builder.Services.AddResponseCaching();

// ImageSharp
builder.Services.AddImageSharp();

// Repositories
builder.Services.AddScoped<IAnnouncementRepository, Husaynia.Infrastructure.Repositories.AnnouncementRepository>();
builder.Services.AddScoped<IEventRepository, Husaynia.Infrastructure.Repositories.EventRepository>();
builder.Services.AddScoped<IMediaRepository, Husaynia.Infrastructure.Repositories.MediaRepository>();
builder.Services.AddScoped<IDonationRepository, Husaynia.Infrastructure.Repositories.DonationRepository>();
builder.Services.AddScoped<IDonationCampaignRepository, Husaynia.Infrastructure.Repositories.DonationCampaignRepository>();

// Services
builder.Services.AddScoped<IPrayerTimeService, PrayerTimeService>();

// Background Services
builder.Services.AddHostedService<PrayerTimesCacheService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }
    await next();
});

app.UseImageSharp();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseResponseCaching();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
