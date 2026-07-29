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
    public async Task Search_volume_response_parses_and_sends_basic_auth()
    {
        var handler = new StubHandler(_ => """
            {"status_code":20000,"tasks":[{"status_code":20000,"result":[
              {"keyword":"deployment automation tool","search_volume":2400,"competition_index":37,"cpc":4.1},
              {"keyword":"cut deployment time","search_volume":320,"competition_index":12,"cpc":1.05}
            ]}]}
            """);
        var provider = CreateProvider(handler);

        var metrics = await provider.GetKeywordMetricsAsync(
            ["deployment automation tool", "cut deployment time"], TestContext.Current.CancellationToken);

        Assert.Equal(2, metrics.Count);
        Assert.Equal(2400, metrics[0].Volume);
        Assert.Equal(0.37, metrics[0].Competition, 2);
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
}
