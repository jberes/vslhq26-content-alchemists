using Castmill.Core.Auth;
using Castmill.UI.Http;
using Castmill.UI.Platform;

namespace Castmill.UI.Auth;

public enum ExternalAuthLinkPhase
{
    Idle,
    Starting,
    OpeningBrowser,
    Waiting,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ExternalAuthLinkSnapshot(
    ExternalAuthLinkPhase Phase,
    string? Message = null,
    bool CanRetry = false)
{
    public bool IsRunning => Phase is ExternalAuthLinkPhase.Starting
        or ExternalAuthLinkPhase.OpeningBrowser
        or ExternalAuthLinkPhase.Waiting;
}

public sealed record ExternalAuthLinkResult(
    bool Succeeded,
    string? ErrorMessage = null,
    bool NavigationStarted = false);

public sealed class ExternalAuthLinkService(
    AuthClient auth,
    IExternalBrowserLauncher browser,
    TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _flowCancellation;
    private bool _explicitCancellation;
    private bool _persistentNavigationInitiated;
    private bool _disposed;

    public ExternalAuthLinkSnapshot Snapshot { get; private set; } = new(ExternalAuthLinkPhase.Idle);

    public event Action? Changed;

    public async Task<ExternalAuthLinkResult> LinkAsync(
        string provider,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _singleFlight.WaitAsync(0, ct))
        {
            return new(false, "Another sign-in method is already being linked.");
        }

        using var flowCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync)
        {
            _flowCancellation = flowCancellation;
            _explicitCancellation = false;
            _persistentNavigationInitiated = false;
        }

        var keepPending = false;
        try
        {
            Set(ExternalAuthLinkPhase.Starting, $"Preparing {ProviderName(provider)}.");
            var pkce = Pkce.Create();
            var loopbackReturnUri = await browser.PrepareCallbackAsync(flowCancellation.Token);
            var start = await auth.LinkStartAsync(new ExternalAuthStartRequest(
                provider,
                browser.ClientKind,
                ExternalAuthReturnRoutes.AccountSettings,
                pkce.CodeChallenge,
                ExternalAuthCodeChallengeMethods.S256,
                loopbackReturnUri?.AbsoluteUri), flowCancellation.Token);
            var pending = new ExternalAuthPendingState(
                start.Response.AttemptId,
                start.Response.PollSecret,
                pkce.CodeVerifier,
                start.Response.ExpiresAt,
                string.Empty,
                ExternalAuthFlowKinds.Link);

            if (browser.UsesPersistentNavigation
                && !await browser.StorePendingAsync(pending, flowCancellation.Token))
            {
                return Fail("Castmill couldn't preserve this linking attempt. Try again.");
            }

            Set(ExternalAuthLinkPhase.OpeningBrowser, "Opening your browser.");
            lock (_sync)
            {
                _persistentNavigationInitiated = browser.UsesPersistentNavigation;
            }
            var launchStatus = await browser.OpenAsync(start.AbsoluteBrowserUri, flowCancellation.Token);
            if (launchStatus == ExternalBrowserLaunchStatus.Failed)
            {
                return Fail("Castmill couldn't open the system browser. Try again.");
            }
            if (launchStatus == ExternalBrowserLaunchStatus.NavigationStarted)
            {
                keepPending = true;
                Set(ExternalAuthLinkPhase.Waiting, "Continue linking in this browser.");
                return new(false, NavigationStarted: true);
            }

            Set(ExternalAuthLinkPhase.Waiting, "Browser opened. Waiting for linking to finish.");
            return await CompletePendingAsync(pending, pollImmediately: false, flowCancellation.Token);
        }
        catch (OperationCanceledException) when (flowCancellation.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (browser.UsesPersistentNavigation
                    && _persistentNavigationInitiated
                    && !_explicitCancellation)
                {
                    keepPending = true;
                    return new(false, NavigationStarted: true);
                }
            }

