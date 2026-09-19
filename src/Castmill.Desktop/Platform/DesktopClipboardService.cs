using Castmill.UI.Design;
using Microsoft.Maui.ApplicationModel.DataTransfer;
#if MACCATALYST
using UIKit;
#endif

namespace Castmill.Desktop.Platform;

/// <summary>
/// Writes through the operating-system clipboard instead of the Blazor WebView. WKWebView can
/// expose navigator.clipboard while still rejecting writes, and by the time that asynchronous
/// rejection returns its execCommand fallback no longer has a live user gesture on macOS.
/// </summary>
public sealed class DesktopClipboardService : IClipboardService
{
    public Task<bool> CopyTextAsync(string text) => CopyNativeAsync(text);

    // MAUI's cross-platform clipboard currently accepts text only. A plain-text representation
    // is always supplied for formatted copies, so desktop copy remains reliable even when the
    // destination does not understand HTML.
    public Task<bool> CopyFormattedAsync(string text, string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return CopyNativeAsync(text);
    }

    private static async Task<bool> CopyNativeAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
#if MACCATALYST
            // Do not route Mac Catalyst through the WebView or MAUI's asynchronous wrapper.
            // UIPasteboard is the authoritative system clipboard and must be touched on the
            // native UI thread.
            await MainThread.InvokeOnMainThreadAsync(() => UIPasteboard.General.String = text);
#else
            await Clipboard.Default.SetTextAsync(text);
#endif
            return true;
        }
        catch (Exception)
        {
            // IClipboardService reports platform failures as false so the UI can show its
            // existing actionable error instead of tearing down the Blazor event circuit.
            return false;
        }
    }
}
