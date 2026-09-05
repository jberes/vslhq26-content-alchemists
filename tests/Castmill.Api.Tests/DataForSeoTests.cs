using System.Net;
using System.Text;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Seo;
using Castmill.Core.Ai;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

public sealed class DataForSeoTests
{
    [Fact]
    public void Thumbnail_concepts_require_three_strategic_directions_before_pixels()
    {
        var spec = Generators.Find("thumbnail-concepts");
        Assert.NotNull(spec);
        using var json = JsonDocument.Parse("""
            {"title":"Directions","concepts":[
              {"name":"One","angle":"a","prompt":"p","overlayText":"ONE","reason":"r"},
              {"name":"Two","angle":"a","prompt":"p","overlayText":"TWO","reason":"r"},
              {"name":"Three","angle":"a","prompt":"p","overlayText":"THREE","reason":"r"}
            ],"citations":["s01"]}
            """);
        var transcript = new TranscriptContent("test", [new TranscriptSegment("s01", 0, 1, null, "Proof")]);

        Assert.True(spec!.Validate(json.RootElement, transcript).Passed);
    }

    // ---- Provider parsing against canned DataForSEO v3 envelopes ---------------

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static DataForSeoProvider CreateProvider(StubHandler handler) =>
        new(new StubFactory(handler), Options.Create(new SeoOptions { ApiKey = "dGVzdDp0ZXN0" }));

