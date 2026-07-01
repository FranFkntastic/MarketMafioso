using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MarketMafioso.Dashboard;
using MarketMafioso.Dashboard.Services;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<DashboardApiClient>();
builder.Services.AddScoped<DashboardStatusService>();
builder.Services.AddScoped<AcquisitionDashboardState>();
builder.Services.AddScoped<DashboardThemeService>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
