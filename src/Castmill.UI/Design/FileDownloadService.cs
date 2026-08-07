using Castmill.UI.Http;
using Microsoft.JSInterop;

namespace Castmill.UI.Design;

/// <summary>
/// Hands a file the API returned to the browser's own download path. Kept behind an
/// interface so bUnit tests can assert what would have been saved without a real browser —
/// the same reason <see cref="IUiStateStore"/> exists.
/// </summary>
public interface IFileDownloader
{
    Task SaveAsync(DownloadedFile file);
}

public sealed class FileDownloader(IJSRuntime js) : IFileDownloader, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-download.js";

    private IJSObjectReference? _module;

    public async Task SaveAsync(DownloadedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        // Base64 across the interop boundary: JS cannot take a .NET byte[] directly, and
        // IJSStreamReference would need a second round trip for what is a small file.
        await _module.InvokeVoidAsync(
            "save", file.FileName, file.ContentType, Convert.ToBase64String(file.Bytes));
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
            // The circuit is already gone (shell closing) — nothing to release.
        }
    }
}
