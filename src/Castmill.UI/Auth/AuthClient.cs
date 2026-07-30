using Castmill.Core.Auth;
using Castmill.UI.Http;

namespace Castmill.UI.Auth;

/// <summary>
/// Typed client for the <c>/api/v1/auth</c> group. The auth calls are the only ones that go
/// out anonymously — sending a stale bearer token to /login would be meaningless, and a 401
/// from it means "wrong password", which must not trigger a silent refresh.
/// </summary>
public sealed class AuthClient(ApiClient api)
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
}
