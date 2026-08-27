using Microsoft.JSInterop;

namespace Castmill.UI.Design;

/// <summary>
/// One clipboard seam for both shells. Browser clipboard permissions and embedded WebViews do
/// not expose the same APIs, so components must not call navigator.clipboard directly.
/// </summary>
public interface IClipboardService
{
    Task<bool> CopyTextAsync(string text);

    Task<bool> CopyFormattedAsync(string text, string html);
}

public sealed class ClipboardService(IJSRuntime js) : IClipboardService, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-clipboard.js";
    private IJSObjectReference? _module;

    public async Task<bool> CopyTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            return await _module.InvokeAsync<bool>("copyText", text);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<bool> CopyFormattedAsync(string text, string html)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(html);

        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            return await _module.InvokeAsync<bool>("copyFormatted", text, html);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
