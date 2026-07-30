using Castmill.Desktop.Platform;
using Castmill.UI;
using Castmill.UI.Platform;
using Microsoft.Extensions.Logging;

namespace Castmill.Desktop;

public static class MauiProgram
{
    /// <summary>
    /// Where the API lives. The desktop shell has no origin of its own, so this is a real
    /// configuration value rather than a relative path. Points at the local dev API in a
    /// Debug build; Phase F9 replaces the Release value with the deployed App Service.
    /// </summary>
    private static readonly Uri ApiBaseAddress =
#if DEBUG
        new("http://localhost:5005/");
#else
        new("https://api.castmill.ai/");
#endif

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
        builder.Services.AddSingleton<IMediaPipeline, DesktopMediaPipeline>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
