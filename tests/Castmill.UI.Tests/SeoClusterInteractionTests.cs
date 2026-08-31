using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Canvas;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Castmill.UI.Tests;

public sealed class SeoClusterInteractionTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("e1111111-1111-1111-1111-111111111111");
    private static readonly Guid TranscriptId = Guid.Parse("e1111111-1111-1111-1111-222222222222");
    private static readonly Guid BlogId = Guid.Parse("e1111111-1111-1111-1111-333333333333");
    private static readonly Guid PlaceholderId = Guid.Parse("e1111111-1111-1111-1111-444444444444");

    public SeoClusterInteractionTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview());
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{TranscriptId}",
            new ArtifactResponse(
                TranscriptId, CampaignId, "transcript", "Source transcript",
                """{"source":"paste","segments":[{"id":"S1","text":"Source"}]}""",
                ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/seo-targets",
            new SeoTargetsResponse("content operations", [], []));
    }

    [Fact]
    public async Task Adding_an_open_channel_creates_a_placeholder_and_navigates_to_generation()
    {
        var view = Render<SeoView>(parameters => parameters.Add(component => component.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-seo__cluster").Count == 1,
            TimeSpan.FromSeconds(5));

        var placeholder = new ArtifactResponse(
            PlaceholderId, CampaignId, "newsletter", "New newsletter",
            """{"markdown":"","placeholder":true}""",
            ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ParentArtifactId: BlogId);
        Http.OnPost($"api/v1/campaigns/{CampaignId}/artifacts", placeholder);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(
            new ArtifactPreviewResponse(
                PlaceholderId, CampaignId, "newsletter", "New newsletter", ArtifactStatus.Draft,
                1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                ParentArtifactId: BlogId, IsPlaceholder: true)));

        await view.InvokeAsync(() =>
            view.FindComponent<ClusterMap>().Instance.HandleNodeAsync("draft", "newsletter"));

        var create = Http.Bodies.Single(item =>
            item.Method == HttpMethod.Post && item.Path.EndsWith("/artifacts", StringComparison.Ordinal));
        using var request = JsonDocument.Parse(create.Body);
        Assert.Equal("newsletter", request.RootElement.GetProperty("kind").GetString());
        using var content = JsonDocument.Parse(
            request.RootElement.GetProperty("contentJson").GetString()!);
        Assert.True(content.RootElement.GetProperty("placeholder").GetBoolean());
        Assert.DoesNotContain(Http.Requests, request =>
            request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.Contains("/generate/", StringComparison.Ordinal));

        var path = new Uri(Services.GetRequiredService<NavigationManager>().Uri).PathAndQuery;
        Assert.Equal($"/campaigns/{CampaignId}/focus?artifact={PlaceholderId}&generate=true", path);
    }

    private static CampaignPreview Preview(ArtifactPreviewResponse? placeholder = null)
    {
        var artifacts = new List<ArtifactPreviewResponse>
        {
            Artifact(TranscriptId, "transcript", "Source transcript"),
            Artifact(BlogId, "blog", "Content operations guide"),
        };
        if (placeholder is not null)
        {
            artifacts.Add(placeholder);
        }
        return new CampaignPreview(Campaign(), artifacts, [], 0, 0);
    }

    private static ArtifactPreviewResponse Artifact(Guid id, string kind, string title) =>
        new(id, CampaignId, kind, title, ArtifactStatus.Draft, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "SEO campaign", null,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
}