using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;
using MyTranslator.Shared.Services;
using MyTranslator.Web;
using MyTranslator.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddLocalization();
builder.Services.AddAuthorizationCore();
builder.Services.AddMudServices();

// 共享服务（RCL，Web 与桌面端共用）
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthStateProvider>());
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<ApiErrorMessageProvider>();
builder.Services.AddScoped<ImportExportService>();
builder.Services.AddScoped<TranslationRunService>();
builder.Services.AddScoped<ThemeService>();

// Web 端服务
builder.Services.AddScoped<HealthService>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
