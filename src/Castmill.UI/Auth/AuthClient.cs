using Castmill.Core.Auth;
using Castmill.UI.Http;

namespace Castmill.UI.Auth;

/// <summary>
/// Typed client for the <c>/api/v1/auth</c> group. The auth calls are the only ones that go
/// out anonymously — sending a stale bearer token to /login would be meaningless, and a 401
/// from it means "wrong password", which must not trigger a silent refresh.
/// </summary>
public sealed class AuthClient(ApiClient api, HttpClient http)
{
    public Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        api.PostAsync<RegisterRequest, AuthResponse>("api/v1/auth/register", request, anonymous: true, ct);

    public Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        api.PostAsync<LoginRequest, AuthResponse>("api/v1/auth/login", request, anonymous: true, ct);

    public Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        api.PostAsync<RefreshRequest, AuthResponse>(
            "api/v1/auth/refresh", new RefreshRequest(refreshToken), anonymous: true, ct);

    public Task LogoutAsync(CancellationToken ct = default) =>
        api.PostAsync("api/v1/auth/logout", new { }, anonymous: false, ct);

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        api.PostAsync("api/v1/auth/change-password", request, anonymous: false, ct);

    public Task<MeResponse> MeAsync(CancellationToken ct = default) =>
        api.GetAsync<MeResponse>("api/v1/me", ct);

    public Task<ExternalAuthProviderStatusResponse> ProvidersAsync(CancellationToken ct = default) =>
        api.GetAnonymousAsync<ExternalAuthProviderStatusResponse>("api/v1/auth/external/providers", ct);

    public Task<ExternalAuthLinksResponse> ExternalLinksAsync(CancellationToken ct = default) =>
        api.GetAsync<ExternalAuthLinksResponse>("api/v1/auth/external/links", ct);

    public async Task<ExternalAuthStartResult> StartExternalAsync(
        ExternalAuthStartRequest request,
        CancellationToken ct = default) =>
        ResolveBrowserUrl(await api.PostAsync<ExternalAuthStartRequest, ExternalAuthStartResponse>(
            "api/v1/auth/external/start", request, anonymous: true, ct));

    public Task<ExternalAuthPollResponse> PollExternalAsync(
        ExternalAuthPollRequest request,
        CancellationToken ct = default) =>
        api.PostAsync<ExternalAuthPollRequest, ExternalAuthPollResponse>(
            "api/v1/auth/external/poll", request, anonymous: true, ct);

    public Task<AuthResponse> ExchangeExternalAsync(
        ExternalAuthExchangeRequest request,
        CancellationToken ct = default) =>
        api.PostAsync<ExternalAuthExchangeRequest, AuthResponse>(
            "api/v1/auth/external/exchange", request, anonymous: true, ct);

    public async Task<ExternalAuthStartResult> LinkStartAsync(
        ExternalAuthStartRequest request,
        CancellationToken ct = default) =>
        ResolveBrowserUrl(await api.PostAsync<ExternalAuthStartRequest, ExternalAuthStartResponse>(
            "api/v1/auth/external/link/start", request, anonymous: false, ct));

    public Task LinkExchangeAsync(
        ExternalAuthExchangeRequest request,
        CancellationToken ct = default) =>
        api.PostAsync("api/v1/auth/external/link/exchange", request, anonymous: false, ct);

    public Task UnlinkAsync(string provider, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/auth/external/link/{Uri.EscapeDataString(provider)}", ct);

    private ExternalAuthStartResult ResolveBrowserUrl(ExternalAuthStartResponse response)
    {
        var baseAddress = http.BaseAddress;
        if (baseAddress is null
            || !baseAddress.IsAbsoluteUri
            || !IsHttpScheme(baseAddress))
        {
            throw new InvalidOperationException("The API base address must be an absolute HTTP or HTTPS URI.");
        }

        var browserUri = Uri.TryCreate(response.BrowserUrl, UriKind.Absolute, out var absolute)
            && IsHttpScheme(absolute)
                ? absolute
                : new Uri(baseAddress, response.BrowserUrl);
        if (!IsHttpScheme(browserUri)
            || !string.Equals(baseAddress.Scheme, browserUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseAddress.Host, browserUri.Host, StringComparison.OrdinalIgnoreCase)
            || baseAddress.Port != browserUri.Port)
        {
            throw new InvalidOperationException("The external authentication URL was not on the API origin.");
        }

        return new ExternalAuthStartResult(response, browserUri);
    }

    private static bool IsHttpScheme(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}

public sealed record ExternalAuthStartResult(
    ExternalAuthStartResponse Response,
    Uri AbsoluteBrowserUri);
