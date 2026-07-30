namespace Castmill.UI.Platform;

/// <summary>
/// Identifies the host shell to the shared UI. This is the first member of the platform
/// seam described in Roadmap-Blazor.md §2.2: the RCL declares the question, each shell
/// answers it in its own bootstrap code. Nothing in the RCL may branch on a shell type.
/// </summary>
public interface IShellInfo
{
    /// <summary>Short shell name, e.g. "Desktop (MAUI Blazor Hybrid)".</summary>
    string Name { get; }

    /// <summary>How this shell hosts Blazor, e.g. "WebView2 / WKWebView".</summary>
    string HostDescription { get; }

    /// <summary>
    /// True in a development build. Gates dev-only surfaces such as the style guide, which
    /// must never render in a shipped app. Each shell answers from its own host
    /// environment — the RCL has no way to know on its own.
    /// </summary>
    bool IsDevelopment { get; }
}
