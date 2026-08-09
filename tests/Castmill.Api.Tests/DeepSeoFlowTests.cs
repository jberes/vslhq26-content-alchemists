using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Seo;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class DeepSeoFlowTests(CastmillApiFactory factory)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Analysis_is_persisted_and_must_be_approved_before_content_generation()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seo:RequireAnalysisBeforeGeneration", "true");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ISeoResearch>(_ => new FixedResearch()));
                services.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new FixedSeoProvider()));
            });
        });
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"deep-seo-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "SEO Producer"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var campaign = await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Analysis first", "Audience: engineering leaders")))
            .Content.ReadFromJsonAsync<CampaignResponse>();
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign!.Id}/transcripts",
            new { text = "We improved deployment speed. The product dashboard proves the change.", source = "test" });
        var ingestBody = await ingest.Content.ReadFromJsonAsync<IngestResponse>();

        var blocked = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = ingestBody!.TranscriptArtifactId });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedBrief = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/brief?transcriptArtifactId={ingestBody.TranscriptArtifactId}",
            new { });
        Assert.Equal(HttpStatusCode.Conflict, blockedBrief.StatusCode);

        var analysis = await client.PostAsJsonAsync("/api/v1/seo/deep-analysis",
            new SeoDeepAnalysisRequest(campaign.Id, ingestBody.TranscriptArtifactId, "https://example.com"));
        Assert.Equal(HttpStatusCode.Created, analysis.StatusCode);
        var report = await analysis.Content.ReadFromJsonAsync<SeoAnalysisReportResponse>();
        Assert.Equal("react data grid", report!.Serp.Keyword);
        Assert.Equal(2, report.Serp.OrganicResults.Count);
        Assert.NotNull(report.Insights);
        Assert.Equal(66.7, report.Insights.Aeo.VisibilityPercent);
        Assert.Equal(3, report.Insights.Aeo.EnginesSucceeded);
        Assert.Single(report.Insights.RankedKeywords);
        Assert.NotNull(report.Insights.SiteAuthority);
        Assert.Equal(3, report.Insights.Competitors!.Count);
        Assert.Equal(0.62, report.Insights.Competitors.Single(c => c.Domain == "one.example").TopicVisibility);
        Assert.NotEmpty(report.Insights.ContentAngles);

        var artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.Contains(artifacts!, a => a.Id == report.ReportArtifactId && a.Kind == "seo-report");
        var placeholder = Assert.Single(artifacts!, a => a.Kind == "blog");
        Assert.True(placeholder.IsPlaceholder);
        Assert.Contains(report.Insights.ContentAngles[0].Angle, placeholder.Title, StringComparison.Ordinal);

        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react data grid", report.Research.Keywords, report.Research.Questions));

        artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.Equal(ArtifactStatus.InReview, artifacts!.Single(a => a.Id == report.ReportArtifactId).Status);

        // Changing approved targets invalidates only the derived angles. The endpoint can
        // rebuild those angles without paying for another DataForSEO crawl.
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("react grid tutorial",
                [new SeoTarget("react grid tutorial", 2200, 28, 78, "provider")],
                report.Research.Questions));
        var storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.True(storedReport.AnglesStale);
        Assert.False(storedReport.InputsStale);

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/seo/reports/{report.ReportArtifactId}/angles/regenerate", new { });
        regenerate.EnsureSuccessStatusCode();
        storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.False(storedReport.AnglesStale);

        // A brief/content-type change invalidates the research inputs themselves.
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}",
            new CampaignUpdateRequest(campaign.Name, "Audience: technical founders",
                Status: CampaignStatus.Ready, ContentType: CampaignContentType.Tutorial));
        storedReport = await ReadReportAsync(client, campaign.Id, report.ReportArtifactId);
        Assert.True(storedReport.InputsStale);

        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/generate/newsletter",
            new { transcriptArtifactId = ingestBody.TranscriptArtifactId });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    private sealed record IngestResponse(Guid TranscriptArtifactId, int SegmentCount);

    private static async Task<SeoAnalysisReportResponse> ReadReportAsync(
        HttpClient client, Guid campaignId, Guid artifactId)
    {
        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}");
        return JsonSerializer.Deserialize<SeoAnalysisReportResponse>(artifact!.ContentJson, WebJson)!;
    }

    private sealed class FixedResearch : ISeoResearch
    {
        public Task<SeoResearchResponse> ResearchAsync(
            Guid userId, TranscriptContent transcript, string? campaignName, CancellationToken ct) =>
            Task.FromResult(new SeoResearchResponse(
                [new SeoTarget("react data grid", 8100, 42, 157.7, "provider")],
                [new SeoQuestion("How do you paginate a React data grid?", "paa")],
                true, []));
    }

    private sealed class FixedSeoProvider : ISeoProvider
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<SeoSerpSnapshot> GetSerpSnapshotAsync(string keyword, CancellationToken ct) =>
            Task.FromResult(new SeoSerpSnapshot(keyword, "AI overview", "Featured answer",
                [new SeoSerpResult(1, "Leader one", "https://one.example", "one.example"),
                 new SeoSerpResult(2, "Leader two", "https://two.example", "two.example")]));
        public Task<IReadOnlyList<SeoRankedKeyword>> GetRankedKeywordsAsync(
            string domain, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoRankedKeyword>>(
                [new SeoRankedKeyword("existing query", 7, 900, 24, 30, "https://example.com/existing", "informational")]);
        public Task<SeoAuthoritySnapshot?> GetAuthorityAsync(string domain, CancellationToken ct) =>
            Task.FromResult<SeoAuthoritySnapshot?>(
                new SeoAuthoritySnapshot(domain, 42, 1200, 160, 120, 4, 2));
        public Task<SeoPositionFootprint?> GetPositionFootprintAsync(string domain, CancellationToken ct) =>
            Task.FromResult<SeoPositionFootprint?>(new SeoPositionFootprint(3, 8, 20, 100, 450));
        public Task<IReadOnlyList<SeoCompetitorCandidate>> GetSerpCompetitorsAsync(
            IReadOnlyList<string> keywords, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoCompetitorCandidate>>(
                [new SeoCompetitorCandidate("one.example", 2.4, 8, 0.62, 440),
                 new SeoCompetitorCandidate("two.example", 4.2, 6, 0.41, 260),
                 new SeoCompetitorCandidate("example.com", 8, 2, 0.12, 40)]);
        public Task<SeoAeoEngineResult> QueryAnswerEngineAsync(
            string provider, string question, string? siteDomain, CancellationToken ct) =>
            Task.FromResult(provider == "perplexity"
                ? new SeoAeoEngineResult(provider, provider, false, false, null, [], "Unavailable")
                : new SeoAeoEngineResult(
                    provider, provider, true, provider is "chat_gpt" or "claude", "Answer",
                    provider is "chat_gpt" or "claude"
                        ? [new SeoCitation("Own", "https://example.com/article", "example.com", true)]
                        : []));
    }
}
