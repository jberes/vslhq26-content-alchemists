using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Castmill.Api.Services.Publish;

public sealed class PublishOptions
{
    public const string SectionName = "Publish";
    /// <summary>Base URL of the Buffer-class scheduling broker (config stub; token is per-user secret custody).</summary>
    public string BrokerBaseUrl { get; set; } = string.Empty;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BrokerBaseUrl);
}

public sealed record BrokerChannel(string Id, string Name, string Platform);
public sealed record BrokerPost(
    string Id,
    string ChannelId,
    string Text,
    DateTimeOffset? ScheduledAt,
    string Status,
    DateTimeOffset? SentAtUtc = null,
    string? Permalink = null);

public interface IPublishBrokerClient
{
    Task<IReadOnlyList<BrokerChannel>> ListChannelsAsync(string token, CancellationToken ct);
    Task<BrokerPost> SchedulePostAsync(string token, string channelId, string text, DateTimeOffset scheduledAt, string? mediaUrl, CancellationToken ct);
    Task CancelPostAsync(string token, string postId, CancellationToken ct);
    Task<IReadOnlyList<BrokerPost>> GetQueueAsync(string token, string channelId, CancellationToken ct);
}

/// <summary>
/// Typed client over a Buffer-class REST broker (ADR-007: the broker owns
/// retries, timezones, and platform quirks). The endpoint shapes here follow
/// the common channels/posts/queue pattern; adjust the paths when the concrete
/// broker is chosen — the app-facing contract stays the same.
/// </summary>
public sealed class PublishBrokerClient(
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<PublishOptions> options) : IPublishBrokerClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PublishOptions _options = options.Value;

    private HttpClient CreateClient(string token)
    {
        var client = httpClientFactory.CreateClient("broker");
        client.BaseAddress = new Uri(_options.BrokerBaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<IReadOnlyList<BrokerChannel>> ListChannelsAsync(string token, CancellationToken ct)
    {
        using var client = CreateClient(token);
        return await client.GetFromJsonAsync<List<BrokerChannel>>("channels", Json, ct) ?? [];
    }

    public async Task<BrokerPost> SchedulePostAsync(
        string token, string channelId, string text, DateTimeOffset scheduledAt, string? mediaUrl, CancellationToken ct)
    {
        using var client = CreateClient(token);
        var response = await client.PostAsJsonAsync("posts",
            new { channelId, text, scheduledAt, mediaUrl }, Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BrokerPost>(Json, ct))!;
    }

    public async Task CancelPostAsync(string token, string postId, CancellationToken ct)
    {
        using var client = CreateClient(token);
        var response = await client.DeleteAsync($"posts/{Uri.EscapeDataString(postId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<BrokerPost>> GetQueueAsync(string token, string channelId, CancellationToken ct)
    {
        using var client = CreateClient(token);
        return await client.GetFromJsonAsync<List<BrokerPost>>(
            $"channels/{Uri.EscapeDataString(channelId)}/queue", Json, ct) ?? [];
    }
}
