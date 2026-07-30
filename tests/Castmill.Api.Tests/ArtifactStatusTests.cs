using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// Artifact review state (frontend ADR-F12). Added with the Status column: the Front Page's
/// review queue, the status bar on every card and the Wire's queue all read it, so it needs
/// the same ETag discipline as any other artifact write.
/// </summary>
[Collection("api")]
public sealed class ArtifactStatusTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_new_artifact_starts_as_a_draft()
    {
        var (client, campaignId) = await SignedInCampaignAsync();

        var created = await CreateArtifactAsync(client, campaignId);

        Assert.Equal(ArtifactStatus.Draft, created.Status);
    }

    [Fact]
    public async Task Status_moves_through_the_four_states_and_is_visible_in_the_preview_projection()
    {
        var (client, campaignId) = await SignedInCampaignAsync();
        var artifact = await CreateArtifactAsync(client, campaignId);
        var version = artifact.Version;

        foreach (var status in new[] { ArtifactStatus.InReview, ArtifactStatus.Queued, ArtifactStatus.Published })
        {
            var updated = await SetStatusAsync(client, campaignId, artifact.Id, status, version);
            Assert.Equal(status, updated.Status);
            version = updated.Version;
        }

        // The preview projection is what every list surface reads — one payload, no
        // per-card fetch (G9's principle, applied to status).
        var previews = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");

        Assert.Equal(ArtifactStatus.Published, Assert.Single(previews!).Status);
    }

    [Fact]
    public async Task An_unknown_status_is_rejected()
    {
        var (client, campaignId) = await SignedInCampaignAsync();
        var artifact = await CreateArtifactAsync(client, campaignId);

        using var request = Patch(campaignId, artifact.Id, "Shipped", artifact.Version);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_status_change_without_an_if_match_is_refused()
    {
        var (client, campaignId) = await SignedInCampaignAsync();
        var artifact = await CreateArtifactAsync(client, campaignId);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}/artifacts/{artifact.Id}/status")
        {
            Content = JsonContent.Create(new ArtifactStatusRequest(ArtifactStatus.InReview)),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_etag_loses_the_race()
    {
        var (client, campaignId) = await SignedInCampaignAsync();
        var artifact = await CreateArtifactAsync(client, campaignId);

        await SetStatusAsync(client, campaignId, artifact.Id, ArtifactStatus.InReview, artifact.Version);

        // Second caller still holds the version from before that change.
        using var stale = Patch(campaignId, artifact.Id, ArtifactStatus.Queued, artifact.Version);
        var response = await client.SendAsync(stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------

    private static HttpRequestMessage Patch(Guid campaignId, Guid artifactId, string status, long version)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}/status")
        {
            Content = JsonContent.Create(new ArtifactStatusRequest(status)),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    private static async Task<ArtifactResponse> SetStatusAsync(
        HttpClient client, Guid campaignId, Guid artifactId, string status, long version)
    {
        using var request = Patch(campaignId, artifactId, status, version);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ArtifactResponse>())!;
    }

    private static async Task<ArtifactResponse> CreateArtifactAsync(HttpClient client, Guid campaignId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest("blog", "A draft", "{\"markdown\":\"# Hello\"}"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ArtifactResponse>())!;
    }

    private async Task<(HttpClient Client, Guid CampaignId)> SignedInCampaignAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"status-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Status Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync(
            "/api/v1/campaigns", new CampaignCreateRequest($"Status {Guid.NewGuid():N}", null));
        campaign.EnsureSuccessStatusCode();
        var created = await campaign.Content.ReadFromJsonAsync<CampaignResponse>();
        return (client, created!.Id);
    }
}
