using System.Text.Json;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Castmill.UI.Platform;

public sealed class WebExternalBrowserLauncher(
    IJSRuntime js,
    HttpClient http) : IExternalBrowserLauncher, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-external-auth.js";
    private IJSObjectReference? _module;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string ClientKind => ExternalAuthClientKinds.Web;

    public bool UsesPersistentNavigation => true;

    public Task<Uri?> PrepareCallbackAsync(CancellationToken ct = default) =>
        Task.FromResult<Uri?>(null);

    public async Task<bool> HasCallbackAsync(CancellationToken ct = default)
    {
        try
        {
            return await (await ModuleAsync(ct)).InvokeAsync<bool>("hasCallback", ct);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<ExternalAuthCallbackResult?> ReceiveCallbackAsync(
        Guid expectedAttemptId,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        _ = expiresAt;
        try
        {
            var json = await (await ModuleAsync(ct)).InvokeAsync<string?>(
                "consumeCallback",
                ct,
                expectedAttemptId.ToString("D"));
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ExternalAuthCallbackResult>(json);
        }
        catch (JSException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> StorePendingAsync(
        ExternalAuthPendingState state,
        CancellationToken ct = default)
    {
        if (!IsLocalReturnUrl(state.ReturnUrl))
        {
            throw new InvalidOperationException("The external sign-in return URL must be local.");
        }

        try
        {
            var json = JsonSerializer.Serialize(state);
            return await (await ModuleAsync(ct)).InvokeAsync<bool>("writePending", ct, json);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<ExternalAuthPendingState?> ReadPendingAsync(CancellationToken ct = default)
    {
        string? json;
        try
        {
            json = await (await ModuleAsync(ct)).InvokeAsync<string?>("readPending", ct);
        }
        catch (JSException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ExternalAuthPendingState>(json);
            return state is not null && IsLocalReturnUrl(state.ReturnUrl) ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task ClearPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await (await ModuleAsync(ct)).InvokeVoidAsync("clearPending", ct);
        }
        catch (JSException)
        {
        }
    }

    public async Task RemoveCallbackMarkerAsync(CancellationToken ct = default)
    {
        try
        {
            await (await ModuleAsync(ct)).InvokeVoidAsync("clearCallback", ct);
        }
        catch (JSException)
        {
        }
    }

    public async Task<ExternalBrowserLaunchStatus> OpenAsync(
        Uri uri,
        CancellationToken ct = default)
    {
        var apiBaseAddress = http.BaseAddress;
        if (apiBaseAddress is null || !apiBaseAddress.IsAbsoluteUri || !SameOrigin(uri, apiBaseAddress))
        {
            throw new InvalidOperationException("The external authentication URL was not on the API origin.");
        }

        try
        {
            await (await ModuleAsync(ct)).InvokeVoidAsync("navigate", ct, uri.AbsoluteUri);
            return ExternalBrowserLaunchStatus.NavigationStarted;
        }
        catch (JSDisconnectedException)
        {
            return ExternalBrowserLaunchStatus.NavigationStarted;
        }
        catch (JSException)
        {
            return ExternalBrowserLaunchStatus.Failed;
        }
    }

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
            }
        }
    }

    internal static bool IsLocalReturnUrl(string value) =>
        string.IsNullOrEmpty(value)
        || (Uri.TryCreate(value, UriKind.Relative, out _)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.StartsWith('\\')
            && !value.Contains('\\'));

    private static bool SameOrigin(Uri left, Uri right) =>
        left.IsAbsoluteUri
        && right.IsAbsoluteUri
        && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct) =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath);
}