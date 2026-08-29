using Castmill.Core.Auth;
using Castmill.UI.Platform;

namespace Castmill.Desktop.Platform;

internal sealed class DesktopExternalBrowserLauncher : IExternalBrowserLauncher
{
    private DesktopLoopbackReceiver? _receiver;
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string ClientKind => ExternalAuthClientKinds.Desktop;

    public bool UsesPersistentNavigation => false;

    public async Task<Uri?> PrepareCallbackAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync();
        }
        _receiver = DesktopLoopbackReceiver.Start();
        return _receiver.ReturnUri;
    }

    public Task<bool> HasCallbackAsync(CancellationToken ct = default) => Task.FromResult(false);

    public async Task<ExternalAuthCallbackResult?> ReceiveCallbackAsync(
        Guid expectedAttemptId,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var receiver = _receiver;
        _receiver = null;
        return receiver is null
            ? null
            : await receiver.ReceiveAsync(expectedAttemptId, expiresAt, TimeProvider.System, ct);
    }

    public Task<bool> StorePendingAsync(ExternalAuthPendingState state, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<ExternalAuthPendingState?> ReadPendingAsync(CancellationToken ct = default) =>
        Task.FromResult<ExternalAuthPendingState?>(null);

    public async Task ClearPendingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync();
            _receiver = null;
        }
    }

    public Task RemoveCallbackMarkerAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ExternalBrowserLaunchStatus> OpenAsync(Uri uri, CancellationToken ct = default) =>
        await Launcher.Default.OpenAsync(uri)
            ? ExternalBrowserLaunchStatus.Opened
            : ExternalBrowserLaunchStatus.Failed;
}