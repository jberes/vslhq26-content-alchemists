using Castmill.Core.Auth;
using Castmill.UI.Http;
using Castmill.UI.Platform;

namespace Castmill.UI.Auth;

public enum ExternalAuthSignInPhase
{
    Idle,
    Starting,
    OpeningBrowser,
    Waiting,
    Exchanging,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ExternalAuthSignInSnapshot(
    ExternalAuthSignInPhase Phase,
    string? Message = null,
    bool CanRetry = false)
{
    public bool IsRunning => Phase is ExternalAuthSignInPhase.Starting
        or ExternalAuthSignInPhase.OpeningBrowser
        or ExternalAuthSignInPhase.Waiting
        or ExternalAuthSignInPhase.Exchanging;
}

public sealed record ExternalAuthSignInResult(
    bool Succeeded,
    string? ErrorMessage = null,
    bool NavigationStarted = false,
    string ReturnUrl = "");

public sealed class ExternalAuthSignInService(
    AuthClient auth,
    AuthState authState,
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

    public ExternalAuthSignInSnapshot Snapshot { get; private set; } = new(ExternalAuthSignInPhase.Idle);

    public event Action? Changed;

    public async Task<ExternalAuthSignInResult> SignInAsync(
        string provider,
        string returnUrl = "",
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _singleFlight.WaitAsync(0, ct))
        {
            return new(false, "Another external sign-in is already in progress.");
        }

        using var flowCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync)
        {
            _flowCancellation = flowCancellation;
            _explicitCancellation = false;
            _persistentNavigationInitiated = false;
        }

        var unauthorizedError = ExternalAuthErrors.AttemptFailed;
        var keepPending = false;
        try
        {
            Set(ExternalAuthSignInPhase.Starting, $"Preparing {ProviderName(provider)} sign-in.");
            var pkce = Pkce.Create();
            var loopbackReturnUri = await browser.PrepareCallbackAsync(flowCancellation.Token);
            var start = await auth.StartExternalAsync(new ExternalAuthStartRequest(
                provider,
                browser.ClientKind,
                ExternalAuthReturnRoutes.SignIn,
                pkce.CodeChallenge,
                ExternalAuthCodeChallengeMethods.S256,
                loopbackReturnUri?.AbsoluteUri), flowCancellation.Token);
            var pending = new ExternalAuthPendingState(
                start.Response.AttemptId,
                start.Response.PollSecret,
                pkce.CodeVerifier,
                start.Response.ExpiresAt,
                returnUrl,
                ExternalAuthFlowKinds.SignIn);

            if (browser.UsesPersistentNavigation
                && !await browser.StorePendingAsync(pending, flowCancellation.Token))
            {
                return Fail("Castmill couldn't preserve this sign-in attempt. Try again.");
            }

            Set(ExternalAuthSignInPhase.OpeningBrowser, "Opening your browser.");
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
                Set(ExternalAuthSignInPhase.Waiting, "Continue sign-in in this browser.");
                return new(false, NavigationStarted: true, ReturnUrl: pending.ReturnUrl);
            }

