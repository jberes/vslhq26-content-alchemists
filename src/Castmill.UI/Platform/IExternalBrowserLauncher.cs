namespace Castmill.UI.Platform;

public enum ExternalBrowserLaunchStatus
{
    Opened,
    NavigationStarted,
    Failed,
}

public static class ExternalAuthFlowKinds
{
    public const string SignIn = "sign-in";
    public const string Link = "link";
}

public sealed record ExternalAuthPendingState(
    Guid AttemptId,
    string PollSecret,
    string CodeVerifier,
    DateTimeOffset ExpiresAt,
    string ReturnUrl,
    string FlowKind = ExternalAuthFlowKinds.SignIn,
    string? CallbackCode = null,
    string? CallbackErrorCode = null);

public sealed record ExternalAuthCallbackResult(
    Guid AttemptId,
    string? Code,
    string? ErrorCode);

public interface IExternalBrowserLauncher
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    string ClientKind { get; }

    bool UsesPersistentNavigation { get; }

    Task<Uri?> PrepareCallbackAsync(CancellationToken ct = default);

    Task<bool> HasCallbackAsync(CancellationToken ct = default);

    Task<ExternalAuthCallbackResult?> ReceiveCallbackAsync(
        Guid expectedAttemptId,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<bool> StorePendingAsync(ExternalAuthPendingState state, CancellationToken ct = default);

    Task<ExternalAuthPendingState?> ReadPendingAsync(CancellationToken ct = default);

    Task ClearPendingAsync(CancellationToken ct = default);

    Task RemoveCallbackMarkerAsync(CancellationToken ct = default);

    Task<ExternalBrowserLaunchStatus> OpenAsync(Uri uri, CancellationToken ct = default);
}

internal sealed class UnsupportedExternalBrowserLauncher : IExternalBrowserLauncher
{
    public bool IsAvailable => false;

    public string UnavailableReason => "External sign-in isn't available in this app.";

    public string ClientKind => Core.Auth.ExternalAuthClientKinds.Desktop;

    public bool UsesPersistentNavigation => false;

    public Task<Uri?> PrepareCallbackAsync(CancellationToken ct = default) =>
        Task.FromResult<Uri?>(null);

    public Task<bool> HasCallbackAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<ExternalAuthCallbackResult?> ReceiveCallbackAsync(
        Guid expectedAttemptId,
        DateTimeOffset expiresAt,
        CancellationToken ct = default) => Task.FromResult<ExternalAuthCallbackResult?>(null);

    public Task<bool> StorePendingAsync(ExternalAuthPendingState state, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<ExternalAuthPendingState?> ReadPendingAsync(CancellationToken ct = default) =>
        Task.FromResult<ExternalAuthPendingState?>(null);

    public Task ClearPendingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveCallbackMarkerAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<ExternalBrowserLaunchStatus> OpenAsync(Uri uri, CancellationToken ct = default) =>
        Task.FromResult(ExternalBrowserLaunchStatus.Failed);
}