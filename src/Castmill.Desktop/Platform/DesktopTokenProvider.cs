using Castmill.UI.Auth;

namespace Castmill.Desktop.Platform;

/// <summary>
/// Desktop token custody: the refresh token goes to MAUI <see cref="SecureStorage"/>, which
/// is the Keychain on macOS and DPAPI-backed credential storage on Windows. This is the one
/// meaningful custody difference between the shells (Roadmap §2.2) — everything else about
/// token handling lives in <see cref="TokenProviderBase"/>.
/// </summary>
internal sealed class DesktopTokenProvider(Func<AuthClient> authClient) : TokenProviderBase(authClient)
{
    private const string Key = "castmill.auth.refresh";

    protected override async Task<string?> ReadRefreshTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A locked or unavailable keychain must not crash startup: the user simply
            // gets the sign-in screen. SecureStorage throws platform-specific exception
            // types, hence the broad catch.
            return null;
        }
    }

    protected override async Task WriteRefreshTokenAsync(string refreshToken)
    {
        try
        {
            await SecureStorage.Default.SetAsync(Key, refreshToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session still works; it just will not survive a restart. Failing the
            // sign-in over this would be worse.
        }
    }

    protected override Task DeleteRefreshTokenAsync()
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}
