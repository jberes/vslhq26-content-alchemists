using Microsoft.JSInterop;

namespace Castmill.UI.Http;

/// <summary>
/// Visible feedback for work the producer started (ADR-F74). <see cref="CastmillHttpHandler"/>
/// reports every request's start and end; castmill-activity.js attributes a request to the
/// control that was activated just before it (or whose previous request it follows) and marks
/// that control busy until its requests settle, plus a slim bar across the app. Background
/// polling is never tied to a click, so it never lights anything up.
///
/// Presentation only and best effort: a failed interop call must never fail the request.
/// </summary>
public interface IUserActivity
{
    void Started(string requestId);

    void Finished(string requestId);
}

public sealed class UserActivity(IJSRuntime js) : IUserActivity, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-activity.js?v=1";
    private Task<IJSObjectReference>? _module;

    /// <summary>Installs the click tracking; the shell calls this on its first render.</summary>
    public async Task StartAsync()
    {
        try
        {
            await (await ModuleAsync()).InvokeVoidAsync("install");
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            _module = null;
        }
    }

    public void Started(string requestId) => Send("begin", requestId);

    public void Finished(string requestId) => Send("end", requestId);

    private void Send(string method, string requestId) => _ = SendAsync(method, requestId);

    private async Task SendAsync(string method, string requestId)
    {
        try
        {
            await (await ModuleAsync()).InvokeVoidAsync(method, requestId);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // No browser (prerender, tests) or a WebView tearing down: feedback is optional.
        }
    }

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private static bool IsInteropUnavailable(Exception ex) =>
        ex is JSException or InvalidOperationException or TaskCanceledException
            or ObjectDisposedException or JSDisconnectedException or NotSupportedException;

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true } module)
        {
            try
            {
                await module.Result.DisposeAsync();
            }
            catch (Exception ex) when (IsInteropUnavailable(ex))
            {
            }
        }
    }
}
