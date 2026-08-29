using Castmill.UI;
using Castmill.UI.Auth;
using Castmill.UI.Platform;
using Castmill.Web.Platform;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The root component lives in the RCL — this shell owns no UI (ADR-F01).
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// In development the API is a separate origin (it runs on 5005); in production the SWA
// serves the client and proxies /api to App Service, so the app's own origin is correct.
var apiBaseAddress = new Uri(
    builder.Configuration["ApiBaseAddress"] ?? builder.HostEnvironment.BaseAddress);

builder.Services.AddCastmillUi(apiBaseAddress);

// Platform seam (Roadmap §2.2): the web implementations.
builder.Services.AddSingleton<IShellInfo, WebShellInfo>();
builder.Services.AddScoped<IAuthTokenProvider, WebTokenProvider>();
builder.Services.AddScoped<IExternalBrowserLauncher, WebExternalBrowserLauncher>();
builder.Services.AddSingleton<IMediaPipeline, WebMediaPipeline>();

await builder.Build().RunAsync();
