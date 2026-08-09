using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Castmill.Core.Resources;

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
    public bool RequireAnalysisBeforeGeneration { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record SeoKeyword(
    string Term, long Volume, double Difficulty, double Competition, double Cpc,
    string? Intent = null);

/// <summary>A domain's visibility across the report's full keyword set, not just one SERP.</summary>
public sealed record SeoCompetitorCandidate(
    string Domain,
    double? AveragePosition,
    int KeywordCount,
    double? Visibility,
    double? EstimatedTraffic);

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
    /// <summary>Exact volume, difficulty, intent, competition and CPC for candidate keywords.</summary>
    Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct);
    /// <summary>Related keyword ideas with volume + difficulty for a seed (dataforseo_labs keyword_suggestions).</summary>
    Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct);
    /// <summary>Category-adjacent keyword ideas for several transcript-grounded seeds.</summary>
    Task<IReadOnlyList<SeoKeyword>> GetKeywordIdeasAsync(
        IReadOnlyList<string> seedKeywords, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
    /// <summary>One-keyword analysis used by /seo/analyze + the share snapshot.</summary>
    Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct);

    /// <summary>
    /// The questions Google shows for a keyword — "People also ask", plus any
    /// related_searches phrased as a question. This is the answer-engine half of the
    /// research: real questions people type, not questions a model imagined.
    /// </summary>
    Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct);

    /// <summary>The real result page shape used by the analysis-first report: organic
    /// competitors plus answer surfaces that can satisfy a query without a click.</summary>
    Task<SeoSerpSnapshot> GetSerpSnapshotAsync(string keyword, CancellationToken ct) =>
        Task.FromResult(new SeoSerpSnapshot(keyword, null, null, []));

    Task<IReadOnlyList<SeoRankedKeyword>> GetRankedKeywordsAsync(
        string domain, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeoRankedKeyword>>([]);

    Task<SeoAuthoritySnapshot?> GetAuthorityAsync(string domain, CancellationToken ct) =>
        Task.FromResult<SeoAuthoritySnapshot?>(null);

    Task<SeoPositionFootprint?> GetPositionFootprintAsync(string domain, CancellationToken ct) =>
        Task.FromResult<SeoPositionFootprint?>(null);

    /// <summary>Domains visible across the complete target-keyword set.</summary>
    Task<IReadOnlyList<SeoCompetitorCandidate>> GetSerpCompetitorsAsync(
        IReadOnlyList<string> keywords, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeoCompetitorCandidate>>([]);

    Task<SeoAeoEngineResult> QueryAnswerEngineAsync(
        string provider, string question, string? siteDomain, CancellationToken ct) =>
        Task.FromResult(new SeoAeoEngineResult(
            provider, provider, false, false, null, [], "Answer-engine analysis is unavailable."));
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
    private static readonly string[] OrganicItemTypes = ["organic"];
    private static readonly string[] EstimatedTrafficOrder = ["ranked_serp_element.serp_item.etv,desc"];
    private static readonly string[] RelevanceOrder = ["relevance,desc", "keyword_info.search_volume,desc"];
    private readonly SeoOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/dataforseo_labs/google/keyword_overview/live", new[]
        {
            new
            {
                keywords,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
            },
        }, ct);

        var results = new List<SeoKeyword>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var info = Object(item, "keyword_info");
            var props = Object(item, "keyword_properties");
            var intent = Object(item, "search_intent_info");
            results.Add(new SeoKeyword(
                Str(item, "keyword"),
                Num(info, "search_volume"),
                Dbl(props, "keyword_difficulty"),
                Dbl(info, "competition"),
                Dbl(info, "cpc"),
                NullableText(intent, "main_intent") ?? NullableText(intent, "intent")));
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
            var intent = item.TryGetProperty("search_intent_info", out var si) ? si : default;
            results.Add(new SeoKeyword(
                Str(item, "keyword"),
                info.ValueKind == JsonValueKind.Object ? Num(info, "search_volume") : 0,
                props.ValueKind == JsonValueKind.Object ? Dbl(props, "keyword_difficulty") : 0,
                info.ValueKind == JsonValueKind.Object ? Dbl(info, "competition") : 0,
                info.ValueKind == JsonValueKind.Object ? Dbl(info, "cpc") : 0,
                intent.ValueKind == JsonValueKind.Object
                    ? NullableText(intent, "main_intent") ?? NullableText(intent, "intent")
                    : null));
        }
        return results;
    }

    public async Task<IReadOnlyList<SeoKeyword>> GetKeywordIdeasAsync(
        IReadOnlyList<string> seedKeywords, int limit, CancellationToken ct)
    {
        var seeds = seedKeywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
        if (seeds.Length == 0)
        {
            return [];
        }

        using var doc = await PostAsync("v3/dataforseo_labs/google/keyword_ideas/live", new[]
        {
            new
            {
                keywords = seeds,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                closely_variants = false,
                ignore_synonyms = true,
                limit = Math.Clamp(limit, 1, 1000),
                order_by = RelevanceOrder,
            },
        }, ct);

        var results = new List<SeoKeyword>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var info = Object(item, "keyword_info");
            var props = Object(item, "keyword_properties");
            var intent = Object(item, "search_intent_info");
            results.Add(new SeoKeyword(
                Str(item, "keyword"),
                Num(info, "search_volume"),
                Dbl(props, "keyword_difficulty"),
                Dbl(info, "competition"),
                Dbl(info, "cpc"),
                NullableText(intent, "main_intent") ?? NullableText(intent, "intent")));
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

    public async Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct)
    {
        // Advanced, not regular: the "people_also_ask" and "related_searches" blocks only
        // appear in the advanced SERP payload. depth 20 is one page — enough for the PAA box,
        // and every extra page is billed.
        using var doc = await PostAsync("v3/serp/google/organic/live/advanced", new[]
        {
            new
            {
                keyword,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                depth = 20,
            },
        }, ct);

        var questions = new List<string>();

        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var type = Str(item, "type");

            if (type == "people_also_ask" && item.TryGetProperty("items", out var paa)
                && paa.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in paa.EnumerateArray())
                {
                    Add(Str(entry, "title"));
                }
            }
            else if (type == "related_searches" && item.TryGetProperty("items", out var related)
                     && related.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in related.EnumerateArray())
                {
                    // Related searches are mostly phrases, not questions. Only the ones that
                    // ARE questions belong in an answer-engine brief.
                    var text = entry.ValueKind == JsonValueKind.String
                        ? entry.GetString()
                        : FirstString(entry, "title", "text", "keyword");
                    if (LooksLikeQuestion(text))
                    {
                        Add(text);
                    }
                }
            }
        }

        return questions;

        void Add(string? text)
        {
            var trimmed = text?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !questions.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                questions.Add(trimmed);
            }
        }
    }

    public async Task<SeoSerpSnapshot> GetSerpSnapshotAsync(string keyword, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/serp/google/organic/live/advanced", new[]
        {
            new
            {
                keyword,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                depth = 20,
            },
        }, ct);

        string? aiOverview = null;
        string? featuredSnippet = null;
        var organic = new List<SeoSerpResult>();

        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var type = Str(item, "type");
            if (type == "organic")
            {
                var url = Str(item, "url");
                organic.Add(new SeoSerpResult(
                    (int)Num(item, "rank_absolute"), Str(item, "title"), url,
                    Str(item, "domain"), NullableText(item, "description")));
            }
            else if (type is "featured_snippet" or "answer_box")
            {
                featuredSnippet ??= FirstText(item, "description", "text", "title")
                    ?? FindFirstAnswer(item);
            }
            else if (type.Contains("ai_overview", StringComparison.OrdinalIgnoreCase))
            {
                // Current advanced-SERP responses can place answer text in nested
                // ai_overview_element/items rather than the outer feature row.
                aiOverview ??= FirstText(item, "markdown", "text", "description")
                    ?? FindFirstAnswer(item);
            }
        }

        return new SeoSerpSnapshot(keyword, aiOverview, featuredSnippet,
            [.. organic.OrderBy(r => r.Rank).Take(10)]);

        static string? FirstText(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                if (NullableText(item, name) is { Length: > 0 } value)
                {
                    return value;
                }
            }
            return null;
        }
    }

    public async Task<IReadOnlyList<SeoRankedKeyword>> GetRankedKeywordsAsync(
        string domain, int limit, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/dataforseo_labs/google/ranked_keywords/live", new[]
        {
            new
            {
                target = NormalizeDomain(domain),
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                limit = Math.Clamp(limit, 1, 50),
                item_types = OrganicItemTypes,
                order_by = EstimatedTrafficOrder,
            },
        }, ct);

        var rows = new List<SeoRankedKeyword>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var keywordData = Object(item, "keyword_data");
            var keywordInfo = Object(keywordData, "keyword_info");
            var properties = Object(keywordData, "keyword_properties");
            var intent = Object(keywordData, "search_intent_info");
            var ranked = Object(item, "ranked_serp_element");
            var serpItem = Object(ranked, "serp_item");
            var term = Str(keywordData, "keyword");
            if (term.Length == 0)
            {
                continue;
            }

            rows.Add(new SeoRankedKeyword(
                term,
                (int)Num(serpItem, "rank_absolute"),
                NullableLong(keywordInfo, "search_volume"),
                NullableDouble(properties, "keyword_difficulty"),
                NullableDouble(serpItem, "etv"),
                Str(serpItem, "url"),
                NullableText(intent, "main_intent") ?? NullableText(intent, "intent")));
        }

        return [.. rows.OrderByDescending(r => r.EstimatedTraffic ?? 0).Take(limit)];
    }

    public async Task<SeoAuthoritySnapshot?> GetAuthorityAsync(string domain, CancellationToken ct)
    {
        var normalized = NormalizeDomain(domain);
        using var doc = await PostAsync("v3/backlinks/summary/live", new[]
        {
            new { target = normalized, include_subdomains = true },
        }, ct);

        var item = TaskResultItems(doc, itemsNestedInResult: false).FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SeoAuthoritySnapshot(
            normalized,
            NullableDouble(item, "rank"),
            NullableLong(item, "backlinks"),
            NullableLong(item, "referring_domains"),
            NullableLong(item, "referring_main_domains"),
            NullableLong(item, "broken_backlinks"),
            NullableDouble(item, "spam_score"));
    }

    public async Task<SeoPositionFootprint?> GetPositionFootprintAsync(
        string domain, CancellationToken ct)
    {
        using var doc = await PostAsync("v3/dataforseo_labs/google/domain_rank_overview/live", new[]
        {
            new
            {
                target = NormalizeDomain(domain),
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
            },
        }, ct);

        var item = TaskResultItems(doc, itemsNestedInResult: true).FirstOrDefault();
        var metrics = Object(item, "metrics");
        var organic = Object(metrics, "organic");
        if (organic.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SeoPositionFootprint(
            Num(organic, "pos_1"), Num(organic, "pos_2_3"), Num(organic, "pos_4_10"),
            Num(organic, "count"), NullableDouble(organic, "etv"));
    }

    public async Task<IReadOnlyList<SeoCompetitorCandidate>> GetSerpCompetitorsAsync(
        IReadOnlyList<string> keywords, int limit, CancellationToken ct)
    {
        var terms = keywords
            .Where(keyword => keyword.Trim().Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        using var doc = await PostAsync("v3/dataforseo_labs/google/serp_competitors/live", new[]
        {
            new
            {
                keywords = terms,
                location_code = _options.LocationCode,
                language_code = _options.LanguageCode,
                include_subdomains = true,
                item_types = OrganicItemTypes,
                limit = Math.Clamp(limit, 1, 100),
            },
        }, ct);

        var rows = new List<SeoCompetitorCandidate>();
        foreach (var item in TaskResultItems(doc, itemsNestedInResult: true))
        {
            var domain = NormalizeDomain(Str(item, "domain"));
            if (domain.Length == 0)
            {
                continue;
            }
            rows.Add(new SeoCompetitorCandidate(
                domain,
                NullableDouble(item, "avg_position"),
                (int)Num(item, "keywords_count"),
                NullableDouble(item, "visibility"),
                NullableDouble(item, "etv")));
        }

        return [.. rows
            .OrderByDescending(row => row.Visibility ?? 0)
            .ThenByDescending(row => row.KeywordCount)
            .Take(limit)];
    }

    public async Task<SeoAeoEngineResult> QueryAnswerEngineAsync(
        string provider, string question, string? siteDomain, CancellationToken ct)
    {
        var label = provider switch
        {
            "chat_gpt" => "ChatGPT",
            "gemini" => "Gemini",
            "claude" => "Claude",
            "perplexity" => "Perplexity",
            _ => provider,
        };
        if (provider is not ("chat_gpt" or "gemini" or "claude" or "perplexity"))
        {
            return new SeoAeoEngineResult(provider, label, false, false, null, [], "Unknown answer engine.");
        }

        var selectedModel = await SelectAnswerEngineModelAsync(provider, ct);
        if (selectedModel is null)
        {
            return new SeoAeoEngineResult(
                provider, label, false, false, null, [],
                $"DataForSEO returned no available {label} model.");
        }

        var prompt = question[..Math.Min(question.Length, 500)];
        object task = selectedModel.Value.SupportsWebSearch
            ? new
            {
                user_prompt = prompt,
                model_name = selectedModel.Value.Name,
                max_output_tokens = 1024,
                web_search = true,
            }
            : new
            {
                user_prompt = prompt,
                model_name = selectedModel.Value.Name,
                max_output_tokens = 1024,
            };

        using var doc = await PostAsync($"v3/ai_optimization/{provider}/llm_responses/live", new[] { task }, ct);
        var result = TaskResults(doc).FirstOrDefault();
        if (result.ValueKind != JsonValueKind.Object)
        {
            return new SeoAeoEngineResult(provider, label, false, false, null, [], "No answer returned.");
        }

        var citations = FindCitationObjects(result)
            .Select(c =>
            {
                var url = FirstString(c, "url", "link", "source_url");
                var citationDomain = NormalizeDomain(url);
                var own = siteDomain is { Length: > 0 }
                    && (citationDomain.Equals(siteDomain, StringComparison.OrdinalIgnoreCase)
                        || citationDomain.EndsWith($".{siteDomain}", StringComparison.OrdinalIgnoreCase));
                return new SeoCitation(
                    FirstString(c, "title", "name", "source") ?? citationDomain,
                    url ?? string.Empty, citationDomain, own);
            })
            .Where(c => c.Url.Length > 0)
            .DistinctBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var answer = FindFirstAnswer(result);
        return new SeoAeoEngineResult(
            provider, label, true, citations.Any(c => c.IsOwnDomain), answer, citations);
    }

    private async Task<(string Name, bool SupportsWebSearch)?> SelectAnswerEngineModelAsync(
        string provider, CancellationToken ct)
    {
        using var doc = await GetAsync(
            $"v3/ai_optimization/{provider}/llm_responses/models", ct);
        var models = TaskResults(doc)
            .Select(item => (
                Name: Str(item, "model_name"),
                SupportsWebSearch: Bool(item, "web_search_supported")))
            .Where(item => item.Name.Length > 0)
            .ToArray();
        if (models.Length == 0)
        {
            return null;
        }

        // Prefer a fast, current general-purpose model. The live catalog remains the
        // source of truth, so provider model retirements cannot break report creation.
        var preferred = provider switch
        {
            "chat_gpt" => new[] { "gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "gpt-4o" },
            "gemini" => new[] { "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-flash" },
            "claude" => new[] { "claude-sonnet-4-0", "claude-sonnet-4-20250514", "claude-3-7-sonnet-latest", "claude-3-5-sonnet-latest" },
            "perplexity" => new[] { "sonar", "sonar-pro" },
            _ => [],
        };
        foreach (var name in preferred)
        {
            var match = models.FirstOrDefault(model =>
                model.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match.Name is { Length: > 0 })
            {
                return match;
            }
        }

        return models[0];
    }

    /// <summary>
    /// A question by shape, not by punctuation: Google's related searches routinely drop the
    /// question mark ("how to build a react data grid").
    /// </summary>
    internal static bool LooksLikeQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains('?', StringComparison.Ordinal))
        {
            return true;
        }

        var first = text.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        return first is "how" or "what" or "why" or "when" or "where" or "which"
            or "who" or "can" or "does" or "do" or "is" or "are" or "should";
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
        var client = CreateClient();

        using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content, ct);
        return await ReadEnvelopeAsync(response, ct);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var client = CreateClient();
        using var response = await client.GetAsync(path, ct);
        return await ReadEnvelopeAsync(response, ct);
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("seo");
        client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _options.ApiKey);
        return client;
    }

    private static async Task<JsonDocument> ReadEnvelopeAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
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

    private static IEnumerable<JsonElement> TaskResults(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.TryGetProperty("status_code", out var status)
                && status.ValueKind == JsonValueKind.Number && status.GetInt32() != 20000)
            {
                var message = task.TryGetProperty("status_message", out var statusMessage)
                    ? statusMessage.GetString()
                    : "unknown";
                throw new InvalidOperationException($"DataForSEO task error: {message}");
            }
            if (task.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in result.EnumerateArray())
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> FindCitationObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if ((element.TryGetProperty("url", out var url) || element.TryGetProperty("link", out url)
                 || element.TryGetProperty("source_url", out url))
                && url.ValueKind == JsonValueKind.String)
            {
                yield return element;
            }
            foreach (var property in element.EnumerateObject())
            {
                foreach (var found in FindCitationObjects(property.Value))
                {
                    yield return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var found in FindCitationObjects(child))
                {
                    yield return found;
                }
            }
        }
    }

    private static string? FindFirstAnswer(JsonElement element)
    {
        foreach (var name in new[] { "answer", "text", "content", "markdown" })
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (FindFirstAnswer(property.Value) is { } nested)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (FindFirstAnswer(child) is { } nested)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    internal static string NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
        return value.Trim().Trim('/').ToLowerInvariant();
    }

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (NullableText(element, name) is { Length: > 0 } value)
            {
                return value;
            }
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long? NullableLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.TryGetInt64(out var number) ? number : (long)v.GetDouble()
            : null;

    private static double? NullableDouble(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static string? NullableText(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
