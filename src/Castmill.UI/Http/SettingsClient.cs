namespace Castmill.UI.Http;

/// <summary>
/// One stored credential, as the server is willing to describe it: whether it exists and when
/// it was last written. The VALUE is never returned by the API and therefore never modelled
/// here — that is the whole point of the secret store, and a DTO with a value field would be
/// an invitation to leak one into a log or a render.
/// </summary>
public sealed record SecretStatus(string Kind, bool Configured, DateTimeOffset? UpdatedAt);

public sealed record SecretWriteRequest(string Value);

/// <summary>
/// The settings surface: AI provider credentials and readiness. Goes through
/// <see cref="ApiClient"/> like everything else, so auth, correlation IDs and typed errors are
/// handled in exactly one place.
/// </summary>
public sealed class SettingsClient(ApiClient api)
{
    public Task<IReadOnlyList<SecretStatus>> SecretsAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<SecretStatus>>("api/v1/settings/secrets", ct);

    public Task SetSecretAsync(string kind, string value, CancellationToken ct = default) =>
        api.PutAsync<SecretWriteRequest, object>(
            $"api/v1/settings/secrets/{kind}", new SecretWriteRequest(value), etag: null, ct);

    public Task RemoveSecretAsync(string kind, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/settings/secrets/{kind}", ct);
}
