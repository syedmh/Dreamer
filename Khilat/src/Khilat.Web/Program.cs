using Blazored.LocalStorage;
using Khilat.Web;
using Khilat.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("https://localhost:7265/") });
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<CartStateService>();
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthStateProvider>());
builder.Services.AddAuthorizationCore();

await builder.Build().RunAsync();