            Set(ExternalAuthLinkPhase.Cancelled, "Linking was cancelled.");
            return new(false, Snapshot.Message);
        }
        catch (ValidationApiException ex)
        {
            return Fail(ExternalAuthFailureMessages.For(
                ex.Errors.Keys.FirstOrDefault(ExternalAuthFailureMessages.IsKnown)
                    ?? ExternalAuthErrors.InvalidRequest));
        }
        catch (ApiException ex)
        {
            return Fail(ExternalAuthFailureMessages.For(
                ExternalAuthFailureMessages.IsKnown(ex.Message)
                    ? ex.Message
                    : ExternalAuthErrors.AttemptFailed));
        }
        catch (HttpRequestException)
        {
            return Fail("Couldn't reach the Castmill API. Check your connection and try again.");
        }
        catch (InvalidOperationException)
        {
            return Fail("The linking response was not safe to open. Try again.");
        }
        finally
        {
            if (!keepPending)
            {
                await browser.ClearPendingAsync(CancellationToken.None);
            }

            lock (_sync)
            {
                if (ReferenceEquals(_flowCancellation, flowCancellation))
                {
                    _flowCancellation = null;
                }
                if (!keepPending)
                {
                    _persistentNavigationInitiated = false;
                }
            }
            _singleFlight.Release();
        }
    }

    public async Task<ExternalAuthLinkResult> ResumeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!browser.UsesPersistentNavigation)
        {
            return Fail("This linking attempt cannot be resumed in this app.");
        }
        if (!await _singleFlight.WaitAsync(0, ct))
        {
            return new(false, "Another sign-in method is already being linked.");
        }

        using var flowCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ownsPending = false;
        var clearPending = false;
        var clearCallback = false;
        lock (_sync)
        {
            _flowCancellation = flowCancellation;
            _explicitCancellation = false;
            _persistentNavigationInitiated = false;
        }

        try
        {
            var pending = await browser.ReadPendingAsync(flowCancellation.Token);
            if (pending is null
                || !string.Equals(pending.FlowKind, ExternalAuthFlowKinds.Link, StringComparison.Ordinal))
            {
                clearCallback = true;
                return Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.AttemptNotFound));
            }
            ownsPending = true;
            if (pending.ExpiresAt <= clock.GetUtcNow())
            {
                clearPending = true;
                clearCallback = true;
                return Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.AttemptExpired));
            }

            Set(ExternalAuthLinkPhase.Waiting, "Checking the provider result.");
            var result = await CompletePendingAsync(
                pending,
                pollImmediately: true,
                flowCancellation.Token);
            clearPending = true;
            clearCallback = true;
            return result;
        }
        catch (OperationCanceledException) when (flowCancellation.IsCancellationRequested)
        {
            Set(ExternalAuthLinkPhase.Cancelled, "Linking was cancelled.");
            return new(false, Snapshot.Message);
        }
        catch (ApiException ex)
        {
            var retryable = ExternalAuthExchangeRetry.IsRetryable(ex);
            clearPending = !retryable;
            clearCallback = !retryable;
            return Fail(ExternalAuthFailureMessages.For(
                ExternalAuthFailureMessages.IsKnown(ex.Message)
                    ? ex.Message
                    : ExternalAuthErrors.AttemptFailed), retryable);
        }
        catch (HttpRequestException)
        {
            return Fail(
                "Couldn't reach the Castmill API. Check your connection and try again.",
                canRetry: true);
        }
        finally
        {
            if (ownsPending && clearPending)
            {
                await browser.ClearPendingAsync(CancellationToken.None);
            }
            if (clearCallback)
            {
                await browser.RemoveCallbackMarkerAsync(CancellationToken.None);
            }
            lock (_sync)
            {
                if (ReferenceEquals(_flowCancellation, flowCancellation))
                {
                    _flowCancellation = null;
                }
                _persistentNavigationInitiated = false;
            }
            _singleFlight.Release();
        }
    }

    public async Task CancelAsync()
    {
        var clearPending = false;
        lock (_sync)
        {
            _explicitCancellation = true;
            if (_flowCancellation is not null)
            {
                _flowCancellation.Cancel();
            }
            else if (browser.UsesPersistentNavigation && _persistentNavigationInitiated)
            {
                clearPending = true;
                _persistentNavigationInitiated = false;
            }
        }

        if (clearPending)
        {
            await browser.ClearPendingAsync(CancellationToken.None);
            Set(ExternalAuthLinkPhase.Cancelled, "Linking was cancelled.");
        }
    }

    public void OnPageDisposed()
    {
        lock (_sync)
        {
            if (browser.UsesPersistentNavigation && _persistentNavigationInitiated)
            {
                return;
            }

            _explicitCancellation = true;
            _flowCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _flowCancellation?.Cancel();
        }
    }

    private async Task<ExternalAuthLinkResult> CompletePendingAsync(
        ExternalAuthPendingState pending,
        bool pollImmediately,
        CancellationToken ct)
    {
        var callback = await browser.ReceiveCallbackAsync(
            pending.AttemptId,
            pending.ExpiresAt,
            ct);
        if (callback is null)
        {
            if (!pollImmediately)
            {
                await Task.Delay(PollInterval, clock, ct);
            }
            pollImmediately = false;
            var poll = await auth.PollExternalAsync(
                new ExternalAuthPollRequest(pending.AttemptId, pending.PollSecret),
                ct);
            return poll.Status == ExternalAuthStatuses.Pending
                || poll.Status == ExternalAuthStatuses.Completed
                ? Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.InvalidExchangeCode))
                : Fail(ExternalAuthFailureMessages.For(
                    poll.ErrorCode ?? ExternalAuthErrors.AttemptFailed));
        }
        if (callback.ErrorCode is not null)
        {
            return Fail(ExternalAuthFailureMessages.For(callback.ErrorCode));
        }
        if (callback.AttemptId != pending.AttemptId || callback.Code is null)
        {
            return Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.InvalidExchangeCode));
        }

        var request = new ExternalAuthExchangeRequest(
            pending.AttemptId,
            callback.Code,
            pending.CodeVerifier);
        if (browser.UsesPersistentNavigation)
        {
            await ExternalAuthExchangeRetry.ExecuteAsync(
                token => auth.LinkExchangeAsync(request, token),
                clock,
                ct);
        }
        else
        {
            await auth.LinkExchangeAsync(request, ct);
        }
        Set(ExternalAuthLinkPhase.Completed, "Sign-in method linked.");
        return new(true);
    }

    private ExternalAuthLinkResult Fail(string message, bool canRetry = false)
    {
        Set(ExternalAuthLinkPhase.Failed, message, canRetry);
        return new(false, message);
    }

    private void Set(ExternalAuthLinkPhase phase, string message, bool canRetry = false)
    {
        Snapshot = new(phase, message, canRetry);
        Changed?.Invoke();
    }

    private static string ProviderName(string provider) => provider switch
    {
        ExternalAuthProviders.Microsoft => "Microsoft",
        ExternalAuthProviders.Google => "Google",
        _ => "external provider",
    };
}