            Set(ExternalAuthSignInPhase.Waiting, "Browser opened. Waiting for sign-in to finish.");
            return await CompletePendingAsync(
                pending,
                pollImmediately: false,
                error => unauthorizedError = error,
                flowCancellation.Token);
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
                    return new(false, NavigationStarted: true, ReturnUrl: returnUrl);
                }
            }

            Set(ExternalAuthSignInPhase.Cancelled, "External sign-in was cancelled.");
            return new(false, Snapshot.Message);
        }
        catch (UnauthorizedApiException)
        {
            return Fail(ExternalAuthFailureMessages.For(unauthorizedError));
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
            return Fail("The sign-in response was not safe to open. Try again.");
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

    public async Task<ExternalAuthSignInResult> ResumeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!browser.UsesPersistentNavigation)
        {
            return Fail("This external sign-in cannot be resumed in this app.");
        }
        if (!await _singleFlight.WaitAsync(0, ct))
        {
            return new(false, "Another external sign-in is already in progress.");
        }

        using var flowCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync)
        {
            _flowCancellation = flowCancellation;
            _explicitCancellation = false;
            _persistentNavigationInitiated = false;
        }

        var unauthorizedError = ExternalAuthErrors.AttemptFailed;
        var ownsPending = false;
        var clearPending = false;
        var clearCallback = false;
        try
        {
            var pending = await browser.ReadPendingAsync(flowCancellation.Token);
            if (pending is null)
            {
                clearCallback = true;
                return Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.AttemptNotFound));
            }
            if (!string.Equals(
                    pending.FlowKind,
                    ExternalAuthFlowKinds.SignIn,
                    StringComparison.Ordinal))
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

            Set(ExternalAuthSignInPhase.Waiting, "Checking the provider sign-in result.");
            var result = await CompletePendingAsync(
                pending,
                pollImmediately: true,
                error => unauthorizedError = error,
                flowCancellation.Token);
            clearPending = true;
            clearCallback = true;
            return result;
        }
        catch (OperationCanceledException) when (flowCancellation.IsCancellationRequested)
        {
            Set(ExternalAuthSignInPhase.Cancelled, "External sign-in was cancelled.");
            return new(false, Snapshot.Message);
        }
        catch (UnauthorizedApiException)
        {
            clearPending = true;
            clearCallback = true;
            return Fail(ExternalAuthFailureMessages.For(unauthorizedError));
        }
        catch (ValidationApiException ex)
        {
            clearPending = true;
            clearCallback = true;
            return Fail(ExternalAuthFailureMessages.For(
                ex.Errors.Keys.FirstOrDefault(ExternalAuthFailureMessages.IsKnown)
                    ?? ExternalAuthErrors.InvalidRequest));
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
        catch (InvalidOperationException)
        {
            return Fail("The sign-in response was not safe to use. Try again.");
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

    public async Task ClearExpiredPendingAsync(CancellationToken ct = default)
    {
        if (!browser.UsesPersistentNavigation)
        {
            return;
        }

        var pending = await browser.ReadPendingAsync(ct);
        if (pending is not null && pending.ExpiresAt <= clock.GetUtcNow())
        {
            await browser.ClearPendingAsync(ct);
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
            Set(ExternalAuthSignInPhase.Cancelled, "External sign-in was cancelled.");
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

    private ExternalAuthSignInResult Fail(string message, bool canRetry = false)
    {
        Set(ExternalAuthSignInPhase.Failed, message, canRetry);
        return new(false, message);
    }

    private async Task<ExternalAuthSignInResult> CompletePendingAsync(
        ExternalAuthPendingState pending,
        bool pollImmediately,
        Action<string> setUnauthorizedError,
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
            setUnauthorizedError(ExternalAuthErrors.InvalidPollSecret);
            var poll = await auth.PollExternalAsync(
                new ExternalAuthPollRequest(pending.AttemptId, pending.PollSecret),
                ct);

            return poll.Status == ExternalAuthStatuses.Pending
                || poll.Status == ExternalAuthStatuses.Completed
                ? Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.InvalidExchangeCode))
                : Fail(ExternalAuthFailureMessages.For(
                    poll.ErrorCode ?? StatusError(poll.Status)));
        }
        if (callback.ErrorCode is not null)
        {
            return Fail(ExternalAuthFailureMessages.For(callback.ErrorCode));
        }
        if (callback.AttemptId != pending.AttemptId || callback.Code is null)
        {
            return Fail(ExternalAuthFailureMessages.For(ExternalAuthErrors.InvalidExchangeCode));
        }

        Set(ExternalAuthSignInPhase.Exchanging, "Finishing sign-in securely.");
        setUnauthorizedError(ExternalAuthErrors.InvalidExchangeCode);
        var request = new ExternalAuthExchangeRequest(
            pending.AttemptId,
            callback.Code,
            pending.CodeVerifier);
        var response = browser.UsesPersistentNavigation
            ? await ExternalAuthExchangeRetry.ExecuteAsync(
                token => auth.ExchangeExternalAsync(request, token),
                clock,
                ct)
            : await auth.ExchangeExternalAsync(request, ct);
        await authState.SignedInAsync(response);
        Set(ExternalAuthSignInPhase.Completed, "Signed in.");
        return new(true, ReturnUrl: pending.ReturnUrl);
    }

    private void Set(ExternalAuthSignInPhase phase, string message, bool canRetry = false)
    {
        Snapshot = new(phase, message, canRetry);
        Changed?.Invoke();
    }

    private static string ProviderName(string provider) => provider switch
    {
        ExternalAuthProviders.Microsoft => "Microsoft",
        ExternalAuthProviders.Google => "Google",
        _ => "external",
    };

    private static string StatusError(string status) => status switch
    {
        ExternalAuthStatuses.Expired => ExternalAuthErrors.AttemptExpired,
        ExternalAuthStatuses.Failed => ExternalAuthErrors.AttemptFailed,
        ExternalAuthStatuses.Consumed => ExternalAuthErrors.CodeConsumed,
        _ => ExternalAuthErrors.AttemptFailed,
    };
}

