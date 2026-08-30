using Castmill.Core.Auth;
using Castmill.UI.Platform;

namespace Castmill.UI.Auth;

/// <summary>
/// The signed-in user, for chrome and guards. A plain scoped store with a change event
/// (ADR-F04) rather than a state-management library.
/// </summary>
public sealed class AuthState(IAuthTokenProvider tokens, AuthClient auth) : IDisposable
{
    private bool _subscribed;

    public MeResponse? User { get; private set; }

    public string? AvatarDataUrl { get; private set; }

    public bool? HasPassword { get; private set; }

    public bool IsSignedIn => tokens.IsSignedIn && User is not null;

    /// <summary>False until the cold-start restore attempt has finished, so screens can
    /// avoid flashing the sign-in form at a user who is in fact signed in.</summary>
    public bool IsReady { get; private set; }

    public event Action? Changed;

    /// <summary>Attempts a silent restore, then loads /me. Safe to call repeatedly.</summary>
    public async Task InitializeAsync()
    {
        if (!_subscribed)
        {
            tokens.Changed += OnTokensChanged;
            _subscribed = true;
        }

        if (IsReady)
        {
            return;
        }

        try
        {
            if (await tokens.TryRestoreAsync())
            {
                await LoadUserAsync();
            }
        }
        finally
        {
            // Ready even on failure: "we tried and you are not signed in" is a valid
            // answer, and leaving IsReady false would hang the UI forever.
            IsReady = true;
            Changed?.Invoke();
        }
    }

    public async Task SignedInAsync(AuthResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        await tokens.StoreAsync(response.AccessToken, response.AccessTokenExpiresAt, response.RefreshToken);
        await LoadUserAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        try
        {
            // Revoke server-side first: a refresh token that outlives the sign-out is the
            // whole problem sign-out exists to solve.
            await auth.LogoutAsync();
        }
        catch (Http.ApiException)
        {
            // Already invalid, or offline. Clearing locally is still correct.
        }
        catch (HttpRequestException)
        {
        }

        User = null;
        AvatarDataUrl = null;
        HasPassword = null;
        await tokens.ClearAsync();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            tokens.Changed -= OnTokensChanged;
            _subscribed = false;
        }
    }

    private async Task LoadUserAsync()
    {
        try
        {
            User = await auth.MeAsync();
            AvatarDataUrl = User.HasAvatar ? await LoadAvatarAsync() : null;
        }
        catch (Http.ApiException)
        {
            User = null;
            AvatarDataUrl = null;
            HasPassword = null;
            return;
        }

        try
        {
            HasPassword = (await auth.ExternalLinksAsync()).HasPassword;
        }
        catch (Http.ApiException)
        {
            HasPassword = null;
        }
    }

    private async Task<string?> LoadAvatarAsync()
    {
        try
        {
            var avatar = await auth.AvatarAsync();
            if (avatar.Bytes.Length > 256 * 1024
                || avatar.ContentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
            {
                return null;
            }

            return $"data:{avatar.ContentType};base64,{Convert.ToBase64String(avatar.Bytes)}";
        }
        catch (Exception exception) when (exception is Http.ApiException or HttpRequestException)
        {
            return null;
        }
    }

    private void OnTokensChanged()
    {
        if (!tokens.IsSignedIn)
        {
            User = null;
            AvatarDataUrl = null;
            HasPassword = null;
        }

        Changed?.Invoke();
    }
}
