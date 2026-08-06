using Microsoft.JSInterop;

namespace Castmill.UI.Design;

/// <summary>
/// <see cref="IUiStateStore"/> over localStorage, via the castmill-ui-state.js island.
/// Works in both shells: the MAUI BlazorWebView has localStorage like any browser.
/// </summary>
public sealed class BrowserUiStateStore(IJSRuntime js) : IUiStateStore, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-ui-state.js";

    private IJSObjectReference? _module;

    public async Task<string?> GetAsync(string key) =>
        await (await ModuleAsync()).InvokeAsync<string?>("get", key);

    public async Task SetAsync(string key, string value) =>
        await (await ModuleAsync()).InvokeVoidAsync("set", key, value);

    public async Task<bool> PrefersDarkAsync() =>
        await (await ModuleAsync()).InvokeAsync<bool>("prefersDark");

    public async Task ApplyThemeAsync(string family, string mode, string density) =>
        await (await ModuleAsync()).InvokeVoidAsync("applyTheme", family, mode, density);

    public async Task ApplyRailAsync(string? state) =>
        await (await ModuleAsync()).InvokeVoidAsync("applyRail", state);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone (shell closing) — nothing to release.
            }
        }
    }

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
}
