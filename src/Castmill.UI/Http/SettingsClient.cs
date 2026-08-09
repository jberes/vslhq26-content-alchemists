namespace Castmill.UI.Http;

/// <summary>
/// One stored credential, as the server is willing to describe it: whether it exists and when
/// it was last written. The VALUE is never returned by the API and therefore never modelled
/// here — that is the whole point of the secret store, and a DTO with a value field would be
/// an invitation to leak one into a log or a render.
/// </summary>
public sealed record SecretStatus(string Kind, bool Configured, DateTimeOffset? UpdatedAt);

public sealed record SecretWriteRequest(string Value);

/// <summary>A public destination — the website, a social profile. Never a credential.</summary>
public sealed record WorkspaceLink(string Label, string Url);

public sealed record SettingWrite(string Value);

public sealed record SettingRow(string Key, string Value);

/// <summary>
/// The settings surface: AI provider credentials and readiness. Goes through
/// <see cref="ApiClient"/> like everything else, so auth, correlation IDs and typed errors are
/// handled in exactly one place.
/// </summary>
public sealed class SettingsClient(ApiClient api)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<SecretStatus>> SecretsAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<SecretStatus>>("api/v1/settings/secrets", ct);

    public Task SetSecretAsync(string kind, string value, CancellationToken ct = default) =>
        // Void PUT: the endpoint answers 204, and the generic overload throws on an empty body.
        api.PutAsync($"api/v1/settings/secrets/{kind}", new SecretWriteRequest(value), ct);

    public Task RemoveSecretAsync(string kind, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/settings/secrets/{kind}", ct);

    /// <summary>
    /// The workspace's website and social URLs. Stored as one JSON value under a single
    /// plaintext key — they are public URLs, so the encrypted secret store would be the wrong
    /// home for them.
    /// </summary>
    public const string LinksKey = "workspace.links";

    public async Task<List<WorkspaceLink>> GetLinksAsync(CancellationToken ct = default)
    {
        var rows = await api.GetAsync<List<SettingRow>>("api/v1/settings", ct);
        var raw = rows.FirstOrDefault(r => r.Key == LinksKey)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<WorkspaceLink>>(raw, Json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public Task SaveLinksAsync(IReadOnlyList<WorkspaceLink> links, CancellationToken ct = default) =>
        api.PutAsync($"api/v1/settings/{LinksKey}",
            new SettingWrite(System.Text.Json.JsonSerializer.Serialize(links, Json)), ct);
}
