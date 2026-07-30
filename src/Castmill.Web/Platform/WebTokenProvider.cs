using Castmill.UI.Auth;
using Castmill.UI.Design;

namespace Castmill.Web.Platform;

/// <summary>
/// Web token custody: the refresh token goes to browser storage via the same
/// <see cref="IUiStateStore"/> island used for UI state, so the web shell adds no JS of its
/// own. The access token stays in memory (see <see cref="TokenProviderBase"/>).
///
/// Browser storage is readable by any script on the origin, which is exactly why the SWA
/// CSP forbids <c>unsafe-inline</c>/<c>unsafe-eval</c> beyond wasm-eval (§6, Security): the
/// CSP is what makes this custody acceptable, so the two must not be loosened separately.
/// </summary>
internal sealed class WebTokenProvider(Func<AuthClient> authClient, IUiStateStore store)
    : TokenProviderBase(authClient)
{
    private const string Key = "cm.auth.refresh";

    protected override Task<string?> ReadRefreshTokenAsync() => store.GetAsync(Key);

    protected override Task WriteRefreshTokenAsync(string refreshToken) => store.SetAsync(Key, refreshToken);

    protected override Task DeleteRefreshTokenAsync() => store.SetAsync(Key, string.Empty);
}
