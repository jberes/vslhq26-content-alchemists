using Castmill.Desktop.Platform;
using Castmill.UI;
using Castmill.UI.Design;
using Castmill.UI.Platform;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Castmill.Desktop;

public static class MauiProgram
{
    /// <summary>
    /// Where the API lives. The desktop shell has no origin of its own, so this is a real
    /// configuration value rather than a relative path. Release is build-locked to the Azure
    /// production API; Debug defaults to the local HTTPS API and may be overridden for dev.
    /// </summary>
    private static readonly Uri ApiBaseAddress = new(
        typeof(MauiProgram).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CastmillApiBaseAddress")
            .Value ?? throw new InvalidOperationException("CastmillApiBaseAddress is not configured.")
        );

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // The media engine prefers an app-managed ffmpeg sidecar (tools/fetch-ffmpeg.sh
        // installs one, pinned-hash verified) and falls back to a system install.
        Castmill.Media.Ffmpeg.SidecarDirectory =
            Path.Combine(FileSystem.AppDataDirectory, "ffmpeg");

        // Every shared UI service comes from the RCL — this shell adds only the seams below.
        builder.Services.AddCastmillUi(ApiBaseAddress);

        // Platform seam (Roadmap §2.2): the desktop implementations.
        builder.Services.AddSingleton<IShellInfo, DesktopShellInfo>();
        builder.Services.AddScoped<IAuthTokenProvider, DesktopTokenProvider>();
        builder.Services.AddScoped<IExternalBrowserLauncher, DesktopExternalBrowserLauncher>();
        builder.Services.AddSingleton<IMediaPipeline, DesktopMediaPipeline>();
        builder.Services.AddScoped<IFileDownloader, DesktopFileDownloader>();
        // Clipboard writes must leave WKWebView on macOS. The shared browser service cannot
        // recover after navigator.clipboard rejects because its user-activation token is gone.
        builder.Services.AddScoped<IClipboardService, DesktopClipboardService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
