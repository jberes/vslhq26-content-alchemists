using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Api.Services.Seo;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>
/// The post-publish check (ADR-059). The judgement is code, not a model: the same AEO rules the
/// writers were given, applied to what the crawler found.
/// </summary>
public sealed class SeoDistributionEvaluationTests
{
    [Fact]
    public void A_well_built_page_passes_every_check()
    {
        var page = new SeoPageSnapshot("https://www.example.com/post", 200, "Blazor grids explained",
            "A complete guide to Blazor grids and virtualisation.", "https://example.com/post/",
            ["Blazor grids explained"], ["What is a Blazor grid?", "How does virtualisation work?", "Setup"], [],
            1420, true, true, 98);

        var checks = SeoDistributionService.Evaluate("https://www.example.com/post", page);

        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Label}: {c.Detail}"));
        Assert.Contains(checks, c => c.Label == "Canonical points here");
        Assert.Contains(checks, c => c.Label == "Question headings" && c.Detail.StartsWith("2 headings", StringComparison.Ordinal));
    }

    [Fact]
    public void A_syndicated_copy_pointing_elsewhere_is_named_as_the_copy()
    {
        var page = new SeoPageSnapshot("https://medium.com/@x/post", 200, "Blazor grids explained", "d",
            "https://www.example.com/post", ["Blazor grids explained"], ["Setup"], [], 400, false, true, null);

        var checks = SeoDistributionService.Evaluate("https://medium.com/@x/post", page);

        var canonical = checks.Single(c => c.Label == "Canonical points here");
        Assert.False(canonical.Passed);
        Assert.Contains("https://www.example.com/post", canonical.Detail, StringComparison.Ordinal);
        Assert.False(checks.Single(c => c.Label == "Question headings").Passed);
        Assert.False(checks.Single(c => c.Label == "Enough depth").Passed);
        Assert.False(checks.Single(c => c.Label == "Structured data").Passed);
    }

    [Fact]
    public void Missing_canonical_and_missing_page_are_reported_not_ignored()
    {
        var noCanonical = new SeoPageSnapshot("https://www.example.com/post", 200, new string('t', 70), null, null,
            [], [], [], 900, false, false, null);

        var checks = SeoDistributionService.Evaluate("https://www.example.com/post", noCanonical);
        Assert.Contains(checks, c => c.Label == "Canonical points here" && !c.Passed && c.Detail.Contains("No canonical", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.Label == "Exactly one H1" && !c.Passed);
        Assert.Contains(checks, c => c.Label == "Title length" && !c.Passed);
        Assert.Contains(checks, c => c.Label == "Meta description" && !c.Passed);
        Assert.Contains(checks, c => c.Label == "Served over HTTPS" && !c.Passed);

        var missing = SeoDistributionService.Evaluate("https://www.example.com/post", null);
        Assert.False(Assert.Single(missing).Passed);
    }

    [Fact]
    public void A_site_that_refuses_the_crawler_is_reported_as_such_and_nothing_else_is_judged()
    {
        // DataForSEO returns the item with "meta": null and status 503 when a site blocks its bot.
        var refused = new SeoPageSnapshot("https://www.example.com/post", 503, null, null, null, [], [], [], 0, false, false, null);

        var checks = SeoDistributionService.Evaluate("https://www.example.com/post", refused);

        Assert.Equal(2, checks.Count);
        Assert.Contains("refused the crawler", checks[0].Detail, StringComparison.Ordinal);
    }
}

[Collection("api")]
public sealed class SeoDistributionApiTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task The_distribution_check_crawls_the_page_and_lists_links_and_copies()
    {
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new PublishedPageProvider()))));
        var (client, campaignId) = await SignedInWithCampaignAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/seo/distribution",
            new SeoDistributionRequest(campaignId, "https://www.example.com/post"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<SeoDistributionReport>())!;

        Assert.NotNull(report.Page);
        Assert.True(report.Checks.Single(c => c.Label == "Canonical points here").Passed);
        Assert.Equal(3, report.ReferringDomainCount);
        Assert.Equal("medium.com", Assert.Single(report.ReferringDomains).Domain);
        // The title is the default mention query — copies reuse it.
        Assert.Equal("Blazor grids explained", report.MentionQuery);
        Assert.Equal(7, report.MentionCount);
        Assert.Contains(report.Sections, s => s.Section == "Page crawl" && s.Available);
    }

    [Fact]
    public async Task Unknown_campaigns_and_bad_urls_are_refused()
    {
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Scoped<ISeoProvider>(_ => new PublishedPageProvider()))));
        var (client, campaignId) = await SignedInWithCampaignAsync(app);

        var missing = await client.PostAsJsonAsync("/api/v1/seo/distribution",
            new SeoDistributionRequest(Guid.NewGuid(), "https://www.example.com/post"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var bad = await client.PostAsJsonAsync("/api/v1/seo/distribution",
            new { campaignId, url = "not a url" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<(HttpClient Client, Guid CampaignId)> SignedInWithCampaignAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"dist-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Publisher"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var campaign = await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Published piece", null))).Content.ReadFromJsonAsync<CampaignResponse>();
        return (client, campaign!.Id);
    }

    private sealed class PublishedPageProvider : ISeoProvider
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<SeoKeyword>> GetKeywordMetricsAsync(IReadOnlyList<string> keywords, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<IReadOnlyList<SeoKeyword>> GetSuggestionsAsync(string seedKeyword, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeoKeyword>>([]);
        public Task<SeoAnalysis> AnalyzeAsync(string keyword, string? targetUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetQuestionsAsync(string keyword, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<SeoPageSnapshot?> GetPageSnapshotAsync(string url, CancellationToken ct) =>
            Task.FromResult<SeoPageSnapshot?>(new SeoPageSnapshot(url, 200, "Blazor grids explained", "A guide.", url,
                ["Blazor grids explained"], ["What is a Blazor grid?", "Setup", "FAQ"], [], 1420, true, true, 98));
        public Task<SeoReferringDomainsResult> GetReferringDomainsAsync(string target, int limit, CancellationToken ct) =>
            Task.FromResult(new SeoReferringDomainsResult(3, [new SeoReferringDomain("medium.com", 812, 2, "2026-09-01", 0)]));
        public Task<SeoMentionsResult> SearchMentionsAsync(string keyword, int limit, CancellationToken ct) =>
            Task.FromResult(new SeoMentionsResult(7, [new SeoMention("https://medium.com/@x/post", "medium.com", keyword, "Originally published…", "2026-09-02", 812)]));
    }
}
