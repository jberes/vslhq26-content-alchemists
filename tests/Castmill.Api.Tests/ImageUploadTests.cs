using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Castmill.Core.Auth;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// Editor image upload. This publishes bytes to a PUBLIC container from a base64 body, so
/// the interesting cases are all the ones where a caller lies: a mislabelled content type,
/// something that is not an image at all, or something enormous.
/// </summary>
[Collection("api")]
public sealed class ImageUploadTests(CastmillApiFactory factory)
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task A_payload_whose_bytes_are_not_an_image_is_refused_whatever_it_claims_to_be()
    {
        var (client, campaignId) = await SetUpAsync();

        // Claims to be a PNG; is actually a shell script.
        var response = await PostAsync(client, campaignId, "evil.png", "image/png",
            "#!/bin/sh\nrm -rf /\n"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// An empty body is rejected by [Required] at the boundary before the size check ever
    /// runs, so this asserts "refused", not a particular status — pinning 400 vs 413 here
    /// would be pinning which guard happened to fire first.
    /// </summary>
    [Fact]
    public async Task An_empty_payload_is_refused()
    {
        var (client, campaignId) = await SetUpAsync();

        var response = await PostAsync(client, campaignId, "empty.png", "image/png", []);

        Assert.False(response.IsSuccessStatusCode);
        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    [Fact]
    public async Task Malformed_base64_is_refused_rather_than_throwing()
    {
        var (client, campaignId) = await SetUpAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/images/upload",
            new { fileName = "x.png", contentType = "image/png", base64 = "not base64 !!!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Another tenant's campaign id is a 404, not a foothold.</summary>
    [Fact]
    public async Task Uploading_into_an_unknown_campaign_is_a_404()
    {
        var (client, _) = await SetUpAsync();

        var response = await PostAsync(client, Guid.NewGuid(), "hero.png", "image/png", PngHeader);

        // 404 when the container is configured; 503 when it is not — either way, never a
        // publish into a campaign this user cannot see.
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable,
            $"Expected 404 or 503, got {(int)response.StatusCode}.");
    }

    // ---- setup -----------------------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, Guid campaignId, string fileName, string contentType, byte[] bytes) =>
        client.PostAsJsonAsync($"/api/v1/campaigns/{campaignId}/images/upload", new
        {
            fileName,
            contentType,
            base64 = Convert.ToBase64String(bytes),
        });

    private async Task<(HttpClient Client, Guid CampaignId)> SetUpAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"up-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Uploader"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Upload campaign", null));
        var campaignId = (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id;
        return (client, campaignId);
    }
}
