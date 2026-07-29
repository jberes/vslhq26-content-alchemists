using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Castmill.Api.Services.Seo;

public sealed class SeoOptions
{
    public const string SectionName = "Seo";
    /// <summary>DataForSEO API base (v3). Override only for their sandbox.</summary>
    public string BaseUrl { get; set; } = "https://api.dataforseo.com";
    /// <summary>DataForSEO Basic credential: base64("login:password") — exactly what their dashboard shows.</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Google Ads location code (2840 = United States).</summary>
    public int LocationCode { get; set; } = 2840;
    public string LanguageCode { get; set; } = "en";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record SeoKeyword(string Term, long Volume, double Difficulty, double Competition, double Cpc);

public sealed record SeoAnalysis(
    string Keyword,
    string? TargetUrl,
    int Score,
    IReadOnlyList<SeoKeyword> Keywords,
    IReadOnlyList<string> ContentAngles,
    JsonElement ProviderRaw);

public interface ISeoProvider
{
    bool IsConfigured { get; }
    /// <summary>Exact volume/competition/CPC for a list of candidate keywords (google_ads/search_volume).</summary>
    Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct);
    /// <summary>Related keyword ideas with volume + difficulty for a seed (dataforseo_labs keyword_suggestions).</summary>
    Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct);
    /// <summary>One-keyword analysis used by /seo/analyze + the share snapshot.</summary>
    Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct);
}

/// <summary>
/// DataForSEO v3 client. Envelope contract: HTTP 200 with status_code 20000 at
/// the top and per-task; anything else is a real failure and is surfaced with
/// DataForSEO's status_message (never the credential).
/// </summary>
public sealed class DataForSeoProvider(
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<SeoOptions> options) : ISeoProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SeoOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/keywords_data/google_ads/search_volume/live", new[]
        {
            new
            {
                keywords,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
            },
        }, ct);

        var results = new List<SeoKeyword>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: false))
        {
            results.Add(new SeoKeyword(
                Str(item, "keyword"),
                Num(item, "search_volume"),
                0, // difficulty comes from the labs endpoints, not google_ads
                Dbl(item, "competition_index") / 100.0,
                Dbl(item, "cpc")));
        }
        return results;
    }

    public async Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/dataforseo_labs/google/keyword_suggestions/live", new[]
        {
            new
            {
                keyword = seedKeyword,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                limit,
                include_seed_keyword = true,
            },
        }, ct);

        var results = new List<SeoKeyword>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var info = item.TryGetProperty("keyword_info", out var ki) ? ki : default;
            var props = item.TryGetProperty("keyword_properties", out var kp) ? kp : default;
            results.Add(new SeoKeyword(
                Str(item, "keyword"),
                info.ValueKind == JsonValueKind.Object ? Num(info, "search_volume") : 0,
                props.ValueKind == JsonValueKind.Object ? Dbl(props, "keyword_difficulty") : 0,
                info.ValueKind == JsonValueKind.Object ? Dbl(info, "competition") : 0,
                info.ValueKind == JsonValueKind.Object ? Dbl(info, "cpc") : 0));
        }
        return results;
    }

    public async Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct)
    {
        var suggestions = await GetSuggestionsAsync(keyword, 25, ct);
        using var raw = JsonDocument.Parse(JsonSerializer.Serialize(suggestions, Json));
        return new SeoAnalysis(
            keyword,
            targetUrl,
            OpportunityScore(suggestions),
            suggestions,
            [.. suggestions.OrderByDescending(Opportunity).Take(5).Select(s => s.Term)],
            raw.RootElement.Clone());
    }

    /// <summary>Ranking heuristic: reward volume, punish difficulty.</summary>
    internal static double Opportunity(SeoKeyword k) => k.Volume / (k.Difficulty + 10.0);

    internal static int OpportunityScore(IReadOnlyList<SeoKeyword> keywords)
    {
        if (keywords.Count == 0)
        {
            return 0;
        }
        var avgVolume = keywords.Average(k => Math.Min(k.Volume, 10_000));
        var avgDifficulty = keywords.Where(k => k.Difficulty > 0).Select(k => k.Difficulty).DefaultIfEmpty(50).Average();
        return (int)Math.Clamp(avgVolume / 100.0 * (1.0 - avgDifficulty / 100.0), 0, 100);
    }

    // ---- Transport & envelope --------------------------------------------------

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("seo");
        client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _options.ApiKey);

        using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content, ct);
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var status = doc.RootElement.TryGetProperty("status_code", out var sc) ? sc.GetInt32() : 0;
        if (status != 20000)
        {
            var message = doc.RootElement.TryGetProperty("status_message", out var sm) ? sm.GetString() : "unknown";
            doc.Dispose();
            throw new InvalidOperationException($"DataForSEO error {status}: {message}");
        }
        return doc;
    }

    private static IEnumerable<JsonElement> TaskResultItems(JsonDocument doc, bool itemsNestedInResult)
    {
        if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.TryGetProperty("status_code", out var sc) && sc.GetInt32() != 20000)
            {
                var message = task.TryGetProperty("status_message", out var sm) ? sm.GetString() : "unknown";
                throw new InvalidOperationException($"DataForSEO task error: {message}");
            }
            if (!task.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var entry in result.EnumerateArray())
            {
                if (!itemsNestedInResult)
                {
                    yield return entry;
                }
                else if (entry.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