internal static class ExternalAuthExchangeRetry
{
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];

    public static bool IsRetryable(ApiException exception) =>
        exception.StatusCode == 429 || exception.StatusCode >= 500;

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeProvider clock,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (ApiException exception) when (attempt < Delays.Length && IsRetryable(exception))
            {
                await Task.Delay(Delays[attempt], clock, ct);
            }
            catch (HttpRequestException) when (attempt < Delays.Length)
            {
                await Task.Delay(Delays[attempt], clock, ct);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        TimeProvider clock,
        CancellationToken ct) =>
        ExecuteAsync(async token =>
        {
            await action(token);
            return true;
        }, clock, ct);
}

public static class ExternalAuthFailureMessages
{
    public static bool IsKnown(string errorCode) => errorCode switch
    {
        ExternalAuthErrors.InvalidRequest
            or ExternalAuthErrors.InvalidProvider
            or ExternalAuthErrors.AttemptNotFound
            or ExternalAuthErrors.AttemptExpired
            or ExternalAuthErrors.AttemptFailed
            or ExternalAuthErrors.AttemptPending
            or ExternalAuthErrors.InvalidPollSecret
            or ExternalAuthErrors.InvalidExchangeCode
            or ExternalAuthErrors.InvalidCodeVerifier
            or ExternalAuthErrors.LoginAlreadyAssociated
            or ExternalAuthErrors.EmailAlreadyExists
            or ExternalAuthErrors.PasswordNotConfigured
            or ExternalAuthErrors.AccountLinkRequired
            or ExternalAuthErrors.ExternalEmailRequired
            or ExternalAuthErrors.ProviderUnavailable
            or ExternalAuthErrors.CodeConsumed
            or ExternalAuthErrors.ExchangeNotAllowed
            or ExternalAuthErrors.LastLoginMethod
            or ExternalAuthErrors.LoginNotLinked
            or ExternalAuthErrors.InvalidProviderIdentity => true,
        _ => false,
    };

    public static string For(string errorCode) => errorCode switch
    {
        ExternalAuthErrors.InvalidRequest => "The sign-in request wasn't valid. Try again.",
        ExternalAuthErrors.InvalidProvider => "That sign-in provider isn't supported.",
        ExternalAuthErrors.AttemptNotFound => "This sign-in request is no longer available. Start again.",
        ExternalAuthErrors.AttemptExpired => "Sign-in took too long. Start again.",
        ExternalAuthErrors.AttemptFailed => "The provider couldn't complete sign-in. Try again.",
        ExternalAuthErrors.AttemptPending => "Sign-in is still waiting for the provider.",
        ExternalAuthErrors.InvalidPollSecret => "The sign-in status couldn't be verified. Start again.",
        ExternalAuthErrors.InvalidExchangeCode => "The sign-in response couldn't be verified. Start again.",
        ExternalAuthErrors.InvalidCodeVerifier => "The sign-in response couldn't be verified. Start again.",
        ExternalAuthErrors.LoginAlreadyAssociated => "That provider account is already linked to another Castmill account.",
        ExternalAuthErrors.EmailAlreadyExists => "An account with that email already exists. Sign in with email and password, then link the provider from settings.",
        ExternalAuthErrors.PasswordNotConfigured => "Set a password before changing this sign-in method.",
        ExternalAuthErrors.AccountLinkRequired => "Sign in with email and password first, then link this provider from settings.",
        ExternalAuthErrors.ExternalEmailRequired => "The provider did not return an email address Castmill can use.",
        ExternalAuthErrors.ProviderUnavailable => "That sign-in provider isn't available right now.",
        ExternalAuthErrors.CodeConsumed => "This sign-in was already used. Start again.",
        ExternalAuthErrors.ExchangeNotAllowed => "A linked sign-in method cannot create a new session.",
        ExternalAuthErrors.LastLoginMethod => "Add another sign-in method before removing this one.",
        ExternalAuthErrors.LoginNotLinked => "That sign-in method is not linked to this account.",
        ExternalAuthErrors.InvalidProviderIdentity => "The provider identity couldn't be verified.",
        _ => "External sign-in couldn't be completed. Try again.",
    };
}