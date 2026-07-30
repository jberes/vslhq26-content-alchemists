using Castmill.UI.Platform;

namespace Castmill.Desktop.Platform;

/// <summary>Desktop implementation of the shell-identity seam. See <see cref="IShellInfo"/>.</summary>
internal sealed class DesktopShellInfo : IShellInfo
{
    public string Name => "Desktop (MAUI Blazor Hybrid)";

    public string HostDescription => DeviceInfo.Current.Platform == DevicePlatform.WinUI
        ? "WebView2 · native .NET runtime"
        : "WKWebView · native .NET runtime";

    // MAUI has no host environment: the build configuration IS the environment. A Release
    // build therefore cannot reach the dev-only surfaces, which is the point.
    public bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif
}
