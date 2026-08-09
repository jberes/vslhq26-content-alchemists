using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// The workspace dashboard projection (frontend item 3): the front page and the campaigns
/// index read one call instead of a full preview per campaign. These tests pin the counts,
/// the review queue and tenant scoping.
/// </summary>
[Collection("api")]
public sealed class CampaignDashboardTests(CastmillApiFactory factory)
{
    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"dash-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Dashboard Tester"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Dashboard_returns_per_campaign_counts_and_the_review_queue_in_one_call()
    {
        var client = await AuthedClientAsync();

        var create = await client.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Dash campaign", null));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;

        var artifactsUrl = $"/api/v1/campaigns/{campaign.Id}/artifacts";
        var blog = await (await client.PostAsJsonAsync(artifactsUrl,
            new ArtifactCreateRequest("blog", "Reviewable blog", """{"body":"x"}"""))).Content.ReadFromJsonAsync<ArtifactResponse>();
        (await client.PostAsJsonAsync(artifactsUrl,
            new ArtifactCreateRequest("social-x", "Waiting draft", """{"body":"y"}"""))).EnsureSuccessStatusCode();

        // Move the blog into review through the real gate.
        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{artifactsUrl}/{blog!.Id}/status")
        {
            Content = JsonContent.Create(new ArtifactStatusRequest(ArtifactStatus.InReview)),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", $"\"{blog.Version}\"");
        (await client.SendAsync(patch)).EnsureSuccessStatusCode();

        var dashboard = await client.GetFromJsonAsync<DashboardResponse>("/api/v1/campaigns/dashboard");

        Assert.NotNull(dashboard);
        var counts = Assert.Single(dashboard!.Campaigns, c => c.CampaignId == campaign.Id);
        Assert.Equal(2, counts.Artifacts);
        Assert.Equal(1, counts.InReview);

        var review = Assert.Single(dashboard.ReviewQueue, r => r.CampaignId == campaign.Id);
        Assert.Equal("Reviewable blog", review.Title);
        Assert.Equal("Dash campaign", review.CampaignName);
        Assert.Equal(ArtifactStatus.InReview, review.Status);

        // Fresh drafts are not "aging".
        Assert.DoesNotContain(dashboard.AgingDrafts, a => a.CampaignId == campaign.Id);
    }

    [Fact]
    public async Task Dashboard_is_tenant_scoped()
    {
        var alice = await AuthedClientAsync();
        var create = await alice.PostAsJsonAsync("/api/v1/campaigns", new CampaignCreateRequest("Alice only", null));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;

        var bob = await AuthedClientAsync();
        var dashboard = await bob.GetFromJsonAsync<DashboardResponse>("/api/v1/campaigns/dashboard");

        Assert.NotNull(dashboard);
        Assert.DoesNotContain(dashboard!.Campaigns, c => c.CampaignId == campaign.Id);
        Assert.DoesNotContain(dashboard.ReviewQueue, r => r.CampaignId == campaign.Id);
    }

    [Fact]
    public async Task Dashboard_excludes_operational_seo_reports_from_edit_work()
    {
        var client = await AuthedClientAsync();
        var create = await client.PostAsJsonAsync(
            "/api/v1/campaigns", new CampaignCreateRequest("Report campaign", null));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;
        var artifactsUrl = $"/api/v1/campaigns/{campaign.Id}/artifacts";
        var report = await (await client.PostAsJsonAsync(artifactsUrl,
            new ArtifactCreateRequest("seo-report", "Deep SEO/AEO report", """{"status":"Draft"}""")))
            .Content.ReadFromJsonAsync<ArtifactResponse>();

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{artifactsUrl}/{report!.Id}/status")
        {
            Content = JsonContent.Create(new ArtifactStatusRequest(ArtifactStatus.InReview)),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", $"\"{report.Version}\"");
        (await client.SendAsync(patch)).EnsureSuccessStatusCode();

        var dashboard = await client.GetFromJsonAsync<DashboardResponse>("/api/v1/campaigns/dashboard");

        Assert.DoesNotContain(dashboard!.ReviewQueue, item => item.ArtifactId == report.Id);
        Assert.DoesNotContain(dashboard.AgingDrafts, item => item.ArtifactId == report.Id);
    }

    [Fact]
    public async Task Wire_queue_contains_distribution_content_not_strategy_documents()
    {
        var client = await AuthedClientAsync();
        var create = await client.PostAsJsonAsync(
            "/api/v1/campaigns", new CampaignCreateRequest("Distribution campaign", null));
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;
        var artifactsUrl = $"/api/v1/campaigns/{campaign.Id}/artifacts";

        var blog = await CreateAndQueueAsync("blog", "Publishable blog");
        var summary = await CreateAndQueueAsync("campaign-summary", "Internal summary");
        var brief = await CreateAndQueueAsync("seo-brief", "Internal SEO brief");

        var dashboard = await client.GetFromJsonAsync<DashboardResponse>("/api/v1/campaigns/dashboard");

        Assert.Contains(dashboard!.ReadyToSchedule!, item => item.ArtifactId == blog.Id);
        Assert.DoesNotContain(dashboard.ReadyToSchedule!, item => item.ArtifactId == summary.Id);
        Assert.DoesNotContain(dashboard.ReadyToSchedule!, item => item.ArtifactId == brief.Id);

        async Task<ArtifactResponse> CreateAndQueueAsync(string kind, string title)
        {
            var artifact = await (await client.PostAsJsonAsync(artifactsUrl,
                new ArtifactCreateRequest(kind, title, """{"body":"x"}""")))
                .Content.ReadFromJsonAsync<ArtifactResponse>();

            using var patch = new HttpRequestMessage(
                HttpMethod.Patch, $"{artifactsUrl}/{artifact!.Id}/status")
            {
                Content = JsonContent.Create(new ArtifactStatusRequest(ArtifactStatus.Queued)),
            };
            patch.Headers.TryAddWithoutValidation("If-Match", $"\"{artifact.Version}\"");
            (await client.SendAsync(patch)).EnsureSuccessStatusCode();
            return artifact;
        }
    }
}
