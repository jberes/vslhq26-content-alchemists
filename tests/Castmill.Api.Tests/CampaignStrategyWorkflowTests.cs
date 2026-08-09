using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Ai;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class CampaignStrategyWorkflowTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Campaign_lifecycle_hierarchy_and_multi_source_transcript_round_trip()
    {
        var client = await AuthenticatedClientAsync(factory, "hierarchy");
        var create = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Working title", "Initial brief",
                ContentType: CampaignContentType.Tutorial));
        create.EnsureSuccessStatusCode();
        var campaign = (await create.Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal(CampaignContentType.Tutorial, campaign.ContentType);

        var update = await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}",
            new CampaignUpdateRequest("Renamed campaign", "Initial brief",
                Status: CampaignStatus.Ready, ContentType: CampaignContentType.Webinar));
        update.EnsureSuccessStatusCode();
        campaign = (await update.Content.ReadFromJsonAsync<CampaignResponse>())!;
        Assert.Equal("Renamed campaign", campaign.Name);
        Assert.Equal(CampaignStatus.Ready, campaign.Status);
        Assert.Equal(CampaignContentType.Webinar, campaign.ContentType);

        var blog = await CreateArtifactAsync(client, campaign.Id,
            new ArtifactCreateRequest("blog", "Pillar blog", """{"markdown":"# Pillar"}"""));
        var social = await CreateArtifactAsync(client, campaign.Id,
            new ArtifactCreateRequest("social-x", "Owned X post",
                """{"text":"Read the pillar"}""", blog.Id));
        Assert.Equal(blog.Id, social.ParentArtifactId);

        var youtube = await CreateArtifactAsync(client, campaign.Id,
            new ArtifactCreateRequest("youtube", "Video", """{"title":"Video"}"""));
        var invalid = await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("social-linkedin", "Bad parent", "{}", youtube.Id));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var preview = await client.GetFromJsonAsync<CampaignPreviewContract>(
            $"/api/v1/campaigns/{campaign.Id}/preview");
        Assert.Equal(blog.Id, preview!.Artifacts.Single(a => a.Id == social.Id).ParentArtifactId);

        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new
            {
                text = "First recording. Second recording.",
                source = "combined",
                segments = new[]
                {
                    new TranscriptSegment("local-1", 0, 4, "Host", "First recording.", "part-one.mp4"),
                    new TranscriptSegment("local-2", 4.25, 9, "Guest", "Second recording.", "part-two.wav"),
                },
            });
        ingest.EnsureSuccessStatusCode();
        var transcriptId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("transcriptArtifactId").GetGuid();
        var transcriptArtifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{transcriptId}");
        var transcript = TranscriptService.Parse(transcriptArtifact!.ContentJson)!;
        Assert.Equal(["part-one.mp4", "part-two.wav"],
            transcript.Segments.Select(segment => segment.SourceLabel));

        var deleted = await client.DeleteAsync($"/api/v1/campaigns/{campaign.Id}/artifacts/{blog.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var remaining = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        Assert.DoesNotContain(remaining!, item => item.Id == social.Id);
    }

    [Fact]
    public async Task Approved_strategy_produces_a_persisted_editable_campaign_summary()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IFoundryClientFactory>(
                _ => new AiGenerationTests.FakeFoundryFactory()))));
        var client = await AuthenticatedClientAsync(app, "summary");
        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Summary campaign", null,
                ContentType: CampaignContentType.ProductDemo)))
            .Content.ReadFromJsonAsync<CampaignResponse>())!;
        await client.PutAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/seo-targets",
            new SeoTargetsRequest("deployment automation",
                [new SeoTarget("deployment automation", 1900, 31, 84, "provider")], []));
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new { text = "The product cut deployment time in half. The dashboard proves it. The team shipped it in six weeks." });
        var transcriptId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("transcriptArtifactId").GetGuid();

        var suggest = await client.PostAsJsonAsync(
            $"/api/v1/ai/campaigns/{campaign.Id}/brief?transcriptArtifactId={transcriptId}", new { });
        suggest.EnsureSuccessStatusCode();

        var artifacts = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts");
        var summary = Assert.Single(artifacts!, item => item.Kind == "campaign-summary");
        var detail = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{summary.Id}");
        Assert.Contains("# Executive summary", detail!.ContentJson, StringComparison.Ordinal);
        Assert.Contains("Deployment time was cut in half", detail.ContentJson, StringComparison.Ordinal);
        Assert.Contains("deployment automation", detail.ContentJson, StringComparison.Ordinal);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(
        WebApplicationFactory<Program> app, string prefix)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"{prefix}-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple", "Workflow Tester"));
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private static async Task<ArtifactResponse> CreateArtifactAsync(
        HttpClient client, Guid campaignId, ArtifactCreateRequest request)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ArtifactResponse>())!;
    }

    private sealed record CampaignPreviewContract(
        CampaignResponse Campaign, IReadOnlyList<ArtifactPreviewResponse> Artifacts);
}