    [Fact]
    public async Task Keyword_overview_parses_complete_metrics_and_sends_basic_auth()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"keyword":"deployment automation tool","keyword_info":{"search_volume":2400,"competition":0.37,"cpc":4.1},
               "keyword_properties":{"keyword_difficulty":31},"search_intent_info":{"main_intent":"commercial"}},
              {"keyword":"cut deployment time","keyword_info":{"search_volume":320,"competition":0.12,"cpc":1.05},
               "keyword_properties":{"keyword_difficulty":14},"search_intent_info":{"main_intent":"informational"}}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var metrics = await provider.GetKeywordMetricsAsync(
            ["deployment automation tool", "cut deployment time"], TestContext.Current.CancellationToken);

        Assert.Equal(2, metrics.Count);
        Assert.Equal(2400, metrics[0].Volume);
        Assert.Equal(0.37, metrics[0].Competition, 2);
        Assert.Equal(31, metrics[0].Difficulty);
        Assert.Equal("commercial", metrics[0].Intent);
        Assert.Contains("/dataforseo_labs/google/keyword_overview/live",
            handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Basic", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("dGVzdDp0ZXN0", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Suggestions_response_parses_nested_items_with_difficulty()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"keyword":"devops dashboard tutorial","keyword_info":{"search_volume":5400,"competition":0.22,"cpc":2.3},
               "keyword_properties":{"keyword_difficulty":18}}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var suggestions = await provider.GetSuggestionsAsync("devops dashboard", 10, TestContext.Current.CancellationToken);

        var s = Assert.Single(suggestions);
        Assert.Equal(5400, s.Volume);
        Assert.Equal(18, s.Difficulty);
    }

    [Fact]
    public async Task Keyword_ideas_add_category_adjacent_terms_with_intent()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"keyword":"release governance checklist","keyword_info":{"search_volume":880,"competition":0.18,"cpc":3.4},
               "keyword_properties":{"keyword_difficulty":21},"search_intent_info":{"main_intent":"informational"}}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var ideas = await provider.GetKeywordIdeasAsync(
            ["deployment automation", "release safety"], 40,
            TestContext.Current.CancellationToken);

        var idea = Assert.Single(ideas);
        Assert.Equal("release governance checklist", idea.Term);
        Assert.Equal("informational", idea.Intent);
        Assert.Contains("/dataforseo_labs/google/keyword_ideas/live",
            handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ranked_keywords_include_position_traffic_page_and_intent()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"keyword_data":{"keyword":"react data grid","keyword_info":{"search_volume":8100},
               "keyword_properties":{"keyword_difficulty":42},"search_intent_info":{"main_intent":"commercial"}},
               "ranked_serp_element":{"serp_item":{"rank_absolute":6,"url":"https://example.com/grid","etv":321.5}}}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var rows = await provider.GetRankedKeywordsAsync(
            "https://www.example.com/products", 50, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(6, row.Position);
        Assert.Equal(321.5, row.EstimatedTraffic);
        Assert.Equal("commercial", row.Intent);
        Assert.Equal("https://example.com/grid", row.Url);
    }

    [Fact]
    public async Task Backlink_and_domain_footprint_responses_power_competitor_comparison()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("backlinks", StringComparison.Ordinal)
            ? """
              {"status_code":20000,"tasks":[{"status_code":20000,"result":[
                {"rank":58,"backlinks":12000,"referring_domains":640,"referring_main_domains":510,"broken_backlinks":22,"spam_score":3.5}
              ]}]}
              """
            : """
              {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
                {"metrics":{"organic":{"pos_1":12,"pos_2_3":34,"pos_4_10":88,"count":420,"etv":7300.5}}}
              ]}]}]}
              """);
        var provider = CreateProvider(handler);

        var authority = await provider.GetAuthorityAsync("example.com", TestContext.Current.CancellationToken);
        var footprint = await provider.GetPositionFootprintAsync("example.com", TestContext.Current.CancellationToken);

        Assert.Equal(640, authority!.ReferringDomains);
        Assert.Equal(3.5, authority.SpamScore);
        Assert.Equal(12, footprint!.Position1);
        Assert.Equal(420, footprint.TotalOrganic);
    }

    [Fact]
    public async Task Serp_competitors_measure_visibility_across_the_full_keyword_set()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"domain":"leader.example","avg_position":3.5,"keywords_count":8,"visibility":0.72,"etv":640.4},
              {"domain":"www.example.com","avg_position":12,"keywords_count":3,"visibility":0.15,"etv":90.2}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var rows = await provider.GetSerpCompetitorsAsync(
            ["deployment automation", "release governance"], 12,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Equal("leader.example", rows[0].Domain);
        Assert.Equal(8, rows[0].KeywordCount);
        Assert.Equal(0.72, rows[0].Visibility);
        Assert.Equal(640.4, rows[0].EstimatedTraffic);
        Assert.Equal("example.com", rows[1].Domain);
        Assert.Contains("/dataforseo_labs/google/serp_competitors/live",
            handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advanced_serp_extracts_nested_ai_overview_text()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"type":"ai_overview","items":[{"type":"ai_overview_element","text":"A source-backed overview."}]},
              {"type":"featured_snippet","description":"A concise featured answer."},
              {"type":"organic","rank_absolute":1,"title":"Leader","url":"https://leader.example/a","domain":"leader.example"}
            ]}]}]}
            """);
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSerpSnapshotAsync(
            "deployment automation", TestContext.Current.CancellationToken);

        Assert.Equal("A source-backed overview.", snapshot.AiOverview);
        Assert.Equal("A concise featured answer.", snapshot.FeaturedSnippet);
        Assert.Single(snapshot.OrganicResults);
    }

    [Fact]
    public async Task Answer_engine_uses_live_model_catalog_and_extracts_exact_domain_citations()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return request.Method == HttpMethod.Get
                ? """
                  {"status_code":20000,"tasks":[{"status_code":20000,"result":[
                    {"model_name":"gpt-current","web_search_supported":true}
                  ]}]}
                  """
                : """
                  {"status_code":20000,"tasks":[{"status_code":20000,"result":[
                    {"answer":"Use a virtualized grid.","citations":[
                      {"title":"Example guide","url":"https://www.example.com/grid"},
                      {"title":"Other guide","url":"https://other.example/grid"}
                    ]}
                  ]}]}
                  """;
        });
        var provider = CreateProvider(handler);

        var result = await provider.QueryAnswerEngineAsync(
            "chat_gpt", "What is a React data grid?", "example.com",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.DomainCited);
        Assert.Equal(2, result.Citations.Count);
        Assert.Single(result.Citations, c => c.IsOwnDomain);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/v3/ai_optimization/chat_gpt/llm_responses/models", requests[0].Path);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        using var body = JsonDocument.Parse(requests[1].Body!);
        var task = body.RootElement[0];
        Assert.Equal("gpt-current", task.GetProperty("model_name").GetString());
        Assert.True(task.GetProperty("web_search").GetBoolean());
    }

    [Fact]
    public async Task Answer_engine_omits_web_search_when_selected_model_does_not_support_it()
    {
        var postBody = string.Empty;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return """
                    {"status_code":20000,"tasks":[{"status_code":20000,"result":[
                      {"model_name":"claude-account-model","web_search_supported":false}
                    ]}]}
                    """;
            }

            postBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """
                {"status_code":20000,"tasks":[{"status_code":20000,"result":[
                  {"answer":"A grounded answer."}
                ]}]}
                """;
        });
        var provider = CreateProvider(handler);

        var result = await provider.QueryAnswerEngineAsync(
            "claude", "What is a React data grid?", "example.com",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(postBody);
        var task = body.RootElement[0];
        Assert.Equal("claude-account-model", task.GetProperty("model_name").GetString());
        Assert.False(task.TryGetProperty("web_search", out _));
    }

    [Fact]
    public async Task Error_envelope_surfaces_message_never_credentials()
    {
        var handler = new StubHandler(_ => """{"status_code":40101,"status_message":"Auth failed."}""");
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetKeywordMetricsAsync(["x"], TestContext.Current.CancellationToken));
        Assert.Contains("40101", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dGVzdDp0ZXN0", ex.Message, StringComparison.Ordinal);
    }

    // ---- seo-brief validator ----------------------------------------------------

    private static readonly TranscriptContent Transcript = new("test", [
        new TranscriptSegment("S1", 0, 5, null, "We built a deployment tool."),
    ]);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Seo_brief_requires_exactly_three_youtube_titles_under_100_chars()
    {
        var spec = Generators.Find("seo-brief")!;

        var twoTitles = Json("""
            {"title":"b","summary":"s","focusKeywords":["a","b","c"],"youtubeTitles":["one","two"],"citations":["S1"]}
            """);
        Assert.False(spec.Validate(twoTitles, Transcript).Passed);

        var longTitle = Json($$"""
            {"title":"b","summary":"s","focusKeywords":["a","b","c"],"youtubeTitles":["ok","fine","{{new string('x', 101)}}"],"citations":["S1"]}
            """);
        Assert.False(spec.Validate(longTitle, Transcript).Passed);

        var valid = Json("""
            {"title":"b","summary":"s","focusKeywords":["a","b","c"],"youtubeTitles":["one","two","three"],"citations":["S1"]}
            """);
        Assert.True(spec.Validate(valid, Transcript).Passed);
    }

    [Fact]
    public void Opportunity_ranks_high_volume_low_difficulty_first()
    {
        var easyPopular = new SeoKeyword("a", 5000, 10, 0, 0);
        var hardPopular = new SeoKeyword("b", 5000, 80, 0, 0);
        var easyNiche = new SeoKeyword("c", 100, 10, 0, 0);
        Assert.True(DataForSeoProvider.Opportunity(easyPopular) > DataForSeoProvider.Opportunity(hardPopular));
        Assert.True(DataForSeoProvider.Opportunity(hardPopular) > DataForSeoProvider.Opportunity(easyNiche));
    }

    // ---- ADR-059: AI Optimization, OnPage, Backlinks, Content Analysis ----------

    [Fact]
    public async Task Ai_search_volume_parses_the_current_and_previous_month()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"keyword":"deployment automation","ai_search_volume":93,"ai_monthly_searches":[{"year":2026,"month":8,"ai_search_volume":93},{"year":2026,"month":7,"ai_search_volume":96}]},
              {"keyword":"blazor grid","ai_search_volume":0,"ai_monthly_searches":[]}]}]}]}
            """);
        var rows = await CreateProvider(handler).GetAiSearchVolumeAsync(["deployment automation", "blazor grid"], CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(93, rows[0].AiSearchVolume);
        Assert.Equal(96, rows[0].PreviousMonth);
        Assert.Null(rows[1].PreviousMonth);
        Assert.EndsWith("v3/ai_optimization/ai_keyword_data/keywords_search_volume/live", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_mentions_send_the_domain_target_shape_and_summarise_by_platform()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """
                {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"total_count":1281,"items_count":2,"items":[
                  {"platform":"google","model_name":"google_ai_overview","question":"model view presenter","answer":"![img](https://x/y)\nMVP is a pattern **derived** from MVC.","ai_search_volume":90500,"last_response_at":"2026-07-30 04:09:09 +00:00"},
                  {"platform":"chat_gpt","model_name":"gpt-4o","question":"best blazor grid","answer":"Ignite UI…","ai_search_volume":null,"last_response_at":"2026-08-01 00:00:00 +00:00"}]}]}]}
                """;
        });
        var summary = await CreateProvider(handler).GetLlmMentionsAsync("https://www.infragistics.com/", 20, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal("infragistics.com", summary!.Domain);
        Assert.Equal(1281, summary.TotalMentions);
        Assert.Equal(1, summary.SampleByPlatform["google"]);
        Assert.Equal(1, summary.SampleByPlatform["chat_gpt"]);
        Assert.Equal("MVP is a pattern **derived** from MVC.", summary.Samples[0].Excerpt);
        Assert.Equal(90500, summary.Samples[0].AiSearchVolume);
        Assert.Null(summary.Samples[1].AiSearchVolume);
        // The API rejects a string target and a "target" key inside the item: it wants {"domain": …}.
        Assert.Contains("\"target\":[{\"domain\":\"infragistics.com\"}]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instant_page_crawl_reads_headings_canonical_word_count_and_schema()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"url":"https://www.example.com/post","status_code":200,"onpage_score":98.17,
               "meta":{"title":"Blazor grids explained","description":"A guide.","canonical":"https://www.example.com/post",
                       "htags":{"h1":["Blazor grids explained"],"h2":["What is a Blazor grid?","Setup"],"h3":["Why virtualise?"]},
                       "content":{"plain_text_word_count":1420}},
               "checks":{"has_micromarkup":true,"is_https":true}}]}]}]}
            """);
        var page = await CreateProvider(handler).GetPageSnapshotAsync("https://www.example.com/post", CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(200, page!.StatusCode);
        Assert.Equal("Blazor grids explained", page.Title);
        Assert.Equal(["Blazor grids explained"], page.H1);
        Assert.Equal(2, page.H2.Count);
        Assert.Equal(["Why virtualise?"], page.H3);
        Assert.Equal(1420, page.WordCount);
        Assert.True(page.HasStructuredData);
        Assert.True(page.IsHttps);
        Assert.Equal(98.17, page.OnPageScore);
        Assert.EndsWith("v3/on_page/instant_pages", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_page_with_null_meta_parses_to_its_status_code()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"items":[
              {"resource_type":"broken","status_code":503,"url":"https://www.example.com/post","meta":null,"onpage_score":null,"checks":null}]}]}]}
            """);
        var page = await CreateProvider(handler).GetPageSnapshotAsync("https://www.example.com/post", CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(503, page!.StatusCode);
        Assert.Null(page.Title);
        Assert.Empty(page.H1);
        Assert.Equal(0, page.WordCount);
    }

    [Fact]
    public async Task Referring_domains_and_mentions_carry_their_totals()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("backlinks", StringComparison.Ordinal)
            ? """
              {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"total_count":3,"items":[
                {"domain":"medium.com","rank":812,"backlinks":2,"first_seen":"2026-09-01 10:00:00 +00:00","backlinks_spam_score":0}]}]}]}
              """
            : """
              {"status_code":20000,"tasks":[{"status_code":20000,"result":[{"total_count":7,"items":[
                {"url":"https://medium.com/@x/blazor-grids-explained","domain":"medium.com","domain_rank":812,"fetch_time":"2026-09-02 08:00:00 +00:00",
                 "content_info":{"title":"Blazor grids explained","snippet":"Originally published at example.com…"}}]}]}]}
              """);
        var provider = CreateProvider(handler);

        var links = await provider.GetReferringDomainsAsync("https://www.example.com/post", 25, CancellationToken.None);
        var mentions = await provider.SearchMentionsAsync("Blazor grids explained", 25, CancellationToken.None);

        Assert.Equal(3, links.TotalCount);
        Assert.Equal("medium.com", Assert.Single(links.Items).Domain);
        Assert.Equal(7, mentions.TotalCount);
        var mention = Assert.Single(mentions.Items);
        Assert.Equal("Blazor grids explained", mention.Title);
        Assert.Equal(812, mention.DomainRank);
    }
}
