using System.Net.Http.Json;
using System.Text.Json;

namespace Castmill.Api.Services.Seo;

public sealed class SeoOptions
{
    public const string SectionName = "Seo";
    /// <summary>SEO data provider base URL (SERP/keyword/AI-overview class API).</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Provider API key — dev config only; a per-user secret kind if this ever multi-users.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record SeoAnalysis(
    string Keyword,
    string? TargetUrl,
    int Score,
    IReadOnlyList<SeoKeyword> Keywords,
    IReadOnlyList<string> ContentAngles,
    JsonElement ProviderRaw);

public sealed record SeoKeyword(string Term, long Volume, double Difficulty);

public interface ISeoProvider
{
    bool IsConfigured { get; }
    Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct);
}

/// <summary>
/// Typed client over the SEO data provider. The typed core (score, keywords,
/// angles) feeds the report UI; the provider's full response is preserved
/// verbatim in ProviderRaw so report views can grow without re-querying.
/// Path/shape follow the common analyze-API pattern; adjust when the concrete
/// provider is chosen.
/// </summary>
public sealed class SeoProvider(
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<SeoOptions> options) : ISeoProvider
{
    private readonly SeoOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("seo");
        client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);

        var response = await client.PostAsJsonAsync("analyze", new { keyword, targetUrl }, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var keywords = new List<SeoKeyword>();
        if (root.TryGetProperty("keywords", out var kws) && kws.ValueKind == JsonValueKind.Array)
        {
            foreach (var kw in kws.EnumerateArray())
            {
                keywords.Add(new SeoKeyword(
                    kw.TryGetProperty("term", out var term) ? term.GetString() ?? "" : "",
                    kw.TryGetProperty("volume", out var vol) ? vol.GetInt64() : 0,
                    kw.TryGetProperty("difficulty", out var diff) ? diff.GetDouble() : 0));
            }
        }
        var angles = new List<string>();
        if (root.TryGetProperty("contentAngles", out var ang) && ang.ValueKind == JsonValueKind.Array)
        {
            angles.AddRange(ang.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String)
                .Select(a => a.GetString()!));
        }

        return new SeoAnalysis(
            keyword,
            targetUrl,
            root.TryGetProperty("score", out var score) ? score.GetInt32() : 0,
            keywords,
            angles,
            root.Clone());
    }
}
