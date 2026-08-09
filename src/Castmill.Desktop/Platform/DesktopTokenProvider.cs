using Castmill.UI.Auth;

namespace Castmill.Desktop.Platform;

/// <summary>
/// Desktop token custody: the refresh token goes to MAUI <see cref="SecureStorage"/> — the
/// Keychain on macOS, DPAPI-backed credential storage on Windows — with a user-only (0600)
/// file in app data as the fallback when the platform store is unavailable. This is the one
/// meaningful custody difference between the shells (Roadmap §2.2) — everything else about
/// token handling lives in <see cref="TokenProviderBase"/>.
///
/// The fallback exists because the Keychain is NOT available on a dev Mac: SecureStorage on
/// Mac Catalyst requires the keychain-access-groups entitlement, and that entitlement is
/// restricted — an ad-hoc-signed Debug build carrying it is killed by launchd at spawn. So
/// dev builds ship no entitlement (see Platforms/MacCatalyst/Entitlements.plist) and land
/// here. The trade-off is deliberate and bounded: the file holds only a rotating single-use
/// refresh token (SHA-256-hashed server-side, family-revoked on reuse, revoked on logout),
/// with the same at-rest posture as az/gh/gcloud CLI token caches. A properly signed and
/// provisioned build (E10.4) validates the entitlement, SecureStorage starts working, and
/// this class prefers it automatically — no code change.
/// </summary>
internal sealed class DesktopTokenProvider(Func<AuthClient> authClient) : TokenProviderBase(authClient)
{
    private const string Key = "castmill.auth.refresh";

    private static string FallbackPath => Path.Combine(FileSystem.AppDataDirectory, ".castmill-refresh");

    protected override async Task<string?> ReadRefreshTokenAsync()
    {
        try
        {
            if (await SecureStorage.Default.GetAsync(Key) is { Length: > 0 } stored)
            {
                return stored;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A locked or unavailable keychain must not crash startup — fall through to the
            // file. SecureStorage throws platform-specific exception types, hence the broad
            // catch.
        }

        try
        {
            return File.Exists(FallbackPath) ? await File.ReadAllTextAsync(FallbackPath) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    protected override async Task WriteRefreshTokenAsync(string refreshToken)
    {
        try
        {
            await SecureStorage.Default.SetAsync(Key, refreshToken);
            TryDeleteFallback(); // the stronger store took it; don't leave a stale copy behind
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keychain unavailable (no entitlement on dev builds) — use the file.
        }

        try
        {
            // Create with owner-only permissions BEFORE the token is written, never chmod
            // after: a world-readable window, however short, is the whole game.
            await using (var stream = new FileStream(FallbackPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(FallbackPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(refreshToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Both stores failed. The live session still works — TokenProviderBase holds the
            // token in memory as the authoritative copy — it just will not survive a restart.
        }
    }

    protected override Task DeleteRefreshTokenAsync()
    {
        try
        {
            SecureStorage.Default.Remove(Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing stored there to begin with on entitlement-less builds.
        }

        TryDeleteFallback();
        return Task.CompletedTask;
    }

    private static void TryDeleteFallback()
    {
        try
        {
            File.Delete(FallbackPath);
        }
        catch (IOException)
        {
            // Best effort — sign-out must not fault over a locked file.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
